using SonicBros3.Logic;
using SonicBros3.Runtime;
using S2Port.Extraction.Smb3;

namespace SonicBros3.Frontend;

/// <summary>
/// What runs, as in SMB3: the title, then the World 1 map; a level panel starts its level (a <see cref="LevelSession"/>
/// of the SMB3 zone); finishing it or losing a life goes back to the map, with the lives, score and rings carried
/// over and the panel cleared (and the goal's card in the status bar). Three cards give extra lives, as SMB3's
/// card matches do (three of a kind: 2 for rings, 3 for shields, 5 for invincibility; a mixed set 1). Game over
/// goes back to the title.
/// </summary>
public sealed class GameFlow
{
    public enum Start { Title, Map, Level, Match }

    private readonly string _dataDir;
    private readonly Audio.ISoundChips? _chips;
    private readonly Vdp _screenVdp = new();
    private Screen? _screen;
    private MapProgress _progress = new();
    private int _stoppedFrames;
    /// <summary>A start position for the next level (testing), used once.</summary>
    private (int X, int Y)? _debugStart;

    /// <summary>Frames a stopped game (unported code reached) stays on screen before the title comes back.</summary>
    public const int StoppedHoldFrames = 180;

    /// <param name="startZone">With <see cref="Start.Level"/>: a registered SMB3 zone ($40 1-1, $41 1-2, $42 1-3).</param>
    public GameFlow(string dataDir, Audio.ISoundChips? chips, Start start = Start.Title, (int X, int Y)? debugStart = null,
        int startZone = 0x40, int startAct = 1, int startWorld = 1, int startMapScreen = 0)
    {
        _debugStart = debugStart;
        _dataDir = dataDir;
        _chips = chips;
        if (startWorld is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(startWorld));
        _progress.World = startWorld;
        _progress.MapScreen = startMapScreen;
        if (startWorld != 1 || startMapScreen != 0)
        {
            var campaign = Smb3Campaign.Load(dataDir) ?? throw new InvalidDataException("Build the campaign to open other world maps.");
            var map = campaign.Worlds.Single(w => w.Number == startWorld);
            if (startMapScreen < 0 || startMapScreen >= map.Map.Length / 144) throw new ArgumentOutOfRangeException(nameof(startMapScreen));
            _progress.Position = startMapScreen == 0
                ? (new[] { 0x40, 0xA0, 0xA0, 0x40, 0x80, 0x60, 0x30, 0x50 }[startWorld - 1] / 16 - 2) * 16 + 2
                : map.Entries.First(e => e.Screen == startMapScreen && e.AreaId is not null).Row * 16 +
                  map.Entries.First(e => e.Screen == startMapScreen && e.AreaId is not null).Column;
        }
        switch (start)
        {
            case Start.Map: _screen = new WorldMapScreen(dataDir, _progress, worldIntro: true, chips); break;
            case Start.Match:
                _progress.Position = WorldMapScreen.SpadeTile;
                _screen = new MatchGameScreen(dataDir, _progress, chips);
                break;
            case Start.Level:
                if (startZone == Smb3Campaign.Zone)
                {
                    var campaign = Smb3Campaign.Load(dataDir) ?? throw new InvalidDataException("Build the campaign first.");
                    var area = campaign.Areas.Single(a => a.Act == startAct);
                    var owner = campaign.Worlds.SelectMany(w => w.Entries.Where(e => e.AreaId == area.Source.Id).Select(e => (World: w, Entry: e))).FirstOrDefault();
                    if (owner.World is not null)
                    {
                        _progress.World = owner.World.Number;
                        _progress.MapScreen = owner.Entry.Screen;
                        _progress.Position = owner.Entry.Row * 16 + owner.Entry.Column;
                    }
                }
                else _progress.Position = WorldMapScreen.PanelZones.First(p => p.Value == startZone).Key;
                StartLevel(startZone, startAct);
                break;
            default: _screen = new Sb3TitleScreen(dataDir, chips); break;
        }
    }

    public LevelSession? Session { get; private set; }
    public Screen? Screen => _screen;
    public MapProgress Progress => _progress;
    public Vdp Vdp => Session?.Vdp ?? _screenVdp;
    public string? StopMessage => Session is { Exited: false } s ? s.StopMessage : null;

    public void Step(int buttons)
    {
        if (Session is { } s)
        {
            if (!s.IsStopped) { s.Step(buttons); return; }
            if (s.Exited) { EndLevel(s); return; }
            if (s.IsGameOver || ++_stoppedFrames >= StoppedHoldFrames) { Session = null; ToTitle(); }
            return;
        }
        _screen!.Step(buttons);
        switch (_screen.Result)
        {
            case ScreenResult.StartGame when _screen is Sb3TitleScreen:
                _progress = new MapProgress();
                _screen = new WorldMapScreen(_dataDir, _progress, worldIntro: true, _chips);
                break;
            case ScreenResult.StartGame g:
                _screen = null;
                StartLevel(g.Zone, g.Act);
                break;
            case ScreenResult.StartMatchGame:
                _screen = new MatchGameScreen(_dataDir, _progress, _chips);
                break;
            case ScreenResult.BackToMap:
                // The spade panel shuts once played (as SMB3's does) and opens again when a level is cleared.
                if (_screen is MatchGameScreen) _progress.Cleared.Add(_progress.PanelKey(WorldMapScreen.SpadeTile));
                _screen = new WorldMapScreen(_dataDir, _progress, worldIntro: false, _chips);
                break;
            case ScreenResult.BackToTitle:
                ToTitle();
                break;
        }
    }

