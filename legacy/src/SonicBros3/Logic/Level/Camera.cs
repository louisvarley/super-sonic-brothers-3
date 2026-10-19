namespace SonicBros3.Logic;

/// <summary>
/// DeformBgLayer (ROM 0xDCD6) for one player: ScrollHoriz (0xF7A6), ScrollVerti (0xF81C), their scroll-flag
/// bookkeeping (0xF784 / 0xF932), DynamicLevelEvents (0x10804) and the per-zone background routine, which fills the
/// per-line horizontal scroll buffer at $E000 and the vertical scroll values at $F616/$F618.
/// The renderer reads its scroll values from that RAM, as the VDP does from the DMA'd copy.
/// </summary>
public sealed class Camera(Ram ram)
{
    /// <summary>The hack's boss music (Sonic 1's, through the 68k driver).</summary>
    private const int MusicBossS1 = 0x8C;

    public void DeformBgLayer()
    {
        if (ram.U8(Vars.DeformLock) != 0) return;   // camera frozen
        for (int a = Vars.ScrollFlags; a <= Vars.ScrollFlags + 0x0E; a += 2) ram.W16(a, 0);
        foreach (int a in new[] { Vars.CameraXPosDiff, Vars.CameraYPosDiff, Vars.CameraXPosDiffP2, Vars.CameraYPosDiffP2 }) ram.W16(a, 0);

        bool skipScroll = ram.U8(Vars.CurrentZoneAndAct) == 0x10 && ram.U16(Vars.DebugPlacementMode) == 0;
        if (!skipScroll && ram.U8(Vars.ScrollLock) == 0)
        {
            if (ram.U16(Vars.PlayerMode) == 2) throw new UnportedException(0xDD4A, "camera following Tails");
            int oldX = ScrollHoriz(Vars.MainCharacter);
            ScrollFlagsH(oldX);
            int oldY = ScrollVerti(Vars.MainCharacter, ram.U16(Vars.CameraYPosBias), oldX);
            ScrollFlagsV(oldY);
        }
        if (!skipScroll && ram.U16(Vars.TwoPlayerMode) != 0) throw new UnportedException(0xDD8C, "2P camera");

        DynamicLevelEvents();
        ram.W16(Vars.VscrollFactorFg, ram.U16(Vars.CameraYPos));
        ram.W16(Vars.VscrollFactorBg, ram.U16(Vars.CameraBgYPos));
        ram.W32(Vars.CameraXPosCopy, ram.U32(Vars.CameraXPos));
        ram.W32(Vars.CameraYPosCopy, ram.U32(Vars.CameraYPos));
        switch (ram.U8(Vars.CurrentZoneAndAct))
        {
            case 0x11 or 0x12: SwScrlGhz(); break;
            case 0x13 or 0x14: SwScrlMz(); break;
            case var zone when IsEasterEgg(zone): SwScrlEasterEgg(); break;
            case 0x00: SwScrlEhz(); break;
            default: throw new UnportedException(0xDDF2, $"background deformation for zone {ram.U8(Vars.CurrentZoneAndAct):X2}");
        }
    }

    /// <summary>ScrollHoriz (0xF7A6). Returns the previous camera X (d4).</summary>
    private int ScrollHoriz(int obj)
    {
        int camX = ram.U16(Vars.CameraXPos);
        int d4 = camX;
        if (ram.U8(Vars.TeleportFlag) != 0) return d4;
        int target;
        int delay = ram.U16(Vars.HorizScrollDelayVal);
        if (delay != 0)
        {
            // Spindash scroll delay: follow Sonic's recorded position from a few frames back.
            delay = (delay - 0x100) & 0xFFFF;
            ram.W16(Vars.HorizScrollDelayVal, delay);
            int d1 = (((delay >> 8) << 2) + 4) & 0xFF;
            int index = ram.U16(Vars.HorizScrollDelayVal + 2);
            index = (index & 0xFF00) | ((index - d1) & 0xFF);
            target = ram.U16(Vars.SonicPosRecordBuf + (short)index) & 0x3FFF;
        }
        else
        {
            target = ram.U16(obj + 0x08);
        }
        int d0 = (short)(target - camX - 0x90);
        int newX;
        if (d0 < 0)
        {
            if (d0 <= -0x10) d0 = -0x10;
            newX = (short)(d0 + camX);
            int min = (short)ram.U16(Vars.CameraMinXPos);
            if (newX <= min) newX = min;
        }
        else
        {
            d0 -= 0x10;
            if (d0 < 0) { ram.W16(Vars.CameraXPosDiff, 0); return d4; }
            if ((d0 & 0xFFFF) >= 0x10) d0 = 0x10;
            newX = (short)(d0 + camX);
            int max = (short)ram.U16(Vars.CameraMaxXPos);
            if (newX >= max) newX = max;
        }
        ram.W16(Vars.CameraXPosDiff, (newX - camX) << 8);
        ram.W16(Vars.CameraXPos, newX);
        return d4;
    }

