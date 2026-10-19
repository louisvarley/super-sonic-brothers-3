using SonicBros3.Logic;
using SonicBros3.Smb3;

namespace SonicBros3.Frontend;

/// <summary>
/// SMB3's title screen (Do_Title_Screen, prg024) with Sonic and Tails in Mario's and Luigi's places. The screen is
/// SMB3's own, run from the player's smb3.nes: the curtain (Title_Display_Curtain), the palette and checkerboard
/// floor, the curtain rising (TitleState_CurtainRaise: 2 lines a frame to 208), the logo loaded one stream a frame
/// (Title_LoadSMB3, Video_Upd_Table2 items $08-$22), dropped (Title_Drop, 5 lines a frame) and shaken
/// (Title_LogoShakeUp/Down, $10 times), the palette fade-in (items $03-$06 every 5 frames), the big 3's glow
/// (Title_3Glow: Title_3GlowColors to $3F0E every 5 frames) and the 1 PLAYER / 2 PLAYER menu (item $07, the cursor
/// at Title_Menu_1P2PCursorY, SELECT moves it, START begins with the coin sound). The floor stays put below line 193
/// (IntIRQ_TitleEnding). START during the intro skips to the menu (Title_IntroSkip); the menu times out after
/// 20 x 96 frames and the title starts again (Title_ResetCnt).
///
/// The characters' part is this game's: Mario's and Luigi's action scripts become a Sonic and Tails routine with their
/// Sonic 2 sprites: they run in from either side, Tails spin-jumps over Sonic, the logo drops, a Goomba falls in and
/// Sonic spindashes through it, Tails flies up past the logo, a Koopa shell drops and Sonic rolls it away, and Tails
/// flies back down beside Sonic as the menu appears.
/// </summary>
public sealed class Sb3TitleScreen : Screen
{
    // Bank 25 / 24 addresses (Smb3Assets).
    private const int VideoUpdTable2 = 0xC5DA, Title3GlowColors = 0xAAAA;
    private const int ItemPaletteAndFloor = 0x01, ItemFadeIn1 = 0x03, ItemMenu = 0x07, ItemLogoFirst = 0x08,
        ItemLogoLast = 0x22;
    private static readonly int[] CursorY = [151, 167];   // Title_Menu_1P2PCursorY
    private const int CursorX = 72, CursorTile = 0xDF;

    private const int SplitLine = 193;   // IntIRQ_TitleEnding: the floor from here down
    private const int FloorY = 192;      // the characters' feet

    // VRAM tiles of the screen's own art (NES tiles are converted below $300).
    private const int SonicTile = 0x500, TailsTile = 0x540, Smb3ObjectsTile = 0x380;

    private readonly Smb3Assets _assets;
    private readonly NesPresenter _nes;
    private readonly NesPpu _ppu = new() { HorizontalMirroring = true };
    private readonly SpriteKit _sprites;
    private readonly SpriteData _sonicArt, _tailsArt, _goombaArt, _koopaArt;

    private int _vertScroll;
    private bool _glow, _menu;
    private int _glowTicker, _glowIndex;
    private int _player;   // 0 = 1 PLAYER GAME, 1 = 2 PLAYER GAME
    private bool _cursorShown = true;

    /// <summary>The actors, in NES screen pixels (x 0-255 is the NES picture; the Mega Drive shows -32 to 287).</summary>
    private sealed class Actor
    {
        public double X, Y;
        public int Frame;
        public bool FlipX, FlipY, Visible;
    }

    private readonly Actor _sonic = new(), _tails = new(), _goomba = new(), _shell = new();
    private IEnumerator<bool>? _sonicScript, _tailsScript, _propScript;
    private int _actorClock;
    /// <summary>Frames since the curtain finished rising; the logo drops at <see cref="LogoDropAt"/>.</summary>
    private int _sinceCurtain = -1;
    private const int LogoDropAt = 0x4C + 0x14 + 0x20 + 0x03;   // Mario's script reaches $FF 03 here
    private bool _introDone;

