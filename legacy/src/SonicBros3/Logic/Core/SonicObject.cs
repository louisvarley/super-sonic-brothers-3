namespace SonicBros3.Logic;

/// <summary>
/// One 64-byte object slot in <see cref="Ram"/>, with the standard Sonic 2 object fields by name (names follow the
/// Sonic 2 disassembly's s2.constants.asm). The bytes stay in RAM, laid out as the original lays them out, so a slot
/// can be compared byte for byte with MAME; this class is only a named window onto them.
///
/// Offsets $2C-$3F (and a few others an object doesn't use) are free for each object type's own variables. Objects
/// name those through a small view of their own (see <c>State</c> in e.g. <see cref="Waterfall"/>), built on
/// <see cref="Byte"/>, <see cref="Word"/> and <see cref="Long"/>.
///
/// Positions are 16.16 fixed point stored as longs at $08/$0C; velocities and inertia are 8.8. Byte fields read back
/// as 0-255 and accept any int (only the low bits are stored), exactly like a <c>move.b</c>.
/// </summary>
public sealed class SonicObject(Ram ram, int addr)
{
    public int Address => addr;
    public Ram Ram => ram;

    // ---- Standard fields (every object) ----

    /// <summary>$00 id: the object type (index into the object table). 0 = empty slot.</summary>
    public int Id { get => ram.U8(addr + 0x00); set => ram.W8(addr + 0x00, value); }
    /// <summary>
    /// $01 render_flags: bit 0 x-flip, 1 y-flip, 2 position is in level coordinates (else screen), 3 ?,
    /// 4 on-screen test uses <see cref="YRadius"/>, 5 static mappings, 6 multi-sprite, 7 on screen (set by BuildSprites).
    /// </summary>
    public int RenderFlags { get => ram.U8(addr + 0x01); set => ram.W8(addr + 0x01, value); }
    /// <summary>$02 art_tile: VRAM tile index plus palette line ($2000 per line), priority ($8000) and flip bits.</summary>
    public int ArtTile { get => ram.U16(addr + 0x02); set => ram.W16(addr + 0x02, value); }
    /// <summary>$04 mappings: ROM address of the sprite mappings (resolved by the sprite builder from extracted data).</summary>
    public uint Mappings { get => ram.U32(addr + 0x04); set => ram.W32(addr + 0x04, value); }
    public uint XLong { get => ram.U32(addr + 0x08); set => ram.W32(addr + 0x08, value); }
    /// <summary>$08 x_pos (pixels).</summary>
    public int X { get => ram.U16(addr + 0x08); set => ram.W16(addr + 0x08, value); }
    /// <summary>$0A x_sub: the fraction of <see cref="X"/>.</summary>
    public int XSub { get => ram.U16(addr + 0x0A); set => ram.W16(addr + 0x0A, value); }
    public uint YLong { get => ram.U32(addr + 0x0C); set => ram.W32(addr + 0x0C, value); }
    /// <summary>$0C y_pos (pixels).</summary>
    public int Y { get => ram.U16(addr + 0x0C); set => ram.W16(addr + 0x0C, value); }
    /// <summary>$0E y_sub: the fraction of <see cref="Y"/>.</summary>
    public int YSub { get => ram.U16(addr + 0x0E); set => ram.W16(addr + 0x0E, value); }
    /// <summary>$10 x_vel (8.8 pixels per frame).</summary>
    public short XVel { get => ram.S16(addr + 0x10); set => ram.W16(addr + 0x10, value); }
    /// <summary>$12 y_vel.</summary>
    public short YVel { get => ram.S16(addr + 0x12); set => ram.W16(addr + 0x12, value); }
    /// <summary>$14 inertia: ground speed (players).</summary>
    public short Inertia { get => ram.S16(addr + 0x14); set => ram.W16(addr + 0x14, value); }
    /// <summary>$16 y_radius (half height). Signed.</summary>
    public int YRadius { get => ram.S8(addr + 0x16); set => ram.W8(addr + 0x16, value); }
    /// <summary>$17 x_radius (half width for terrain collision). Signed.</summary>
    public int XRadius { get => ram.S8(addr + 0x17); set => ram.W8(addr + 0x17, value); }
    /// <summary>$18 priority: sprite display layer, 0 (front) to 7 (back).</summary>
    public int Priority { get => ram.U8(addr + 0x18); set => ram.W8(addr + 0x18, value); }
    /// <summary>$19 width_pixels: half width, for the on-screen test.</summary>
    public int WidthPixels { get => ram.U8(addr + 0x19); set => ram.W8(addr + 0x19, value); }
    /// <summary>$1A mapping_frame: the sprite frame shown.</summary>
    public int MappingFrame { get => ram.U8(addr + 0x1A); set => ram.W8(addr + 0x1A, value); }
    /// <summary>$1B anim_frame: position in the current animation script.</summary>
    public int AnimFrame { get => ram.U8(addr + 0x1B); set => ram.W8(addr + 0x1B, value); }
    /// <summary>$1C anim: the animation script playing.</summary>
    public int Anim { get => ram.U8(addr + 0x1C); set => ram.W8(addr + 0x1C, value); }
    /// <summary>$1D prev_anim: the script last frame (a change restarts the animation).</summary>
    public int PrevAnim { get => ram.U8(addr + 0x1D); set => ram.W8(addr + 0x1D, value); }
    /// <summary>$1E anim_frame_duration: frames until the next animation step.</summary>
    public int AnimFrameDuration { get => ram.U8(addr + 0x1E); set => ram.W8(addr + 0x1E, value); }
    /// <summary>
    /// $20 collision_flags: what touching it does (bits 6-7: 0 enemy, 1 ring/monitor, 2 hurts, 3 special) and its
    /// hit box size (bits 0-5, index into the TouchResponse size table). 0 = not touchable.
    /// </summary>
    public int CollisionFlags { get => ram.U8(addr + 0x20); set => ram.W8(addr + 0x20, value); }
    /// <summary>$21 collision_property: hit count for bosses; bits set by TouchResponse for "touched by" for others.</summary>
    public int CollisionProperty { get => ram.U8(addr + 0x21); set => ram.W8(addr + 0x21, value); }
    /// <summary>bit 0 facing left, 1 in air, 2 rolling, 3 on object, 4 roll-jumping, 5 pushing, 6 underwater.</summary>
    public int StatusAddr => addr + 0x22;
    /// <summary>$22 status (see <see cref="StatusAddr"/> for the player bits; objects: bit 0 x-flip, 3/4 player 1/2 standing on it, 5/6 pushing, 7 destroyed).</summary>
    public int Status { get => ram.U8(addr + 0x22); set => ram.W8(addr + 0x22, value); }
    /// <summary>$23 respawn_index: entry in the object respawn table (0 = none).</summary>
    public int RespawnIndex { get => ram.U8(addr + 0x23); set => ram.W8(addr + 0x23, value); }
    /// <summary>$24 routine: the state the object's code jumps to (always even: 0, 2, 4, ...).</summary>
    public int Routine { get => ram.U8(addr + 0x24); set => ram.W8(addr + 0x24, value); }
    /// <summary>$25 routine_secondary: a sub-state within <see cref="Routine"/>.</summary>
    public int RoutineSecondary { get => ram.U8(addr + 0x25); set => ram.W8(addr + 0x25, value); }
    /// <summary>$26 angle.</summary>
    public int Angle { get => ram.U8(addr + 0x26); set => ram.W8(addr + 0x26, value); }
    /// <summary>$28 subtype: the object's parameter from the level's object layout.</summary>
    public int Subtype { get => ram.U8(addr + 0x28); set => ram.W8(addr + 0x28, value); }