    /// <summary>0xF784: mark a column redraw when the camera crosses a 16 px boundary.</summary>
    private void ScrollFlagsH(int oldX)
    {
        int cam = ram.U16(Vars.CameraXPos);
        if (((cam & 0x10) ^ ram.U8(Vars.HorizBlockCrossedFlag)) != 0) return;
        ram.W8(Vars.HorizBlockCrossedFlag, ram.U8(Vars.HorizBlockCrossedFlag) ^ 0x10);
        ram.Set(Vars.ScrollFlags, (short)(cam - oldX) >= 0 ? 3 : 2);
    }

    /// <summary>0xF932: as above, for rows.</summary>
    private void ScrollFlagsV(int oldY)
    {
        int cam = ram.U16(Vars.CameraYPos);
        if (((cam & 0x10) ^ ram.U8(Vars.VertiBlockCrossedFlag)) != 0) return;
        ram.W8(Vars.VertiBlockCrossedFlag, ram.U8(Vars.VertiBlockCrossedFlag) ^ 0x10);
        ram.Set(Vars.ScrollFlags, (short)(cam - oldY) >= 0 ? 1 : 0);
    }

    /// <summary>
    /// ScrollVerti (0xF81C). d3 = camera bias. Returns d4: the previous camera Y when the camera moved, otherwise
    /// the incoming d4 (ScrollHoriz's previous X), which the vertical flag routine then sees.
    /// </summary>
    private int ScrollVerti(int obj, int bias, int d4)
    {
        var o = new SonicObject(ram, obj);
        int camY = ram.U16(Vars.CameraYPos);
        int d0 = (o.Y - camY) & 0xFFFF;
        if (ram.U16(Vars.CameraMinYPos) == 0xFF00) d0 &= 0x7FF;
        if (o.Rolling) d0 = (d0 - 5) & 0xFFFF;
        int speed;

        if (o.InAir)
        {
            d0 = (d0 + 0x20) & 0xFFFF;
            int u = d0 - bias;
            if (u < 0) return Fast(u & 0xFFFF);
            u -= 0x40;
            if (u >= 0) return Fast(u);
            d0 = u & 0xFFFF;
            if (ram.U8(Vars.CameraMaxYPosChanging) != 0) return Settle();
            ram.W16(Vars.CameraYPosDiff, 0);
            return d4;
        }

        d0 = (d0 - bias) & 0xFFFF;
        if (d0 == 0)
        {
            if (ram.U8(Vars.CameraMaxYPosChanging) != 0) return Settle();
            ram.W16(Vars.CameraYPosDiff, 0);
            return d4;
        }
        int sd0 = (short)d0;
        if (bias == 0x60)
        {
            int inertia = Math.Abs((int)o.Inertia) & 0xFFFF;
            if (inertia >= 0x800) return Fast(d0);
            speed = 0x600;
            if (sd0 > 6) return MoveDown(speed);
            if (sd0 < -6) return MoveUp(speed);
            return Snap(sd0);
        }
        speed = 0x200;
        if (sd0 > 2) return MoveDown(speed);
        if (sd0 < -2) return MoveUp(speed);
        return Snap(sd0);

        int Fast(int v)
        {
            int s = (short)v;
            if (s > 0x10) return MoveDown(0x1000);
            if (s < -0x10) return MoveUp(0x1000);
            return Snap(s);
        }

        int Settle()
        {
            ram.W8(Vars.CameraMaxYPosChanging, 0);
            return Snap(0);
        }

        // 0xF8B6: move straight to the target; sub-pixel cleared.
        int Snap(int delta)
        {
            int pos = (delta + camY) & 0xFFFF;
            long full = (long)pos << 16;
            return delta >= 0 ? ClampDown(full) : ClampUp(full);
        }

        int MoveUp(int sp)
        {
            long full = ((long)ram.U32(Vars.CameraYPos) - ((long)sp << 8)) & 0xFFFFFFFF;
            return ClampUp(full);
        }

        int MoveDown(int sp)
        {
            long full = ((long)ram.U32(Vars.CameraYPos) + ((long)sp << 8)) & 0xFFFFFFFF;
            return ClampDown(full);
        }

        // 0xF8D0.
        int ClampUp(long full)
        {
            int pos = (int)(full >> 16) & 0xFFFF;
            int min = (short)ram.U16(Vars.CameraMinYPos);
            if ((short)pos > min) return Commit(full);
            if ((short)pos > -0x100) return Commit(((long)(min & 0xFFFF) << 16) | (full & 0xFFFF));
            pos &= 0x7FF;
            ram.W16(Vars.CameraYPos, ram.U16(Vars.CameraYPos) & 0x7FF);
            ram.Sonic.Y &= 0x7FF;   // the level wraps vertically
            ram.Tails.Y &= 0x7FF;
            return Commit(((long)pos << 16) | (full & 0xFFFF));
        }

        // 0xF900.
        int ClampDown(long full)
        {
            int pos = (int)(full >> 16) & 0xFFFF;
            int max = (short)ram.U16(Vars.CameraMaxYPos);
            if ((short)pos < max) return Commit(full);
            int wrapped = pos - 0x800;
            if (wrapped < 0) return Commit(((long)(max & 0xFFFF) << 16) | (full & 0xFFFF));
            ram.W16(Vars.CameraYPos, ram.U16(Vars.CameraYPos) - 0x800);
            ram.Sonic.Y &= 0x7FF;   // the level wraps vertically
            ram.Tails.Y &= 0x7FF;
            return Commit(((long)wrapped << 16) | (full & 0xFFFF));
        }

        // 0xF922: store, and the scroll difference (ror.l #8 of the long delta).
        int Commit(long full)
        {
            int old = ram.U16(Vars.CameraYPos);
            uint delta = (uint)(full - ram.U32(Vars.CameraYPos));
            ram.W16(Vars.CameraYPosDiff, (int)((delta >> 8) | (delta << 24)) & 0xFFFF);
            ram.W32(Vars.CameraYPos, full);
            return old;
        }
    }

