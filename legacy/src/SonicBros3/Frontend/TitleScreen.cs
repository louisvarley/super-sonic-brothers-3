using S2Port.Extraction.Compression;
using SonicBros3.Logic;

namespace SonicBros3.Frontend;

/// <summary>
/// The title screen, game mode $04 (ROM 0x46B8): the "Sonic and Miles 'Tails' Prower in" text, then the emblem, Sonic
/// and Tails (object $0E), the palette changers ($C9) and the menu. The menu is this port's: START GAME / LEVEL SELECT
/// instead of the hack's NEW GAME / ARENA MODE / OPTIONS (object $0F, 0x16544); its letters are the hack's menu font.
/// When the title times out the original plays a demo; here the title starts again. The music and sounds are the Z80
/// driver's (not ported), so the title is silent.
/// </summary>
public sealed class TitleScreen(string dataDir, Audio.ISoundChips? chips = null) : Screen(dataDir, chips)
{
    // The title's objects sit in fixed slots.
    public const int SonicSlot = 0xB040, TailsSlot = 0xB080, FlashingStarSlot = 0xB0C0, PaletteChangerSlot = 0xB100,
        LogoTopSlot = 0xB140, MaskingSlot = 0xB180, SonicHandSlot = 0xB1C0, TailsHandSlot = 0xB200,
        PaletteChanger2Slot = 0xB240, MenuSlot = 0xB400, FallingStarSlot = 0xB440;
    /// <summary>Obj0E's "intro complete" flag in the Sonic object ($2F).</summary>
    public const int IntroComplete = SonicSlot + 0x2F;

    /// <summary>
    /// Frames the intro text stays up while the title art is decompressed (with interrupts off on the original, so
    /// no frames are counted). Measured in MAME: the fade-in's last step at frame 371, the fade-out's first at 443.
    /// </summary>
    public const int IntroTextFrames = 71;
    private const int StartButton = 0x80;

    private TitleObjects? _objects;
    private SpriteBuilder? _sprites;

    protected override IEnumerable<bool> Mode()
    {
        // 0x46B8: stop the music, fade out.
        Ram.W8(Vars.HackZoneFlag, 0);
        Sound.PlayMusic(Music.Stop);
        foreach (var f in FadeToBlack()) yield return f;
        ClearScreen();
        ClearRam(Vars.ObjectDisplayLists, 0x400);
        ClearRam(Vars.MainCharacter, Vars.DynamicObjectRamEnd - Vars.MainCharacter);
        ClearRam(0xF700, 0x100);
        ClearRam(Vars.CameraXPos, 0x100);
        BackdropIndex = 0x20;

        // The intro text (0xC9DC with the list at 0xCA1A) on the credit font, faded in with palette 3.
        LoadArt("title-intro");
        DrawIntroText();
        ClearRam(Vars.TargetPalette, 0x80);
        PaletteFade.LoadForFade(Ram, DataDir, 3);
        foreach (var f in FadeFromBlack()) yield return f;
        LoadArt("title");                                                 // 0x478C: the title art
        for (int i = 0; i < IntroTextFrames; i++) yield return true;   // ... which takes this long on the original
        foreach (var f in FadeToBlack()) yield return f;

        // Plane maps (0x481A): background (two parts) on plane B, the emblem with the copyright on plane A.
        var maps = "title-maps.json";
        PlaneMapToVram(Enigma.Decompress(RomFrom(maps, 0x7BCF6), 0x4000), PlaneB, 40, 28);
        PlaneMapToVram(Enigma.Decompress(RomFrom(maps, 0x7BD6A), 0x4000), PlaneB + 0x50, 24, 28);
        var logo = Enigma.Decompress(RomFrom(maps, 0x7BDB6), 0xE000);
        if (logo.Length < 40 * 28) Array.Resize(ref logo, 40 * 28);
        for (int i = 0; i < 11; i++) logo[0x42C + i] = (ushort)RomWord("copyright.json", 0x4BD0 + i * 2);
        PlaneMapToVram(logo, PlaneA, 40, 28);

        ClearRam(Vars.NormalPalette, 0x100);
        PaletteFade.LoadForFade(Ram, DataDir, 1);
        Ram.W16(Vars.DemoTimeLeft, 0x280);
        var sonic = new SonicObject(Ram, SonicSlot);
        sonic.Id = TitleObjects.TitleObjectId;
        sonic.Subtype = TitleObjects.SonicPart;
        _objects = new TitleObjects(this);
        _sprites = new SpriteBuilder(Ram, Art);
        _objects.Run();
        _sprites.BuildSprites();
        Ram.W16(Vars.CameraXPos, 0xFD80);                             // -$280
        foreach (var f in FadeFromBlack()) yield return f;

        // TitleScreen_Loop (0x493A).
        while (true)
        {
            yield return true;
            if (Ram.U16(Vars.DemoTimeLeft) != 0) Ram.W16(Vars.DemoTimeLeft, Ram.U16(Vars.DemoTimeLeft) - 1);   // Vint_Title
            _objects.Run();
            SwScrlTitle();
            _sprites.BuildSprites();
            MaskingSprites();
            if (Ram.U16(Vars.DemoTimeLeft) == 0) { Result = new ScreenResult.BackToTitle(); yield break; }   // the demo, in the original
            if (Ram.U8(IntroComplete) == 0) continue;
            if ((Ram.U8(Vars.Ctrl1Press) & StartButton) == 0) continue;
            Result = Ram.U8(Vars.TitleScreenOption) == 0 ? new ScreenResult.StartGame(0x11, 1) : new ScreenResult.OpenLevelSelect();
            yield break;
        }
    }

