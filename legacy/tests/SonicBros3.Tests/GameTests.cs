using S2Port.Extraction.Smb3;
using SonicBros3.Frontend;
using SonicBros3.Logic;
using SonicBros3.Smb3;

namespace SonicBros3.Tests;

/// <summary>The game's data: sonicbros3.bin beside rom.bin, searched upwards; tests that need it skip without it.</summary>
internal static class TestData
{
    public static string? Dir { get; } = Find();

    private static string? Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string archive = Path.Combine(dir.FullName, "sonicbros3.bin");
            if (File.Exists(archive) && Smb3Assets.Available(archive)) return archive;
        }
        return null;
    }
}

/// <summary>The player's smb3.nes, searched upwards (tests that need it skip without it).</summary>
internal static class Smb3Rom
{
    public static NesRom? Image { get; } = Find();

    private static NesRom? Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string path = Path.Combine(dir.FullName, "smb3.nes");
            if (!File.Exists(path)) continue;
            var rom = NesRom.Load(path);
            return rom.Sha1 == Smb3Map.SupportedSha1 ? rom : null;
        }
        return null;
    }
}

/// <summary>The ported level loader against SMB3's own, run on the 6502 core (as tools/Validate smb3-level-6502).</summary>
public class LevelLoaderTests
{
    [Theory]
    [InlineData("1-1")]
    [InlineData("1-2")]
    public void PortedLoader_MatchesTheOriginalOn6502(string level)
    {
        if (Smb3Rom.Image is not { } rom) return;
        var spec = level == "1-1" ? Smb3Levels.W11 : Smb3Levels.W12;
        byte[] pool = Convert.FromHexString("F9E211D5F65DB10A6800");
        var decoder = new Smb3LevelDecoder(rom, spec.Layout, spec.Tileset);
        pool.CopyTo(decoder.RandomPool, 0);
        decoder.Load();
        var cpu = new Cpu6502(rom);
        cpu.Banks[1] = spec.Layout.Bank;
        cpu.Banks[2] = 14;
        cpu.Ram[0x61] = (byte)spec.Layout.Addr;
        cpu.Ram[0x62] = (byte)(spec.Layout.Addr >> 8);
        cpu.Ram[0x70A] = (byte)spec.Tileset;
        pool.CopyTo(cpu.Ram, 0x781);
        cpu.Call(0xA408);   // LevelLoad_TS1 / LevelLoad_TS3
        int cells = (decoder.Width + 1) * Smb3Map.ScreenSize;
        Assert.Equal(cpu.PrgRam[..cells], decoder.TileMem[..cells].ToArray());
    }
}

public class NesPpuTests
{
    [Fact]
    public void Stream_WritesRunsRepeatsVerticalRunsAndPalette()
    {
        var ppu = new NesPpu();
        byte[] stream =
        [
            0x20, 0x00, 0x03, 0x11, 0x12, 0x13,          // 3 bytes across
            0x20, 0x20, 0x40 | 0x04, 0x55,               // VU_REPEAT 4
            0x20, 0x1F, 0x80 | 0x02, 0x66, 0x67,         // VU_VERT 2
            0x3F, 0x10, 0x01, 0x2A,                      // $3F10 mirrors $3F00
            0x00,
        ];
        ppu.RunStream(a => stream[a], 0);
        Assert.Equal([0x11, 0x12, 0x13], new[] { ppu.Tile(0, 0, 0), ppu.Tile(0, 1, 0), ppu.Tile(0, 2, 0) });
        Assert.All(Enumerable.Range(0, 4), c => Assert.Equal(0x55, ppu.Tile(0, c, 1)));
        Assert.Equal(0x66, ppu.Tile(0, 31, 0));
        Assert.Equal(0x67, ppu.Tile(0, 31, 1));   // written after the repeat: the vertical run overwrites nothing else
        Assert.Equal(0x2A, ppu.Palette[0]);
    }

    [Fact]
    public void HorizontalMirroring_StacksScreensVertically()
    {
        var ppu = new NesPpu { HorizontalMirroring = true };
        ppu.Write(0x2400, 7);
        Assert.Equal(7, ppu.Tile(0, 0, 0));        // $2400 = $2000
        ppu.Write(0x2800, 9);
        Assert.Equal(7, ppu.Tile(0, 0, 0));        // $2800 is its own screen
        Assert.Equal(9, ppu.Tile(3, 0, 0));        // ... mirrored at $2C00
    }

    [Fact]
    public void Attributes_SelectPalettePer16x16()
    {
        var ppu = new NesPpu();
        ppu.SetCellPalette(2, 6, 10, 3);
        Assert.Equal(3, ppu.CellPalette(2, 6, 10));
        Assert.Equal(3, ppu.CellPalette(2, 7, 11));
        Assert.Equal(0, ppu.CellPalette(2, 4, 10));
    }
}