    /// <summary>The zone's own event routine alone (used to set up debug starts).</summary>
    public void RunLevelEvents()
    {
        int zone = ram.U8(Vars.CurrentZoneAndAct), act = ram.U8(Vars.CurrentZoneAndAct + 1);
        ZoneEvents(zone, act);
    }

    /// <summary>Zones whose level events (and so whose gameplay) are ported.</summary>
    public static bool IsZonePorted(int zone) => zone is 0x00 or 0x11 or 0x12 or 0x13 or 0x14 || IsEasterEgg(zone);

    /// <summary>The SMB3 World 1-1 Easter egg (built from smb3.nes by Smb3ZoneBuilder): no events, a flat background.</summary>
    public const int EasterEggZone = 0x40;
    /// <summary>SMB3 World 1-2 (Sonic Bros. 3), built like 1-1 as its own zone.</summary>
    public const int World12Zone = 0x41;
    /// <summary>Registered SMB3 levels share the flat background and native SMB3 object handling.</summary>
    public static bool IsEasterEgg(int zone) => zone == S2Port.Extraction.Smb3.Smb3Campaign.Zone ||
        S2Port.Extraction.Smb3.Smb3Levels.ByZone(zone) is not null;

    private void ZoneEvents(int zone, int act)
    {
        switch (zone)
        {
            case 0x00: EhzEvents(act); break;
            case 0x11 or 0x12: GhzEvents(act); break;
            case var smb3Zone when IsEasterEgg(smb3Zone): break;
            case 0x13: if (act == 0) Mz1Events(); else Mz2Events(); break;
            case 0x14: Mz3Events(); break;
        }
    }

    /// <summary>DynamicLevelEvents (0x10804): per-zone events, then ease Camera_Max_Y toward its target.</summary>
    private void DynamicLevelEvents()
    {
        int zone = ram.U8(Vars.CurrentZoneAndAct), act = ram.U8(Vars.CurrentZoneAndAct + 1);
        if (IsZonePorted(zone)) ZoneEvents(zone, act);
        else throw new UnportedException(0x10804, $"level events for zone {zone:X2}");

        int target = ram.U16(Vars.CameraMaxYPosTarget), max = ram.U16(Vars.CameraMaxYPos);
        if (target == max) return;
        int step = 2;
        if (target < max)
        {
            step = -2;
            int camY = ram.U16(Vars.CameraYPos);
            if (camY > target) ram.W16(Vars.CameraMaxYPos, camY & 0xFFFE);
            ram.W16(Vars.CameraMaxYPos, ram.U16(Vars.CameraMaxYPos) + step);
            ram.W8(Vars.CameraMaxYPosChanging, 1);
            return;
        }
        if (((ram.U16(Vars.CameraYPos) + 8) & 0xFFFF) >= max && ram.Sonic.InAir) step *= 4;
        ram.W16(Vars.CameraMaxYPos, max + step);
        ram.W8(Vars.CameraMaxYPosChanging, 1);
    }

