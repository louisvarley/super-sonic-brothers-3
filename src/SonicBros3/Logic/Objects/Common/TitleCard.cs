using System.Text.Json;
using S2Port.Extraction.Levels;

namespace SonicBros3.Logic;

/// <summary>
/// The title card, object $34 (ROM 0x16EC0). Its first routine turns the slots from $B080 into six pieces:
/// - the zone name, "ZONE" and the act number: sprites sliding in to their place, 16 px a frame, after a delay;
/// - the blue background (fills plane A two rows a frame);
/// - the yellow band with "SONIC THE HEDGEHOG" (fills from the right);
/// - the red strip (fills columns, with the zig-zag edge sprite).
/// The fills are drawn in VBlank by <see cref="TitleCardPlane"/> from the plane addresses the pieces leave in
/// <see cref="State.PlaneRequest"/>.
///
/// Leaving (started by the level start: <see cref="BeginExit"/>, <see cref="SlideTextOut"/>): the red strip retracts,
/// the yellow band turns blue, then the blue piece calls DeformBgLayer and uncovers the level from the bottom, 32 px
/// a frame; the text pieces wait $2D frames and slide back out, and the zone name then queues the after-card PLCs.
/// </summary>
public sealed class TitleCardObject(ObjectContext ctx) : GameObject(ctx)
{
    public const int Id = 0x34;
    public const uint Mappings = 0x17C3E;

    // Routines.
    private const int Init = 0, BlueIn = 2, YellowIn = 4, RedIn = 6, ZoneNameIn = 8, ZoneIn = 0x0A, ActIn = 0x0C,
        RedOut = 0x0E, YellowOut = 0x10, BlueOutStart = 0x12, BlueOut = 0x14, TextOut = 0x16;

    // The pieces' slots.
    public const int ZoneName = Vars.TitleCardZoneName, ZoneWord = Vars.TitleCardZone, ActNumber = Vars.TitleCardActNumber,
        Blue = Vars.TitleCardBackground, Yellow = Vars.TitleCardBottom, Red = Vars.TitleCardLeft;

    /// <summary>Per piece: routine, frame, half width, delay, start x (also where it leaves to), target x, screen y (0x16F4A).</summary>
    private static readonly (int Routine, int Frame, int Width, int Delay, int X, int Target, int Y)[] Pieces =
    [
        (ZoneNameIn, 0x00, 0x80, 0x1B, 0x240, 0x120, 0xB8),
        (ZoneIn, 0x12, 0x40, 0x1C, 0x028, 0x148, 0xD0),
        (ActIn, 0x13, 0x18, 0x1C, 0x068, 0x188, 0xD0),
        (BlueIn, 0x00, 0x00, 0x00, 0x000, 0x000, 0x00),
        (YellowIn, 0x16, 0x48, 0x08, 0x2A8, 0x168, 0x120),
        (RedIn, 0x17, 0x08, 0x15, 0x080, 0x0F0, 0xF0),
    ];

    /// <summary>A piece's variables.</summary>
    public readonly struct State(SonicObject o)
    {
        /// <summary>$1E: frames to wait (before sliding in, or before sliding out).</summary>
        public int Delay { get => o.Byte(0x1E); set => o.SetByte(0x1E, value); }
        public int OutDelay { get => o.Word(0x1E); set => o.SetWord(0x1E, value); }
        /// <summary>$30: where it slides in to; $32: where it slides out to.</summary>
        public int TargetX { get => o.Word(0x30); set => o.SetWord(0x30, value); }
        public int ExitX { get => o.Word(0x32); set => o.SetWord(0x32, value); }
        /// <summary>$34: the fill's progress (rows, columns or lines; negative when done).</summary>
        public int Progress { get => o.Word(0x34); set => o.SetWord(0x34, value); }
        /// <summary>$36: the plane address VBlank should fill next (0 = nothing).</summary>
        public int PlaneRequest { get => o.Word(0x36); set => o.SetWord(0x36, value); }
        /// <summary>$3A: a copy of the progress for VBlank (the band's or strip's current size).</summary>
        public int Size { get => o.Word(0x3A); set => o.SetWord(0x3A, value); }
        /// <summary>Zone name, $3E: set while leaving (VBlank then paints the level back).</summary>
        public bool Leaving { get => o.Word(0x3E) != 0; set => o.SetWord(0x3E, value ? 0xFFFF : 0); }
    }