    public Sb3TitleScreen(string dataDir, Audio.ISoundChips? chips = null) : base(dataDir, chips)
    {
        _assets = new Smb3Assets(dataDir);
        _nes = new NesPresenter(Vram, Ram, _assets);
        _sprites = new SpriteKit(Ram, Vram);
        _sonicArt = Art["sonic"];
        _tailsArt = Art["tails"];
        _goombaArt = Art["smb3-goomba"];
        _koopaArt = Art["smb3-koopa-green"];
        // Title graphics (PRG030_845A): BG $78/$7A, sprites $20/$21/$04/$7F.
        _nes.BgBanks[0] = 0x78; _nes.BgBanks[1] = 0x79; _nes.BgBanks[2] = 0x7A; _nes.BgBanks[3] = 0x7B;
        _nes.SpriteBanks[0] = 0x20; _nes.SpriteBanks[1] = 0x21; _nes.SpriteBanks[2] = 0x04; _nes.SpriteBanks[3] = 0x7F;
        LineScrollA = PlaneALine;
        LineScrollB = PlaneBLine;
    }

    private byte Prg25(int a) => _assets.Prg(25, a);
    private void RunItem(int item) => _ppu.RunStream(Prg25, _assets.PrgWord(25, VideoUpdTable2 + item * 2));

    protected override IEnumerable<bool> Mode()
    {
        ClearScreen();
        BackdropIndex = 0x20;
        Sound.PlayMusic(Music.Stop);
        LoadOwnArt();
        _ppu.Clear(0xFC);                                     // Reset_PPU_Clear_Nametables
        for (int i = 0; i < 0x300; i++) _ppu.Write(0x2000 + i, 0x08 | (i & 1));   // Title_Display_Curtain
        RunItem(ItemPaletteAndFloor);
        PaletteFade.LoadNow(Ram, DataDir, 3);                  // Sonic and Tails: line 0
        _sonicScript = SonicScript().GetEnumerator();
        _tailsScript = TailsScript().GetEnumerator();
        _propScript = PropScript().GetEnumerator();
        Present();

        // 53 frames before the curtain rises.
        for (int i = 0; i < 54; i++)
        {
            if (StartPressed) { foreach (var f in Menu(skip: true)) yield return f; yield break; }
            Present();
            yield return true;
        }

        // TitleState_CurtainRaise: two lines a frame; the characters are held at the sides from 78 on.
        while (_vertScroll < 208)
        {
            if (StartPressed) { foreach (var f in Menu(skip: true)) yield return f; yield break; }
            _vertScroll = Math.Min(_vertScroll + 2, 208);
            if (_vertScroll >= 78) StepActors();
            Present();
            yield return true;
        }

        // Title_LoadSMB3: one logo stream a frame while the characters run on.
        _sinceCurtain = 0;
        for (int item = ItemLogoFirst; item <= ItemLogoLast; item++)
        {
            if (StartPressed) { foreach (var f in Menu(skip: true)) yield return f; yield break; }
            RunItem(item);
            Frame();
            yield return true;
        }
        while (_sinceCurtain < LogoDropAt)
        {
            if (StartPressed) { foreach (var f in Menu(skip: true)) yield return f; yield break; }
            Frame();
            yield return true;
        }

        // Title_Drop: 5 lines a frame down to 0, then Title_LogoShakeUp/Down 2 lines up and down, $10 + 1 times.
        while (_vertScroll > 0)
        {
            if (StartPressed) { foreach (var f in Menu(skip: true)) yield return f; yield break; }
            _vertScroll = Math.Max(_vertScroll - 5, 0);
            if (_vertScroll == 0) Sound.PlaySound(Sfx.Smash);
            Frame();
            yield return true;
        }
        for (int i = 0; i <= 0x10; i++)
        {
            _vertScroll = (_vertScroll - 2 + 480) % 480;
            Frame();
            yield return true;
            _vertScroll = (_vertScroll + 2) % 480;
            Frame();
            yield return true;
        }

        // Title_InitObjects (3 frames), then Title_PalFadeIn: items $03-$06, one every 5 frames; the 3 starts glowing.
        for (int i = 0; i < 3 + 0x17; i++) { Frame(); yield return true; }
        for (int item = ItemFadeIn1; item <= ItemFadeIn1 + 3; item++)
        {
            RunItem(item);
            for (int i = 0; i < 5; i++)
            {
                if (StartPressed) { foreach (var f in Menu(skip: true)) yield return f; yield break; }
                Frame();
                yield return true;
            }
        }
        _glow = true;

        // The routine plays out; the menu comes when Sonic and Tails are done (Mario's script's $FF 08 / $FE).
        while (!_introDone)
        {
            if (StartPressed) break;
            Frame();
            yield return true;
        }
        foreach (var f in Menu(skip: false)) yield return f;
    }

