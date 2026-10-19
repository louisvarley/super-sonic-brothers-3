using SonicBros3.Logic;
using SonicBros3.Smb3;
using S2Port.Extraction.Smb3;

namespace SonicBros3.Frontend;

/// <summary>Where the map is in the adventure: lives, score, rings, the panels cleared and the cards collected.</summary>
public sealed class MapProgress
{
    public int Lives { get; set; } = 4;
    public int Score { get; set; }
    public int Rings { get; set; }
    public int World { get; set; } = 1;
    public int MapScreen { get; set; }
    public int PanelKey(int tile) => (World - 1) * 4096 + MapScreen * 256 + tile;
    public bool IsCleared(int tile) => Cleared.Contains(PanelKey(tile));
    /// <summary>Cleared panels by map tile position (row * 16 + column of the World 1 layout).</summary>
    public HashSet<int> Cleared { get; } = [];
    /// <summary>The goal cards in the three status bar slots (<see cref="Card"/>), oldest first.</summary>
    public List<Card> Cards { get; } = [];
    /// <summary>Mini Sonic's tile (row * 16 + column) and the direction of his last move (SMB3's pad bits).</summary>
    public int Position { get; set; } = StartTile;
    /// <summary>Monitor items (Sonic 2's: 5 shoes, 6 shield, 7 invincibility) won at the card game, given at the
    /// start of the next level.</summary>
    public List<int> PowerUps { get; } = [];
    /// <summary>The card game in progress (SMB3's Map_NSpadeInProg: the deck stays as left until it is cleared).</summary>
    public int[]? Deck { get; set; }
    /// <summary>Pairs found in <see cref="Deck"/> (Map_NSpadeMatches).</summary>
    public int DeckMatches { get; set; }
    public int LastMove { get; set; }

    public const int StartTile = 2 * 16 + 2;
}

/// <summary>The goal roulette's three cards (SMB3's mushroom, flower and star), as Sonic monitor icons.</summary>
public enum Card { Ring, Shield, Invincibility }

/// <summary>
/// SMB3's World 1 map with a mini Sonic. The map is SMB3's own: the World 1 layout (W1_Map_Layout, prg012), drawn with
/// the map tileset (Tile_Layout_TS0) into tile memory rows 15-26 as Map_Reload_with_Completions does (black row, top
/// border $4E, the 16x9 layout, bottom border Map_Bottom_Tiles), the map palette (PalSet_Maps, World 1), the four
/// animation frames of the first background bank (Map_DoAnimations: $14/$70/$72/$74 every $11+1 frames) and the status
/// bar (the StatusBar stream, prg030, on the status bar banks $5C-$5F from line 193). Movement follows
/// Map_CheckDoMove: a direction is taken when the next tile that way is a path for that direction, and moves 32 pixels
/// at 2 per frame; on a level panel not yet cleared only the way back is open (as SMB3 stops Mario on it).
/// Cleared panels show an "S" panel in place of SMB3's "M" (the same CHR tiles, redrawn).
/// </summary>
public sealed class WorldMapScreen : Screen
{
    // Addresses in the copied banks (Smb3Assets).
    private const int TileLayoutTs0 = 0xA000, TileAttributesTs0 = 0xA400, W1Layout = 0xA5AA;   // bank 12
    private const int PalSetMaps = 0xABD2, MapObjectColours = PalSetMaps + 8 * 16;            // bank 27
    private const int StatusBarStream = 0x811B;                                              // bank 30
    private const int WorldIntroStream = 0xC000;                                             // bank 10
    private static readonly int[] AnimBanks = [0x14, 0x70, 0x72, 0x74];
    private const int AnimTicks = 0x11 + 1;

    // Map tiles (smb3.asm TILE_*).
    private const int TileHorzPath = 0x45, TileVertPath = 0x46, TileBridge = 0xB3, TilePanel1 = 0x03, TileStart = 0xE5,
        TileCompleted = 0x00, TileBorder3 = 0x4E, TileBorder1 = 0x4F, TileBlack = 0x02;
    private static readonly int[] HorzPaths = [TileHorzPath, 0xB2, TileBridge, 0xAC, 0xB7, 0xB8, 0xDA, 0xB9, 0xE6];
    private static readonly int[] VertPaths = [TileVertPath, 0xB1, 0xAA, 0xAB, 0xB0, 0xDB, 0xBA];
    private static readonly int[] CompletableTiles = [0x50, 0xE8, 0xE6, 0xBD, 0xE0];