    /// <summary>
    /// 0xC9DC: (text address, VRAM address) pairs until a negative address. A text is a high byte for every cell, then
    /// one low byte per cell until a byte with bit 7 set.
    /// </summary>
    private void DrawIntroText()
    {
        for (int e = 0xCA1A; ; e += 6)
        {
            int ptr = (RomWord("intro-text.json", e) << 16) | RomWord("intro-text.json", e + 2);
            if (ptr < 0) return;
            int dest = RomWord("intro-text.json", e + 4);
            int high = Rom("intro-text-strings.json", ptr) << 8;
            for (int p = ptr + 1; ; p++)
            {
                int low = Rom("intro-text-strings.json", p);
                if ((low & 0x80) != 0) break;
                WriteVramWord(dest, high | low);
                dest += 2;
            }
        }
    }

    /// <summary>SwScrl_Title (0xDE3C): lines 160-191 scroll at a quarter of the camera, 192-207 ripple.</summary>
    private void SwScrlTitle()
    {
        Ram.W16(Vars.VscrollFactorBg, Ram.U16(Vars.CameraBgYPos));
        Ram.W16(Vars.CameraXPos, Ram.U16(Vars.CameraXPos) + 1);
        int d2 = (short)(-(short)Ram.U16(Vars.CameraXPos)) >> 2;
        int a1 = Vars.HorizScrollBuf;
        for (int i = 0; i < 0xA0; i++, a1 += 4) Ram.W32(a1, 0);
        for (int i = 0; i < 0x20; i++, a1 += 4) Ram.W32(a1, (uint)(d2 & 0xFFFF));
        if ((Ram.U8(Vars.VintRuncountLow) & 7) == 0) Ram.W16(Vars.TempArrayLayerDef, Ram.U16(Vars.TempArrayLayerDef) - 1);
        int start = Ram.U16(Vars.TempArrayLayerDef) & 0x1F;
        for (int i = 0; i < 0x10; i++, a1 += 4)
            Ram.W32(a1, (uint)((d2 + (sbyte)Rom("title-scroll.json", 0xDFA8 + start + i)) & 0xFFFF));
    }

    /// <summary>The masking sprites (0x4954): sprites with art tile 0 go to x 0 / 4 alternately.</summary>
    private void MaskingSprites()
    {
        int d0 = 0;
        for (int i = 0; i < 0x50; i++)
        {
            int a = Vars.SpriteTable + i * 8 + 4;   // the sprite's pattern word
            if (Ram.U16(a) != 0) continue;
            d0 ^= 4;
            Ram.W16(a + 2, d0);
        }
    }
}