    // ---- Player fields (Sonic and Tails) ----

    public int FlipAngle { get => ram.U8(addr + 0x27); set => ram.W8(addr + 0x27, value); }
    public int AirLeft { get => ram.U8(addr + 0x28); set => ram.W8(addr + 0x28, value); }
    public int FlipTurned { get => ram.U8(addr + 0x29); set => ram.W8(addr + 0x29, value); }
    public int ObjControl { get => ram.U8(addr + 0x2A); set => ram.W8(addr + 0x2A, value); }
    /// <summary>bit 1 invincible, bit 2 speed shoes, bit 7 sliding/no input.</summary>
    public int StatusSecondaryAddr => addr + 0x2B;
    public int StatusSecondary { get => ram.U8(addr + 0x2B); set => ram.W8(addr + 0x2B, value); }
    public int FlipsRemaining { get => ram.U8(addr + 0x2C); set => ram.W8(addr + 0x2C, value); }
    public int FlipSpeed { get => ram.U8(addr + 0x2D); set => ram.W8(addr + 0x2D, value); }
    public int MoveLock { get => ram.U16(addr + 0x2E); set => ram.W16(addr + 0x2E, value); }
    public int InvulnerableTime { get => ram.U16(addr + 0x30); set => ram.W16(addr + 0x30, value); }
    public int InvincibilityTime { get => ram.U16(addr + 0x32); set => ram.W16(addr + 0x32, value); }
    public int SpeedShoesTime { get => ram.U16(addr + 0x34); set => ram.W16(addr + 0x34, value); }
    public int NextTiltCopy { get => ram.U8(addr + 0x36); set => ram.W8(addr + 0x36, value); }
    public int TiltCopy { get => ram.U8(addr + 0x37); set => ram.W8(addr + 0x37, value); }
    public int StickToConvex { get => ram.U8(addr + 0x38); set => ram.W8(addr + 0x38, value); }
    public int SpindashFlag { get => ram.U8(addr + 0x39); set => ram.W8(addr + 0x39, value); }
    public int SpindashCounter { get => ram.U16(addr + 0x3A); set => ram.W16(addr + 0x3A, value); }
    public int Jumping { get => ram.U8(addr + 0x3C); set => ram.W8(addr + 0x3C, value); }
    /// <summary>$3D interact: the slot number of the object the player stands on.</summary>
    public int Interact { get => ram.U8(addr + 0x3D); set => ram.W8(addr + 0x3D, value); }
    /// <summary>The object the player stands on.</summary>
    public SonicObject InteractObject => Objects.Slot(ram, Interact);
    /// <summary>$3E-$3F as one word: both solidity bits (saved and restored together).</summary>
    public int SolidBits { get => ram.U16(addr + 0x3E); set => ram.W16(addr + 0x3E, value); }
    /// <summary>Solidity bit numbers for the current layer: $0C/$0D (layer A) or $0E/$0F (layer B).</summary>
    public int TopSolidBit { get => ram.U8(addr + 0x3E); set => ram.W8(addr + 0x3E, value); }
    public int LrbSolidBit { get => ram.U8(addr + 0x3F); set => ram.W8(addr + 0x3F, value); }

