namespace SonicBros3.Logic;

/// <summary>
/// The variables that differ between Sonic's code (Obj01, 0x1DCEE) and Tails' copy of it (Obj02, 0x1F6B8).
/// The original duplicates the whole player code for Tails; the copies differ only in these addresses and constants
/// plus a few routines (see <c>PlayerTails.cs</c>).
/// </summary>
public sealed record PlayerProfile(
    int Address, bool IsTails,
    int Speed,          // top speed, acceleration, deceleration (3 words)
    int Ctrl,           // logical joypad: held, then pressed
    int LookDelay, int CameraBias,
    int CamMinX, int CamMaxX, int CamMaxY,
    int StandYRadius, int RollShift,
    int Dust,           // the player's dust/splash object (Obj08)
    int ScrollDelay)    // horizontal scroll delay set by a spindash release
{
    public static readonly PlayerProfile Sonic = new(Vars.MainCharacter, false, Vars.SonicTopSpeed, Vars.Ctrl1HeldLogical,
        Vars.SonicLookDelayCounter, Vars.CameraYPosBias, Vars.CameraMinXPos, Vars.CameraMaxXPos, Vars.CameraMaxYPos,
        StandYRadius: 0x13, RollShift: 5, Dust: Vars.SonicDust, ScrollDelay: Vars.HorizScrollDelayVal);
    public static readonly PlayerProfile Tails = new(Vars.Sidekick, true, Vars.TailsTopSpeed, Vars.Ctrl2HeldLogical,
        Vars.TailsLookDelayCounter, Vars.CameraYPosBiasP2, Vars.TailsMinXPos, Vars.TailsMaxXPos, Vars.TailsMaxYPos,
        StandYRadius: 0x0F, RollShift: 1, Dust: Vars.TailsDust, ScrollDelay: Vars.HorizScrollDelayValP2);
}

/// <summary>The player objects' routines (routine, $24).</summary>
public static class PlayerRoutine
{
    public const int Init = 0, Control = 2, Hurt = 4, Dead = 6, Gone = 8;
    /// <summary>Tails only: flying back in after a 2-player death.</summary>
    public const int Respawning = 0x0A;
}

/// <summary>A player's animation routine (Sonic_Animate 0x1F138 / Tails_Animate 0x20C24).</summary>
public interface IPlayerAnimation
{
    void Animate(SonicObject o);
}

/// <summary>
/// Sonic (object 01, ROM 0x1DCEE) and Tails (object 02, 0x1F6B8), ported routine by routine. Comments give the
/// original address of each part (Sonic's copy; Tails' differences are noted where they matter).
/// State lives in <see cref="Ram"/> at the object slot, so this class holds no physics state of its own.
/// </summary>
public sealed partial class Player(Ram ram, Collision col, IPlayerAnimation anim, ISoundRequests sound, PlayerProfile? profile = null)
{
    private readonly PlayerProfile P = profile ?? PlayerProfile.Sonic;
    public SonicObject O { get; } = new(ram, (profile ?? PlayerProfile.Sonic).Address);
    public bool IsTails => P.IsTails;

    /// <summary>Sprite data and VRAM for LoadSonicDynPLC; when null, art loading is skipped (validation runs).</summary>
    public SpriteData? Sprites { get; set; }
    public byte[]? Vram { get; set; }
    /// <summary>LoadPLC (0x17F2), for the game-over art.</summary>
    public Action<int>? LoadPlc { get; set; }
    public Func<int, int, bool>? WaterAt { get; set; }
    private readonly TouchResponse _touch = new(ram, sound);

    private const int ButtonAbc = 0x70, Up = 0, Down = 1, Left = 2, Right = 3;
    private const int GameOverId = 0x39, MusicGameOver = 0x9B;
    /// <summary>After death, $3A (the spindash counter's word) counts down to the level restart.</summary>
    private int RestartTimer { get => O.Word(0x3A); set => O.SetWord(0x3A, value); }
    private int Held => ram.U8(P.Ctrl);
    private int Pressed => ram.U8(P.Ctrl + 1);
    private bool HeldBit(int bit) => (Held & (1 << bit)) != 0;
    private bool IsSuper => !P.IsTails && ram.U8(Vars.SuperSonicFlag) != 0;

    /// <summary>Obj01 entry (0x1DCEE): dispatch on the routine counter.</summary>
    public void Step()
    {
        if (P.IsTails) { TailsStep(); return; }
        switch (O.Routine)
        {
            case PlayerRoutine.Init: Init(); Control(); break;   // Obj01_Init falls through into Obj01_Control
            case PlayerRoutine.Control: Control(); break;
            case PlayerRoutine.Hurt: Hurt(); break;
            case PlayerRoutine.Dead: Dead(); break;
            case PlayerRoutine.Gone: Gone(); break;
            default: throw new UnportedException(0x1DCEE, $"Obj01 routine {O.Routine}");
        }
    }

    /// <summary>Obj01_Init (0x1DD08).</summary>
    private void Init()
    {
        O.Routine = PlayerRoutine.Control;
        O.YRadius = 0x13;
        O.XRadius = 9;
        O.Mappings = 0x76860;     // mappings (Obj01 sprite mappings in ROM)
        O.Priority = 2;            // priority
        O.WidthPixels = 0x18;
        O.RenderFlags = 4;
        SetNormalSpeeds();
        if (ram.U8(Vars.LastStarPoleHit) == 0)                // no checkpoint: set and save the spawn state
        {
            O.ArtTile = 0x780;   // art_tile (2P adjustment at 0x1AAE6 not ported)
            O.TopSolidBit = 0x0C;
            O.LrbSolidBit = 0x0D;
            ram.W16(Vars.SavedXPos, O.X);
            ram.W16(Vars.SavedYPos, O.Y);
            ram.W16(Vars.SavedArtTile, O.ArtTile);
            ram.W16(Vars.SavedSolidBits, O.SolidBits);
        }
        O.FlipsRemaining = 0;
        O.FlipSpeed = 4;
        ram.W8(Vars.SuperSonicFlag, 0);
        O.AirLeft = 0x1E;
        // Fill the position history (0x1DD90): record 64 times from 32 px left / 4 px down, clearing the stat entries.
        O.X -= 0x20;
        O.Y += 4;
        ram.W16(Vars.SonicPosRecordIndex, 0);
        for (int i = 0; i < 0x40; i++)
        {
            int index = ram.U16(Vars.SonicPosRecordIndex);
            RecordPos();
            ram.W32(Vars.SonicStatRecordBuf + index, 0);   // subq.w #4,a1 / move.l #0,(a1): clear the entry just written
        }
        O.X += 0x20;
        O.Y -= 4;
    }

