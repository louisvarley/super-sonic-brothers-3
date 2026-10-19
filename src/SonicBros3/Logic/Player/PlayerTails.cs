namespace SonicBros3.Logic;

/// <summary>
/// Tails' own routines (object 02, ROM 0x1F6B8): init, control (with the CPU driving his joypad), timers, position
/// history, hurt, death and respawn, and his DPLC loader. Movement shares the ported Sonic code through
/// <see cref="PlayerProfile.Tails"/>.
/// </summary>
public sealed partial class Player
{

    private void TailsStep()
    {
        switch (O.Routine)
        {
            case 0: TailsInit(); TailsControl(); break;
            case 2: TailsControl(); break;
            case 4: TailsHurt(); break;
            case 6: TailsDead(); break;
            case 8: TailsGone(); break;
            case 0x0A: TailsRespawning(); break;
            default: throw new UnportedException(0x1F6D2, $"Obj02 routine {O.Routine}");
        }
    }

    /// <summary>Obj02_Init (0x1F6EC), Sonic-and-Tails mode.</summary>
    private void TailsInit()
    {
        if (ram.U16(Vars.PlayerMode) == 2) throw new UnportedException(0x1F730, "Tails-alone mode");
        O.Routine += 2;
        O.YRadius = 0x0F;
        O.XRadius = 9;
        O.Mappings = 0x7A6F6;
        O.Priority = 2;
        O.WidthPixels = 0x18;
        O.RenderFlags = 0x84;
        SetNormalSpeeds();
        O.ArtTile = 0x7A0;
        O.SolidBits = ram.Sonic.SolidBits;   // Sonic's collision layer
        if ((short)ram.Sonic.ArtTile < 0) O.ArtTile |= 0x8000;
        ram.W16(Vars.SavedXPosP2, O.X);              // Tails' respawn state
        ram.W16(Vars.SavedYPosP2, O.Y);
        ram.W16(Vars.SavedArtTileP2, O.ArtTile);
        ram.W16(Vars.SavedSolidBitsP2, O.SolidBits);
        O.FlipsRemaining = 0;
        O.FlipSpeed = 4;
        O.AirLeft = 0x1E;
        ram.W16(Vars.TailsCpuRoutine, 0);
        ram.W16(Vars.TailsControlCounter, 0);
        ram.W16(Vars.TailsRespawnCounter, 0);
        ram.W8(Vars.TailsTails, 5);         // his tails (object 05)
        ram.W16(Vars.TailsTails + 0x3E, O.Address);
    }

    /// <summary>Obj02_Control (0x1F7C8), as the sidekick in slot $B040.</summary>
    private void TailsControl()
    {
        TailsWater();
        if (ram.U8(Vars.ControlLockedP2) == 0) ram.W16(P.Ctrl, ram.U16(Vars.Ctrl2Held));   // player 2's joypad
        if (ram.U16(Vars.TwoPlayerMode) == 0) TailsCpu();
        if ((O.ObjControl & 1) == 0)
        {
            switch (O.Status & 6)
            {
                case 0: MdNormal(); break;
                case 2: MdAir(); break;
                case 4: MdRoll(); break;
                case 6: MdAir(); break;
            }
        }
        if (ram.U16(Vars.CameraMinYPos) == 0xFF00) O.Y &= 0x7FF;
        Display();
        TailsRecordPos();
        TailsWater();
        O.NextTiltCopy = ram.U8(Vars.PrimaryAngle);
        O.TiltCopy = ram.U8(Vars.SecondaryAngle);
        if (ram.Bit(Vars.WindTunnelFlag, 1) && O.Anim == PlayerAnim.Walk) O.Anim = O.PrevAnim;
        anim.Animate(O);
        if ((sbyte)O.ObjControl >= 0) _touch.Run(O);
        TailsDplc();
    }

    /// <summary>0x1FF2E/0x1FF78: Tails' simpler balancing (no Super or fall-off variants). True if he balances.</summary>
    private bool TailsBalance()
    {
        if (O.OnObject)
        {
            var obj = O.InteractObject;
            if ((sbyte)obj.Status < 0) return false;
            int d1 = obj.WidthPixels;
            int d2 = d1 * 2 - 4;
            d1 = (short)(d1 + O.X - obj.X);
            if (d1 < 4) { O.FacingLeft = true; O.Anim = PlayerAnim.Balance; return true; }
            if (d1 >= d2) { O.FacingLeft = false; O.Anim = PlayerAnim.Balance; return true; }
            return false;
        }
        if (col.FloorEdge(O, O.X).Distance < 0x0C) return false;
        if (O.NextTiltCopy == 3) { O.FacingLeft = false; O.Anim = PlayerAnim.Balance; return true; }
        if (O.TiltCopy == 3) { O.FacingLeft = true; O.Anim = PlayerAnim.Balance; return true; }
        return false;
    }