    /// <summary>Level events can load art (LoadPLC), play music and spawn objects (the boss).</summary>
    public Action<int>? LoadPlc { get; set; }
    public ISoundRequests? Sound { get; set; }

    // ------------------------------------------------------------------ Level events (DynamicResize)
    // Each zone's events move the camera's limits as the camera progresses, and start the boss. The event script's
    // state is EventRoutine (0, 2, 4, ...). The "Tails" limits are the sidekick's copy of the camera limits.

    private int EventRoutine
    {
        get => ram.U8(Vars.DynamicResizeRoutine);
        set => ram.W8(Vars.DynamicResizeRoutine, value);
    }

    private void NextEvent() => EventRoutine += 2;

    /// <summary>The bottom limit the camera eases toward.</summary>
    private void SetBottomTarget(int y) { ram.W16(Vars.CameraMaxYPosTarget, y); ram.W16(Vars.TailsMaxYPos, y); }
    private void SetTop(int y) { ram.W16(Vars.CameraMinYPos, y); ram.W16(Vars.TailsMinYPos, y); }
    private void SetLeft(int x) { ram.W16(Vars.CameraMinXPos, x); ram.W16(Vars.TailsMinXPos, x); }
    private void SetRight(int x) { ram.W16(Vars.CameraMaxXPos, x); ram.W16(Vars.TailsMaxXPos, x); }

    /// <summary>Lock the camera between two x positions (a boss arena).</summary>
    private void LockArena(int left, int right)
    {
        SetLeft(left);
        SetRight(right);
    }

    private void SpawnBoss(int id, int x, int y, int subtype = 0)
    {
        int slot = Objects.FindFreeSlot(ram);
        if (slot < 0) return;
        var boss = new SonicObject(ram, slot);
        boss.Id = id;
        boss.Subtype = subtype;
        boss.X = x;
        boss.Y = y;
    }

    /// <summary>
    /// Emerald Hill (zone $00, Sonic 2's events, 0x108A4). Act 1 has none. Act 2: past x $2780 the left limit follows
    /// the camera and the bottom limit becomes $390; at $28F0 the arena locks ($28F0-$2940), the music fades, the boss
    /// flag becomes 2 and the boss art loads (PLC $29); once the camera is down at $388, 90 frames later the boss
    /// (object $56, subtype $81) appears at $29D0,$426 with the boss music; once it is beaten the left limit follows.
    /// </summary>
    private void EhzEvents(int act)
    {
        if (act == 0) return;
        int camX = ram.U16(Vars.CameraXPos);
        switch (EventRoutine)
        {
            case 0:   // 0x108C2
                if (ram.U16(Vars.TwoPlayerMode) != 0) { SetRight(0x2920); return; }
                if (camX < 0x2780) return;
                SetLeft(camX);
                ram.W16(Vars.CameraMaxYPosTarget, 0x390);
                ram.W16(Vars.TailsMaxYPos, 0x390);
                NextEvent();
                return;
            case 2:   // 0x108FC: the arena
                if (camX < 0x28F0) return;
                LockArena(0x28F0, 0x2940);
                NextEvent();
                Sound?.PlayMusic(Music.FadeOut);
                ram.W8(Vars.BossSpawnDelay, 0);
                ram.W8(Vars.CurrentBossId, 2);
                LoadPlc?.Invoke(0x29);
                return;
            case 4:   // 0x1093A: the boss comes 90 frames later
                if (ram.U16(Vars.CameraYPos) >= 0x388) SetTop(0x388);
                ram.W8(Vars.BossSpawnDelay, ram.U8(Vars.BossSpawnDelay) + 1);
                if (ram.U8(Vars.BossSpawnDelay) < 0x5A) return;
                SpawnBoss(0x56, 0x29D0, 0x426, subtype: 0x81);
                NextEvent();
                Sound?.PlayMusic(Music.Boss);
                return;
            case 6:   // 0x10984: beaten; the left limit follows the camera
                if (ram.U8(Vars.BossDefeatedFlag) == 0) return;
                ram.W16(Vars.CameraMinXPos, camX);
                ram.W16(Vars.TailsMaxXPos, ram.U16(Vars.CameraMaxXPos));
                ram.W16(Vars.TailsMinXPos, camX);
                return;
        }
    }