    private static State Piece(Ram ram, int slot) => new(new SonicObject(ram, slot));

    /// <summary>0x4EBC: clear the card's slots and place the card object.</summary>
    public static void Start(Ram ram)
    {
        ram.Bytes.AsSpan(ZoneName, 6 * Objects.SlotSize).Clear();
        ram.W8(ZoneName, Id);
    }

    /// <summary>The card is in place (0x4EFC: the zone name reached its target).</summary>
    public static bool IsIn(Ram ram) => new SonicObject(ram, ZoneName).X == Piece(ram, ZoneName).TargetX;

    /// <summary>0x5132: VBlank switches to uncovering and the red strip starts retracting.</summary>
    public static void BeginExit(Ram ram)
    {
        Piece(ram, ZoneName).Leaving = true;
        new SonicObject(ram, Red).Routine = RedOut;
        Piece(ram, Red).Progress = 0x0A;
    }

    /// <summary>The blue piece has gone (0x5152): the level is uncovered.</summary>
    public static bool Uncovered(Ram ram) => ram.U8(Blue) == 0;

    /// <summary>0x5158: the text pieces wait $2D frames, then slide away.</summary>
    public static void SlideTextOut(Ram ram)
    {
        foreach (int slot in new[] { ZoneName, ZoneWord })
        {
            new SonicObject(ram, slot).Routine = TextOut;
            Piece(ram, slot).OutDelay = 0x2D;
        }
        if (ram.U8(ActNumber) != 0)
        {
            new SonicObject(ram, ActNumber).Routine = TextOut;
            Piece(ram, ActNumber).OutDelay = 0x2D;
        }
    }

    public override void Run(SonicObject o)
    {
        var s = new State(o);
        switch (o.Routine)
        {
            case Init: CreatePieces(o); break;
            case BlueIn: FillBlue(s); break;
            case YellowIn: GrowYellow(o, s); break;
            case RedIn: GrowRed(o, s); break;
            case ZoneNameIn:
                if (Wait(o)) return;
                int zone = CurrentZone;
                if (Ram.U16(Vars.CurrentZoneAndAct) == 0x1801) zone = 0x1B;
                if (Camera.IsEasterEgg(zone))
                {
                    o.Mappings = TitleCardArt.EasterEggNameMappings;
                    o.MappingFrame = 0;
                }
                else o.MappingFrame = Ctx.TitleCardNameFrame(zone);
                SlideIn(o, s);
                break;
            case ZoneIn:
                if (Wait(o)) return;
                SlideIn(o, s);
                break;
            case ActIn: ActNumberIn(o, s); break;
            case RedOut: ShrinkRed(o, s); break;
            case YellowOut: YellowToBlue(o, s); break;
            case BlueOutStart:
                Ctx.DeformBgLayer?.Invoke();   // 0x1719C
                o.Routine = BlueOut;
                s.Progress = 0xF0;
                goto case BlueOut;
            case BlueOut:
            {
                // Uncover the level from the bottom, 32 lines a frame.
                int line = (short)s.Progress - 0x20;
                if (line == -0x30) { o.Clear(); return; }
                s.Progress = line;
                s.PlaneRequest = line;
                break;
            }
            case TextOut: SlideTextOut(o, s); break;
        }
    }