    /// <summary>The 1-1 and 1-2 panels (row 0, columns 4 and 8 of the World 1 layout) and the spade panel (row 4,
    /// column 8): the ones that lead somewhere.</summary>
    public const int Level11Tile = 0 * 16 + 4, Level12Tile = 0 * 16 + 8, Level13Tile = 0 * 16 + 10,
        SpadeTile = 4 * 16 + 8;

    /// <summary>The zone each built level panel starts.</summary>
    public static readonly Dictionary<int, int> PanelZones = new()
    {
        [Level11Tile] = S2Port.Extraction.Smb3.Smb3Levels.W11.Zone,
        [Level12Tile] = S2Port.Extraction.Smb3.Smb3Levels.W12.Zone,
        [Level13Tile] = S2Port.Extraction.Smb3.Smb3Levels.W13.Zone,
    };

    // VRAM tiles of the screen's own art.
    private const int MiniSonicTile = 0x5C0, MonitorTile = 0x580, CustomTile = 0x5F0;

    private readonly Smb3Assets _assets;
    private readonly NesPresenter _nes;
    private readonly NesPpu _ppu = new() { HorizontalMirroring = false };
    private readonly SpriteKit _sprites;
    private readonly MapProgress _progress;
    private readonly bool _intro;
    private readonly int[] _layout = new int[9 * 16];
    private readonly SpriteData _monitor;
    private readonly Dictionary<int, Smb3CampaignArea> _areas = [];
    private readonly bool _legacyMap;
    private readonly Smb3World? _world;
    private int? _nextMapScreen;

    private int _animFrame, _animTicks = AnimTicks;
    private int _x, _y, _moveLeft, _moveDir, _frames;
    private bool _boxShown;
    private readonly Audio.Smps68kDriver? _mapMusic;
    public int EntryProgress { get; private set; }

    /// <summary>Set when Sonic enters a level panel: the panel's tile position.</summary>
    public int? EnteredTile { get; private set; }

    public WorldMapScreen(string dataDir, MapProgress progress, bool worldIntro, Audio.ISoundChips? chips = null)
        : base(dataDir, chips)
    {
        _progress = progress;
        _intro = worldIntro;
        _assets = new Smb3Assets(dataDir);
        _nes = new NesPresenter(Vram, Ram, _assets);
        _sprites = new SpriteKit(Ram, Vram);
        _monitor = Art["monitor"];
        _legacyMap = progress.World == 1 && progress.MapScreen == 0;
        var campaign = Smb3Campaign.Load(dataDir);
        if (campaign is not null && chips is not null)
        {
            Ram.W8(Vars.SoundDriverSelect, 1);
            string music = Path.Combine(dataDir, "conversion", "music", $"set2A_{progress.World * 16:X2}.json");
            if (!S2Port.Extraction.DataFile.Exists(music))
                throw new InvalidDataException($"World {progress.World} map music was not converted.");
            _mapMusic = new Audio.Smps68kDriver(Ram,
                new Audio.Smps68kData(dataDir, music), chips);
            Ram.W8(Audio.Smps68kDriver.Base + 9, 0x80);
            Sound.PlayMusic(0x94);
        }
        _world = campaign?.Worlds.Single(w => w.Number == progress.World);
        for (int i = 0; i < _layout.Length; i++)
            _layout[i] = _world is null ? _assets.Prg(12, W1Layout + i) : _world.Map[progress.MapScreen * 144 + i];
        for (int t = 0; t < _layout.Length; t++)
            if (progress.IsCleared(t)) _layout[t] = CompletedTile(_layout[t]);
        if (campaign is not null)
            foreach (var entry in _world!.Entries.Where(e => e.Screen == progress.MapScreen && e.AreaId is not null && e.Row is >= 0 and < 9))
            {
                var area = campaign.Areas.Single(a => a.Source.Id == entry.AreaId);
                if (area.Status is not ("failed" or "blocked")) _areas[entry.Row * 16 + entry.Column] = area;
            }
        (_x, _y) = TilePixel(progress.Position);
        // The cleared panel's "M" (CHR $88-$8B: upper left, lower left, upper right, lower right) as an "S".
        for (int q = 0; q < 4; q++) _nes.TileOverrides[(0x16, 0x08 + q)] = StatusBar.Glyph(SPanel, (q >> 1) * 8, (q & 1) * 8);
        // Nametable row r is plane row r; NES line n is Mega Drive line n - 8, and the status bar (row 24) starts on
        // NES line 193 (the IRQ), one line lower than the nametable alone would put it.
        LineScrollA = line => line + 8 < 193 ? 8 : 7;
    }