    /// <summary>Obj01_Control (0x1DDC2).</summary>
    private void Control()
    {
        Water();
        if (ram.U8(Vars.ControlLocked) == 0) ram.W16(Vars.Ctrl1HeldLogical, ram.U16(Vars.Ctrl1Held));
        if ((O.ObjControl & 1) == 0)
        {
            switch (O.Status & 6)
            {
                case 0: MdNormalChecks(); break;
                case 2: MdAir(); break;
                case 4: MdRoll(); break;
                case 6: MdAir(); break;   // MdJump (0x1E0B0) is identical to MdAir
            }
        }
        if (ram.U16(Vars.CameraMinYPos) == 0xFF00 && ram.U8(Vars.HackZoneFlag) == 0) O.Y &= 0x7FF;
        Display();
        if (IsSuper) throw new UnportedException(0x1E976, "Sonic_Super");
        RecordPos();
        Water();
        O.NextTiltCopy = ram.U8(Vars.PrimaryAngle);
        O.TiltCopy = ram.U8(Vars.SecondaryAngle);
        if (ram.Bit(Vars.WindTunnelFlag, 0) && O.Anim == PlayerAnim.Walk) O.Anim = O.PrevAnim;
        anim.Animate(O);
        if ((sbyte)O.ObjControl >= 0) _touch.Run(O);
        LoadDynPlc();
    }

    /// <summary>LoadSonicDynPLC (0x1F65E): when the mapping frame changes, copy its tiles to VRAM tile $780.</summary>
    private void LoadDynPlc()
    {
        int frame = O.MappingFrame;
        if (frame == ram.U8(Vars.SonicLastLoadedDplc)) return;
        ram.W8(Vars.SonicLastLoadedDplc, frame);
        if (Sprites?.Dplc is not { } dplc || Vram is null) return;
        int dest = 0xF000;
        foreach (var (first, count) in dplc[frame])
        {
            Sprites.Art.AsSpan(first * 32, count * 32).CopyTo(Vram.AsSpan(dest));
            dest += count * 32;
        }
    }

    /// <summary>Obj01_Dead (0x1EFB4): fall off the bottom of the screen, then lose a life.</summary>
    private void Dead()
    {
        if (ram.U16(Vars.DebugModeFlag) != 0 && (ram.U8(Vars.Ctrl1Press) & 0x10) != 0)
            throw new UnportedException(0x1EFC2, "debug mode");
        GameOverCheck();
        ObjectMoveAndFall();
        RecordPos();
        anim.Animate(O);
        LoadDynPlc();
        SpriteBuilder.DisplaySprite(ram, O);
    }

    /// <summary>
    /// CheckGameOver (0x1EFEA): once Sonic is $100 below the camera's bottom limit, take a life and wait 60 frames
    /// (routine 8) before the level restarts. With no lives left the game-over objects take over (not ported).
    /// </summary>
    private void GameOverCheck()
    {
        ram.W8(Vars.ScrollLock, 1);   // the camera stops following
        O.SpindashFlag = 0;
        int limit = (short)(ram.U16(Vars.CameraMaxYPos) + 0x100);
        if (limit >= (short)O.Y) return;
        O.Routine = PlayerRoutine.Gone;
        RestartTimer = 0x3C;
        if (ram.U8(Vars.TimedMode) != 0) throw new UnportedException(0x1F062, "death in the hack's timed mode");
        ram.W8(Vars.UpdateHudLives, ram.U8(Vars.UpdateHudLives) + 1);
        ram.W8(Vars.LifeCount, ram.U8(Vars.LifeCount) - 1);
        if (ram.U8(Vars.LifeCount) == 0)
        {
            // 0x1F022: no restart; the game-over objects take over.
            RestartTimer = 0;
            // "GAME" and "OVER" (object $39) in the title card's first two slots.
            var game = new SonicObject(ram, Vars.TitleCardZoneName);
            var over = new SonicObject(ram, Vars.TitleCardZone);
            game.Id = GameOverId;
            over.Id = GameOverId;
            over.MappingFrame = 1;
            game.SetWord(0x3E, O.Address);
            ram.W8(Vars.TimeOverFlag, 0);
            ram.W8(Vars.UpdateHudTimer, 0);
            ram.W8(Vars.UpdateHudTimerP2, 0);
            O.Routine = PlayerRoutine.Gone;
            sound.PlayMusic(MusicGameOver);
            LoadPlc?.Invoke(3);
            return;
        }
        if (ram.U8(Vars.TimeOverFlag) != 0) throw new UnportedException(0x1F068, "time over");
        if (ram.U16(Vars.TwoPlayerMode) != 0) throw new UnportedException(0x1F0A6, "death in 2-player mode");
    }

    /// <summary>Obj01_Gone (0x1F104): count down, then request the level restart ($FE02).</summary>
    private void Gone()
    {
        int t = RestartTimer;
        if (t == 0) return;
        RestartTimer = t - 1;
        if (t - 1 == 0) ram.W16(Vars.LevelInactiveFlag, 1);
    }

    /// <summary>Obj01_Hurt (0x1EEEE): knocked back, until landing.</summary>
    private void Hurt()
    {
        if (ram.U16(Vars.DebugModeFlag) != 0 && (ram.U8(Vars.Ctrl1Press) & 0x10) != 0)
            throw new UnportedException(0x1EEFC, "debug mode");
        if ((sbyte)O.RoutineSecondary < 0)
        {
            // 0x1EF98: back to normal control.
            O.Routine = PlayerRoutine.Control;
            O.RoutineSecondary = 0;
            RecordPos();
            anim.Animate(O);
            LoadDynPlc();
            SpriteBuilder.DisplaySprite(ram, O);
            return;
        }
        ObjectMove();
        O.YVel = (short)(O.YVel + 0x30);
        if (O.Underwater) O.YVel = (short)(O.YVel - 0x20);
        if (ram.U16(Vars.CameraMinYPos) == 0xFF00) O.Y &= 0x7FF;
        HurtStop();
        LevelBound();
        RecordPos();
        anim.Animate(O);
        LoadDynPlc();
        SpriteBuilder.DisplaySprite(ram, O);
    }

    /// <summary>Sonic_HurtStop (0x1EF52): land from the knockback.</summary>
    private void HurtStop()
    {
        int bottom = (short)(ram.U16(P.CamMaxY) + 0xE0);
        if (bottom < (short)O.Y) { FellOut(); return; }
        DoLevelCollision();
        if (O.InAir) return;
        O.YVel = 0;
        O.XVel = 0;
        O.Inertia = 0;
        O.ObjControl = 0;
        O.Anim = PlayerAnim.Walk;
        O.Routine = PlayerRoutine.Control;
        O.InvulnerableTime = 0x78;
        O.SpindashFlag = 0;
    }