/// <summary>
/// The title screen's objects: $0E (0x15D5C: Sonic, Tails, their hands, the emblem top, the stars, the masking
/// sprite: its subtype picks the part), $C9 (0x16234: palette changers) and the menu. Positions are screen
/// coordinates (x at $08, y in the x_sub word at $0A; +128 each).
/// </summary>
public sealed class TitleObjects
{
    public const int TitleObjectId = 0x0E, PaletteChangerId = 0xC9, MenuId = 0x0F;
    // Obj0E's parts (subtype = routine).
    public const int SonicPart = 2, TailsPart = 4, LogoTop = 6, FlashingStar = 8, SonicHand = 0x0A, FallingStar = 0x0C,
        MaskingSprite = 0x0E, TailsHand = 0x10;

    private const string Obj = "title-object.json";
    private const uint Mappings = 0x16610;
    /// <summary>Made-up mapping pointer for this port's menu frames (outside the ROM).</summary>
    private const uint MenuMappings = 0xF10000;
    private const int Art = 0x1D0;
    private const int StartButton = 0x80;

    private readonly TitleScreen _s;
    private readonly Ram _ram;
    private readonly RamSound _sound;
    /// <summary>Sonic 2's ids (the Z80 driver plays the front end): the title music and the sparkle.</summary>
    private const int TitleMusic = 0x99, SparkleSfx = 0xA7;
    private readonly byte[] _anims;

    /// <summary>An Obj0E part's variables.</summary>
    private readonly struct State(SonicObject o)
    {
        /// <summary>$2A: a frame counter or timer; $2C: the position in a path table.</summary>
        public int Counter { get => o.Word(0x2A); set => o.SetWord(0x2A, value); }
        public int PathIndex { get => o.Word(0x2C); set => o.SetWord(0x2C, value); }
        /// <summary>Sonic, $2F: the intro is over (the menu works).</summary>
        public bool IntroComplete { get => o.Byte(0x2F) != 0; set => o.SetByte(0x2F, value ? 0xFF : 0); }
        /// <summary>Sonic, $34: frames since the title appeared.</summary>
        public int Time { get => o.Word(0x34); set => o.SetWord(0x34, value); }
    }

    public TitleObjects(TitleScreen screen)
    {
        _s = screen;
        _ram = screen.Ram;
        _sound = new RamSound(_ram);
        _anims = screen.Art["title-intro"].Animations!;
        screen.Art.Add("title-menu", new SpriteData { Frames = MenuFrames() }, MenuMappings);
    }

    private byte Rom(int a)
    {
        var (b, data) = _s.Art.DataTable($"frontend/{Obj}");
        return data[a - b];
    }

    private int RomWord(int a) => (Rom(a) << 8) | Rom(a + 1);

    /// <summary>RunObjects for the title (all slots, in order).</summary>
    public void Run()
    {
        for (int a = Vars.MainCharacter; a < Vars.PrimaryCollision; a += Objects.SlotSize)
        {
            switch (_ram.U8(a))
            {
                case TitleObjectId: Obj0E(new SonicObject(_ram, a)); break;
                case PaletteChangerId: ObjC9(new SonicObject(_ram, a)); break;
                case MenuId: Menu(new SonicObject(_ram, a)); break;
            }
        }
    }

    private void Display(SonicObject o) => SpriteBuilder.DisplaySprite(_ram, o);
    private void Delete(int slot) => _ram.Bytes.AsSpan(slot, Objects.SlotSize).Clear();
    private void Spawn(int slot, int id, int subtype) { _ram.W8(slot, id); _ram.W8(slot + 0x28, subtype); }
    private static void Next(SonicObject o) => o.RoutineSecondary += 2;
    /// <summary>Screen position: x, and y (kept in the x_sub word).</summary>
    private static void Place(SonicObject o, int x, int y) { o.X = x; o.XSub = y; }
    private void Animate(SonicObject o) => ObjectKit.Animate(_ram, o, _anims);
    private void LoadPalette(int line, int rom)
    {
        for (int i = 0; i < 16; i++) _ram.W16(line + i * 2, RomWord(rom + i * 2));
    }

    // ------------------------------------------------------------------ $0E