    private bool StartPressed => (Ram.U8(Vars.Ctrl1Press) & 0x80) != 0;

    /// <summary>Title_DrawMenu / Title_Do1P2PMenu (and Title_IntroSkip first when <paramref name="skip"/>).</summary>
    private IEnumerable<bool> Menu(bool skip)
    {
        if (skip)
        {
            // Title_IntroSkip: the whole logo at once, full palette, the characters in their final places.
            // ($22, the dark palette, first; $06, the full palette, last; $07 is the menu.)
            _vertScroll = 0;
            for (int item = ItemLogoLast; item >= ItemFadeIn1 + 3; item--) RunItem(item);
            _glow = true;
            FinalPoses();
        }
        RunItem(ItemMenu);
        _menu = true;
        Ram.W8(Vars.Ctrl1Press, 0);
        int resetCount = 20, resetFine = 60;
        while (true)
        {
            if (--resetFine == 0)
            {
                resetFine = 96;
                if (--resetCount == 0) { Result = new ScreenResult.BackToTitle(); yield break; }
            }
            int press = Ram.U8(Vars.Ctrl1Press);
            if ((press & 0x70) != 0 && (press & 0x80) == 0 || (press & 0x03) != 0)   // A/B/C or up/down as SELECT
            {
                _player ^= 1;
                Sound.PlaySound(Sfx.Blip);
            }
            if ((press & 0x80) != 0) break;
            Frame();
            yield return true;
        }

        // START: the coin sound; the cursor flashes until it ends.
        Sound.PlaySound(Sfx.Ring);
        for (int i = 0; i < 60; i++)
        {
            _cursorShown = (i & 0x18) != 0;
            Frame();
            yield return true;
        }
        _cursorShown = true;
        foreach (var f in FadeToBlack()) yield return f;
        Result = new ScreenResult.StartGame(0, 0);
    }

    /// <summary>One frame after the curtain: the characters, the glow, the picture.</summary>
    private void Frame()
    {
        if (_sinceCurtain >= 0) _sinceCurtain++;
        StepActors();
        if (_glow && --_glowTicker < 0)
        {
            // Title_3Glow
            _glowTicker = 4;
            _ppu.Write(0x3F0E, _assets.Prg(24, Title3GlowColors + _glowIndex));
            _glowIndex = (_glowIndex + 1) & 7;
        }
        Present();
    }

    private void StepActors()
    {
        _actorClock++;
        _sonicScript?.MoveNext();
        _tailsScript?.MoveNext();
        _propScript?.MoveNext();
    }

    // ------------------------------------------------------------------ the picture

    /// <summary>NES line shown on Mega Drive line <paramref name="line"/> (the top 8 NES lines are off-screen).</summary>
    private static int Nes(int line) => line + 8;

    /// <summary>Above the split: nametables 0 and 2 stacked (horizontal mirroring), scrolled by Vert_Scroll.</summary>
    private int? PlaneALine(int line)
    {
        int n = Nes(line);
        if (n >= SplitLine) return null;
        int y = (n + _vertScroll) % 480;
        int q = y >> 3;                               // virtual row: nametable 0 rows 0-29, then nametable 2
        return (q & 31) * 8 + (y & 7) - line;
    }

    /// <summary>Below the split: nametable 2 from row 24 ($0B00), unscrolled.</summary>
    private static int? PlaneBLine(int line)
    {
        int n = Nes(line);
        if (n < SplitLine) return null;
        return n - SplitLine - line;
    }