    /// <summary>Sonic_Display (0x1DE46): invulnerability/invincibility/speed-shoe timers (drawing is separate).</summary>
    private void Display()
    {
        int timer = O.InvulnerableTime;
        if (timer != 0) O.InvulnerableTime--;
        if (timer == 0 || ((timer >> 2) & 1) != 0) SpriteBuilder.DisplaySprite(ram, O);   // blinks while invulnerable
        if (O.Invincible && O.InvincibilityTime != 0 && --O.InvincibilityTime == 0)
        {
            if (ram.U8(Vars.CurrentBossId) == 0 && O.AirLeft >= 0x0C) sound.PlayMusic(ram.U16(Vars.LevelMusic));
            O.Invincible = false;
        }
        if (O.HasSpeedShoes && O.SpeedShoesTime != 0 && --O.SpeedShoesTime == 0)
        {
            SetNormalSpeeds();
            O.HasSpeedShoes = false;
            sound.PlayMusic(Music.SlowDown);   // speed shoes over
        }
    }

    private void SetNormalSpeeds()
    {
        bool super = IsSuper;
        ram.W16(P.Speed, super ? 0xA00 : O.HasSpeedShoes ? 0xC00 : 0x600);
        ram.W16(P.Speed + 2, super ? 0x30 : O.HasSpeedShoes ? 0x18 : 0x0C);
        ram.W16(P.Speed + 4, super ? 0x100 : 0x80);
    }

    /// <summary>Sonic_Water (0x1DF06). Only the "stays above water" path is ported.</summary>
    private void Water()
    {
        if (WaterAt is not null)
        {
            SetWater(WaterAt(O.X, O.Y));
            return;
        }
        if (ram.U8(Vars.WaterFlag) == 0) return;
        if (ram.S16(Vars.WaterLevel1) >= (short)O.Y)
        {
            if (!ram.TestClear(O.StatusAddr, 6)) return;
            throw new UnportedException(0x1DF86, "leaving water");
        }

        throw new UnportedException(0x1DF18, "entering/under water");
    }

    private void SetWater(bool wet)
    {
        if (wet == O.Underwater) return;
        O.Underwater = wet;
        if (wet)
        {
            ram.W16(P.Speed, 0x300);
            ram.W16(P.Speed + 2, 6);
            ram.W16(P.Speed + 4, 0x40);
            O.XVel /= 2;
            O.YVel /= 2;
        }
        else
        {
            SetNormalSpeeds();
            if (O.YVel < 0) O.YVel = (short)Math.Max(-0x600, O.YVel * 2);
        }
    }
    /// <summary>Sonic_RecordPos (0x1DEDC): position/input history used by Tails.</summary>
    private void RecordPos()
    {
        int index = ram.U16(Vars.SonicPosRecordIndex);
        ram.W16(Vars.SonicPosRecordBuf + index, O.X);
        ram.W16(Vars.SonicPosRecordBuf + index + 2, O.Y);
        ram.W8(Vars.SonicPosRecordIndex + 1, ram.U8(Vars.SonicPosRecordIndex + 1) + 4);
        ram.W16(Vars.SonicStatRecordBuf + index, ram.U16(Vars.Ctrl1HeldLogical));
        ram.W16(Vars.SonicStatRecordBuf + index + 2, ram.U16(O.StatusAddr));
    }

    // =============================================================== ground

    /// <summary>Obj01_MdNormal_Checks (0x1DFEE): idle animations suppress physics until input.</summary>
    private void MdNormalChecks()
    {
        if (P.IsTails) { MdNormal(); return; }   // Tails has no idle-animation check
        if ((Pressed & ButtonAbc) == 0)
        {
            if (O.Anim is 0x0A or 0x0B) return;
            if (O.Anim == 5 && O.AnimFrame >= 0x1E)
            {
                if ((Held & 0x7F) == 0) return;
                O.Anim = PlayerAnim.Blink;
                if (O.AnimFrame >= 0xAC) O.Anim = PlayerAnim.GetUp;
                return;
            }
        }
        MdNormal();
    }

    /// <summary>Obj01_MdNormal (0x1E038).</summary>
    private void MdNormal()
    {
        if (CheckSpindash()) return;
        if (Jump()) return;
        SlopeResist();
        Move();
        Roll();
        LevelBound();
        ObjectMove();
        AnglePos();
        SlopeRepel();
    }

    /// <summary>Obj01_MdRoll (0x1E08A).</summary>
    private void MdRoll()
    {
        if (O.SpindashFlag == 0 && Jump()) return;
        RollRepel();
        RollSpeed();
        LevelBound();
        ObjectMove();
        AnglePos();
        SlopeRepel();
    }

    /// <summary>Obj01_MdAir / MdJump (0x1E060 / 0x1E0B0).</summary>
    private void MdAir()
    {
        if (WaterAt is not null && O.Underwater)
        {
            if ((Pressed & ButtonAbc) != 0)
            {
                O.YVel = -0x280;
                O.Jumping = 0;
                O.RollJumping = false;
                sound.PlaySound(Sfx.Jump);
            }
            O.YVel = Math.Min(O.YVel, (short)0x180);
        }
        else JumpHeight();
        ChgJumpDir();
        LevelBound();
        ObjectMoveAndFall();
        if (O.Underwater) O.YVel = (short)(O.YVel - 0x28);
        if (WaterAt is not null && O.Underwater) O.YVel = Math.Min(O.YVel, (short)0x180);
        JumpAngle();
        DoLevelCollision();
    }

    /// <summary>Sonic_Move (0x1E0DA).</summary>
    private void Move()
    {
        int top = ram.S16(P.Speed), acc = ram.S16(P.Speed + 2), dec = ram.S16(P.Speed + 4);
        if ((sbyte)O.StatusSecondary < 0) { ApplyGroundVelocity(); return; }
        if (O.MoveLock != 0) { LookTimerReset(); FrictionAndVelocity(acc); return; }
        if (HeldBit(Left)) MoveLeft(top, acc, dec);
        if (HeldBit(Right)) MoveRight(top, acc, dec);
        if (((O.Angle + 0x20) & 0xC0) != 0 || O.Inertia != 0) { LookTimerReset(); FrictionAndVelocity(acc); return; }

        // Standing still: balancing and looking (0x1E126).
        O.Pushing = false;
        O.Anim = PlayerAnim.Wait;
        bool balancing = P.IsTails ? TailsBalance() : O.OnObject ? BalanceOnObject() : BalanceOnLedge();
        if (!balancing) { LookUpDown(acc); return; }
        LookTimerReset();
        FrictionAndVelocity(acc);
    }