    /// <summary>
    /// Zone $12 events (0x118F8): Sonic 1's Green Hill act 3. Camera limits by position; then at x $2960 the camera
    /// locks, the boss music starts, the boss art loads (PLCs $46, $47, $40) and the boss (object $3D) appears;
    /// afterwards the left limit follows the camera.
    /// </summary>
    private void Ghz3Events()
    {
        int camX = ram.U16(Vars.CameraXPos), camY = ram.U16(Vars.CameraYPos);
        switch (EventRoutine)
        {
            case 0:
                SetBottomTarget(0x300);
                if (camX < 0x380) return;
                SetBottomTarget(0x310);
                if (camX < 0x960) return;
                if (camY >= 0x280)
                {
                    SetBottomTarget(0x400);
                    if (camX < 0x1380) ram.W16(Vars.CameraMaxYPos, 0x4C0);
                    if (camX < 0x1380) SetBottomTarget(0x4C0);
                    if (camX < 0x1700) return;
                }
                SetBottomTarget(0x300);
                NextEvent();
                return;
            case 2:
                if (camX < 0x960) EventRoutine -= 2;
                if (camX < 0x2960) return;
                SpawnBoss(GhzBoss.Id, 0x2A60, 0x280);
                Sound?.PlayMusic(MusicBossS1);
                LockArena(0x2960, 0x2960);
                NextEvent();
                ram.W8(Vars.CurrentBossId, 1);
                LoadPlc?.Invoke(0x46);
                LoadPlc?.Invoke(0x47);
                LoadPlc?.Invoke(0x40);
                return;
            default:
                ram.W16(Vars.CameraMinXPos, camX);
                return;
        }
    }

    /// <summary>Zone $11 events (0x11872): Sonic 1's Green Hill act 1 and 2 camera limits.</summary>
    private void GhzEvents(int act)
    {
        int camX = ram.U16(Vars.CameraXPos);
        if (ram.U8(Vars.CurrentZoneAndAct) == 0x12) { Ghz3Events(); return; }
        switch (act)
        {
            case 0:
                SetBottomTarget(0x300);
                if (camX >= 0x1780) { SetBottomTarget(0x400); ram.W16(Vars.CameraMaxYPos, 0x400); }
                break;
            case 1:
                SetBottomTarget(0x300);
                if (camX < 0xED0) break;
                SetBottomTarget(0x200);
                if (camX < 0x1600) break;
                SetBottomTarget(0x400);
                if (camX < 0x1D60) break;
                SetBottomTarget(0x300);
                break;
            default:
                throw new UnportedException(0x11882, $"zone 11 act {act + 1} events");
        }
    }

    // ------------------------------------------------------------------ Marble Zone (the hack's zones $13, $14)

    /// <summary>Zone $13 act 1 events (0x11A02, Sonic 1 Marble Zone 1): the limits by camera position.</summary>
    private void Mz1Events()
    {
        int camX = ram.U16(Vars.CameraXPos), camY = ram.U16(Vars.CameraYPos);
        switch (EventRoutine)
        {
            case 0:   // 0x11A18
                SetBottomTarget(0x1D0);
                if (camX < 0x700) return;
                SetBottomTarget(0x220);
                if (camX < 0xD00) return;
                SetBottomTarget(0x340);
                if (camY < 0x340) return;
                NextEvent();
                return;
            case 2:   // 0x11A5A
                ram.W16(Vars.TailsMaxYPos, ram.U16(Vars.CameraMaxYPosTarget));
                if (camY < 0x340) { EventRoutine -= 2; return; }
                SetTop(0);
                if (camX >= 0xE00) return;
                SetTop(0x340);
                SetBottomTarget(0x340);
                if (camX >= 0xA90) return;
                SetBottomTarget(0x500);
                if (camY >= 0x370) NextEvent();
                return;
            case 4:   // 0x11ABC
                ram.W16(Vars.TailsMaxYPos, ram.U16(Vars.CameraMaxYPosTarget));
                if (camY < 0x370) { EventRoutine -= 2; return; }
                if (camY >= 0x500 && camX >= 0xB80)
                {
                    SetTop(0x500);
                    NextEvent();
                }
                return;
            case 6:   // 0x11AF2
                if (camX < 0xB80)
                {
                    // Ease the top limit back up to $340.
                    if (ram.U16(Vars.CameraMinYPos) == 0x340) return;
                    ram.W16(Vars.CameraMinYPos, ram.U16(Vars.CameraMinYPos) - 2);
                    ram.W16(Vars.TailsMinYPos, ram.U16(Vars.TailsMinYPos) - 2);
                    return;
                }
                if (ram.U16(Vars.CameraMinYPos) != 0x500)
                {
                    if (camY < 0x500) return;
                    SetTop(0x500);
                }
                if (camX < 0xE70) return;
                SetTop(0);
                SetBottomTarget(0x500);
                if (camX < 0x1430) return;
                SetBottomTarget(0x210);
                return;
            default:
                throw new UnportedException(0x14C0C, "zone 13 act 1 event routine 8");
        }
    }