    private void Present()
    {
        // Plane A: the virtual rows visible above the split.
        int first = (Nes(0) + _vertScroll) % 480 >> 3;
        for (int i = 0; i < 25; i++)
        {
            int q = (first + i) % 60;
            _nes.Row(_ppu, q < 30 ? 0 : 2, q % 30, PlaneA, q & 31, Margin);
        }
        // Plane B: the floor rows 24-29 of nametable 2.
        for (int r = 24; r < 30; r++) _nes.Row(_ppu, 2, r, PlaneB, r - 24, Margin);
        _nes.Palettes(_ppu);
        // Line 3 is SMB3's 1-1 sprite palette (the Goomba and the Koopa are the Easter egg zone's sprites).
        for (int i = 1; i < 16; i++)
            if ((i & 3) != 0) Ram.W16(Vars.NormalPaletteLine4 + i * 2, S2Port.Extraction.Smb3.Smb3Palettes.ToMd(_assets.Prg(27, 0xAC92 + 0x80 + i)));

        _sprites.Begin();
        if (_menu && _cursorShown)
        {
            int t = _nes.SpriteTile(CursorTile, 0);
            _sprites.Piece(CursorX + 32, CursorY[_player] + 1 - 8, 1, 0xE000 | t);
        }
        DrawActor(_goomba, _goombaArt, Smb3ObjectsTile | 0x6000, null);
        DrawActor(_shell, _koopaArt, Smb3ObjectsTile | 0x6000, null);
        DrawActor(_tails, _tailsArt, TailsTile, TailsTile);
        DrawActor(_sonic, _sonicArt, SonicTile, SonicTile);
        _sprites.End();
    }

    /// <summary>The Mega Drive's extra columns repeat the picture's own (the curtain and the floor are patterns).</summary>
    private static int? Margin(int c) => ((c % 32) + 32) % 32;

    private void DrawActor(Actor a, SpriteData art, int artTile, int? dplcTile)
    {
        if (!a.Visible) return;
        if (dplcTile is { } t) _sprites.LoadDplc(art, a.Frame, t);
        _sprites.Frame(art, a.Frame, (int)a.X + 32, (int)a.Y - 8, artTile, a.FlipX, a.FlipY);
    }

    private void LoadOwnArt()
    {
        // The SMB3 zones' object art: its art list entry (Smb3ZoneBuilder's list 94) gives the file and size.
        using var doc = System.Text.Json.JsonDocument.Parse(S2Port.Extraction.DataFile.ReadAllText(
            Path.Combine(DataDir, "plc", S2Port.Extraction.Smb3.Smb3ZoneBuilder.PlcListsFile)));
        var entry = doc.RootElement.GetProperty("lists")[S2Port.Extraction.Smb3.Smb3ZoneBuilder.PlcObjects].GetProperty("entries")[0];
        var objects = S2Port.Extraction.Levels.LevelLoader.LoadTileArt(
            Path.GetFullPath(Path.Combine(DataDir, "plc", entry.GetProperty("art").GetString()!)), entry.GetProperty("tiles").GetInt32());
        objects.CopyTo(Vram.AsSpan(Smb3ObjectsTile * 32));
    }

    // ------------------------------------------------------------------ Sonic and Tails

    // Sonic 2 frames: Sonic stand 1, walk 13-20, run 45-48, roll 61-65, look up 76, duck 77, spindash 66-71.
    // Tails stand 1, walk 16-21, run 46-49, roll 70-72, fly 94/95.
    private static readonly int[] SonicWalk = [13, 14, 15, 16, 17, 18, 19, 20], SonicRun = [45, 46, 47, 48],
        SonicRoll = [61, 65, 62, 65, 63, 65, 64, 65], SonicDash = [66, 67, 66, 68, 66, 69, 66, 70, 66, 71];
    private static readonly int[] TailsWalk = [16, 17, 18, 19, 20, 21, 14, 15], TailsRun = [46, 47, 48, 49],
        TailsRoll = [72, 71, 70], TailsFly = [94, 95];
    private const int SonicStand = 1, SonicDuck = 77, SonicLookUp = 76, TailsStand = 1;
    private const double SonicGround = FloorY - 20, TailsGround = FloorY - 16;
    /// <summary>Where they end, either side of the menu (NES x 80-184).</summary>
    private const double SonicHomeX = 48, TailsHomeX = 212;

    private static int Anim(int[] frames, int clock, int speed) => frames[(clock / speed) % frames.Length];