    private void ToTitle() => _screen = new Sb3TitleScreen(_dataDir, _chips);

    private void StartLevel(int zone, int act = 1)
    {
        _stoppedFrames = 0;
        var p = _progress;
        var debugStart = _debugStart;
        _debugStart = null;
        Session = new LevelSession(_dataDir, zone, act, debugStart, _chips, newGame: ram =>
        {
            // The adventure's lives and score go into the level; rings start at 0 as every Sonic act does.
            ram.W8(Vars.LifeCount, Math.Clamp(p.Lives, 1, 99));
            ram.W32(Vars.Score, (uint)(p.Score / 10));
            ram.W8(Vars.UpdateHudLives, 1);
            ram.W8(Vars.UpdateHudScore, 1);
            // Power-ups won at the card game: each comes out of a monitor at Sonic as the level starts.
            foreach (int item in p.PowerUps)
                if (Objects.FindFreeSlot(ram) is var slot and >= 0)
                {
                    var c = new SonicObject(ram, slot);
                    c.Id = 0x2E;
                    c.X = ram.Sonic.X;
                    c.Y = ram.Sonic.Y - 0x18;
                    c.Anim = item;
                    c.SetWord(Logic.Monitor.BrokenBy, ram.Sonic.Address);
                }
            p.PowerUps.Clear();
        }) { ExitOnRestart = true, CampaignWorld = _progress.World };
    }

    private void EndLevel(LevelSession s)
    {
        var ram = s.World.Ram;
        _progress.Lives = ram.U8(Vars.LifeCount);
        _progress.Score = (int)Math.Min(ram.U32(Vars.Score) * 10L, 9_999_990);
        if (s.MapPipeExit)
        {
            _progress.Rings = Math.Min(_progress.Rings + ram.U16(Vars.RingCount), 99);
            if (s.MapPipeFarExit)
            {
                var campaign = Smb3Campaign.Load(_dataDir)!;
                var source = campaign.Areas.Single(a => a.Act == s.CurrentAct.Act).Source;
                var destinations = campaign.Worlds.Single(w => w.Number == _progress.World).Entries
                    .Where(e => e.AreaId is not null && campaign.Areas.Single(a => a.Source.Id == e.AreaId).Source.ObjectAddress == source.ObjectAddress)
                    .Where(e => e.Screen != _progress.MapScreen || e.Row * 16 + e.Column != _progress.Position).ToArray();
                if (destinations.Length != 1) throw new InvalidDataException($"Pipeway {source.Id} does not have one paired map exit.");
                _progress.MapScreen = destinations[0].Screen;
                _progress.Position = destinations[0].Row * 16 + destinations[0].Column;
                _progress.LastMove = 0;
            }
        }
        if (s.GoalCard is { } card)
        {
            _progress.Rings = Math.Min(_progress.Rings + ram.U16(Vars.RingCount), 99);
            _progress.Cleared.Add(_progress.PanelKey(_progress.Position));
            _progress.Cleared.Remove(_progress.PanelKey(WorldMapScreen.SpadeTile));
            _progress.Cards.Add((Card)card);
            if (_progress.Cards.Count == 3) CardMatch();
        }
        Session = null;
        if (_progress.Lives == 0) { ToTitle(); return; }
        _screen = new WorldMapScreen(_dataDir, _progress, worldIntro: false, _chips);
    }

    /// <summary>EndLevelCard_xUpByMatch: lives for three cards (three alike by kind, else 1), then the slots empty.</summary>
    private void CardMatch()
    {
        var c = _progress.Cards;
        int lives = c.Distinct().Count() > 1 ? 1 : c[0] switch { Card.Ring => 2, Card.Shield => 3, _ => 5 };
        _progress.Lives = Math.Min(_progress.Lives + lives, 99);
        c.Clear();
    }

    public void Render()
    {
        if (Session is { } s) s.Render();
        else _screen!.Render(_screenVdp);
    }

    /// <summary>A line for headless runs: what is showing.</summary>
    public string Describe() => Session is { } s
        ? $"level, phase {s.Phase}, Sonic {s.World.Sonic.O.X},{s.World.Sonic.O.Y}, exited {s.Exited}, card {s.GoalCard}"
        : $"{_screen!.GetType().Name}, result {_screen.Result?.ToString() ?? "none"}, lives {_progress.Lives}, cards {string.Join(",", _progress.Cards)}";
}