    /// <summary>Tails_RecordPos (0x1FD6E): his own position history at $E600 (used in 2-player mode).</summary>
    private void TailsRecordPos()
    {
        int index = ram.U16(Vars.TailsPosRecordIndex);
        ram.W16(Vars.TailsPosRecordBuf + index, O.X);
        ram.W16(Vars.TailsPosRecordBuf + index + 2, O.Y);
        ram.W8(Vars.TailsPosRecordIndex + 1, ram.U8(Vars.TailsPosRecordIndex + 1) + 4);
    }

    /// <summary>Tails_Water (0x1FD88). Only the "stays above water" path is ported.</summary>
    private void TailsWater()
    {
        if (WaterAt is not null) { SetWater(WaterAt(O.X, O.Y)); return; }
        if (ram.U8(Vars.WaterFlag) == 0) return;
        if (ram.S16(Vars.WaterLevel1) >= (short)O.Y)
        {
            if (!ram.TestClear(O.StatusAddr, 6)) return;
            throw new UnportedException(0x1FDF0, "Tails leaving water");
        }
        throw new UnportedException(0x1FD9A, "Tails entering/under water");
    }

    /// <summary>LoadTailsDynPLC (0x21022): Tails' frame tiles go to VRAM tile $7A0.</summary>
    private void TailsDplc()
    {
        int frame = O.MappingFrame;
        if (frame == ram.U8(Vars.TailsLastLoadedDplc)) return;
        ram.W8(Vars.TailsLastLoadedDplc, frame);
        if (Sprites?.Dplc is not { } dplc || Vram is null) return;
        int dest = 0xF400;
        foreach (var (first, count) in dplc[frame])
        {
            Sprites.Art.AsSpan(first * 32, count * 32).CopyTo(Vram.AsSpan(dest));
            dest += count * 32;
        }
    }

    /// <summary>
    /// Falling below the level: KillCharacter (Sonic 0x1F6B2, Tails 0x2118E). KillCharacter's sound depends on the
    /// byte at a2, which here is whatever the caller left; the port passes the player itself (giving the normal hurt
    /// sound) — an inference, not checked against the emulator.
    /// </summary>
    private void FellOut() => _touch.Kill(O, O);

    /// <summary>Obj02_Hurt (0x20A26).</summary>
    private void TailsHurt()
    {
        ObjectMove();
        O.YVel = (short)(O.YVel + 0x30);
        if (O.Underwater) O.YVel = (short)(O.YVel - 0x20);
        if (ram.U16(Vars.CameraMinYPos) == 0xFF00) O.Y &= 0x7FF;
        if (TailsHurtStop()) return;
        LevelBound();
        TailsRecordPos();
        anim.Animate(O);
        TailsDplc();
        SpriteBuilder.DisplaySprite(ram, O);
    }

    /// <summary>Tails_HurtStop (0x20A68). Returns true if he was killed (the original jumps away).</summary>
    private bool TailsHurtStop()
    {
        int bottom = (short)(ram.U16(P.CamMaxY) + 0xE0);
        if (bottom < (short)O.Y) { FellOut(); return true; }
        DoLevelCollision();
        if (O.InAir) return false;
        O.YVel = 0;
        O.XVel = 0;
        O.Inertia = 0;
        O.ObjControl = 0;
        O.Anim = PlayerAnim.Walk;
        O.Routine = 2;
        O.InvulnerableTime = 0x78;
        O.SpindashFlag = 0;
        return false;
    }

    /// <summary>Obj02_Dead (0x20AB0): fall off screen, then the CPU respawns him.</summary>
    private void TailsDead()
    {
        TailsGameOverCheck();
        ObjectMoveAndFall();
        TailsRecordPos();
        anim.Animate(O);
        TailsDplc();
        SpriteBuilder.DisplaySprite(ram, O);
    }

    /// <summary>0x20ACC.</summary>
    private void TailsGameOverCheck()
    {
        if (ram.U16(Vars.PlayerMode) == 2) throw new UnportedException(0x1EFEA, "Tails-alone game over");
        ram.W8(Vars.ScrollLockP2, 1);
        O.SpindashFlag = 0;
        int limit = (short)(ram.U16(P.CamMaxY) + 0x100);
        if (limit >= (short)O.Y) return;
        O.Routine = 2;
        if (ram.U16(Vars.TwoPlayerMode) != 0) throw new UnportedException(0x20B02, "Tails' death in 2-player mode");
        CpuRespawn();
    }