    /// <summary>Run along the floor to <paramref name="x"/>, walking then running.</summary>
    private IEnumerable<bool> RunTo(Actor a, double x, double speed, int[] walk, int[] run)
    {
        int t = 0;
        a.FlipX = x < a.X;
        while (Math.Abs(a.X - x) > speed)
        {
            double v = Math.Min(speed, 1 + t * 0.125);
            a.X += a.FlipX ? -v : v;
            a.Frame = v >= 4 ? Anim(run, t, 3) : Anim(walk, t, Math.Max(2, 6 - (int)v));
            t++;
            yield return true;
        }
        a.X = x;
    }

    private static IEnumerable<bool> Wait(int frames)
    {
        for (int i = 0; i < frames; i++) yield return true;
    }

    private IEnumerable<bool> SonicScript()
    {
        var s = _sonic;
        // Held at the right side while the curtain rises (Title_MLHoldTick $40), Mario's place: x 240.
        s.X = 300; s.Y = SonicGround; s.FlipX = true; s.Visible = true; s.Frame = SonicStand;
        foreach (var f in Wait(0x40)) yield return f;
        foreach (var f in RunTo(s, 168, 5, SonicWalk, SonicRun)) yield return f;
        s.Frame = SonicStand;
        // Tails jumps over: Sonic ducks under him.
        foreach (var f in Wait(10)) yield return f;
        s.Frame = SonicDuck;
        foreach (var f in Wait(40)) yield return f;
        s.Frame = SonicStand; s.FlipX = false;
        // Waits for the logo, looks up as it lands.
        while (_vertScroll != 0 || _sinceCurtain < LogoDropAt) yield return true;
        s.Frame = SonicLookUp;
        foreach (var f in Wait(70)) yield return f;
        s.Frame = SonicStand; s.FlipX = true;
        // A Goomba falls in on the left: spindash through it.
        while (!_goombaLanded) yield return true;
        foreach (var f in Wait(20)) yield return f;
        s.Frame = SonicDuck;
        foreach (var f in Wait(12)) yield return f;
        for (int rev = 0; rev < 3; rev++)
        {
            Sound.PlaySound(Sfx.SpindashRev);
            for (int i = 0; i < 16; i++) { s.Frame = SonicDash[i % SonicDash.Length]; yield return true; }
        }
        Sound.PlaySound(Sfx.SpindashRelease);
        for (int t = 0; s.X > -48; t++)
        {
            s.X -= 9;
            s.Frame = SonicRoll[t % SonicRoll.Length];
            if (!_goombaHit && _goomba.Visible && Math.Abs(s.X - _goomba.X) < 16) { _goombaHit = true; Sound.PlaySound(Sfx.Explosion); }
            yield return true;
        }
        s.Visible = false;
        // Back in from the left, rolling, for the Koopa shell.
        while (!_shellLanded) yield return true;
        foreach (var f in Wait(30)) yield return f;
        s.Visible = true; s.X = -40; s.FlipX = false;
        Sound.PlaySound(Sfx.Roll);
        for (int t = 0; s.X < _shell.X - 14; t++)
        {
            s.X += 6;
            s.Frame = SonicRoll[t % SonicRoll.Length];
            yield return true;
        }
        _shellKicked = true;
        Sound.PlaySound(Sfx.Smash);
        // Uncurls and runs back to the left of the menu; Tails lands on its right.
        for (int t = 0; t < 12; t++) { s.X += 3 - t * 0.25; s.Frame = SonicRoll[t % SonicRoll.Length]; yield return true; }
        foreach (var f in RunTo(s, SonicHomeX, 4, SonicWalk, SonicRun)) yield return f;
        s.FlipX = false; s.Frame = SonicStand;
        while (!_tailsHome) yield return true;
        foreach (var f in Wait(30)) yield return f;
        _introDone = true;
        while (true) { s.Frame = SonicStand; yield return true; }
    }

    private bool _goombaLanded, _goombaHit, _shellLanded, _shellKicked, _tailsHome;