    /// <summary>Zone $13 act 2 events (0x11B5E).</summary>
    private void Mz2Events()
    {
        SetBottomTarget(0x520);
        if (ram.U16(Vars.CameraXPos) >= 0x1700) SetBottomTarget(0x200);
    }

    /// <summary>Zone $14 (Marble Zone act 3) events (0x11B80): the boss arena and the boss.</summary>
    private void Mz3Events()
    {
        int camX = ram.U16(Vars.CameraXPos);
        if (EventRoutine != 0)
        {
            ram.W16(Vars.CameraMinXPos, camX);   // 0x11C0A
            return;
        }
        SetBottomTarget(0x720);
        if (camX < 0x1560) return;
        SetBottomTarget(0x210);
        if (camX < 0x17F0) return;
        SpawnBoss(MzBoss.Id, 0x19F0, 0x22C);
        Sound?.PlayMusic(MusicBossS1);
        LockArena(0x1800, 0x1800);
        EventRoutine = 2;
        ram.W8(Vars.CurrentBossId, 1);
        LoadPlc?.Invoke(0x47);
        LoadPlc?.Invoke(0x40);
    }

    /// <summary>Background layer scroll with redraw flags (0xF6E8 layer 1 / 0xF71C layer 2 / 0xF750 layer 3).</summary>
    private void LayerScroll(int pos, int toggle, int flags, int delta, int bit)
    {
        uint old = ram.U32(pos);
        uint now = (uint)(old + delta);
        ram.W32(pos, now);
        int d1 = (int)(now >> 16) & 0x10;
        if ((d1 ^ ram.U8(toggle)) != 0) return;
        ram.W8(toggle, ram.U8(toggle) ^ 0x10);
        if ((int)(now - old) < 0) ram.Set(flags, bit);
        else ram.Set(flags, bit + 1);
    }

    /// <summary>0xF6B8: set background layer 1's y, flagging a row redraw on each 16-px boundary crossed.</summary>
    private void SetBgY(int y)
    {
        int old = ram.U16(Vars.CameraBgYPos);
        ram.W16(Vars.CameraBgYPos, y);
        if (((y & 0x10) ^ ram.U8(Vars.VertiBlockCrossedFlagBg)) != 0) return;
        ram.W8(Vars.VertiBlockCrossedFlagBg, ram.U8(Vars.VertiBlockCrossedFlagBg) ^ 0x10);
        if ((short)(y - old) < 0) ram.Set(Vars.ScrollFlagsBg, 0);
        else ram.Set(Vars.ScrollFlagsBg, 1);
    }