    /// <summary>0x1E13C: balancing on the edge of an object. Returns true if a balance animation was chosen.</summary>
    private bool BalanceOnObject()
    {
        var obj = O.InteractObject;
        if ((sbyte)obj.Status < 0) return false;
        int d1 = obj.WidthPixels;
        int d2 = d1 * 2 - 2;
        d1 = (short)(d1 + O.X - obj.X);
        if (IsSuper)
        {
            if (d1 < 2) { O.FacingLeft = true; O.Anim = PlayerAnim.Balance; return true; }
            if (d1 >= d2) { O.FacingLeft = false; O.Anim = PlayerAnim.Balance; return true; }
            return false;
        }
        if (d1 < 2)
        {
            if (O.FacingLeft) { O.Anim = PlayerAnim.Balance; if (d1 < -4) O.Anim = PlayerAnim.Balance2; }
            else { O.Anim = PlayerAnim.Balance3; if (d1 < -4) { O.Anim = PlayerAnim.Balance4; O.FacingLeft = true; } }
            return true;
        }
        if (d1 >= d2)
        {
            if (!O.FacingLeft) { O.Anim = PlayerAnim.Balance; if (d1 >= d2 + 6) O.Anim = PlayerAnim.Balance2; }
            else { O.Anim = PlayerAnim.Balance3; if (d1 >= d2 + 6) { O.Anim = PlayerAnim.Balance4; O.FacingLeft = false; } }
            return true;
        }
        return false;
    }

    /// <summary>0x1E20C: balancing on a ledge, using the tilt copies at $36/$37 and ChkFloorEdge.</summary>
    private bool BalanceOnLedge()
    {
        if (col.FloorEdge(O, O.X).Distance < 0x0C) return false;
        if (IsSuper)
        {
            if (O.NextTiltCopy == 3) { O.FacingLeft = false; O.Anim = PlayerAnim.Balance; return true; }
            if (O.TiltCopy == 3) { O.FacingLeft = true; O.Anim = PlayerAnim.Balance; return true; }
            return false;
        }
        if (O.NextTiltCopy == 3)
        {
            if (!O.FacingLeft)
            {
                O.Anim = PlayerAnim.Balance;
                if (col.FloorEdge(O, O.X - 6).Distance >= 0x0C) O.Anim = PlayerAnim.Balance2;
            }
            else
            {
                O.Anim = PlayerAnim.Balance3;
                if (col.FloorEdge(O, O.X - 6).Distance >= 0x0C) { O.Anim = PlayerAnim.Balance4; O.FacingLeft = false; }
            }
            return true;
        }
        if (O.TiltCopy == 3)
        {
            if (O.FacingLeft)
            {
                O.Anim = PlayerAnim.Balance;
                if (col.FloorEdge(O, O.X + 6).Distance >= 0x0C) O.Anim = PlayerAnim.Balance2;
            }
            else
            {
                O.Anim = PlayerAnim.Balance3;
                if (col.FloorEdge(O, O.X + 6).Distance >= 0x0C) { O.Anim = PlayerAnim.Balance4; O.FacingLeft = true; }
            }
            return true;
        }
        return false;
    }

    /// <summary>0x1E304: looking up / ducking, which moves the camera bias after a delay.</summary>
    private void LookUpDown(int acc)
    {
        int delay = P.LookDelay, bias = P.CameraBias;
        if (HeldBit(Up))
        {
            O.Anim = PlayerAnim.LookUp;
            ram.W16(delay, ram.U16(delay) + 1);
            if (ram.U16(delay) < 0x78) { BiasTowardDefault(); FrictionAndVelocity(acc); return; }
            ram.W16(delay, 0x78);
            if (ram.U16(bias) != 0xC8) ram.W16(bias, ram.U16(bias) + 2);
            FrictionAndVelocity(acc);
            return;
        }
        if (HeldBit(Down))
        {
            O.Anim = PlayerAnim.Duck;
            ram.W16(delay, ram.U16(delay) + 1);
            if (ram.U16(delay) < 0x78) { BiasTowardDefault(); FrictionAndVelocity(acc); return; }
            ram.W16(delay, 0x78);
            if (ram.U16(bias) != 8) ram.W16(bias, ram.U16(bias) - 2);
            FrictionAndVelocity(acc);
            return;
        }
        LookTimerReset();
        FrictionAndVelocity(acc);
    }

    /// <summary>0x1E360 / 0x1E366: clear the look delay and ease the camera bias back to $60.</summary>
    private void LookTimerReset()
    {
        ram.W16(P.LookDelay, 0);
        BiasTowardDefault();
    }

    private void BiasTowardDefault()
    {
        int bias = ram.U16(P.CameraBias);
        if (bias == 0x60) return;
        if (bias < 0x60) bias += 4;
        ram.W16(P.CameraBias, bias - 2);
    }

    /// <summary>0x1E378: friction when no direction is held, then 0x1E3B0.</summary>
    private void FrictionAndVelocity(int acc)
    {
        int d5 = IsSuper ? 0x0C : acc;
        if ((Held & 0x0C) == 0)
        {
            int d0 = O.Inertia & 0xFFFF;
            if (d0 != 0)
            {
                if ((short)d0 > 0) { d0 = d0 < d5 ? 0 : d0 - d5; }
                else { int sum = d0 + d5; d0 = sum > 0xFFFF ? 0 : sum; }
                O.Inertia = (short)d0;
            }
        }
        ApplyGroundVelocity();
    }

    /// <summary>0x1E3B0: velocity from inertia and angle, then the wall check in the direction of travel (0x1E3CE).</summary>
    private void ApplyGroundVelocity()
    {
        var (sin, cos) = MdMath.CalcSine(O.Angle);
        O.XVel = (short)((cos * O.Inertia) >> 8);
        O.YVel = (short)((sin * O.Inertia) >> 8);
        WallCheckAhead();
    }

    /// <summary>0x1E3CE: stop against walls in the direction of travel.</summary>
    private void WallCheckAhead()
    {
        if ((sbyte)(O.Angle + 0x40) < 0) return;
        if (O.Inertia == 0) return;
        int d1 = O.Inertia > 0 ? -0x40 : 0x40;
        int d0 = (O.Angle + d1) & 0xFF;
        var (dist, _) = col.CalcRoomInFront(O, d0);
        if (dist >= 0) return;
        int push = (short)(dist << 8);
        switch ((d0 + 0x20) & 0xC0)
        {
            case 0x00: O.YVel = (short)(O.YVel + push); break;
            case 0x40: O.XVel = (short)(O.XVel - push); O.Pushing = true; O.Inertia = 0; break;
            case 0x80: O.YVel = (short)(O.YVel - push); break;
            default: O.XVel = (short)(O.XVel + push); O.Pushing = true; O.Inertia = 0; break;
        }
    }