/// <summary>The whole game headless through <see cref="GameFlow"/>: the pad as SMB3's player would press it.</summary>
public class FlowTests
{
    private const int Up = 0x01, Down = 0x02, Right = 0x08, A = 0x40, Start = 0x80;

    private static void Run(GameFlow flow, int frames, int buttons = 0)
    {
        for (int i = 0; i < frames; i++) flow.Step(buttons);
    }

    private static void Press(GameFlow flow, int buttons, int hold = 4)
    {
        Run(flow, hold, buttons);
        Run(flow, 2);
    }

    /// <summary>From the map's intro: right to the junction, up to panel 1.</summary>
    private static void WalkToPanel1(GameFlow flow)
    {
        Run(flow, 160);                 // the WORLD 1 box goes
        Press(flow, Right);
        Run(flow, 20);
        Press(flow, Up);
        Run(flow, 20);
    }

    [Fact]
    public void Title_StartTwice_GoesToTheWorldMap()
    {
        if (TestData.Dir is not { } data) return;
        var flow = new GameFlow(data, null);
        Run(flow, 100);
        Press(flow, Start);             // skips the intro to the menu
        Assert.IsType<Sb3TitleScreen>(flow.Screen);
        Press(flow, Start);             // 1 PLAYER GAME
        Run(flow, 120);
        Assert.IsType<WorldMapScreen>(flow.Screen);
        Assert.Equal(4, flow.Progress.Lives);
    }

    [Fact]
    public void Map_WalksToPanel1_ThenOnlyTheWayBackIsOpen()
    {
        if (TestData.Dir is not { } data) return;
        var flow = new GameFlow(data, null, GameFlow.Start.Map);
        WalkToPanel1(flow);
        Assert.Equal(WorldMapScreen.Level11Tile, flow.Progress.Position);
        Press(flow, Right);             // a path leads on, but the panel is not cleared
        Run(flow, 20);
        Assert.Equal(WorldMapScreen.Level11Tile, flow.Progress.Position);
        Press(flow, Down);              // back the way he came
        Run(flow, 20);
        Assert.Equal(2 * 16 + 4, flow.Progress.Position);
    }

    [Fact]
    public void Map_Panel1_StartsWorld11()
    {
        if (TestData.Dir is not { } data) return;
        var flow = new GameFlow(data, null, GameFlow.Start.Map);
        WalkToPanel1(flow);
        Press(flow, A);
        Run(flow, 60);
        Assert.NotNull(flow.Session);
        Assert.Equal((S2Port.Extraction.Smb3.Smb3ZoneBuilder.Zone, 1), flow.Session!.CurrentAct);
    }

    [Fact]
    public void Goal_GivesACard_ClearsThePanel_AndReturnsToTheMap()
    {
        if (TestData.Dir is not { } data) return;
        var flow = new GameFlow(data, null, GameFlow.Start.Level, debugStart: (2600, 370));
        Run(flow, 200);                 // the title card
        Run(flow, 40, Right);
        Run(flow, 20, Right | A);       // jump into the goal box
        for (int i = 0; i < 1000 && flow.Session is not null; i++) flow.Step(Right);
        Assert.IsType<WorldMapScreen>(flow.Screen);
        Assert.Single(flow.Progress.Cards);
        Assert.Contains(WorldMapScreen.Level11Tile, flow.Progress.Cleared);
        Assert.True(flow.Progress.Score >= 5000, $"score {flow.Progress.Score}");   // the time bonus
    }

    [Fact]
    public void Death_ReturnsToTheMap_WithALifeLess()
    {
        if (TestData.Dir is not { } data) return;
        var flow = new GameFlow(data, null, GameFlow.Start.Level);
        for (int i = 0; i < 2000 && flow.Session is not null; i++) flow.Step(0);   // the first Goomba walks into him
        Assert.IsType<WorldMapScreen>(flow.Screen);
        Assert.Equal(3, flow.Progress.Lives);
        Assert.Empty(flow.Progress.Cleared);
    }

    [Fact]
    public void ThreeCards_GiveLives_AndEmptyTheSlots()
    {
        if (TestData.Dir is not { } data) return;
        var flow = new GameFlow(data, null, GameFlow.Start.Level, debugStart: (2600, 370));
        flow.Progress.Cards.AddRange([Card.Ring, Card.Shield]);
        Run(flow, 200);
        Run(flow, 40, Right);
        Run(flow, 20, Right | A);
        for (int i = 0; i < 1000 && flow.Session is not null; i++) flow.Step(Right);
        Assert.Empty(flow.Progress.Cards);
        Assert.Equal(4 + 1, flow.Progress.Lives);   // a mixed set: 1 extra life
    }