    /// <summary>
    /// Zone $13/$14 background (0xF0F6, Sonic 1 Marble Zone): layer 1 at 3/4 of the camera's horizontal speed, layer 2
    /// at 1/2, layer 3 at 1/4; all at one height following the camera at 3/4 below $1C8. All redraw flags go to layer
    /// 3's byte (one drawing routine handles the rows of every layer). The scroll table at $E700 has one entry per
    /// 16-px band: 5 interpolated sky bands, 2 of layer 3, 9 of layer 2, 16 of layer 1.
    /// </summary>
    private void SwScrlMz()
    {
        int diff = (short)ram.U16(Vars.CameraXPosDiff);
        LayerScroll(Vars.CameraBgXPos, Vars.HorizBlockCrossedFlagBg, Vars.ScrollFlagsBg, (diff << 6) * 3, 2);
        LayerScroll(Vars.CameraBg3XPos, Vars.HorizBlockCrossedFlagBg3, Vars.ScrollFlagsBg3, diff << 6, 6);
        LayerScroll(Vars.CameraBg2XPos, Vars.HorizBlockCrossedFlagBg2, Vars.ScrollFlagsBg2, diff << 7, 4);
        int d0 = 0x200;
        int d1 = (ram.U16(Vars.CameraYPos) - 0x1C8) & 0xFFFF;
        if (ram.U16(Vars.CameraYPos) >= 0x1C8) d0 += (short)(d1 * 3) >> 2;
        d0 &= 0xFFFF;
        ram.W16(Vars.CameraBg2YPos, d0);
        ram.W16(Vars.CameraBg3YPos, d0);
        SetBgY(d0);
        ram.W16(Vars.VscrollFactorBg, ram.U16(Vars.CameraBgYPos));
        ram.W8(Vars.ScrollFlagsBg3, ram.U8(Vars.ScrollFlagsBg3) | ram.U8(Vars.ScrollFlagsBg) | ram.U8(Vars.ScrollFlagsBg2));
        ram.W8(Vars.ScrollFlagsBg, 0);
        ram.W8(Vars.ScrollFlagsBg2, 0);

        // The band table at $E700.
        int a1 = Vars.CloudCounters;
        int d2 = (-ram.U16(Vars.CameraXPos)) & 0xFFFF;
        int s2 = (short)d2;
        int step = (short)(((s2 >> 2) - s2) * 8 / 5);   // ext.l / asl.l #3 / divs.w #5 (quotient)
        uint d3 = (uint)((s2 >> 1) & 0xFFFF);
        uint acc = d3 << 16;
        for (int i = 0; i < 5; i++)
        {
            ram.W16(a1, (int)(acc >> 16));
            a1 += 2;
            acc += (uint)(step << 12);
        }
        for (int i = 0; i < 2; i++) { ram.W16(a1, -ram.U16(Vars.CameraBg3XPos)); a1 += 2; }
        for (int i = 0; i < 9; i++) { ram.W16(a1, -ram.U16(Vars.CameraBg2XPos)); a1 += 2; }
        for (int i = 0; i < 16; i++) { ram.W16(a1, -ram.U16(Vars.CameraBgXPos)); a1 += 2; }

        // H-scroll: 16-line bands from the background's y.
        int y = (ram.U16(Vars.CameraBgYPos) - 0x200) & 0xFFFF;
        int a2 = Vars.CloudCounters + (((y >= 0x100 ? 0x100 : y) & 0x1F0) >> 3);   // unsigned clamp (bcs)
        int fg = (-ram.U16(Vars.CameraXPos)) & 0xFFFF;
        int line = 0, first = 16 - (y & 0xF);
        int bg = ram.U16(a2); a2 += 2;
        for (int i = 0; i < first; i++) WriteLine(bg);
        for (int b = 0; b < 15; b++)
        {
            bg = ram.U16(a2); a2 += 2;
            for (int i = 0; i < 16; i++) WriteLine(bg);
        }

        void WriteLine(int value)
        {
            if (line >= 0x100) return;
            ram.W16(Vars.HorizScrollBuf + line * 4, fg);
            ram.W16(Vars.HorizScrollBuf + line * 4 + 2, value);
            line++;
        }
    }

    /// <summary>
    /// Emerald Hill (SwScrl_EHZ, 0xDE9C, Sonic 2's): the foreground at the camera; the background in bands down the
    /// screen: still sky (22 lines), clouds at 1/64 (58), rippling water (21, the wave table at 0xDFA8, stepped every 8
    /// frames), still (11), hills at 1/16 and 3/32 (16 each), then a perspective ramp of the near hills (1, 2 and 3
    /// steps of a fixed-point increment per band of 1, 2 and 5 lines).
    /// </summary>
    private void SwScrlEhz()
    {
        if (ram.U16(Vars.TwoPlayerMode) != 0) throw new UnportedException(0xDFEA, "Emerald Hill background in 2-player mode");
        ram.W16(Vars.VscrollFactorBg, ram.U16(Vars.CameraBgYPos));
        int a1 = Vars.HorizScrollBuf;
        int d2 = (short)(-(short)ram.U16(Vars.CameraXPos));
        int fg = d2 & 0xFFFF;
        void Line(int bg) { ram.W16(a1, fg); ram.W16(a1 + 2, bg & 0xFFFF); a1 += 4; }
        for (int i = 0; i <= 0x15; i++) Line(0);
        int d0 = (short)d2 >> 6;
        for (int i = 0; i <= 0x39; i++) Line(d0);
        int d3 = d0;
        if ((ram.U8(Vars.VintRuncountLow) & 7) == 0) ram.W16(Vars.TempArrayLayerDef, ram.U16(Vars.TempArrayLayerDef) - 1);
        int wave = ram.U16(Vars.TempArrayLayerDef) & 0x1F;
        for (int i = 0; i <= 0x14; i++) Line((sbyte)WaveByte(0xDFA8 + wave + i) + d3);
        for (int i = 0; i <= 0x0A; i++) Line(0);
        d0 = (short)d2 >> 4;
        for (int i = 0; i <= 0x0F; i++) Line(d0);
        d0 = ((short)d2 >> 4) + (((short)d2 >> 4) >> 1);
        for (int i = 0; i <= 0x0F; i++) Line(d0);
        // The near hills: d3 is 16.16 fixed point kept swapped (integer in the low word while it is written).
        int step = (((short)d2 >> 1) - ((short)d2 >> 3)) << 8;
        step = (short)(step / 0x30) << 8;
        uint acc = (uint)(((short)d2 >> 3) & 0xFFFF) << 16;   // integer part in the high word
        void Advance(int n) { for (int k = 0; k < n; k++) acc += (uint)step; }
        for (int i = 0; i <= 0x0E; i++) { Line((int)(acc >> 16)); Advance(1); }
        for (int i = 0; i <= 0x08; i++) { Line((int)(acc >> 16)); Line((int)(acc >> 16)); Advance(2); }
        for (int i = 0; i <= 0x0E; i++) { for (int k = 0; k < 5; k++) Line((int)(acc >> 16)); Advance(3); }
    }