    /// <summary>Sonic_MoveLeft (0x1E440).</summary>
    private void MoveLeft(int top, int acc, int dec)
    {
        int d0 = O.Inertia;
        if (d0 <= 0)
        {
            if (!ram.TestSet(O.StatusAddr, 0)) { O.Pushing = false; O.PrevAnim = 1; }
            d0 -= acc;
            int d1 = -top;
            if (d0 <= d1)
            {
                d0 += acc;
                if (d0 > d1) d0 = d1;
            }
            O.Inertia = (short)d0;
            O.Anim = PlayerAnim.Walk;
            return;
        }
        int u = (d0 & 0xFFFF) - (dec & 0xFFFF);
        d0 = u < 0 ? -0x80 : u;            // sub.w d4,d0 / bcc
        O.Inertia = (short)d0;
        if (((O.Angle + 0x20) & 0xC0) != 0) return;
        if ((short)(d0 & 0xFF00) < 0x400) return;  // cmpi.w #$400 sees inertia's high byte (low byte replaced by angle)
        Skid(faceLeft: false);
    }

    /// <summary>Sonic_MoveRight (0x1E4C6).</summary>
    private void MoveRight(int top, int acc, int dec)
    {
        int d0 = O.Inertia;
        if (d0 >= 0)
        {
            if (ram.TestClear(O.StatusAddr, 0)) { O.Pushing = false; O.PrevAnim = 1; }
            d0 += acc;
            if (d0 >= top)
            {
                d0 -= acc;
                if (d0 < top) d0 = top;
            }
            O.Inertia = (short)d0;
            O.Anim = PlayerAnim.Walk;
            return;
        }
        int u = (d0 & 0xFFFF) + (dec & 0xFFFF);
        d0 = u > 0xFFFF ? 0x80 : u;        // add.w d4,d0 / bcc
        O.Inertia = (short)d0;
        if (((O.Angle + 0x20) & 0xC0) != 0) return;
        if ((short)(d0 & 0xFF00) > unchecked((short)0xFC00)) return;
        Skid(faceLeft: true);
    }

    private void Skid(bool faceLeft)
    {
        O.Anim = PlayerAnim.Stop;
        if (faceLeft) O.FacingLeft = true; else O.FacingLeft = false;
        sound.PlaySound(Sfx.Skidding);
        if (O.AirLeft >= 0x0C)
        {
            ram.W8(P.Dust + 0x24, 6);      // skid dust: dust object routine and frame
            ram.W8(P.Dust + 0x1A, 0x15);
        }
    }

    /// <summary>Sonic_RollSpeed (0x1E546).</summary>
    private void RollSpeed()
    {
        int d6 = (short)(ram.S16(P.Speed) << 1);
        int d5 = ram.S16(P.Speed + 2) >> 1;
        int d4 = P.IsTails ? ram.S16(P.Speed + 4) >> 2 : 0x20;   // Tails: deceleration / 4
        if ((sbyte)O.StatusSecondary >= 0)
        {
            if (O.MoveLock == 0)
            {
                if (HeldBit(Left)) RollLeft(d4);
                if (HeldBit(Right)) RollRight(d4);
            }
            int d0 = O.Inertia & 0xFFFF;
            if (d0 != 0)
            {
                if ((short)d0 > 0) d0 = d0 < d5 ? 0 : d0 - d5;
                else { int sum = d0 + d5; d0 = sum > 0xFFFF ? 0 : sum; }
                O.Inertia = (short)d0;
            }
            if (O.Inertia == 0)
            {
                if (O.SpindashFlag == 0)
                {
                    O.Rolling = false;
                    O.YRadius = (sbyte)P.StandYRadius;
                    O.XRadius = 9;
                    O.Anim = PlayerAnim.Wait;
                    O.Y -= P.RollShift;
                }
                else
                {
                    O.Inertia = 0x400;
                    if (O.FacingLeft) O.Inertia = (short)-O.Inertia;
                }
            }
        }
        BiasTowardDefault();
        var (sin, cos) = MdMath.CalcSine(O.Angle);
        O.YVel = (short)((sin * O.Inertia) >> 8);
        int d1 = (cos * O.Inertia) >> 8;
        if (d1 > 0x1000) d1 = 0x1000;
        if (d1 < -0x1000) d1 = -0x1000;
        O.XVel = (short)d1;
        WallCheckAhead();
        _ = d6;   // computed by the original but unused here
    }

    /// <summary>Sonic_RollLeft (0x1E622).</summary>
    private void RollLeft(int d4)
    {
        int d0 = O.Inertia;
        if (d0 <= 0) { O.FacingLeft = true; O.Anim = PlayerAnim.Roll; return; }
        int u = (d0 & 0xFFFF) - d4;
        O.Inertia = (short)(u < 0 ? -0x80 : u);
    }

    /// <summary>Sonic_RollRight (0x1E646).</summary>
    private void RollRight(int d4)
    {
        int d0 = O.Inertia;
        if (d0 >= 0) { O.FacingLeft = false; O.Anim = PlayerAnim.Roll; return; }
        int u = (d0 & 0xFFFF) + d4;
        O.Inertia = (short)(u > 0xFFFF ? 0x80 : u);
    }

    /// <summary>Sonic_Roll (0x1E774).</summary>
    private void Roll()
    {
        if ((sbyte)O.StatusSecondary < 0) return;
        int speed = Math.Abs((int)O.Inertia) & 0xFFFF;
        if (speed < 0x80) return;
        if ((Held & 0x0C) != 0) return;
        if (!HeldBit(Down)) return;
        if (O.Rolling) return;
        O.Rolling = true;
        O.YRadius = 0x0E;
        O.XRadius = 7;
        O.Anim = PlayerAnim.Roll;
        O.Y += P.RollShift;
        sound.PlaySound(Sfx.Roll);
        if (O.Inertia == 0) O.Inertia = 0x200;
    }

    /// <summary>Sonic_Jump (0x1E7DA). Returns true if Sonic jumped (the original pops its caller's return address).</summary>
    private bool Jump()
    {
        if ((Pressed & ButtonAbc) == 0) return false;
        if (col.CalcRoomOverHead(O, (O.Angle + 0x80) & 0xFF) < 6) return false;
        int d2 = IsSuper ? 0x800 : 0x680;
        if (O.Underwater) d2 = 0x380;
        var (sin, cos) = MdMath.CalcSine((O.Angle - 0x40) & 0xFF);
        O.XVel = (short)(O.XVel + ((cos * d2) >> 8));
        O.YVel = (short)(O.YVel + ((sin * d2) >> 8));
        O.InAir = true;
        O.Pushing = false;
        O.Jumping = 1;
        O.StickToConvex = 0;
        sound.PlaySound(Sfx.Jump);
        O.YRadius = (sbyte)P.StandYRadius;
        O.XRadius = 9;
        if (O.Rolling) { O.RollJumping = true; return true; }
        O.YRadius = 0x0E;
        O.XRadius = 7;
        O.Anim = PlayerAnim.Roll;
        O.Rolling = true;
        O.Y += P.RollShift;
        return true;
    }