    private void Obj0E(SonicObject o)
    {
        while (true)
        {
            switch (o.Routine)
            {
                case 0:   // 0x15D7C
                    o.Mappings = Mappings;
                    o.ArtTile = Art;
                    o.Priority = 4;
                    o.Routine = o.Subtype;
                    continue;
                case SonicPart: Sonic(o, new State(o)); return;
                case TailsPart: Tails(o); return;
                case LogoTop: UpdateLogoTop(o); return;
                case FlashingStar: UpdateFlashingStar(o, new State(o)); return;
                case SonicHand: UpdateSonicHand(o); return;
                case FallingStar: UpdateFallingStar(o, new State(o)); return;
                case MaskingSprite: UpdateMaskingSprite(o); return;
                case TailsHand: UpdateTailsHand(o); return;
                default: return;
            }
        }
    }

    /// <summary>0x15E64 (Obj0E_Move): every 4 frames the next position from a table of (x, y) words.</summary>
    private void FollowPath(SonicObject o, int pathLength, int table)
    {
        var s = new State(o);
        s.Counter++;
        if ((s.Counter & 3) == 0)
        {
            int next = s.PathIndex + 4;
            if (next >= pathLength) { Next(o); Display(o); return; }
            s.PathIndex = next;
            o.XSub = RomWord(table + next - 4 + 2);
            o.X = RomWord(table + next - 4);
        }
        Display(o);
    }

    /// <summary>0x15D9C: Sonic, who also runs the title's timeline.</summary>
    private void Sonic(SonicObject o, State s)
    {
        s.Time++;
        if (s.Time < 0x120) SetFinalState(o, s);
        int t = s.Time;
        switch (o.RoutineSecondary)
        {
            case 0:   // 0x15DCE
                Next(o);
                o.MappingFrame = 5;
                Place(o, 0x110, 0xE0);
                Spawn(TitleScreen.FlashingStarSlot, TitleObjectId, FlashingStar);
                Spawn(TitleScreen.LogoTopSlot, TitleObjectId, LogoTop);
                _sound.PlaySound(SparkleSfx);
                break;
            case 2:   // 0x15E06
                if (t < 0x38) return;
                Next(o);
                Spawn(TitleScreen.PaletteChangerSlot, PaletteChangerId, 0);
                o.SetByte(0x30, 0xFF);   // the music has started
                _sound.PlayMusic(TitleMusic);
                return;
            case 4:   // 0x15E2C
                if (t < 0x80) return;
                Next(o);
                LoadPalette(Vars.NormalPalette, 0x16330);
                Spawn(TitleScreen.MaskingSlot, TitleObjectId, MaskingSprite);
                return;
            case 6: FollowPath(o, 0x24, 0x15F8A); return;
            case 8: Animate(o); Display(o); return;
            case 0x0A:   // 0x15EA4
                Next(o);
                o.MappingFrame = 0x12;
                Spawn(TitleScreen.SonicHandSlot, TitleObjectId, SonicHand);
                Display(o);
                return;
            case 0x0C:   // 0x15EC0
                if (t >= 0xC0) { Next(o); Spawn(TitleScreen.TailsSlot, TitleObjectId, TailsPart); }
                Display(o);
                return;
            case 0x0E:   // 0x15EDE: the intro is over; the menu appears
                if (t >= 0x120)
                {
                    Next(o);
                    s.PathIndex = 0;
                    s.IntroComplete = true;
                    for (int i = 0; i < 16; i++) _ram.W16(Vars.NormalPaletteLine3 + i * 2, 0xEEE);
                    Spawn(TitleScreen.PaletteChanger2Slot, PaletteChangerId, 2);
                    _ram.W8(TitleScreen.MenuSlot, MenuId);
                }
                Display(o);
                return;
            case 0x10:   // 0x15F1A (NTSC: $1D0)
                if (t == 0x1D0)
                {
                    Spawn(TitleScreen.FallingStarSlot, TitleObjectId, FallingStar);
                    Next(o);
                    Delete(TitleScreen.MaskingSlot);
                }
                Display(o);
                return;
            case 0x12:   // 0x15F58: the star colour cycle
                if ((_ram.U8(Vars.VintRuncountLow) & 7) == 0)
                {
                    int colour = s.PathIndex + 2;
                    if (colour >= 0x0C) colour = 0;
                    s.PathIndex = colour;
                    _ram.W16(Vars.NormalPaletteLine3 + 0xA, RomWord(0x15F7E + colour));
                }
                Display(o);
                return;
        }
    }