    /// <summary>Routine 8 (0x20BF0): the act-reset timer after a 2-player death.</summary>
    private void TailsGone()
    {
        int t = RestartTimer;
        if (t == 0) return;
        RestartTimer = t - 1;
        if (t - 1 == 0) ram.W16(Vars.LevelInactiveFlag, 1);
    }

    /// <summary>Routine $A (0x20C04).</summary>
    private void TailsRespawning()
    {
        if (ram.U16(Vars.CameraXPosDiffP2) == 0 && ram.U16(Vars.CameraYPosDiffP2) == 0) O.Routine = 2;
        anim.Animate(O);
        TailsDplc();
        SpriteBuilder.DisplaySprite(ram, O);
    }

    // =============================================================== TailsCPU_Control (0x1F8EA)

    private void TailsCpu()
    {
        if ((ram.U8(Vars.Ctrl2Held) & 0x7F) != 0) ram.W16(Vars.TailsControlCounter, 0x258);   // player 2 took over: 10 s
        var sonic = ram.Sonic;
        switch (ram.U16(Vars.TailsCpuRoutine))
        {
            case 0: CpuInit(); break;
            case 2: CpuWaitToSpawn(sonic); break;
            case 4: CpuFlyIn(sonic); break;
            case 6: CpuFollow(sonic); break;
            case 8: CpuPanic(sonic); break;
            default: throw new UnportedException(0x1F902, $"TailsCPU routine {ram.U16(Vars.TailsCpuRoutine)}");
        }
    }

    /// <summary>0x1F914.</summary>
    private void CpuInit()
    {
        ram.W16(Vars.TailsCpuRoutine, 6);
        O.ObjControl = 0;
        O.Anim = PlayerAnim.Walk;
        O.XVel = 0;
        O.YVel = 0;
        O.Inertia = 0;
        O.Status = 0;
        ram.W16(Vars.TailsRespawnCounter, 0);
    }

    /// <summary>0x1F946: wait (every 64 frames, Sonic on the ground and free) before flying in.</summary>
    private void CpuWaitToSpawn(SonicObject sonic)
    {
        if ((ram.U8(P.Ctrl) & 0xF0) == 0)
        {
            if ((ram.U16(Vars.LevelFrameCounter) & 0x3F) != 0) return;
            if (sonic.ObjControl != 0) return;
            if ((sonic.Status & 0xD2) != 0) return;
        }
        ram.W16(Vars.TailsCpuRoutine, 4);
        O.X = sonic.X;
        ram.W16(Vars.TailsCpuTargetX, sonic.X);
        ram.W16(Vars.TailsCpuTargetY, sonic.Y);
        O.Y = (sonic.Y - 0xC0) & 0xFFFF;
        O.ArtTile |= 0x8000;
        O.SpindashFlag = 0;
        O.SpindashCounter = 0;
    }