    /// <summary>Map_CompleteByML_Tiles: the "M" (here "S") panel of the tile's colour quadrant.</summary>
    private static int CompletedTile(int tile) => (tile & 0xC0) | TileCompleted;

    /// <summary>NES pixel of a layout tile (layout row r is tile memory row 17 + r, NES row 2 + r of the screen).</summary>
    private static (int X, int Y) TilePixel(int tile) => ((tile & 15) * 16, ((tile >> 4) + 2) * 16);

    private int LayoutAt(int x, int y)
    {
        int c = x >> 4, r = (y >> 4) - 2;
        return c is < 0 or > 15 || r is < 0 or > 8 ? TileBlack : _layout[r * 16 + c];
    }

    protected override IEnumerable<bool> Mode()
    {
        ClearScreen();
        BackdropIndex = 0x20;
        LoadOwnArt();
        for (int i = 0; i < 16; i++)
        {
            _ppu.Palette[i] = _assets.Prg(27, PalSetMaps + (_progress.World - 1) * 16 + i);
            _ppu.Palette[16 + i] = _assets.Prg(27, MapObjectColours + i);
        }
        PaletteFade.LoadForFade(Ram, DataDir, 3);   // Sonic's line (0) for mini Sonic, line 1 for the ring icon
        DrawMap();
        DrawStatusBar();
        if (_intro) { DrawWorldIntroBox(); _boxShown = true; }
        _fading = true;
        Present();
        foreach (var f in FadeFromBlack()) yield return f;
        _fading = false;

        if (_boxShown)
        {
            for (int i = 0; i < 120 && (Ram.U8(Vars.Ctrl1Press) & 0xF0) == 0; i++) { Step(); yield return true; }
            _boxShown = false;
            DrawMap();
        }

        while (true)
        {
            Step();
            int press = Ram.U8(Vars.Ctrl1Press), held = Ram.U8(Vars.Ctrl1Held);
            if (_moveLeft == 0)
            {
                // Built levels can be played again once cleared (SMB3 shuts cleared panels), to collect more cards.
                // The spade panel (a card game) shuts once played, as SMB3's does.
                bool legacyStage = _legacyMap && PanelZones.ContainsKey(CurrentTile);
                bool minigame = _legacyMap && CurrentTile == SpadeTile && !_progress.IsCleared(SpadeTile);
                bool built = legacyStage || _areas.ContainsKey(CurrentTile) || minigame;
                if ((press & 0xF0) != 0 && (built || IsLevel(LayoutAt(_x, _y)) && !_progress.IsCleared(CurrentTile)))
                {
                    if (built)
                    {
                        if (Chips is Audio.SoundMixer mixer) mixer.PlayEffect(DataDir, "enter-level");
                        Sound.PlaySound(0xFD);
                        EnteredTile = CurrentTile;
                        _fading = true;
                        for (int i = 1; i <= 32; i++) { EntryProgress = i; Step(); yield return true; }
                        Result = legacyStage ? new ScreenResult.StartGame(PanelZones[CurrentTile], 1)
                            : _areas.TryGetValue(CurrentTile, out var area) ? new ScreenResult.StartGame(Smb3Campaign.Zone, area.Act)
                            : new ScreenResult.StartMatchGame();
                        yield break;
                    }
                    Sound.PlaySound(Sfx.Hurt);
                }
                else TryMove(held & 0x0F);
            }
            yield return true;
        }
    }

    private int CurrentTile => ((_y >> 4) - 2) * 16 + (_x >> 4);

    protected override void UpdateSound()
    {
        if (_mapMusic is not null) _mapMusic.Update();
        else base.UpdateSound();
    }

    public override void Render(Runtime.Vdp vdp)
    {
        base.Render(vdp);
        if (EntryProgress == 0) return;
        vdp.Cram[63] = 0;
        int marginX = EntryProgress * 160 / 32, marginY = EntryProgress * 112 / 32;
        for (int y = 0; y < 224; y++)
        for (int x = 0; x < 320; x++)
            if (x < marginX || x >= 320 - marginX || y < marginY || y >= 224 - marginY)
                vdp.Frame[y * 320 + x] = 63;
    }