    /// <summary>0x16400 (TitleScreen_SetFinalState): Start during the intro jumps to the finished picture.</summary>
    private void SetFinalState(SonicObject o, State s)
    {
        if (s.IntroComplete) return;
        int press = _ram.U8(Vars.Ctrl1Press);
        _ram.W8(Vars.Ctrl1Press, press & ~StartButton);
        if ((press & StartButton) == 0) return;
        s.IntroComplete = true;
        o.RoutineSecondary = 0x10;
        o.MappingFrame = 0x12;
        Place(o, 0x108, 0x98);
        SetPart(TitleScreen.SonicHandSlot, SonicHand, 2, 9, 4, 0x141, 0xC1);
        SetPart(TitleScreen.TailsSlot, TailsPart, 3, 4, 6, 0xC8, 0xA0);
        SetPart(TitleScreen.TailsHandSlot, TailsHand, 2, 0x13, 4, 0x10D, 0xD1);
        Spawn(TitleScreen.LogoTopSlot, TitleObjectId, LogoTop);
        Spawn(TitleScreen.MaskingSlot, TitleObjectId, MaskingSprite);
        _ram.W8(TitleScreen.MenuSlot, MenuId);
        Delete(TitleScreen.FlashingStarSlot);
        LoadPalette(Vars.NormalPaletteLine4, 0x16370);
        LoadPalette(Vars.NormalPaletteLine3, 0x16350);
        LoadPalette(Vars.NormalPalette, 0x16330);
        if (o.Byte(0x30) == 0) _sound.PlayMusic(TitleMusic);   // 0x16520: unless it already started
    }

    /// <summary>0x1652E plus the fields SetFinalState sets: a part already in its final place.</summary>
    private void SetPart(int slot, int routine, int priority, int frame, int routineSecondary, int x, int y)
    {
        var part = new SonicObject(_ram, slot);
        part.Mappings = Mappings;
        part.ArtTile = Art;
        part.Id = TitleObjectId;
        part.Routine = routine;
        part.Priority = priority;
        part.MappingFrame = frame;
        part.RoutineSecondary = routineSecondary;
        Place(part, x, y);
    }

    /// <summary>0x15FAA.</summary>
    private void Tails(SonicObject o)
    {
        switch (o.RoutineSecondary)
        {
            case 0: Next(o); Place(o, 0xD8, 0xD8); o.Anim = 1; return;
            case 2: FollowPath(o, 0x20, 0x15FFC); return;
            case 4: Animate(o); Display(o); return;
            case 6: Next(o); Spawn(TitleScreen.TailsHandSlot, TitleObjectId, TailsHand); Display(o); return;
            default: Display(o); return;
        }
    }

    /// <summary>0x16018: the top of the emblem (frame $A with the trademark, $B without).</summary>
    private void UpdateLogoTop(SonicObject o)
    {
        if (o.RoutineSecondary == 0)
        {
            bool overseas = (sbyte)_ram.U8(Vars.GraphicsFlags) < 0;
            o.MappingFrame = overseas ? 0x0B : 0x0A;
            o.Priority = 2;
            Place(o, 0x120, 0xE8);
            Next(o);
        }
        Display(o);
    }

    /// <summary>0x16056: a sprite with art tile 0 that hides Sonic's legs below the emblem (see MaskingSprites).</summary>
    private void UpdateMaskingSprite(SonicObject o)
    {
        if (o.RoutineSecondary == 0)
        {
            Next(o);
            o.ArtTile = 0;
            o.MappingFrame = 0x11;
            o.Priority = 2;
            Place(o, 0x100, 0xF0);
        }
        Display(o);
    }