    /// <summary>Sonic_CheckSpindash / Sonic_UpdateSpindash (0x1EA0E). Returns true when it ends the frame's mode code.</summary>
    private bool CheckSpindash()
    {
        if (O.SpindashFlag == 0)
        {
            if (O.Anim != PlayerAnim.Duck) return false;
            if ((Pressed & ButtonAbc) == 0) return false;
            O.Anim = PlayerAnim.Spindash;
            sound.PlaySound(Sfx.SpindashRev);
            O.SpindashFlag = 1;
            O.SpindashCounter = 0;
            if (O.AirLeft >= 0x0C) ram.W8(P.Dust + 0x1C, 2);
            LevelBound();
            AnglePos();
            return true;
        }
        if (!HeldBit(Down))
        {
            // Release (0x1EA6A).
            O.YRadius = 0x0E;
            O.XRadius = 7;
            O.Anim = PlayerAnim.Roll;
            O.Y += P.RollShift;
            O.SpindashFlag = 0;
            int index = (O.SpindashCounter >> 8) * 2;   // move.b $3A: high byte of the counter
            O.Inertia = (short)SpindashSpeeds[(IsSuper ? 9 : 0) + index / 2];
            int d0 = O.Inertia - 0x800;
            d0 = (d0 * 2) & 0x1F00;
            d0 = -d0 + 0x2000;
            ram.W16(P.ScrollDelay, d0);                  // horizontal scroll delay
            if (O.FacingLeft) O.Inertia = (short)-O.Inertia;
            O.Rolling = true;
            ram.W8(P.Dust + 0x1C, 0);
            sound.PlaySound(Sfx.SpindashRelease);
        }
        else
        {
            // Charging (0x1EB00).
            if (O.SpindashCounter != 0)
            {
                int c = O.SpindashCounter;
                int u = c - (c >> 5);
                O.SpindashCounter = (ushort)(u < 0 ? 0 : u);
            }
            if ((Pressed & ButtonAbc) != 0)
            {
                O.Anim = PlayerAnim.Spindash;   // restart the rev animation
                O.PrevAnim = 0;
                sound.PlaySound(Sfx.SpindashRev);
                int c = O.SpindashCounter + 0x200;
                O.SpindashCounter = (ushort)(c >= 0x800 ? 0x800 : c);
            }
        }
        // 0x1EB48: common tail (the original pops its caller's return address here).
        BiasTowardDefault();
        LevelBound();
        AnglePos();
        return true;
    }

    /// <summary>Spindash release speeds (0x1EADC normal, 0x1EAEE super), indexed by charge level 0-8.</summary>
    private static readonly int[] SpindashSpeeds =
    [
        0x800, 0x880, 0x900, 0x980, 0xA00, 0xA80, 0xB00, 0xB80, 0xC00,
        0xB00, 0xB80, 0xC00, 0xC80, 0xD00, 0xD80, 0xE00, 0xE80, 0xF00,
    ];

    /// <summary>Sonic_SlopeResist (0x1EB66).</summary>
    private void SlopeResist()
    {
        if ((byte)(O.Angle + 0x60) >= 0xC0) return;
        int d0 = (MdMath.CalcSine(O.Angle).Sin * 0x20) >> 8;
        if (O.Inertia == 0) return;
        if (O.Inertia > 0 && d0 == 0) return;
        O.Inertia = (short)(O.Inertia + d0);
    }

    /// <summary>Sonic_RollRepel (0x1EB9C).</summary>
    private void RollRepel()
    {
        if ((byte)(O.Angle + 0x60) >= 0xC0) return;
        int d0 = (MdMath.CalcSine(O.Angle).Sin * 0x50) >> 8;
        if (O.Inertia >= 0) { if (d0 < 0) d0 >>= 2; }
        else { if (d0 >= 0) d0 >>= 2; }
        O.Inertia = (short)(O.Inertia + d0);
    }

    /// <summary>Sonic_SlopeRepel (0x1EBD8): fall off steep slopes when too slow.</summary>
    private void SlopeRepel()
    {
        if (O.StickToConvex != 0) return;
        if (O.MoveLock != 0) { O.MoveLock--; return; }
        if (((O.Angle + 0x20) & 0xC0) == 0) return;
        if ((Math.Abs((int)O.Inertia) & 0xFFFF) >= 0x280) return;
        O.Inertia = 0;
        O.InAir = true;
        O.MoveLock = 0x1E;
    }

    /// <summary>Sonic_LevelBound (0x1E6F4): keep Sonic inside the camera's horizontal limits.</summary>
    private void LevelBound()
    {
        int nextX = (int)((O.XLong + ((long)O.XVel << 8)) >> 16) & 0xFFFF;
        int left = (ram.U16(P.CamMinX) + 0x10) & 0xFFFF;
        int right = (ram.U16(P.CamMaxX) + 0x128 + (ram.U8(Vars.CurrentBossId) == 0 ? 0x40 : 0)) & 0xFFFF;
        if (left > nextX) StopAt(left);
        else if (right <= nextX) StopAt(right);

        int bottom = (short)(ram.U16(P.CamMaxY) + 0xE0);
        if (bottom < (short)O.Y)
        {
            if (ram.U16(Vars.CurrentZoneAndAct) == 0x1B01 && O.X >= 0x2000)
                throw new UnportedException(0x1E74A, "act transition below the bottom boundary");
            FellOut();
        }
    }

    private void StopAt(int x)
    {
        O.X = x;
        O.XSub = 0;
        O.XVel = 0;
        O.Inertia = 0;
    }

    /// <summary>ObjectMove (0x1A0F2).</summary>
    private void ObjectMove()
    {
        O.XLong = (uint)(O.XLong + ((long)O.XVel << 8));
        O.YLong = (uint)(O.YLong + ((long)O.YVel << 8));
    }

    /// <summary>ObjectMoveAndFall (0x1A0C6): gravity is added after the old y velocity is used.</summary>
    private void ObjectMoveAndFall()
    {
        short yv = O.YVel;
        O.YVel = (short)(yv + 0x38);
        O.XLong = (uint)(O.XLong + ((long)O.XVel << 8));
        O.YLong = (uint)(O.YLong + ((long)yv << 8));
    }