    [Fact]
    public void World12_SonicRunsUpTheFirstHill()
    {
        if (TestData.Dir is not { } data) return;
        var flow = new GameFlow(data, null, GameFlow.Start.Level, startZone: Smb3Levels.W12.Zone);
        Run(flow, 200);
        var angles = new HashSet<int>();
        for (int i = 0; i < 140; i++)
        {
            flow.Step(Right);
            angles.Add(flow.Session!.World.Sonic.O.Angle);
        }
        var o = flow.Session!.World.Sonic.O;
        Assert.Contains(0xF0, angles);                        // the 22.5 degree slope
        Assert.True(o.X > 300 && o.Y < 340, $"Sonic at {o.X},{o.Y}");   // up on the hill, not stopped at its foot
    }

    [Fact]
    public void World12_GoombaPipe_SendsGoombasOut()
    {
        if (TestData.Dir is not { } data) return;
        // The first pipe (OBJ_CFIRE_GOOMBAPIPE_R at column $19, row $13): Sonic stands to its right.
        var flow = new GameFlow(data, null, GameFlow.Start.Level, debugStart: (0x19 * 16 + 80, 360), startZone: Smb3Levels.W12.Zone);
        int seen = 0;
        for (int i = 0; i < 200 + 0x70 * 3; i++)
        {
            flow.Step(0);
            if (flow.Session is not { } s) break;
            var ram = s.World.Ram;
            for (int slot = Vars.FirstDynamicSlot; slot < Vars.DynamicObjectRamEnd; slot += 0x40)
                if (ram.U8(slot) == SonicBros3.Logic.Smb3.Goomba && new SonicObject(ram, slot).Byte(Smb3Goomba.Emerge) != 0) seen++;
        }
        Assert.True(seen > 0, "no Goomba came out of the pipe");
    }

    [Fact]
    public void World12_Goal_ClearsPanel2()
    {
        if (TestData.Dir is not { } data) return;
        var flow = new GameFlow(data, null, GameFlow.Start.Level, debugStart: (2670, 370), startZone: Smb3Levels.W12.Zone);
        Run(flow, 200);
        Run(flow, 18, Right);
        Run(flow, 20, Right | A);
        for (int i = 0; i < 1000 && flow.Session is not null; i++) flow.Step(Right);
        Assert.IsType<WorldMapScreen>(flow.Screen);
        Assert.Contains(WorldMapScreen.Level12Tile, flow.Progress.Cleared);
        Assert.Single(flow.Progress.Cards);
    }

    /// <summary>Runs the card game up to its first card choice (the host's text and the fades pass).</summary>
    private static GameFlow CardGame(string data)
    {
        var flow = new GameFlow(data, null, GameFlow.Start.Match);
        for (int i = 0; i < 2000 && flow.Progress.Deck is null; i++) flow.Step(0);
        Run(flow, 40);   // the cards fade in
        return flow;
    }

    /// <summary>Moves the cursor from <paramref name="from"/> to <paramref name="to"/> (right, wrapping) and flips it.</summary>
    private static void Pick(GameFlow flow, int from, int to)
    {
        for (int i = from; i != to; i = (i + 1) % 18) { Run(flow, 1, Right); Run(flow, 9); }
        Run(flow, 1, A);
        Run(flow, 40);   // the flip
    }

    [Fact]
    public void CardGame_APair_PaysOut()
    {
        if (TestData.Dir is not { } data) return;
        var flow = CardGame(data);
        var deck = flow.Progress.Deck!;
        int first = 0, second = Array.FindIndex(deck, 1, c => c == deck[0]);
        int lives = flow.Progress.Lives, rings = flow.Progress.Rings, powerUps = flow.Progress.PowerUps.Count;
        Pick(flow, 0, first);
        Pick(flow, first, second);
        Run(flow, 30);   // rings are given one a frame
        Assert.Equal(1, flow.Progress.DeckMatches);
        Assert.True(flow.Progress.Lives > lives || flow.Progress.Rings > rings || flow.Progress.PowerUps.Count > powerUps);
        Assert.True((flow.Progress.Deck![first] & 0x80) != 0 && (flow.Progress.Deck![second] & 0x80) != 0);
    }

    [Fact]
    public void CardGame_TwoMisses_EndIt_AndShutTheSpadePanel()
    {
        if (TestData.Dir is not { } data) return;
        var flow = CardGame(data);
        var deck = flow.Progress.Deck!;
        int at = 0;
        for (int miss = 0; miss < 2; miss++)
        {
            // Two cards that differ, face down.
            int a = Enumerable.Range(0, 18).First(i => (deck[i] & 0x80) == 0 && i >= at);
            int b = Enumerable.Range(a + 1, 17 - a).First(i => (deck[i] & 0x80) == 0 && deck[i] != deck[a]);
            Pick(flow, at, a);
            Pick(flow, a, b);
            at = b;
            Run(flow, 0x40 + 60);   // the miss: both flip back
        }
        for (int i = 0; i < 300 && flow.Screen is MatchGameScreen; i++) flow.Step(0);
        Assert.IsType<WorldMapScreen>(flow.Screen);
        Assert.Contains(WorldMapScreen.SpadeTile, flow.Progress.Cleared);
        Assert.NotNull(flow.Progress.Deck);   // the game in progress is kept
    }
}