    /// <summary>0x16EE6.</summary>
    private void CreatePieces(SonicObject o)
    {
        int a = o.Address;
        foreach (var p in Pieces)
        {
            var piece = new SonicObject(Ram, a);
            var s = new State(piece);
            piece.Id = Id;
            piece.Routine = p.Routine;
            piece.Mappings = Mappings;
            piece.MappingFrame = p.Frame;
            piece.WidthPixels = p.Width;
            s.Delay = p.Delay;
            piece.X = p.X;
            s.ExitX = p.X;
            s.TargetX = p.Target;
            piece.XSub = p.Y;          // screen y
            piece.RenderFlags = 0;     // screen coordinates
            a += Objects.SlotSize;
        }
        Piece(Ram, Yellow).Progress = 0x26;
        Ram.W16(Vars.VscrollFactorFg, 0);
        Ram.W16(Vars.VscrollFactorP2Fg, 0xFF20);
        Ram.Bytes.AsSpan(Vars.HorizScrollBuf, 0x400).Clear();
    }

    /// <summary>0x16F86: count the delay down; true while it runs (the original pops the caller's return).</summary>
    private static bool Wait(SonicObject o)
    {
        o.AnimFrameDuration--;   // the delay at $1E
        if (o.AnimFrameDuration != 0) return true;
        o.AnimFrameDuration = 1;
        return false;
    }

    /// <summary>0x16F98: two more rows of blue each frame, 16 times.</summary>
    private static void FillBlue(State s)
    {
        int rows = s.Progress;
        if (rows == 0x10) return;
        s.PlaneRequest = 0xC000 + (rows << 8);
        s.Progress = rows + 1;
    }

    /// <summary>0x16FCE: the yellow band grows leftwards from column $26, two columns a frame, to column 6.</summary>
    private void GrowYellow(SonicObject o, State s)
    {
        if (Wait(o)) return;
        int column = (short)s.Progress;
        if (column >= 0)
        {
            s.PlaneRequest = 0xC000 + column * 2 + 0xA00;
            int next = (s.Progress - 2) & 0xFFFF;
            s.Progress = next;
            s.Size = next;
            if (next == 6) o.SetByte(0x34, 0xFF);   // seq.b on the high byte: done
        }
        SlideIn(o, s);
    }

    /// <summary>0x1701C: the red strip widens two columns a frame, up to $E.</summary>
    private void GrowRed(SonicObject o, State s)
    {
        if (Wait(o)) return;
        if ((short)s.Progress >= 0)
        {
            s.PlaneRequest = 0xC000;
            int next = (s.Progress + 2) & 0xFFFF;
            s.Progress = next;
            s.Size = next;
            if (next == 0x0E) o.SetByte(0x34, 0xFF);
        }
        SlideIn(o, s);
    }

    /// <summary>0x1707E: the act number (not in single-act zones).</summary>
    private void ActNumberIn(SonicObject o, State s)
    {
        if (Wait(o)) return;
        int zone = CurrentZone;
        if (zone == S2Port.Extraction.Smb3.Smb3Campaign.Zone) { o.Clear(); return; }
        if (zone is 0x10 or 0x06 or 0x0E) { o.Clear(); return; }
        bool act2Only = zone is 0x05 or 0x12 or 0x14 or 0x16 or 0x18 or 0x1A or 0x1C;
        o.MappingFrame = act2Only ? 0x15 : Ram.U8(Vars.CurrentAct) + 0x13;
        if (zone == Camera.World12Zone) o.MappingFrame = 0x14;   // SMB3 1-2: GRASS LAND act 2
        if (zone == S2Port.Extraction.Smb3.Smb3Levels.W13.Zone) o.MappingFrame = 0x15;
        SlideIn(o, s);
    }

    /// <summary>0x170D2: 16 px a frame towards the target; shown once within x $200.</summary>
    private void SlideIn(SonicObject o, State s)
    {
        int target = s.TargetX;
        if (o.X != target)
        {
            o.X += o.X > target ? -0x10 : 0x10;
            if (o.X > 0x200) return;
        }
        Display(o);
    }