    // =============================================================== air

    /// <summary>Sonic_JumpHeight (0x1E892).</summary>
    private void JumpHeight()
    {
        if (O.Jumping != 0)
        {
            int cap = O.Underwater ? -0x200 : -0x400;
            if (cap > O.YVel && (Held & ButtonAbc) == 0) O.YVel = (short)cap;
            if (!P.IsTails && (O.YVel >> 8) == 0) CheckSuperTransform();
            return;
        }
        if (O.SpindashFlag == 0 && O.YVel < -0xFC0) O.YVel = -0xFC0;
    }

    /// <summary>0x1E8DA: the transform conditions; the transformation itself is not ported.</summary>
    private void CheckSuperTransform()
    {
        if (IsSuper) return;
        if (ram.U8(Vars.EmeraldCount) != 0x0D) return;
        if (ram.U16(Vars.RingCount) < 0x32) return;
        if (ram.U8(Vars.TimedMode) != 0) return;
        if (ram.U8(Vars.UpdateHudTimer) == 0) return;
        throw new UnportedException(0x1E904, "Super Sonic transformation");
    }

    /// <summary>Sonic_ChgJumpDir (0x1E668): air control and air drag.</summary>
    private void ChgJumpDir()
    {
        int top = ram.S16(P.Speed);
        int acc = ram.S16(P.Speed + 2) * 2;
        if (!O.RollJumping)
        {
            int d0 = O.XVel;
            if (HeldBit(Left))
            {
                O.FacingLeft = true;
                d0 -= acc;
                if (d0 <= -top) d0 = -top;
            }
            if (HeldBit(Right))
            {
                O.FacingLeft = false;
                d0 += acc;
                if (d0 >= top) d0 = top;
            }
            O.XVel = (short)d0;
        }
        BiasTowardDefault();
        if ((O.YVel & 0xFFFF) < 0xFC00) return;   // only while rising slower than $400
        int x = O.XVel & 0xFFFF;
        int drag = (short)x >> 5;
        if (drag == 0) return;
        int u = x - (drag & 0xFFFF);
        if (drag > 0) O.XVel = (short)(u < 0 ? 0 : u);
        else O.XVel = (short)(u < 0 ? u : 0);
    }

    /// <summary>Sonic_JumpAngle (0x1EC1A): return the angle to 0 and advance flips.</summary>
    private void JumpAngle()
    {
        int a = O.Angle;
        if (a != 0)
        {
            if ((sbyte)a < 0) { a += 2; if (a > 0xFF) a = 0; }
            else { a -= 2; if (a < 0) a = 0; }
            O.Angle = (byte)a;
        }
        int d0 = O.FlipAngle;
        if (d0 == 0) return;
        bool forward = O.Inertia >= 0 || O.FlipTurned != 0;
        int speed = O.FlipSpeed;
        if (forward)
        {
            d0 += speed;
            if (d0 > 0xFF) { d0 &= 0xFF; if (O.FlipsRemaining-- == 0) { O.FlipsRemaining = 0; d0 = 0; } }
        }
        else
        {
            d0 -= speed;
            if (d0 < 0) { d0 &= 0xFF; if (O.FlipsRemaining-- == 0) { O.FlipsRemaining = 0; d0 = 0; } }
        }
        O.FlipAngle = (byte)d0;
    }

    /// <summary>Sonic_DoLevelCollision (0x1EC7A).</summary>
    private void DoLevelCollision()
    {
        col.SelectLayer(O);
        int solid = O.LrbSolidBit;
        int dir = (MdMath.CalcAngle(O.XVel, O.YVel) - 0x20) & 0xC0;
        switch (dir)
        {
            case 0x40: AirMovingLeft(solid); return;
            case 0x80: AirMovingUp(solid); return;
            case 0xC0: AirMovingRight(solid); return;
        }

        // Moving down (0x1ECC4).
        var w = col.LeftWallDist(O.Y, O.X, solid);
        if (w.Distance < 0) { O.X -= w.Distance; O.XVel = 0; }
        w = col.RightWallDist(O.Y, O.X, solid);
        if (w.Distance < 0) { O.X += w.Distance; O.XVel = 0; }
        var f = col.CheckFloor(O);
        if (f.Distance >= 0) return;
        sbyte d2 = (sbyte)-(sbyte)((O.YVel >> 8) + 8);
        if ((sbyte)f.Distance < d2 && (sbyte)f.Other < d2) return;
        O.Y += f.Distance;
        O.Angle = f.Angle;
        ResetOnFloor();
        if (((f.Angle + 0x20) & 0x40) != 0)
        {
            O.XVel = 0;
            if (O.YVel > 0xFC0) O.YVel = 0xFC0;
            InertiaFromYVel(f.Angle);
            return;
        }
        if (((f.Angle + 0x10) & 0x20) != 0)
        {
            O.YVel = (short)(O.YVel >> 1);
            InertiaFromYVel(f.Angle);
            return;
        }
        O.YVel = 0;
        O.Inertia = O.XVel;
    }

    private void InertiaFromYVel(byte angle)
    {
        O.Inertia = O.YVel;
        if ((sbyte)angle < 0) O.Inertia = (short)-O.Inertia;
    }

    /// <summary>0x1ED5C.</summary>
    private void AirMovingLeft(int solid)
    {
        var w = col.LeftWallDist(O.Y, O.X, solid);
        if (w.Distance < 0) { O.X -= w.Distance; O.XVel = 0; O.Inertia = O.YVel; return; }
        var c = col.CheckCeiling(O, solid);
        if (c.Distance < 0) { O.Y -= c.Distance; if (O.YVel < 0) O.YVel = 0; return; }
        if (O.YVel < 0) return;
        LandIfOnFloor();
    }

    /// <summary>0x1EE14.</summary>
    private void AirMovingRight(int solid)
    {
        var w = col.RightWallDist(O.Y, O.X, solid);
        if (w.Distance < 0) { O.X += w.Distance; O.XVel = 0; O.Inertia = O.YVel; return; }
        var c = col.CheckCeiling(O, solid);
        if (c.Distance < 0) { O.Y -= c.Distance; if (O.YVel < 0) O.YVel = 0; return; }
        if (O.YVel < 0) return;
        LandIfOnFloor();
    }

    private void LandIfOnFloor()
    {
        var f = col.CheckFloor(O);
        if (f.Distance >= 0) return;
        O.Y += f.Distance;
        O.Angle = f.Angle;
        ResetOnFloor();
        O.YVel = 0;
        O.Inertia = O.XVel;
    }

