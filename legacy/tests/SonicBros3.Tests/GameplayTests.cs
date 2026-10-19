using S2Port.Extraction;
using S2Port.Extraction.Levels;
using S2Port.Extraction.Smb3;
using SonicBros3.Audio;
using SonicBros3.Frontend;
using SonicBros3.Logic;
using SonicBros3.Runtime;

namespace SonicBros3.Tests;

public class GameplayTests
{
    [Theory]
    [InlineData(1000, 300, 0x300)]
    [InlineData(1000, 300, 0x600)]
    [InlineData(1000, 300, 0x900)]
    [InlineData(1000, 300, 0xC00)]
    [InlineData(420, 290, 0x600)]
    [InlineData(420, 290, 0xC00)]
    [InlineData(700, 250, 0x600)]
    [InlineData(1620, 270, 0x600)]
    [InlineData(1620, 270, 0xC00)]
    [InlineData(2250, 200, 0x900)]
    public void World12_RollingDownhillTowardsPipe_DoesNotKillPlayer(int x, int y, int speed)
    {
        if (CampaignTests.Archive is not { } archive) return;
        var session = new LevelSession(archive, Smb3Levels.W12.Zone, 1, (x, y), titleCards: false);
        for (int f = 0; f < 30; f++) session.Step(0);
        var p = session.World.Sonic.O;
        p.Rolling = true;
        p.Anim = PlayerAnim.Roll;
        p.YRadius = 14;
        p.XRadius = 7;
        p.Y += 5;
        p.XVel = p.Inertia = (short)speed;
        var trail = new Queue<string>();
        for (int f = 0; f < 90; f++)
        {
            trail.Enqueue($"{f}: {p.X},{p.Y} velocity {p.XVel},{p.YVel} angle {p.Angle:X2} air {p.InAir} roll {p.Rolling} routine {p.Routine}");
            if (trail.Count > 8) trail.Dequeue();
            session.Step(10);
            Assert.True(session.StopMessage is null && p.Routine < PlayerRoutine.Hurt,
                $"{session.StopMessage}\n{string.Join("\n", trail)}");
            if (x == 1620)
                Assert.True(p.Y <= 400, $"Sonic passed through the floor at {p.X},{p.Y}.");
        }
        if (x == 1620) Assert.InRange(p.X, 1740, 1800);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void World12_SteepSlopeMeetsFlatFloor_ForBothPlayersAndAreaFormats(bool tails, bool campaign)
    {
        if (CampaignTests.Archive is not { } archive) return;
        var source = Smb3Campaign.Load(archive)!.Areas.Single(a =>
            a.Source.Tileset == Smb3Levels.W12.Tileset &&
            a.Source.LayoutAddress == Smb3Levels.W12.Layout.Addr && a.Source.ObjectAddress == Smb3Levels.W12.Objects.Addr);
        var session = new LevelSession(archive, campaign ? Smb3Campaign.Zone : Smb3Levels.W12.Zone,
            campaign ? source.Act : 1, (1620, 270), titleCards: false);
        var ram = session.World.Ram;
        for (int f = 0; f < 30; f++) session.Step(0);
        var p = tails ? ram.Tails : ram.Sonic;
        if (tails)
        {
            p.X = ram.Sonic.X;
            p.Y = ram.Sonic.Y + 4;
            p.Status = ram.Sonic.Status;
            p.ObjControl = 0;
            p.Routine = PlayerRoutine.Control;
            ram.W16(Vars.TailsCpuRoutine, 6);
        }
        p.Rolling = true;
        p.Anim = PlayerAnim.Roll;
        p.YRadius = 14;
        p.XRadius = 7;
        p.Y += tails ? 1 : 5;
        p.XVel = p.Inertia = 0xC00;
        bool reachedFlatFloor = false;
        for (int f = 0; f < 90; f++)
        {
            ram.W16(Vars.Ctrl2Held, 0x0A00);
            session.Step(10);
            Assert.Null(session.StopMessage);
            Assert.Equal(PlayerRoutine.Control, p.Routine);
            Assert.True(p.Y <= 400, $"Player passed through the floor at {p.X},{p.Y}.");
            reachedFlatFloor |= p.X >= 1740 && p.Angle == 0 && !p.InAir;
        }
        Assert.True(reachedFlatFloor);
    }

    private static LevelSession Session(string archive, Smb3CampaignArea area) =>
        new(archive, Smb3Campaign.Zone, area.Act, titleCards: false);

    private static void PlaceAtPipe(LevelSession session, Smb3Pipe pipe)
    {
        session.Step(0);
        var p = session.World.Sonic.O;
        p.X = pipe.X + (pipe.Direction == 8 ? -p.XRadius : pipe.Direction == 4 ? p.XRadius : 0);
        p.Y = pipe.Y + (pipe.Direction == 2 ? -p.YRadius : pipe.Direction == 1 ? p.YRadius : 0);
        p.XVel = p.YVel = p.Inertia = 0;
        p.InAir = pipe.Direction == 1;
    }

    [Fact]
    public void AlternatePipes_TravelAndReturnWithoutResettingHealthOrTimer()
    {
        if (CampaignTests.Archive is not { } archive) return;
        var campaign = Smb3Campaign.Load(archive)!;
        var source = campaign.Areas.First(a => a.Source.LayoutAddress == Smb3Levels.W11.Layout.Addr &&
            a.Source.ObjectAddress == Smb3Levels.W11.Objects.Addr);
        var session = Session(archive, source);
        var pipe = session.Level.Gameplay!.Pipes.First(p => p.Kind == "alternate");
        PlaceAtPipe(session, pipe);
        var ram = session.World.Ram;
        ram.W16(Vars.RingCount, 37);
        ram.W8(Vars.LifeCount, 8);
        ram.W32(Vars.Score, 1234);
        ram.W32(Vars.Timer, 0x00010200);
        session.World.Sonic.O.HasShield = true;
        session.Step(pipe.Direction);
        Assert.True(session.InPipe);
        for (int f = 0; f < 130; f++) session.Step(0);
        Assert.Null(session.StopMessage);
        Assert.Equal(campaign.Areas.Single(a => a.Source.Id == source.AlternateAreaId).Act, session.CurrentAct.Act);
        Assert.Equal(LevelSession.StartPhase.Playing, session.Phase);
        Assert.False(session.InPipe);
        Assert.Equal(37, session.World.Ram.U16(Vars.RingCount));
        Assert.Equal(8, session.World.Ram.U8(Vars.LifeCount));
        Assert.Equal(1234u, session.World.Ram.U32(Vars.Score));
        Assert.True(session.World.Sonic.O.HasShield);
        Assert.True(session.World.Ram.U32(Vars.Timer) >= 0x00010200);
        var back = session.Level.Gameplay!.Pipes.First(p => p.Kind == "alternate");
        PlaceAtPipe(session, back);
        session.Step(back.Direction);
        for (int f = 0; f < 90; f++) session.Step(0);
        Assert.Equal(source.Act, session.CurrentAct.Act);
        Assert.Equal(37, session.World.Ram.U16(Vars.RingCount));
    }

    [Fact]
    public void LocalTransit_UsesConnectedPipeAndKeepsTheSameWorld()
    {
        if (CampaignTests.Archive is not { } archive) return;
        var source = Smb3Campaign.Load(archive)!.Areas.Single(a => a.Source.Id == "ts01-15-A5CD-C171");
        var session = Session(archive, source);
        var pipe = session.Level.Gameplay!.Pipes.First(p => p.Kind == "local" && p.Direction == 2);
        Assert.True(pipe.Path.Length > 2);
        Assert.Equal(pipe.X, pipe.TargetX);
        Assert.True(pipe.TargetY > pipe.Y);
        PlaceAtPipe(session, pipe);
        var world = session.World;
        session.Step(pipe.Direction);
        for (int f = 0; f < 32 + pipe.Path.Length * 8 + 48; f++) session.Step(0);
        Assert.Same(world, session.World);
        Assert.False(session.InPipe);
        Assert.InRange(session.World.Sonic.O.X, pipe.TargetX - 12, pipe.TargetX + 12);
        Assert.True(session.World.Sonic.O.Y >= pipe.TargetY + 19);
        Assert.Null(session.StopMessage);
    }

    [Fact]
    public void VerticalMaze_TraversesElbowsAndRestoresControls()
    {
        if (CampaignTests.Archive is not { } archive) return;
        var area = Smb3Campaign.Load(archive)!.Areas.Single(a => a.Source.Id == "ts08-18-AB97-CD93");
        var session = Session(archive, area);
        var pipe = session.Level.Gameplay!.Pipes.First(p => p.Kind == "local" && p.Path.Length is > 3 and < 100 &&
            p.Path.Any(point => point[0] != p.X) && p.Path.Any(point => point[1] != p.Y) &&
            p.TargetX is > 16 and < 240);
        PlaceAtPipe(session, pipe);
        session.Step(pipe.Direction);
        for (int f = 0; f < 32 + pipe.Path.Length * 8 + 48; f++) session.Step(0);
        Assert.False(session.InPipe);
        Assert.Null(session.StopMessage);
        Assert.Equal(0, session.World.Sonic.O.ObjControl);
        Assert.Equal(area.Act, session.CurrentAct.Act);
        Assert.InRange(session.World.Sonic.O.X, pipe.TargetX - 60, pipe.TargetX + 60);
        Assert.InRange(session.World.Sonic.O.Y, pipe.TargetY - 60, pipe.TargetY + 60);
    }

    [Fact]
    public void Pipes_RequireCorrectDirectionAndDoNotIncludeDecorativeMouths()
    {
        if (CampaignTests.Archive is not { } archive) return;
        var campaign = Smb3Campaign.Load(archive)!;
        var source = campaign.Areas.First(a => a.Source.LayoutAddress == Smb3Levels.W11.Layout.Addr &&
            a.Source.ObjectAddress == Smb3Levels.W11.Objects.Addr);
        var session = Session(archive, source);
        var gameplay = session.Level.Gameplay!;
        var pipe = gameplay.Pipes.First(p => p.Kind == "alternate");
        PlaceAtPipe(session, pipe);
        session.Step(pipe.Direction == 2 ? 1 : 2);
        Assert.False(session.InPipe);
        for (int i = 0; i < gameplay.Tiles.Length; i++)
            if (gameplay.Tiles[i] == 0xB1)
                Assert.DoesNotContain(gameplay.Pipes, p => p.X == (i % gameplay.Columns) * 16 + 16 &&
                    Math.Abs(p.Y - i / gameplay.Columns * 16) <= 16);
    }

    [Fact]
    public void CommonExit_UsesSourceRoomAndDoesNotResetRings()
    {
        if (CampaignTests.Archive is not { } archive) return;
        var campaign = Smb3Campaign.Load(archive)!;
        var source = campaign.Areas.Where(a => a.Status == "native-partial")
            .First(a => LevelLoader.Load(archive, Smb3Campaign.Zone, a.Act).Gameplay!.Pipes.Any(p => p.Kind == "common-exit"));
        var session = Session(archive, source);
        for (int f = 0; f < 60; f++) session.Step(0);
        var pipe = session.Level.Gameplay!.Pipes.First(p => p.Kind == "common-exit");
        PlaceAtPipe(session, pipe);
        session.World.Ram.W16(Vars.RingCount, 23);
        session.Step(pipe.Direction);
        for (int f = 0; f < 85; f++) session.Step(0);
        Assert.Equal(campaign.Areas.Single(a => a.Source.Id == "ts01-15-BD68-C560").Act, session.CurrentAct.Act);
        Assert.Equal(23, session.World.Ram.U16(Vars.RingCount));
        Assert.False(session.InPipe);
    }

    [Fact]
    public void BonusPipe_ReturnsToTheOriginalAreaRatherThanTheOverworld()
    {
        if (CampaignTests.Archive is not { } archive) return;
        var campaign = Smb3Campaign.Load(archive)!;
        var source = campaign.Areas.Single(a => a.Source.Id == "ts01-15-AF6F-C32D");
        var session = new LevelSession(archive, Smb3Campaign.Zone, source.Act, titleCards: false) { CampaignWorld = 2 };
        for (int f = 0; f < 60; f++) session.Step(0);
        var pipe = session.Level.Gameplay!.Pipes.First(p => p.Kind == "bonus");
        PlaceAtPipe(session, pipe);
        session.Step(pipe.Direction);
        for (int f = 0; f < 120; f++) session.Step(0);
        Assert.Equal(campaign.Areas.Single(a => a.Source.Id == "ts14-13-B1BC-C978").Act, session.CurrentAct.Act);
        var back = session.Level.Gameplay!.Pipes.First(p => p.Kind is "map" or "alternate");
        PlaceAtPipe(session, back);
        session.Step(back.Direction);
        Assert.True(session.InPipe);
        for (int f = 0; f < 90; f++) session.Step(0);
        Assert.Equal(source.Act, session.CurrentAct.Act);
        Assert.False(session.Exited);
        Assert.Null(session.StopMessage);
    }

    [Fact]
    public void CampaignActBeyondByteRange_SurvivesDeathRestart()
    {
        if (CampaignTests.Archive is not { } archive) return;
        var area = Smb3Campaign.Load(archive)!.Areas.Single(a => a.Act == 257);
        var session = Session(archive, area);
        session.World.Ram.W16(Vars.LevelInactiveFlag, 1);
        session.Step(0);
        for (int f = 0; f < 60; f++) session.Step(0);
        Assert.Equal((Smb3Campaign.Zone, 257), session.CurrentAct);
        Assert.Null(session.StopMessage);
    }

    [Fact]
    public void MapPipeway_ChangesMapPositionWithoutClearingPanelOrAwardingCard()
    {
        if (CampaignTests.Archive is not { } archive) return;
        var campaign = Smb3Campaign.Load(archive)!;
        var area = campaign.Areas.Single(a => a.Source.Id == "ts14-13-AD99-C143");
        var game = new GameFlow(archive, null, GameFlow.Start.Level, startZone: Smb3Campaign.Zone, startAct: area.Act);
        for (int f = 0; f < 240; f++) game.Step(0);
        var session = game.Session!;
        var pipe = session.Level.Gameplay!.Pipes.MaxBy(p => p.X)!;
        PlaceAtPipe(session, pipe);
        game.Step(pipe.Direction);
        for (int f = 0; f < 32; f++) game.Step(0);
        Assert.IsType<WorldMapScreen>(game.Screen);
        Assert.Equal(2, game.Progress.World);
        Assert.Equal(4 * 16 + 14, game.Progress.Position);
        Assert.Empty(game.Progress.Cards);
        Assert.Empty(game.Progress.Cleared);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Swimming_RepeatedStrokesAndWaterExitWorkForBothPlayers(bool tails)
    {
        if (CampaignTests.Archive is not { } archive) return;
        var campaign = Smb3Campaign.Load(archive)!;
        var area = campaign.Areas.Single(a => a.Source.Id == "ts03-16-A9D7-C60E");
        var session = Session(archive, area);
        session.Step(0);
        var player = tails ? session.World.Tails : session.World.Sonic;
        var water = session.Level.Gameplay!;
        var cell = Enumerable.Range(3, water.Rows - 6).SelectMany(r =>
            Enumerable.Range(3, water.Columns - 6).Select(c => (X: c * 16 + 8, Y: r * 16 + 8)))
            .First(p => Enumerable.Range(-2, 5).All(dy => Enumerable.Range(-2, 5)
                .All(dx => water.IsWater(p.X + dx * 16, p.Y + dy * 16))));
        var p = player.O;
        p.X = cell.X; p.Y = cell.Y; p.InAir = true; p.YVel = 0x100; p.ObjControl = 0;
        int ctrl = tails ? Vars.Ctrl2Held : Vars.Ctrl1Held;
        var ram = session.World.Ram;
        if (tails) ram.W16(Vars.TwoPlayerMode, 1);
        for (int stroke = 0; stroke < 3; stroke++)
        {
            ram.W16(ctrl, 0);
            player.Step();
            p.Y = cell.Y;
            ram.W16(ctrl, 0x4040);
            player.Step();
            Assert.True(p.Underwater);
            Assert.True(p.YVel < -0x100, $"Stroke {stroke}: velocity {p.YVel}, control {p.ObjControl}, in air {p.InAir}, logical {ram.U16(tails ? Vars.Ctrl2HeldLogical : Vars.Ctrl1HeldLogical):X4}");
            Assert.Equal(PlayerRoutine.Control, p.Routine);
        }
        player.WaterAt = (_, _) => false;
        ram.W16(ctrl, 0);
        player.Step();
        Assert.False(p.Underwater);
        Assert.Equal(0x600, ram.U16(tails ? Vars.TailsTopSpeed : Vars.SonicTopSpeed));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void MapMusic_IsAudibleInEveryWorld(int world)
    {
        if (CampaignTests.Archive is not { } archive) return;
        var mixer = new SoundMixer(archive);
        var map = new WorldMapScreen(archive, new MapProgress { World = world }, false, mixer);
        var samples = new short[1600];
        long energy = 0;
        for (int f = 0; f < 120; f++)
        {
            map.Step(0);
            mixer.Render(samples, 800);
            energy += samples.Sum(s => Math.Abs((int)s));
        }
        Assert.True(energy > 1_000_000, $"World {world} map energy: {energy}");
    }

    [Fact]
    public void MapMusic_EachWorldPlaysItsOwnSongNotAFallback()
    {
        if (CampaignTests.Archive is not { } archive) return;
        var hashes = new HashSet<string>();
        for (int world = 1; world <= 8; world++)
        {
            var mixer = new SoundMixer(archive);
            var map = new WorldMapScreen(archive, new MapProgress { World = world }, false, mixer);
            var audio = new short[1600 * 120];
            for (int frame = 0; frame < 120; frame++)
            {
                map.Step(0);
                mixer.Render(audio.AsSpan(frame * 1600, 1600), 800);
            }
            hashes.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(audio.AsSpan()))));
        }
        Assert.Equal(8, hashes.Count);
    }

    [Fact]
    public void EntryEffect_ClosesBlackBorderAndUsesSourceSound()
    {
        if (CampaignTests.Archive is not { } archive) return;
        var mixer = new SoundMixer(archive);
        var map = new WorldMapScreen(archive, new MapProgress { Position = WorldMapScreen.Level11Tile }, false, mixer);
        for (int f = 0; f < 60; f++) map.Step(0);
        map.Step(0x40);
        var vdp = new Vdp();
        map.Render(vdp);
        Assert.Equal(1, map.EntryProgress);
        Assert.Equal(63, vdp.Frame[0]);
        Assert.NotEqual(63, vdp.Frame[112 * 320 + 160]);
        for (int f = 0; f < 31; f++) map.Step(0);
        map.Render(vdp);
        Assert.Equal(32, map.EntryProgress);
        Assert.All(vdp.Frame, p => Assert.Equal(63, p));
        var bytes = DataFile.ReadAllBytes(Path.Combine(archive, "campaign", "effects", "enter-level.pcm"));
        Assert.InRange(bytes.Length, 10000, 150000);
        Assert.Contains(bytes, b => b != 0);
    }
}