    /// <summary>Map_CheckDoMove: pad bits Up 1, Down 2, Left 4, Right 8 (Mega Drive order).</summary>
    private void TryMove(int pad)
    {
        (int Bit, int Dx, int Dy, int[] Paths)? dir = pad switch
        {
            0x08 => (0x08, 1, 0, HorzPaths),
            0x04 => (0x04, -1, 0, HorzPaths),
            0x02 => (0x02, 0, 1, VertPaths),
            0x01 => (0x01, 0, -1, VertPaths),
            _ => null,
        };
        if (dir is not { } d) return;
        // On a level panel not cleared yet only the way back is open (the start panel is not a level).
        int here = LayoutAt(_x, _y);
        if (IsLevel(here) && !_progress.IsCleared(CurrentTile) && d.Bit != Reverse(_progress.LastMove)) return;
        int next = LayoutAt(_x + d.Dx * 16, _y + d.Dy * 16);
        int destinationX = _x + d.Dx * 32;
        if (_world is not null && d.Dy == 0 && (destinationX < 0 || destinationX >= 256))
        {
            int screen = _progress.MapScreen + d.Dx;
            if (screen < 0 || screen >= _world.Map.Length / 144) return;
            int column = ((_x + d.Dx * 16) / 16 + 16) % 16;
            next = _world.Map[screen * 144 + (CurrentTile >> 4) * 16 + column];
            _nextMapScreen = screen;
        }
        if (!d.Paths.Contains(next)) { _nextMapScreen = null; return; }
        _moveDir = d.Bit;
        _moveLeft = 32;
        _progress.LastMove = d.Bit;
        Sound.PlaySound(Sfx.Blip);
    }

    private static int Reverse(int bit) => bit switch { 0x08 => 0x04, 0x04 => 0x08, 0x02 => 0x01, 0x01 => 0x02, _ => 0 };

    /// <summary>Tile_Attributes_TS0: a tile at or above its quadrant's threshold can be entered (level panels,
    /// fortresses...), and so can the "completable" tiles (Toad houses, the spade panel).</summary>
    private bool IsLevel(int tile) =>
        tile != TileStart && (tile >= _assets.Prg(12, TileAttributesTs0 + (tile >> 6)) || CompletableTiles.Contains(tile));

    /// <summary>One frame: animation, movement, sprites, the planes.</summary>
    private void Step()
    {
        _frames++;
        if (--_animTicks <= 0) { _animTicks = AnimTicks; _animFrame = (_animFrame + 1) & 3; }
        if (_moveLeft > 0)
        {
            int dx = _moveDir == 0x08 ? 2 : _moveDir == 0x04 ? -2 : 0, dy = _moveDir == 0x02 ? 2 : _moveDir == 0x01 ? -2 : 0;
            _x += dx;
            _y += dy;
            _moveLeft -= 2;
            if (_moveLeft == 0)
            {
                if (_nextMapScreen is { } screen)
                {
                    _progress.MapScreen = screen;
                    _x = (_x + 256) % 256;
                    Result = new ScreenResult.BackToMap();
                }
                _progress.Position = CurrentTile;
            }
        }
        Present();
    }

    private void Present()
    {
        // Map rows use the animated bank; the status bar its own banks (the IRQ switches at line 193).
        SetBanks(AnimBanks[_animFrame], AnimBanks[_animFrame] + 1, 0x16, 0x17);
        for (int row = 0; row < 24; row++) _nes.Row(_ppu, 2, row, PlaneA, row);
        SetBanks(0x5C, 0x5D, 0x5E, 0x5F);
        for (int row = 24; row < 30; row++) _nes.Row(_ppu, 2, row, PlaneA, row);
        _nes.Palettes(_ppu, _fading ? Vars.TargetPalette : Vars.NormalPalette);

        _sprites.Begin();
        // Mini Sonic: two frames, 8 frames each while walking, slower when standing (as Map_DrawPlayer).
        int frame = (_frames >> (_moveLeft > 0 ? 3 : 4)) & 1;
        _sprites.Piece(_x + 32, _y - 8 - 1, 5, 0x8000 | (MiniSonicTile + frame * 4));
        DrawCards();
        _sprites.End();
    }

    private void SetBanks(int a, int b, int c, int d)
    {
        _nes.BgBanks[0] = a; _nes.BgBanks[1] = b; _nes.BgBanks[2] = c; _nes.BgBanks[3] = d;
    }

    /// <summary>Tile memory rows 15-26 (Map_Reload_with_Completions) into nametable 2, through Tile_Layout_TS0.</summary>
    private void DrawMap()
    {
        for (int r = 0; r < 12; r++)
        for (int c = 0; c < 16; c++)
        {
            int t = r switch { 0 => TileBlack, 1 => TileBorder3, 11 => TileBorder1, _ => _layout[(r - 2) * 16 + c] };
            DrawMetatile(c, r, t);
        }
    }