    /// <summary>0x1608E: the big star flashing at nine places.</summary>
    private void UpdateFlashingStar(SonicObject o, State s)
    {
        switch (o.RoutineSecondary)
        {
            case 0:
                Next(o);
                o.MappingFrame = 0x0C;
                o.ArtTile |= 0x8000;
                o.Anim = 2;
                o.Priority = 1;
                Place(o, 0x100, 0xA8);
                s.Counter = 4;
                return;
            case 2: Animate(o); Display(o); return;   // the animation ends by advancing the state
            case 4:
                s.Counter--;
                if ((short)s.Counter < 0) Next(o);
                return;
            case 6:
            {
                // The next place.
                o.RoutineSecondary = 2;
                o.AnimFrame = 0;
                o.AnimFrameDuration = 0;
                s.Counter = 6;
                int next = s.PathIndex + 4;
                if (next >= 0x28) { Delete(o.Address); return; }
                s.PathIndex = next;
                o.XSub = RomWord(0x1611C + next + 2);
                o.X = RomWord(0x1611C + next);
                return;   // sound $A7
            }
        }
    }

    /// <summary>0x16144.</summary>
    private void UpdateSonicHand(SonicObject o)
    {
        switch (o.RoutineSecondary)
        {
            case 0: Next(o); o.MappingFrame = 9; o.Priority = 3; Place(o, 0x145, 0xBF); Display(o); return;
            case 2: FollowPath(o, 0x10, 0x16184); return;
            default: Display(o); return;
        }
    }

    /// <summary>0x16190.</summary>
    private void UpdateTailsHand(SonicObject o)
    {
        switch (o.RoutineSecondary)
        {
            case 0: Next(o); o.MappingFrame = 0x13; o.Priority = 3; Place(o, 0x10F, 0xD5); Display(o); return;
            case 2: FollowPath(o, 0x0C, 0x161D0); return;
            default: Display(o); return;
        }
    }

    /// <summary>0x161D8: the small star falling across the sky.</summary>
    private void UpdateFallingStar(SonicObject o, State s)
    {
        if (o.RoutineSecondary == 0)
        {
            Next(o);
            o.MappingFrame = 0x0C;
            o.Priority = 5;
            Place(o, 0x170, 0x80);
            o.Anim = 3;
            s.Counter = 0x8C;
            Display(o);
            return;
        }
        s.Counter--;
        if ((short)s.Counter < 0) { Delete(o.Address); return; }
        o.X -= 2;
        o.XSub++;   // down 1 px
        Animate(o);
        Display(o);
    }

    // ------------------------------------------------------------------ $C9

    /// <summary>A palette changer's variables.</summary>
    private readonly struct ChangerState(SonicObject o)
    {
        /// <summary>$30: frames until the next step; $31: the frames between steps; $32: steps left.</summary>
        public int Timer { get => o.Byte(0x30); set => o.SetByte(0x30, value); }
        public int Interval { get => o.Byte(0x31); set => o.SetByte(0x31, value); }
        public int StepsLeft { get => o.Byte(0x32); set => o.SetByte(0x32, value); }
        /// <summary>$34: the first colour's offset in the palette; $36: the colour count - 1.</summary>
        public int Offset { get => o.Word(0x34); set => o.SetWord(0x34, value); }
        public int Length { get => o.Word(0x36); set => o.SetWord(0x36, value); }
        /// <summary>$3A: the step routine (the original's ROM address).</summary>
        public uint Routine { get => o.Long(0x3A); set => o.SetLong(0x3A, value); }
    }