    private IEnumerable<bool> TailsScript()
    {
        var a = _tails;
        // Luigi's place: x 0, coming in from the left.
        a.X = -44; a.Y = TailsGround; a.FlipX = false; a.Visible = true; a.Frame = TailsStand;
        foreach (var f in Wait(0x40 + 20)) yield return f;
        foreach (var f in RunTo(a, 96, 5, TailsWalk, TailsRun)) yield return f;
        // Spin jump over Sonic, landing on his right.
        Sound.PlaySound(Sfx.Jump);
        double vy = -6.5;
        for (int t = 0; a.Y <= TailsGround || vy < 0; t++)
        {
            a.X += 2.6;
            a.Y += vy;
            vy += 0.21875;
            a.Frame = TailsRoll[(t / 2) % TailsRoll.Length];
            yield return true;
        }
        a.Y = TailsGround; a.Frame = TailsStand; a.FlipX = true;
        // Waits for the logo, then for Sonic's spindash to go by.
        while (!_goombaHit) yield return true;
        foreach (var f in Wait(30)) yield return f;
        // Flies up past the logo and off the top.
        Sound.PlaySound(Sfx.Helicopter);
        for (int t = 0; a.Y > -40; t++)
        {
            a.Y -= Math.Min(2.5, 0.5 + t * 0.06);
            a.X += Math.Sin(t / 12.0) * 1.2;
            a.Frame = Anim(TailsFly, t, 2);
            yield return true;
        }
        a.Visible = false;
        // Back down from the top once the shell is gone, to land beside Sonic.
        while (!_shellKicked) yield return true;
        foreach (var f in Wait(40)) yield return f;
        a.Visible = true; a.X = 250; a.Y = -40; a.FlipX = true;
        Sound.PlaySound(Sfx.Helicopter);
        for (int t = 0; a.Y < TailsGround; t++)
        {
            a.Y += 1.5;
            a.X += (TailsHomeX - a.X) * 0.03;
            a.Frame = Anim(TailsFly, t, 2);
            yield return true;
        }
        a.Y = TailsGround; a.X = Math.Round(a.X); a.Frame = TailsStand;
        _tailsHome = true;
        while (true) yield return true;
    }

    /// <summary>The Goomba and the Koopa shell that fall in (SMB3's title has them drop from the top too).</summary>
    private IEnumerable<bool> PropScript()
    {
        var g = _goomba;
        while (_vertScroll != 0 || _sinceCurtain < LogoDropAt + 60) yield return true;
        g.Visible = true; g.X = 56; g.Y = -16; g.Frame = 0;
        double vy = 0;
        while (g.Y < FloorY - 8) { vy = Math.Min(vy + 0.25, 4); g.Y += vy; yield return true; }
        g.Y = FloorY - 8;
        _goombaLanded = true;
        for (int t = 0; !_goombaHit; t++)
        {
            g.X += (t & 1) == 0 ? 0.5 : 0;   // half a pixel a frame towards Sonic
            g.Frame = (t >> 3) & 1;
            g.FlipX = ((t >> 3) & 1) != 0;
            yield return true;
        }
        // Knocked over: flips and falls off the bottom.
        g.FlipY = true;
        vy = -4;
        while (g.Y < 260) { g.X -= 1.5; g.Y += vy; vy += 0.25; yield return true; }
        g.Visible = false;

        var k = _shell;
        foreach (var f in Wait(60)) yield return f;
        k.Visible = true; k.X = 120; k.Y = -16; k.Frame = 2;
        vy = 0;
        while (k.Y < FloorY - 8) { vy = Math.Min(vy + 0.25, 4); k.Y += vy; yield return true; }
        k.Y = FloorY - 8;
        _shellLanded = true;
        while (!_shellKicked) yield return true;
        // Kicked: slides off the right side.
        while (k.X < 300) { k.X += 5; yield return true; }
        k.Visible = false;
        while (true) yield return true;
    }

    private void FinalPoses()
    {
        _sonicScript = null; _tailsScript = null; _propScript = null;
        _sonic.Visible = true; _sonic.X = SonicHomeX; _sonic.Y = SonicGround; _sonic.Frame = SonicStand; _sonic.FlipX = false; _sonic.FlipY = false;
        _tails.Visible = true; _tails.X = TailsHomeX; _tails.Y = TailsGround; _tails.Frame = TailsStand; _tails.FlipX = true;
        _goomba.Visible = false; _shell.Visible = false;
    }
}