    /// <summary>Drawn last frame (render flag bit 7, set by BuildSprites).</summary>
    public bool OnScreen => (RenderFlags & Render.OnScreen) != 0;

    // The player status bits (status, $22).
    public bool FacingLeft { get => StatusBit(0); set => SetStatusBit(0, value); }
    public bool InAir { get => StatusBit(1); set => SetStatusBit(1, value); }
    public bool Rolling { get => StatusBit(2); set => SetStatusBit(2, value); }
    /// <summary>Standing on an object (whose slot number is at $3D).</summary>
    public bool OnObject { get => StatusBit(3); set => SetStatusBit(3, value); }
    /// <summary>Jumped while rolling (no air control).</summary>
    public bool RollJumping { get => StatusBit(4); set => SetStatusBit(4, value); }
    public bool Pushing { get => StatusBit(5); set => SetStatusBit(5, value); }
    public bool Underwater { get => StatusBit(6); set => SetStatusBit(6, value); }

    // The player secondary status bits (status_secondary, $2B).
    public bool HasShield { get => ram.Bit(StatusSecondaryAddr, 0); set => SetBit(StatusSecondaryAddr, 0, value); }
    public bool Invincible { get => ram.Bit(StatusSecondaryAddr, 1); set => SetBit(StatusSecondaryAddr, 1, value); }
    public bool HasSpeedShoes { get => ram.Bit(StatusSecondaryAddr, 2); set => SetBit(StatusSecondaryAddr, 2, value); }