    /// <summary>0x1F9A0: fly toward where Sonic was 16 frames ago; land when there.</summary>
    private void CpuFlyIn(SonicObject sonic)
    {
        if ((sbyte)O.RenderFlags < 0) ram.W16(Vars.TailsRespawnCounter, 0);
        else
        {
            ram.W16(Vars.TailsRespawnCounter, ram.U16(Vars.TailsRespawnCounter) + 1);
            if (ram.U16(Vars.TailsRespawnCounter) >= 0x12C)
            {
                ram.W16(Vars.TailsRespawnCounter, 0);
                ram.W16(Vars.TailsCpuRoutine, 2);
                O.ObjControl = 0x81;
                O.Status = 2;
                O.X = 0;
                O.Y = 0;
                O.Anim = PlayerAnim.TailsFly;
                return;
            }
        }
        int d3 = HistoryIndex();
        ram.W16(Vars.TailsCpuTargetX, ram.U16(Vars.SonicPosRecordBuf + d3));
        ram.W16(Vars.TailsCpuTargetY, ram.U16(Vars.SonicPosRecordBuf + d3 + 2));
        if (ram.U8(Vars.WaterFlag) != 0)
        {
            int limit = (short)(ram.U16(Vars.WaterLevel1) - 0x10);
            if (limit < (short)ram.U16(Vars.TailsCpuTargetY)) ram.W16(Vars.TailsCpuTargetY, limit);
        }

        int d0 = (short)(O.X - ram.U16(Vars.TailsCpuTargetX));
        if (d0 != 0)
        {
            int d2 = Math.Abs(d0) >> 4;
            if (d2 >= 0x0C) d2 = 0x0C;
            int d1 = sonic.XVel >> 8;   // Sonic's x speed in whole pixels
            if (d1 < 0) d1 = -d1;
            d2 = ((d2 & 0xFF00) | ((d2 + d1) & 0xFF)) + 1;
            if (d0 >= 0)
            {
                O.FacingLeft = true;
                if (d2 >= d0) { d2 = d0; d0 = 0; }
                d2 = -d2;
            }
            else
            {
                O.FacingLeft = false;
                d0 = -d0;
                if (d2 >= d0) { d2 = (d2 & 0xFF00) | (d0 & 0xFF); d0 = 0; }
            }
            O.X = (O.X + d2) & 0xFFFF;
        }
        int dy = (short)(O.Y - ram.U16(Vars.TailsCpuTargetY));
        if (dy != 0) O.Y = (O.Y + (dy > 0 ? -1 : 1)) & 0xFFFF;

        if (ram.U8(Vars.SpecialStageRequested) == 0)
        {
            int recordedStatus = ram.U8(Vars.SonicStatRecordBuf + d3 + 2);
            if ((recordedStatus & 0xD2) != 0) return;
            if ((d0 | dy) != 0) return;
        }
        // Arrived (0x1FA96).
        ram.W16(Vars.TailsCpuRoutine, 6);
        O.ObjControl = 0;
        O.Anim = (byte)(ram.U8(Vars.SpecialStageRequested) != 0 ? 2 : 0);
        O.XVel = 0;
        O.YVel = 0;
        O.Inertia = 0;
        O.Status = 2;
        O.MoveLock = 0;
        O.ArtTile &= 0x7FFF;
        if ((short)sonic.ArtTile < 0) O.ArtTile |= 0x8000;
        O.TopSolidBit = sonic.TopSolidBit;
        O.LrbSolidBit = sonic.LrbSolidBit;
        if (sonic.Anim == PlayerAnim.Spindash) return;
        int dash = O.SpindashFlag;
        if (dash == 0) return;
        sonic.SpindashFlag = (byte)dash;
        // 0x25318: put Sonic into his rolling state.
        if (sonic.Rolling) return;
        sonic.Rolling = true;
        sonic.YRadius = 0x0E;
        sonic.XRadius = 7;
        sonic.Anim = PlayerAnim.Roll;
        sonic.Y += 5;
        sound.PlaySound(Sfx.Roll);
    }

    /// <summary>Index of the position-history entry 16 frames back (lsl.b/addq.b/sub.b: byte arithmetic).</summary>
    private int HistoryIndex()
    {
        int d2 = ((0x10 << 2) + 4) & 0xFF;
        int index = ram.U16(Vars.SonicPosRecordIndex);
        return (index & 0xFF00) | ((index - d2) & 0xFF);
    }

    /// <summary>0x1FB08: follow Sonic by replaying his recorded input with corrections; jump when he is above.</summary>
    private void CpuFollow(SonicObject sonic)
    {
        if (ram.Sonic.Routine >= 6)
        {
            ram.W16(Vars.TailsCpuRoutine, 4);
            O.SpindashFlag = 0;
            O.SpindashCounter = 0;
            O.ObjControl = 0x81;
            O.Status = 2;
            O.Anim = PlayerAnim.TailsFly;
            return;
        }
        CpuStuckCheck();
        if (ram.U16(Vars.TailsControlCounter) != 0 || (sbyte)O.ObjControl < 0) { CpuManualCountdown(); return; }
        if (ram.U8(Vars.SpecialStageRequested) != 0) { ram.W16(P.Ctrl, 0x0808); return; }
        if (O.MoveLock != 0 && O.Inertia == 0) ram.W16(Vars.TailsCpuRoutine, 8);

        int d0 = HistoryIndex();
        int d2 = ram.U16(Vars.SonicPosRecordBuf + d0);
        int d3 = ram.U16(Vars.SonicPosRecordBuf + d0 + 2);
        int d1 = ram.U16(Vars.SonicStatRecordBuf + d0);
        int d4 = ram.U8(Vars.SonicStatRecordBuf + d0 + 2);

        bool skipX = O.Pushing && (d4 & 0x20) == 0;
        if (skipX) { CpuMaybeJump(ref d1); ram.W16(P.Ctrl, d1); return; }
        d2 = (short)(d2 - O.X);
        if (d2 == 0)
        {
            O.FacingLeft = false;
            if ((d4 & 1) != 0) O.FacingLeft = true;
        }
        else if (d2 < 0)
        {
            d2 = -d2;
            if (d2 >= 0x10) d1 = (d1 & 0xF3F3) | 0x0404;
            if (O.Inertia != 0 && O.FacingLeft) O.X = (O.X - 1) & 0xFFFF;
        }
        else
        {
            if (d2 >= 0x10) d1 = (d1 & 0xF3F3) | 0x0808;
            if (O.Inertia != 0 && !O.FacingLeft) O.X = (O.X + 1) & 0xFFFF;
        }
        if (ram.U8(Vars.TailsCpuJumping) != 0)
        {
            d1 |= 0x7000;
            if (O.InAir) { ram.W16(P.Ctrl, d1); return; }
            ram.W8(Vars.TailsCpuJumping, 0);
        }
        if ((ram.U16(Vars.LevelFrameCounter) & 0xFF) != 0 && (d2 & 0xFFFF) >= 0x40) { ram.W16(P.Ctrl, d1); return; }
        int dy = (short)(d3 - O.Y);
        if (dy >= 0) { ram.W16(P.Ctrl, d1); return; }
        if (-dy < 0x20) { ram.W16(P.Ctrl, d1); return; }
        CpuMaybeJump(ref d1);
        ram.W16(P.Ctrl, d1);
    }

