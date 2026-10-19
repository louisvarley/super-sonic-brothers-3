using SonicBros3.Smb3;

namespace SonicBros3.Logic;

/// <summary>
/// The end of World 1-1: SMB3's goal roulette (OBJ_ENDLEVELCARD, prg002 ObjNorm_EndLevelCard) in place of the signpost
/// (it takes the signpost's slot, object $0D, in the SMB3 zone). The card in the goal box changes every 8 frames
/// through SMB3's three (mushroom, flower, star), here Sonic's monitor icons: rings, shield, invincibility. Touched,
/// it keeps the card it showed: Sonic runs off to the right (controls locked, as the signpost does), the card spins
/// up and away, then "COURSE CLEAR!" and "YOU GOT A CARD" appear with the card in its frame (Video_CourseClear /
/// Video_YouGotCard, in SMB3's own font from the player's smb3.nes), and the level ends with that card.
/// Subtype 1 is the text (screen coordinates), spawned by the goal.
/// </summary>
public sealed class RouletteGoal(ObjectContext ctx) : GameObject(ctx)
{
    public const int Id = 0x0D, TextId = 0xE7;
    /// <summary>Made-up mapping pointer for <see cref="Sprites"/> (outside the ROM, like the Smb3 art's).</summary>
    public const uint Mappings = 0xF30000;

    private const int Untouched = 2, FlyAway = 4, Messages = 6;
    private const int MusicActClear = 0x9A;
    private const int RightButton = 0x0800;
    /// <summary>VRAM tiles for the font and the card frame (free in the SMB3 zone, whose art ends at $B5).</summary>
    private const int TextTile = 0x2C0;
    /// <summary>Frames after the card leaves the screen: "COURSE CLEAR!" at $60-$1E, "YOU GOT A CARD" at $60-1
    /// (EndLevelCard_MsgAndCard's timer), then the level ends.</summary>
    private const int CourseClearAt = 0x60 - 0x1E, GotCardAt = 0x60 - 1, EndAt = GotCardAt + 150;

    private SpriteData? _sprites;

    public override void Run(SonicObject o)
    {
        if (o.Id == TextId) { RunText(o); return; }
        switch (o.Routine)
        {
            case 0:
                LoadSprites();
                o.Routine = Untouched;
                o.Mappings = Mappings;
                o.ArtTile = 0;
                o.RenderFlags = Render.LevelCoords;
                o.Priority = 4;
                o.WidthPixels = 16;
                o.Y -= 8;           // the signpost stands 8 below the card's tile
                o.MappingFrame = 0;
                goto case Untouched;
            case Untouched:
                // EndLevelCard_Untouched: frames 0-2, one step every 8 ticks.
                if ((Ram.U16(Vars.LevelFrameCounter) & 7) == 0) o.MappingFrame = (o.MappingFrame + 1) % 3;
                if (Touching(o)) Touched(o);
                DisplayOrMarkGone(o);
                return;
            case FlyAway:
                RunOff();
                o.Y -= 4;
                if ((Ram.U16(Vars.LevelFrameCounter) & 3) == 0) o.RenderFlags ^= Render.XFlip;   // spinning away
                if (o.Y < Ram.U16(Vars.CameraYPos) - 24)
                {
                    o.Routine = Messages;
                    o.SetWord(0x30, 0);
                }
                Display(o);
                return;
            case Messages:
                RunOff();
                int t = o.Word(0x30) + 1;
                o.SetWord(0x30, t);
                if (t == CourseClearAt) SpawnText(3, 104 - 4, 56 + 16);
                if (t == GotCardAt) SpawnText(4 + o.Byte(0x32), 88 - 4, 80 + 16);
                if (t == EndAt) Ctx.GoalReached?.Invoke(o.Byte(0x32));
                return;
        }
    }

    private bool Touching(SonicObject o)
    {
        var s = Sonic;
        if (s.Routine >= 4) return false;   // hurt or dying
        return Math.Abs((short)(s.X - o.X)) < 8 + 9 && Math.Abs((short)(s.Y - o.Y)) < 8 + s.YRadius;
    }

    /// <summary>ObjHit_EndLevelCard: keep the card shown, stop the timer and the power-ups, start the run-off.</summary>
    private void Touched(SonicObject o)
    {
        o.SetByte(0x32, o.MappingFrame);
        o.Routine = FlyAway;
        Ram.W8(Vars.UpdateHudTimer, 0);
        Sonic.StatusSecondary = 0;
        Ctx.Sound.PlaySound(Sfx.Ring);
        Ctx.Sound.PlayMusic(MusicActClear);
        // The time bonus as Sonic's results give it, into the score (SMB3 counts its timer down into the score).
        int seconds = Ram.U8(Vars.TimerMinute) * 60 + Ram.U8(Vars.TimerSecond);
        int[] bonus = [5000, 5000, 1000, 500, 400, 400, 300, 300, 200, 200, 200, 200, 100, 100, 100, 100, 50, 50, 50, 50, 0];
        Ram.W32(Vars.Score, Ram.U32(Vars.Score) + (uint)((bonus[Math.Min(seconds / 15, 20)] + Ram.U16(Vars.RingCount) * 10) / 10));
        Ram.W8(Vars.UpdateHudScore, 1);
    }

    /// <summary>EndLevelCard_PlayerRunOff: Sonic runs to the right (once on the ground).</summary>
    private void RunOff()
    {
        if (Sonic.InAir) return;
        Ram.W8(Vars.ControlLocked, 1);
        Ram.W16(Vars.Ctrl1HeldLogical, RightButton);
    }