    /// <summary>0x16234: loads a palette into Target_palette, then every few frames steps part of Normal_palette towards it.</summary>
    private void ObjC9(SonicObject o)
    {
        var s = new ChangerState(o);
        if (o.Routine == 0)
        {
            o.Routine = 2;
            int entry = 0x162C0 + RomWord(0x162C0 + o.Subtype);
            s.Routine = (uint)((RomWord(entry) << 16) | RomWord(entry + 2));
            int data = (RomWord(entry + 4) << 16) | RomWord(entry + 6);
            int offset = Rom(entry + 8), length = Rom(entry + 9);
            s.Offset = offset;
            s.Length = length;
            for (int i = 0; i <= length; i++) _ram.W16(Vars.TargetPalette + offset + i * 2, RomWord(data + i * 2));
            s.Timer = Rom(entry + 10);
            s.Interval = Rom(entry + 10);
            s.StepsLeft = Rom(entry + 11);
            return;
        }
        s.Timer--;
        if ((sbyte)s.Timer >= 0) return;
        s.Timer = s.Interval;
        s.StepsLeft--;
        if ((sbyte)s.StepsLeft < 0) { Delete(o.Address); return; }
        uint step = s.Routine;
        for (int i = 0; i <= s.Length; i++)
        {
            int n = Vars.NormalPalette + s.Offset + i * 2, t = Vars.TargetPalette + s.Offset + i * 2;
            int c = _ram.U16(n), target = _ram.U16(t);
            _ram.W16(n, step switch
            {
                0x2B52 => PaletteFade.UpdateColour(target, c),   // Pal_FadeFromBlack.UpdateColour
                0x16390 => Darken(c, target),
                0x163CE => Brighten(c),
                _ => throw new UnportedException((int)step, "palette changer routine"),
            });
        }
    }

    /// <summary>0x16390: each channel steps down towards the target.</summary>
    private static int Darken(int c, int target)
    {
        int b = (c >> 8) & 0xE, tb = (target >> 8) & 0xE;
        if (b > tb) b -= 2;
        int g = c & 0xE0, tg = target & 0xE0, r = c & 0xE, tr = target & 0xE;
        if (g > tg) g -= 0x20;
        if (r > tr) r -= 2;
        return (b << 8) | g | r;
    }

    /// <summary>0x163CE: each channel steps up towards white.</summary>
    private static int Brighten(int c)
    {
        int b = (c >> 8) & 0xE, g = c & 0xE0, r = c & 0xE;
        if (b < 0xE) b += 2;
        if (g < 0xE0) g += 0x20;
        if (r < 0xE) r += 2;
        return (b << 8) | g | r;
    }

    // ------------------------------------------------------------------ the menu

    /// <summary>
    /// This port's menu (in place of the hack's $0F, 0x16544, at the same place): up/down chooses START GAME or LEVEL
    /// SELECT; the chosen line is in palette line 3 with the cursor, as the hack's frames are.
    /// </summary>
    private void Menu(SonicObject o)
    {
        if (o.Routine == 0)
        {
            o.Routine = 2;
            Place(o, 0x128, 0x14C);
            o.Mappings = MenuMappings;
            o.ArtTile = 0;
            _ram.W8(Vars.TitleScreenOption, _ram.U8(Vars.TitleScreenOption) & 1);
        }
        int option = _ram.U8(Vars.TitleScreenOption);
        int press = _ram.U8(Vars.Ctrl1Press);
        const int UpOrDown = 3;
        if ((press & UpOrDown) != 0) option ^= 1;   // two items: up and down both switch
        _ram.W8(Vars.TitleScreenOption, option);
        o.MappingFrame = option;
        Display(o);
    }

    /// <summary>Menu font: tile $680 + index of "0123456789*@:.ABCDEFGHIJKLMNOPQRSTUVWXYZ" (the hack's charset).</summary>
    private static int Letter(char c) => 0x680 + "0123456789*@:.ABCDEFGHIJKLMNOPQRSTUVWXYZ".IndexOf(c);

    private static List<MappingPiece[]> MenuFrames()
    {
        // As the hack's frames (0x16AD8): the chosen item on the top line (y -12) in palette line 3 with the cursor
        // ($47A, 2x1) at x -68, the next item below it (y 4); the last item alone (y -8), as OPTIONS is.
        MappingPiece[] Line(string text, int y, bool chosen)
        {
            var pieces = new List<MappingPiece>();
            int x = -44;
            foreach (char c in text)
            {
                if (c != ' ') pieces.Add(new MappingPiece(y, 0, Letter(c) | (chosen ? 0x6000 : 0), x));
                x += 8;
            }
            if (chosen) pieces.Add(new MappingPiece(y, 4, 0x647A, -68));
            return [.. pieces];
        }
        return
        [
            [.. Line("START GAME", -12, true), .. Line("LEVEL SELECT", 4, false)],
            Line("LEVEL SELECT", -8, true),
        ];
    }
}