    private void SetBit(int address, int bit, bool on)
    {
        if (on) ram.Set(address, bit);
        else ram.Clear(address, bit);
    }

    private bool StatusBit(int bit) => ram.Bit(StatusAddr, bit);
    private void SetStatusBit(int bit, bool on)
    {
        if (on) ram.Set(StatusAddr, bit);
        else ram.Clear(StatusAddr, bit);
    }

    // ---- Object-specific variables ----

    /// <summary>A byte at an offset in the slot (for an object type's own variables).</summary>
    public int Byte(int offset) => ram.U8(addr + offset);
    public void SetByte(int offset, int value) => ram.W8(addr + offset, value);
    /// <summary>A word at an offset in the slot.</summary>
    public int Word(int offset) => ram.U16(addr + offset);
    public void SetWord(int offset, int value) => ram.W16(addr + offset, value);
    /// <summary>A long at an offset in the slot.</summary>
    public uint Long(int offset) => ram.U32(addr + offset);
    public void SetLong(int offset, long value) => ram.W32(addr + offset, value);

    /// <summary>Clears the whole slot (DeleteObject).</summary>
    public void Clear() => ram.Bytes.AsSpan(addr, 0x40).Clear();
}

/// <summary>
/// The fields of a multi-sprite object (render flag <see cref="Render.MultiSprite"/>): one slot drawing a main frame
/// plus up to 8 child sprites at their own positions (chains, bridges). The fields reuse the standard offsets.
/// </summary>
public readonly struct MultiSprite(SonicObject o)
{
    /// <summary>$0B: the main sprite's frame (0 = none).</summary>
    public int MainFrame { get => o.Byte(0x0B); set => o.SetByte(0x0B, value); }
    /// <summary>$0E: half width for the on-screen test.</summary>
    public int Width { get => o.Byte(0x0E); set => o.SetByte(0x0E, value); }
    /// <summary>$0F: number of child sprites.</summary>
    public int ChildCount { get => o.Byte(0x0F); set => o.SetByte(0x0F, value); }
    /// <summary>$14: half height for the on-screen test (with <see cref="Render.UseYRadius"/>).</summary>
    public int Height { get => o.Byte(0x14); set => o.SetByte(0x14, value); }

    /// <summary>Child <paramref name="i"/>'s entry: x word, y word, unused byte, frame byte, from $10 (6 bytes each).</summary>
    private static int Child(int i) => 0x10 + i * 6;
    public int ChildX(int i) => o.Word(Child(i));
    public void SetChildX(int i, int x) => o.SetWord(Child(i), x);
    public int ChildY(int i) => o.Word(Child(i) + 2);
    public void SetChildY(int i, int y) => o.SetWord(Child(i) + 2, y);
    public int ChildFrame(int i) => o.Byte(Child(i) + 5);
    public void SetChildFrame(int i, int frame) => o.SetByte(Child(i) + 5, frame);
    /// <summary>Child <paramref name="i"/>'s whole entry (x, y, frame word) at once.</summary>
    public void SetChild(int i, int x, int y, int frameWord)
    {
        o.SetWord(Child(i), x);
        o.SetWord(Child(i) + 2, y);
        o.SetWord(Child(i) + 4, frameWord);
    }
}