    private void SpawnText(int frame, int x, int y)
    {
        if (Spawn() is not { } t) return;
        t.Id = TextId;
        t.Mappings = Mappings;
        t.ArtTile = 0;
        t.RenderFlags = 0;   // screen coordinates: x at $08, y at $0A (+128 each)
        t.Priority = 0;
        t.MappingFrame = frame;
        t.X = x + 128;
        t.XSub = y - 8 + 128;
    }

    private void RunText(SonicObject o)
    {
        // "YOU GOT A CARD" flashes its card for a moment as SMB3's EndLevelCard_DrawFlashing does.
        int age = o.Word(0x30) + 1;
        o.SetWord(0x30, age);
        if (o.MappingFrame >= 4 && age < 0x30 && (age & 4) != 0) return;
        Display(o);
    }

    // ------------------------------------------------------------------ art

    /// <summary>
    /// Frames 0-2: the three cards (monitor icons at VRAM $680, in front of the box outline); 3: "COURSE CLEAR!";
    /// 4-6: "YOU GOT A CARD" with the card frame and card 0-2 in it. Patterns are absolute (art tile 0).
    /// </summary>
    private void LoadSprites()
    {
        if (_sprites is not null && Ctx.Art.TryTable(Mappings, out _)) return;
        var monitor = Ctx.Art["monitor"];
        MappingPiece Icon(int card, int x, int y)
        {
            var p = monitor.Frames[CardFrame(card)][0];
            return new MappingPiece(y, p.Size, (p.Pattern + 0x8680) & 0xFFFF, x);
        }
        string line1 = "COURSE CLEAR!", line2 = "YOU GOT A CARD";
        var frames = new List<MappingPiece[]> { new[] { Icon(0, -8, -8) }, new[] { Icon(1, -8, -8) }, new[] { Icon(2, -8, -8) } };
        frames.Add(TextPieces(TextTile, line1.Length, 0).ToArray());
        int boxTile = TextTile + line1.Length + line2.Length;
        for (int c = 0; c < 3; c++)
        {
            var pieces = TextPieces(TextTile + line1.Length, line2.Length, 0).ToList();
            int bx = (22 - 7) * 8, by = -16;   // the frame at NES column 22, rows 6-9 (the text is column 7, row 7)
            pieces.Insert(0, Icon(c, bx + 8, by + 8));
            pieces.Add(new MappingPiece(by, 0x0F, 0xE000 | boxTile, bx));
            frames.Add(pieces.ToArray());
        }
        _sprites = new SpriteData { Frames = frames, RomPointer = 0 };
        Ctx.Art.Add("sb3-goal", _sprites, Mappings);
        if (Ctx.Vram is { } vram) DrawTiles(vram, line1, line2, boxTile);
    }

    private static IEnumerable<MappingPiece> TextPieces(int firstTile, int count, int y)
    {
        for (int i = 0; i < count; i += 4)
        {
            int w = Math.Min(4, count - i);
            yield return new MappingPiece(y, (w - 1) << 2, 0xE000 | (firstTile + i), i * 8);
        }
    }

    /// <summary>The monitor frame of each card (as <see cref="Frontend.WorldMapScreen.CardMonitorFrame"/>).</summary>
    public static int CardFrame(int card) => card switch { 0 => 5, 1 => 7, _ => 8 };

    /// <summary>SMB3's goal font (CHR bank $5E, tiles $80-$9B) and the card frame (status bar tiles $A0-$A8) as MD
    /// tiles on palette line 3, NES sprite palette 1 (black outline, white letters); the font's background is
    /// left transparent.</summary>
    private void DrawTiles(byte[] vram, string line1, string line2, int boxTile)
    {
        var assets = new Smb3Assets(Ctx.Art.DataDir);
        int t = TextTile;
        foreach (char ch in line1 + line2) Tile(assets, vram, t++, Letter(ch));
        // The 4x4 card frame, column-major: corners $A0/$A2/$A8/$A5, edges $A1/$A6/$A7/$A4, inside blank.
        int[,] frame =
        {
            { 0xA0, 0xA1, 0xA1, 0xA2 },
            { 0xA6, 0xFE, 0xFE, 0xA7 },
            { 0xA6, 0xFE, 0xFE, 0xA7 },
            { 0xA8, 0xA4, 0xA4, 0xA5 },
        };
        for (int x = 0; x < 4; x++)
        for (int y = 0; y < 4; y++) Tile(assets, vram, boxTile + x * 4 + y, frame[y, x]);
    }

    /// <summary>Video_CourseClear's letters: A $80, R $82, S $83, E $84, C $85, G $86, D $87, T $89, U $8A, L $8B,
    /// Y $8D, O $8E, ! $9B; space $FC.</summary>
    private static int Letter(char c) => c switch
    {
        'A' => 0x80, 'R' => 0x82, 'S' => 0x83, 'E' => 0x84, 'C' => 0x85, 'G' => 0x86, 'D' => 0x87, 'T' => 0x89,
        'U' => 0x8A, 'L' => 0x8B, 'Y' => 0x8D, 'O' => 0x8E, '!' => 0x9B, _ => 0xFC,
    };

    private static void Tile(Smb3Assets assets, byte[] vram, int mdTile, int nesTile)
    {
        var chr = assets.Chr(nesTile < 0xC0 ? 0x5E : 0x5F, nesTile & 0x3F);
        var md = vram.AsSpan(mdTile * 32, 32);
        md.Clear();
        for (int y = 0; y < 8; y++)
        for (int x = 0; x < 8; x++)
        {
            int v = ((chr[y] >> (7 - x)) & 1) | (((chr[y + 8] >> (7 - x)) & 1) << 1);
            int c = v is 0 or 3 ? 0 : 4 + v;   // NES sprite palette 1: colours 5-6 of line 3; the background transparent
            md[y * 4 + x / 2] |= (byte)((x & 1) == 0 ? c << 4 : c);
        }
    }
}