    private byte WaveByte(int address) => _waveTable is null ? (byte)0 : _waveTable[address - 0xDFA8];

    /// <summary>The ripple table at 0xDFA8 (shared with the title screen), set by the world from extracted data.</summary>
    public byte[]? WaveTableData { set => _waveTable = value; }
    private byte[]? _waveTable;

    /// <summary>The SMB3 Easter egg: SMB3's sky is the backdrop colour, so plane B stays still and empty.</summary>
    private void SwScrlEasterEgg()
    {
        ram.W16(Vars.VscrollFactorBg, 0);
        int fg = (-ram.U16(Vars.CameraXPos)) & 0xFFFF;
        for (int line = 0; line < 0x100; line++)
        {
            ram.W16(Vars.HorizScrollBuf + line * 4, fg);
            ram.W16(Vars.HorizScrollBuf + line * 4 + 2, 0);
        }
    }

    /// <summary>Zone $11 background (0xF006): Sonic 1's Green Hill deformation with three cloud bands.</summary>
    private void SwScrlGhz()
    {
        int diff = (short)ram.U16(Vars.CameraXPosDiff);
        BgScroll(Vars.CameraBg3XPos, Vars.HorizBlockCrossedFlagBg3, Vars.ScrollFlagsBg3, diff * 96, 0);
        BgScroll(Vars.CameraBg2XPos, Vars.HorizBlockCrossedFlagBg2, Vars.ScrollFlagsBg2, diff << 7, 0);

        int a1 = Vars.HorizScrollBuf;
        int d0 = (ram.U16(Vars.CameraYPos) & 0x7FF) >> 5;
        d0 = -d0 + 0x20;
        if (d0 < 0) d0 = 0;
        int d4 = d0;
        ram.W16(Vars.VscrollFactorBg, d0);
        int fg = (-ram.U16(Vars.CameraXPos)) & 0xFFFF;

        ram.W32(Vars.CloudCounters, ram.U32(Vars.CloudCounters) + 0x10000);
        ram.W32(Vars.CloudCounters + 4, ram.U32(Vars.CloudCounters + 4) + 0xC000);
        ram.W32(Vars.CloudCounters + 8, ram.U32(Vars.CloudCounters + 8) + 0x8000);
        int bg3 = ram.U16(Vars.CameraBg3XPos);

        void Lines(int count, int bg)
        {
            for (int i = 0; i < count; i++) { ram.W16(a1, fg); ram.W16(a1 + 2, bg); a1 += 4; }
        }

        int n = 0x1F - d4;
        if (n >= 0) Lines(n + 1, -(ram.U16(Vars.CloudCounters) + bg3));
        Lines(16, -(ram.U16(Vars.CloudCounters + 4) + bg3));
        Lines(16, -(ram.U16(Vars.CloudCounters + 8) + bg3));
        Lines(0x30, -bg3);
        int bg2 = ram.U16(Vars.CameraBg2XPos);
        Lines(0x28, -bg2);

        // Water: interpolate from the BG2 position to the camera position over $68 lines.
        int d2 = (short)(ram.U16(Vars.CameraXPos) - bg2);
        d2 = ((d2 << 8) / 0x68) << 8;            // divs.w then ext.l of the quotient
        long d3 = (long)bg2 << 16;
        for (int i = 0; i <= 0x47 + d4; i++)
        {
            ram.W16(a1, fg);
            ram.W16(a1 + 2, -(int)(d3 >> 16));
            a1 += 4;
            d3 = (d3 + d2) & 0xFFFFFFFF;
        }
    }

    /// <summary>0xF71C / 0xF750: move a background layer and mark redraw flags (bit d6 / d6+1).</summary>
    private void BgScroll(int pos, int toggle, int flags, int delta, int bit)
    {
        uint old = ram.U32(pos);
        uint now = (uint)(old + delta);
        ram.W32(pos, now);
        int d1 = (int)(now >> 16) & 0x10;
        if ((d1 ^ ram.U8(toggle)) != 0) return;
        ram.W8(toggle, ram.U8(toggle) ^ 0x10);
        if ((int)(now - old) < 0) ram.Set(flags, bit);
        else ram.Set(flags, bit + 1);
    }
}