    /// <summary>0x1EDB8.</summary>
    private void AirMovingUp(int solid)
    {
        var w = col.LeftWallDist(O.Y, O.X, solid);
        if (w.Distance < 0) { O.X -= w.Distance; O.XVel = 0; }
        w = col.RightWallDist(O.Y, O.X, solid);
        if (w.Distance < 0) { O.X += w.Distance; O.XVel = 0; }
        var c = col.CheckCeiling(O, solid);
        if (c.Distance >= 0) return;
        O.Y -= c.Distance;
        if (((c.Angle + 0x20) & 0x40) == 0) { O.YVel = 0; return; }
        O.Angle = c.Angle;
        ResetOnFloor();
        InertiaFromYVel(c.Angle);
    }

    /// <summary>Sonic_ResetOnFloor (0x1EE70).</summary>
    private void ResetOnFloor()
    {
        if (O.SpindashFlag == 0)
        {
            O.Anim = PlayerAnim.Walk;
            ResetOnFloorPart2(ram, O);
            return;
        }
        ClearLandingState(ram, O);
    }

    /// <summary>Sonic_ResetOnFloor_Part2 (0x1EE7C), also used by HurtCharacter/KillCharacter.</summary>
    public static void ResetOnFloorPart2(Ram ram, SonicObject o)
    {
        bool tails = o.Id != 1;   // 0x1EE7C: anything but Sonic takes Tails' copy (0x209BC)
        if (o.Rolling)
        {
            o.Rolling = false;
            o.YRadius = (sbyte)(tails ? 0x0F : 0x13);
            o.XRadius = 9;
            o.Anim = PlayerAnim.Walk;
            o.Y -= tails ? 1 : 5;
        }
        ClearLandingState(ram, o);
    }

    /// <summary>0x1EEA8: clear air/push/roll-jump state on landing.</summary>
    private static void ClearLandingState(Ram ram, SonicObject o)
    {
        o.InAir = false;
        o.Pushing = false;
        o.RollJumping = false;
        o.Jumping = 0;
        ram.W16(Vars.ChainBonusCounter, 0);
        o.FlipAngle = 0;
        o.FlipTurned = 0;
        o.FlipsRemaining = 0;
        ram.W16(o.Id != 1 ? PlayerProfile.Tails.LookDelay : PlayerProfile.Sonic.LookDelay, 0);
        if (o.Anim == PlayerAnim.Hang2) o.Anim = PlayerAnim.Walk;
    }

    // =============================================================== ground collision

    /// <summary>AnglePos (0x220B4): keep Sonic on the surface for his current quadrant.</summary>
    private void AnglePos()
    {
        col.SelectLayer(O);
        int solid = O.TopSolidBit;
        if (O.OnObject)
        {
            ram.W8(Vars.PrimaryAngle, 0);
            ram.W8(Vars.SecondaryAngle, 0);
            return;
        }
        ram.W8(Vars.PrimaryAngle, 3);
        ram.W8(Vars.SecondaryAngle, 3);
        switch (Collision.RoundedQuadrant(O.Angle))
        {
            case 0x00:
                {
                    int y = O.Y + O.YRadius;
                    int d0 = col.FindFloor(y, O.X + O.XRadius, 0x10, solid, 0, Vars.PrimaryAngle);
                    int d1 = col.FindFloor(y, O.X - O.XRadius, 0x10, solid, 0, Vars.SecondaryAngle);
                    d1 = PickAngle(d0, d1);
                    StickOrFall(d1, O.XVel, v => O.Y += v);
                    break;
                }
            case 0x80:
                {
                    int y = (O.Y - O.YRadius) ^ 0xF;
                    int d0 = col.FindFloor(y, O.X + O.XRadius, -0x10, solid, 0x800, Vars.PrimaryAngle);
                    int d1 = col.FindFloor(y, O.X - O.XRadius, -0x10, solid, 0x800, Vars.SecondaryAngle);
                    d1 = PickAngle(d0, d1);
                    StickOrFall(d1, O.XVel, v => O.Y -= v);
                    break;
                }
            case 0xC0:
                {
                    int x = O.X + O.YRadius;
                    int d0 = col.FindWall(O.Y - O.XRadius, x, 0x10, solid, 0, Vars.PrimaryAngle);
                    int d1 = col.FindWall(O.Y + O.XRadius, x, 0x10, solid, 0, Vars.SecondaryAngle);
                    d1 = PickAngle(d0, d1);
                    StickOrFall(d1, O.YVel, v => O.X += v);
                    break;
                }
            default: // 0x40
                {
                    int x = (O.X - O.YRadius) ^ 0xF;
                    int d0 = col.FindWall(O.Y - O.XRadius, x, -0x10, solid, 0x400, Vars.PrimaryAngle);
                    int d1 = col.FindWall(O.Y + O.XRadius, x, -0x10, solid, 0x400, Vars.SecondaryAngle);
                    d1 = PickAngle(d0, d1);
                    StickOrFall(d1, O.YVel, v => O.X -= v);
                    break;
                }
        }
    }

    /// <summary>
    /// Shared tail of the four AnglePos quadrants: snap to the surface, or leave the ground when it falls away
    /// faster than the current speed allows (0x2218C).
    /// </summary>
    private void StickOrFall(int d1, short velocity, Action<int> move)
    {
        if (d1 == 0) return;
        if (d1 < 0)
        {
            if (d1 >= -0x0E) move(d1);
            return;
        }
        int d0 = (sbyte)(velocity >> 8);
        if (d0 < 0) d0 = -d0;
        d0 = (byte)(d0 + 4);
        if (d0 >= 0x0E) d0 = 0x0E;
        if ((sbyte)d1 > (sbyte)d0 && O.StickToConvex == 0)
        {
            O.InAir = true;
            O.Pushing = false;
            O.PrevAnim = 1;
            return;
        }
        move(d1);
    }

    /// <summary>Sonic_Angle (0x221D6): take the nearer probe and its angle, unless it differs too much.</summary>
    private int PickAngle(int d0, int d1)
    {
        byte d2 = ram.U8(Vars.SecondaryAngle);
        if (d1 > d0) { d2 = ram.U8(Vars.PrimaryAngle); d1 = d0; }
        if ((d2 & 1) == 0)
        {
            int diff = (sbyte)(d2 - O.Angle);
            if (diff < 0) diff = -diff;
            if ((diff & 0xFF) < 0x20) { O.Angle = d2; return d1; }
        }
        // SMB3's 45-degree slopes meet flat blocks directly; keep floor/ceiling ties consistent with AnglePos.
        O.Angle = (byte)(Camera.IsEasterEgg(ram.U8(Vars.CurrentZoneAndAct))
            ? Collision.RoundedQuadrant(O.Angle) : (O.Angle + 0x20) & 0xC0);
        return d1;
    }
}