    private void DrawMetatile(int c, int r, int t)
    {
        int nt = 0x2800 + r * 64 + c * 2;
        _ppu.Write(nt, _assets.Prg(12, TileLayoutTs0 + t));
        _ppu.Write(nt + 32, _assets.Prg(12, TileLayoutTs0 + 256 + t));
        _ppu.Write(nt + 1, _assets.Prg(12, TileLayoutTs0 + 512 + t));
        _ppu.Write(nt + 33, _assets.Prg(12, TileLayoutTs0 + 768 + t));
        _ppu.SetCellPalette(2, c * 2, r * 2, t >> 6);
    }

    /// <summary>The status bar on the status bar banks, as levels and the map have it ($2B00).</summary>
    private void DrawStatusBar() => StatusBar.Draw(_ppu, _nes, _progress, StatusBar.Stream2B00);

    /// <summary>Video_DoWXMario00 (the "WORLD 1" box) with SONIC for MARIO; "S" is drawn over the unused "Z".</summary>
    private void DrawWorldIntroBox()
    {
        _ppu.RunStream(a => _assets.Prg(10, a), WorldIntroStream);
        int[] sonic = [0xFD, 0xF0, 0xDB, 0xFC, 0xED];
        for (int i = 0; i < 5; i++) _ppu.Write(0x29AA + i, sonic[i]);
        _ppu.Write(0x2952, 0xF0 + _progress.World);
        int lives = Math.Min(_progress.Lives, 9);
        _ppu.Write(0x29B4, 0xF0 + lives);
        _nes.TileOverrides[(0x17, 0x3D)] = StatusBar.Glyph(SLetter, 0);
    }

    /// <summary>The cards in the status bar slots: monitor icons (16x16) inside SMB3's card frames.</summary>
    private void DrawCards()
    {
        for (int i = 0; i < _progress.Cards.Count && i < 3; i++)
        {
            int frame = CardMonitorFrame(_progress.Cards[i]);
            var p = _monitor.Frames[frame][0];
            int x = 32 + (22 + i * 3) * 8, y = 25 * 8 - 8;
            _sprites.Piece(x, y, p.Size, ((p.Pattern + MonitorTile) | 0x8000) & 0xFFFF);
        }
    }

    /// <summary>The monitor mapping frame of each card (Sonic 2's Obj26 frames: rings 5, shield 7, invincibility 8).</summary>
    public static int CardMonitorFrame(Card c) => c switch { Card.Ring => 5, Card.Shield => 7, _ => 8 };

    private void LoadOwnArt()
    {
        // Monitor icons (the monitor art list entry, Nemesis art at VRAM $680 in levels) for the cards.
        var art = S2Port.Extraction.Levels.LevelLoader.LoadTileArt(Path.Combine(DataDir, "art", "nemesis", "nem_080B08.png"), 60);
        art.CopyTo(Vram.AsSpan(MonitorTile * 32));
        MiniSonicArt.Tiles().CopyTo(Vram.AsSpan(MiniSonicTile * 32));
    }

    /// <summary>While a fade runs the NES colours go to its target palette.</summary>
    private bool _fading;

    // Pixel art (values 0-3) for hand-drawn tiles in SMB3's styles: '.' 0, 'w' 1, 'b' 2, 'r' 3.
    private static readonly string[] SLetter =
    [
        "rwwwwwwr",
        "wwbbbbww",
        "wbbwwwww",
        "wwbbbbwr",
        "rwwwwbbw",
        "wwwwwbbw",
        "wbbbbbww",
        "wwwwwwwr",
    ];

    private static readonly string[] SPanel =
    [
        "................",
        ".bbbbbbbbbbbbbb.",
        ".bbbbbbbbbbbbbb.",
        ".bb.........bbb.",
        ".b.rrrrrrrrr.bb.",
        ".b.rr.......bbb.",
        ".b.rr.bbbbbbbbb.",
        ".b..rrrrrrr.bbb.",
        ".bb.......rr.bb.",
        ".bbbbbbbb.rr.bb.",
        ".b........rr.bb.",
        ".b.rrrrrrrr.bbb.",
        ".b.........bbbb.",
        ".bbbbbbbbbbbbbb.",
        ".bbbbbbbbbbbbbb.",
        "................",
    ];
}