    /// <summary>0x170F8: the red strip retracts four columns a frame, then hands over to the yellow band.</summary>
    private void ShrinkRed(SonicObject o, State s)
    {
        int columns = (short)s.Progress;
        if (columns < 0)
        {
            new SonicObject(Ram, Yellow).Routine = YellowOut;
            Piece(Ram, Yellow).Progress = 0;
            o.Clear();
            return;
        }
        s.PlaneRequest = 0xC000 + columns * 2;
        int next = (s.Progress - 4) & 0xFFFF;
        if (next == 0xFFFE) next = 0;
        s.Progress = next;
        SlideOut(o, s);
    }

    /// <summary>0x1713A: the yellow band turns blue four columns a frame, then the blue piece starts leaving.</summary>
    private void YellowToBlue(SonicObject o, State s)
    {
        int column = s.Progress;
        if (column == 0x28)
        {
            new SonicObject(Ram, Blue).Routine = BlueOutStart;
            o.Clear();
            return;
        }
        s.PlaneRequest = 0xC000 + column * 2 + 0xA00;
        s.Progress = column + 4;
        SlideOut(o, s);
    }

    /// <summary>0x1717A: 32 px a frame back toward where it came from; not shown once there or beyond x $200.</summary>
    private void SlideOut(SonicObject o, State s)
    {
        int target = s.ExitX;
        if (o.X == target) return;
        o.X += o.X > target ? -0x20 : 0x20;
        if (o.X > 0x200) return;
        Display(o);
    }

    /// <summary>0x171D0: wait, slide out, then (the zone name) queue the after-card PLCs and go.</summary>
    private void SlideTextOut(SonicObject o, State s)
    {
        if (s.OutDelay != 0)
        {
            s.OutDelay--;
            Display(o);
            return;
        }
        int target = s.ExitX;
        if (o.X != target)
        {
            o.X += o.X > target ? -0x20 : 0x20;
            if (o.X <= 0x200) { Display(o); return; }
        }
        if (o.Address == ZoneName) Ctx.AfterTitleCard?.Invoke();
        o.Clear();
    }
}

/// <summary>
/// The title card's plane A drawing (0x18B26, run in the title-card VBlank): a 64x32 nametable override over the
/// level's plane A (<see cref="Unset"/> = show the level). Coming in, it fills what the pieces asked for (blue rows,
/// the yellow band, red columns); leaving, it paints the red columns back blue and yellow, the yellow band blue,
/// and redraws two block rows of the level from the camera position each frame.
/// </summary>
public sealed class TitleCardPlane
{
    public const ushort Unset = 0xFFFF;
    private const ushort BlueTile = 0x85DA, YellowTile = 0xA5DC, RedTile = 0x85D8;
    public ushort[] Cells { get; } = new ushort[64 * 32];
    public bool Active { get; private set; }

    public void Begin()
    {
        Array.Clear(Cells);   // ClearScreen: an empty plane A
        Active = true;
    }

    public void End()
    {
        Array.Fill(Cells, Unset);
        Active = false;
    }

    private static int Cell(int address) => ((address - 0xC000) >> 1) & 0x7FF;

    private void Fill(int address, int count, ushort tile, int step = 1)
    {
        int c = Cell(address);
        for (int i = 0; i < count; i++) Cells[(c + i * step) & 0x7FF] = tile;
    }

    private static TitleCardObject.State Piece(Ram ram, int slot) => new(new SonicObject(ram, slot));