    /// <summary>0x1FC3C: every 64 frames, unless ducking, press jump.</summary>
    private void CpuMaybeJump(ref int d1)
    {
        if ((ram.U8(Vars.LevelFrameCounter + 0x1) & 0x3F) != 0) return;
        if (O.Anim == PlayerAnim.Duck) return;
        d1 |= 0x7070;
        ram.W8(Vars.TailsCpuJumping, 1);
    }

    private void CpuManualCountdown()
    {
        if (ram.U16(Vars.TailsControlCounter) != 0) ram.W16(Vars.TailsControlCounter, ram.U16(Vars.TailsControlCounter) - 1);
    }

    /// <summary>0x1FC9C: off screen for 300 frames (or the object he stood on changed) -> respawn.</summary>
    private void CpuStuckCheck()
    {
        if ((sbyte)O.RenderFlags < 0)
        {
            ram.W16(Vars.TailsRespawnCounter, 0);
        }
        else
        {
            if (O.OnObject)
            {
                int obj = O.InteractObject.Address;
                if (ram.U8(Vars.TailsInteractId) != ram.U8(obj)) { CpuRespawn(); return; }
            }
            ram.W16(Vars.TailsRespawnCounter, ram.U16(Vars.TailsRespawnCounter) + 1);
            if (ram.U16(Vars.TailsRespawnCounter) >= 0x12C) { CpuRespawn(); return; }
        }
        ram.W8(Vars.TailsInteractId, O.InteractObject.Id);
    }

    /// <summary>0x1FC6A: take Tails off the field; the CPU flies him back in.</summary>
    private void CpuRespawn()
    {
        ram.W16(Vars.TailsControlCounter, 0);
        ram.W16(Vars.TailsRespawnCounter, 0);
        ram.W16(Vars.TailsCpuRoutine, 2);
        O.ObjControl = 0x81;
        O.Status = 2;
        O.X = 0x4000;
        O.Y = 0;
        O.Anim = PlayerAnim.TailsFly;
    }

    /// <summary>0x1FCEE: stuck against something: duck and spindash toward Sonic.</summary>
    private void CpuPanic(SonicObject sonic)
    {
        CpuStuckCheck();
        if (ram.U16(Vars.TailsControlCounter) != 0) return;
        if (O.MoveLock != 0) return;
        if (O.SpindashFlag != 0)
        {
            ram.W16(P.Ctrl, 0x0202);
            int t = ram.U8(Vars.LevelFrameCounter + 0x1) & 0x7F;
            if (t == 0) { ram.W16(P.Ctrl, 0); ram.W16(Vars.TailsCpuRoutine, 6); return; }
            if ((t & 0x1F) == 0) ram.W16(P.Ctrl, ram.U16(P.Ctrl) | 0x7070);
            return;
        }
        if (O.Inertia != 0) return;
        O.FacingLeft = false;
        if (O.X >= sonic.X) O.FacingLeft = true;
        ram.W16(P.Ctrl, 0x0202);
        if ((ram.U8(Vars.LevelFrameCounter + 0x1) & 0x7F) == 0) { ram.W16(P.Ctrl, 0); ram.W16(Vars.TailsCpuRoutine, 6); return; }
        if (O.Anim != PlayerAnim.Duck) return;
        ram.W16(P.Ctrl, 0x7272);
    }
}