    /// <summary>One VBlank.</summary>
    public void Update(Ram ram)
    {
        if (!Active) return;
        var blue = Piece(ram, TitleCardObject.Blue);
        var yellow = Piece(ram, TitleCardObject.Yellow);
        var red = Piece(ram, TitleCardObject.Red);
        if (!Piece(ram, TitleCardObject.ZoneName).Leaving)
        {
            // Blue rows: 2 rows (128 cells).
            int a = Take(blue);
            if (a != 0) Fill(a, 128, BlueTile);
            // The yellow band: 8 rows, (($26 - its size) / 2) * 2 cells wide.
            int width = ((0x26 - yellow.Size) >> 1) * 2;
            a = Take(yellow);
            if (a != 0)
                for (int row = 0; row < 8; row++) Fill(a + row * 0x80, width, YellowTile);
            // Red columns: as many columns as its size, 28 cells down each.
            int columns = red.Size;
            a = Take(red);
            if (a != 0)
                for (int col = 0; col < columns; col++) Fill(a + col * 2, 28, RedTile, 64);
            return;
        }
        // Leaving: red columns back to 20 blue + 8 yellow cells, 4 columns.
        int r = Take(red);
        if (r != 0)
            for (int col = 0; col < 4; col++)
            {
                Fill(r + col * 2, 20, BlueTile, 64);
                Fill(r + col * 2 + 20 * 0x80, 8, YellowTile, 64);
            }
        // Yellow to blue: 4 cells on each of 8 rows.
        int y = Take(yellow);
        if (y != 0)
            for (int row = 0; row < 8; row++) Fill(y + row * 0x80, 4, BlueTile);
        // Level rows: two block rows at camera y + the blue piece's line, the whole plane width.
        int line = blue.PlaneRequest;
        if (line != 0)
        {
            int camY = ram.U16(Vars.CameraYPos);
            for (int i = 0; i < 2; i++)
            {
                int row = ((camY + (short)line + i * 16) & 0xF0) >> 3;
                for (int k = 0; k < 2; k++) Array.Fill(Cells, Unset, ((row + k) & 31) * 64, 64);
            }
        }
        blue.PlaneRequest = 0;
    }

    /// <summary>Takes a piece's plane request (clearing it).</summary>
    private static int Take(TitleCardObject.State piece)
    {
        int v = piece.PlaneRequest;
        if (v != 0) piece.PlaneRequest = 0;
        return v;
    }
}

/// <summary>The title card's art (LoadTitleCard 0x18D7C) and name frames, from objects/title-card/.</summary>
public sealed class TitleCardArt
{
    private readonly byte[] _base, _font;
    private readonly int _baseTile, _letterTile;
    private readonly Dictionary<int, (int FontTile, int Count)[]> _letters = [];
    private readonly int[] _nameFrames;
    public SpriteData? CampaignName { get; }

    public TitleCardArt(string dataDir, string? campaignName = null)
    {
        string dir = Path.Combine(dataDir, "objects", "title-card");
        using var doc = JsonDocument.Parse(S2Port.Extraction.DataFile.ReadAllText(Path.Combine(dir, "title-card.json")));
        var r = doc.RootElement;
        _base = LevelLoader.LoadTileArt(Path.Combine(dir, "base.png"), r.GetProperty("baseTiles").GetInt32());
        _font = LevelLoader.LoadTileArt(Path.Combine(dir, "font.png"), r.GetProperty("fontTiles").GetInt32());
        _baseTile = r.GetProperty("baseVramTile").GetInt32();
        _letterTile = r.GetProperty("letterVramTile").GetInt32();
        foreach (var z in r.GetProperty("letters").EnumerateObject())
            _letters[Convert.ToInt32(z.Name, 16)] = z.Value.EnumerateArray().Select(e => (e[0].GetInt32(), e[1].GetInt32())).ToArray();
        _nameFrames = r.GetProperty("nameFrames").EnumerateArray().Select(v => v.GetInt32()).ToArray();
        if (campaignName is not null)
        {
            const string alphabet = "ABCDFGHIJKLMPQRSTUVWXY";
            int[] offsets = [0, 4, 8, 12, 16, 20, 24, 28, 30, 34, 38, 42, 48, 52, 56, 60, 64, 68, 72, 76, 82, 86];
            var loaded = new Dictionary<char, (int Tile, int Size, int Width)>();
            var letters = new List<(int FontTile, int Count)>();
            int nextTile = _letterTile;
            foreach (char c in campaignName.Distinct().Where(c => c != ' '))
            {
                int baseIndex = "ENOZ".IndexOf(c);
                if (baseIndex >= 0) { loaded[c] = (_baseTile + baseIndex * 4, 5, 16); continue; }
                int index = alphabet.IndexOf(c);
                if (index < 0) throw new ArgumentException($"Unsupported title-card letter {c}.");
                int width = c == 'I' ? 8 : c is 'M' or 'W' ? 24 : 16;
                int count = width / 4;
                letters.Add((offsets[index], count));
                loaded[c] = (nextTile, c == 'I' ? 1 : c is 'M' or 'W' ? 9 : 5, width);
                nextTile += count;
            }
            _letters[S2Port.Extraction.Smb3.Smb3Campaign.Zone] = letters.ToArray();
            int x = -campaignName.Sum(c => c == ' ' ? 16 : loaded[c].Width) / 2;
            var pieces = new List<MappingPiece>();
            foreach (char c in campaignName)
            {
                if (c == ' ') { x += 16; continue; }
                var glyph = loaded[c];
                pieces.Add(new(0, glyph.Size, 0x8000 | glyph.Tile, x));
                x += glyph.Width;
            }
            CampaignName = new SpriteData { Frames = [[.. pieces]] };
        }
    }

    public int NameFrame(int zone) => zone < _nameFrames.Length ? _nameFrames[zone] : 0;

    // The SMB3 Easter egg's name, "GRASS LAND" (SMB3's World 1). E, N, O and Z are in the base art (for "ZONE");
    // the other letters come from the font, 2x2 tiles each: A 0, B 4, C 8, D 12, F 16, G 20, H 24, I 28 (1x2),
    // J 30, K 34, L 38, M 42 (3x2), P 48, Q 52, R 56, S 60, T 64, U 68, V 72, W 76 (3x2), X 82, Y 86.
    private static readonly (int FontTile, int Count)[] EasterEggLetters = [(20, 4), (56, 4), (0, 4), (60, 4), (38, 4), (12, 4)];

    /// <summary>A made-up mapping pointer (outside the ROM) for the Easter egg's name frame.</summary>
    public const uint EasterEggNameMappings = 0xF20000;

    /// <summary>"GRASS LAND" as a zone-name frame, using the letters <see cref="Load"/> puts at the letter tiles.</summary>
    public static SpriteData EasterEggName(int letterTile, int baseTile)
    {
        int Letter(int index) => 0x8000 | (letterTile + index * 4);   // in EasterEggLetters' order
        int g = Letter(0), r = Letter(1), a = Letter(2), s = Letter(3), l = Letter(4), d = Letter(5);
        int n = 0x8000 | (baseTile + 4);   // base art: E, N, O, Z
        int[] word = [g, r, a, s, s, -1, l, a, n, d];
        var pieces = new List<MappingPiece>();
        int x = -72;
        foreach (int pattern in word)
        {
            if (pattern >= 0) pieces.Add(new MappingPiece(0, 5, pattern, x));   // size 5: 2x2 tiles
            x += 16;
        }
        return new SpriteData { Frames = [[.. pieces]] };
    }

    public int LetterTile => _letterTile;
    public int BaseTile => _baseTile;

    /// <summary>Copies the base art and the zone's letters to VRAM.</summary>
    public void Load(byte[] vram, int zone)
    {
        _base.CopyTo(vram, _baseTile * 32);
        int dest = _letterTile * 32;
        if (!_letters.TryGetValue(zone, out var list))
        {
            if (!Camera.IsEasterEgg(zone)) return;
            list = EasterEggLetters;
        }
        foreach (var (tile, count) in list)
        {
            _font.AsSpan(tile * 32, count * 32).CopyTo(vram.AsSpan(dest));
            dest += count * 32;
        }
    }
}
