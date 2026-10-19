namespace SonicBros3.Logic;

/// <summary>
/// The star post (checkpoint), object $79 (ROM 0x22FAC). Touching a post numbered higher than the last one hit
/// saves the restart state (<see cref="Save"/>) and spins its ball (a second object with this id, routine
/// <see cref="SpinningBall"/>). Posts already passed show as lit.
/// Not ported: the special-stage star ring that appears with 50 or more rings (0x233C0). The special stage is outside
/// this port, so this is a deliberate difference from the original.
/// </summary>
public sealed class Starpost(ObjectContext ctx) : GameObject(ctx)
{
    public const int Id = 0x79;
    public const uint Mappings = 0x23320;
    private readonly byte[] _anims = ctx.Art["starpost"].Animations!;

    private const int Init = 0, Main = 2, Animating = 4, SpinningBall = 6;
    private const int AnimIdle = 0, AnimHit = 1, AnimLit = 2;
    private const int SpinFrames = 0x20;
    private const int SfxStarpost = 0xA1;

    /// <summary>The spinning ball's own variables.</summary>
    private readonly struct BallState(SonicObject o)
    {
        /// <summary>$30, $32: the centre it circles.</summary>
        public int CentreX { get => o.Word(0x30); set => o.SetWord(0x30, value); }
        public int CentreY { get => o.Word(0x32); set => o.SetWord(0x32, value); }
        /// <summary>$36: frames left to spin.</summary>
        public int Timer { get => o.Word(0x36); set => o.SetWord(0x36, value); }
        /// <summary>$3E: the post's slot.</summary>
        public int Post { get => o.Word(0x3E); set => o.SetWord(0x3E, value); }
    }

    public override void Run(SonicObject o)
    {
        switch (o.Routine)
        {
            case Init: Initialise(o); WaitForTouch(o); break;
            case Main: WaitForTouch(o); break;
            case Animating: AnimateAndDisplay(o); break;
            case SpinningBall: Spin(o, new BallState(o)); break;
            default: throw new UnportedException(0x2342E, "special-stage star ring");
        }
    }

    /// <summary>The post's number: subtype bits 0-6 (bit 7 locks the camera's left edge on restart).</summary>
    private static int Number(int subtype) => subtype & 0x7F;

    private void Initialise(SonicObject o)
    {
        o.Routine = Main;
        o.Mappings = Mappings;
        o.ArtTile = 0x47C;
        o.RenderFlags = Render.LevelCoords;
        o.WidthPixels = 8;
        o.Priority = 5;
        int entry = ObjectKit.RespawnEntry(Ram, o);
        Ram.Clear(entry, 7);
        bool passed = Ram.Bit(entry, 0) || Number(Ram.U8(Vars.LastStarPoleHit)) >= Number(o.Subtype);
        if (passed)
        {
            Ram.Set(entry, 0);
            o.Anim = AnimLit;
        }
    }

    /// <summary>0x23024.</summary>
    private void WaitForTouch(SonicObject o)
    {
        if (Ram.U16(Vars.DebugPlacementMode) == 0)
        {
            CheckTouch(o, Sonic, Ram.U8(Vars.LastStarPoleHit));
            if (TwoPlayerMode) throw new UnportedException(0x2303E, "star posts in 2-player mode");
        }
        AnimateAndDisplay(o);
    }

    private void AnimateAndDisplay(SonicObject o)
    {
        ObjectKit.Animate(Ram, o, _anims);
        DisplayOrMarkGone(o);
    }

    /// <summary>0x2304C.</summary>
    private void CheckTouch(SonicObject o, SonicObject player, int lastHit)
    {
        if (Number(lastHit) >= Number(o.Subtype))
        {
            if (o.Anim == AnimIdle) o.Anim = AnimLit;
            return;
        }
        // The touch box: 16 px wide, from $40 above to $28 below the post's centre.
        if ((((player.X - o.X) + 8) & 0xFFFF) >= 0x10) return;
        if ((((player.Y - o.Y) + 0x40) & 0xFFFF) >= 0x68) return;
        Ctx.Sound.PlaySound(SfxStarpost);
        if (Spawn() is { } ball)
        {
            var b = new BallState(ball);
            ball.Id = Id;
            ball.Routine = SpinningBall;
            b.CentreX = o.X;
            b.CentreY = o.Y - 0x14;
            ball.Mappings = o.Mappings;
            ball.ArtTile = o.ArtTile;
            ball.RenderFlags = Render.LevelCoords;
            ball.WidthPixels = 8;
            ball.Priority = 4;
            ball.MappingFrame = 2;
            b.Timer = SpinFrames;
            b.Post = o.Address;
        }
        // (0x230E2: with 50+ rings the special-stage star ring would be spawned here; not ported.)
        o.Anim = AnimHit;
        Save(o, player);
        Ram.Set(ObjectKit.RespawnEntry(Ram, o), 0);
    }

    /// <summary>0x2313C: the ball circles the post for 32 frames, then lights the post and goes.</summary>
    private void Spin(SonicObject o, BallState s)
    {
        s.Timer--;
        if ((short)s.Timer < 0)
        {
            var post = new SonicObject(Ram, s.Post);
            if (post.Id == Id)
            {
                post.Anim = AnimLit;
                post.MappingFrame = 0;
            }
            Delete(o);
            return;
        }
        int angle = o.Angle;
        o.Angle -= 0x10;
        var (sin, cos) = MdMath.CalcSine((angle - 0x40) & 0xFF);
        o.X = ((cos * 0xC00) >> 16) + s.CentreX;
        o.Y = ((sin * 0xC00) >> 16) + s.CentreY;
        DisplayOrMarkGone(o);
    }

    /// <summary>0x23194: save the restart state (Sonic's copy; Tails' is for 2-player mode).</summary>
    private void Save(SonicObject o, SonicObject player)
    {
        if (player.Address != Vars.MainCharacter) throw new UnportedException(0x23222, "Tails' star post (2-player)");
        Ram.W8(Vars.LastStarPoleHit, o.Subtype);
        Ram.W8(Vars.SavedLastStarPoleHit, Ram.U8(Vars.LastStarPoleHit));
        Ram.W16(Vars.SavedXPos, o.X);
        Ram.W16(Vars.SavedYPos, o.Y);
        Ram.W16(Vars.SavedArtTile, Sonic.ArtTile);
        Ram.W16(Vars.SavedSolidBits, Sonic.Word(0x3E));
        Ram.W16(Vars.SavedRingCount, Ram.U16(Vars.RingCount));
        Ram.W8(Vars.SavedExtraLifeFlags, Ram.U8(Vars.ExtraLifeFlags));
        Ram.W32(Vars.SavedTimer, Ram.U32(Vars.Timer));
        Ram.W8(Vars.SavedDynamicResizeRoutine, Ram.U8(Vars.DynamicResizeRoutine));
        Ram.W16(Vars.SavedCameraMaxYPos, Ram.U16(Vars.CameraMaxYPos));
        // The camera and background positions (8 x/y longs from Camera_X_pos; the high words are saved).
        for (int i = 0; i < 8; i++) Ram.W16(Vars.SavedCameraXPos + i * 2, Ram.U16(Vars.CameraXPos + i * 4));
        Ram.W16(Vars.SavedWaterLevel, Ram.U16(Vars.WaterLevel2));
        Ram.W8(Vars.SavedWaterRoutine, Ram.U8(Vars.WaterRoutine));
        Ram.W8(Vars.SavedWaterMove, Ram.U8(Vars.WaterFullscreenFlag));
    }

    /// <summary>Obj79_LoadData (0x2325A), called from LevelSizeLoad when restarting from a star post.</summary>
    public static void LoadData(Ram ram)
    {
        ram.W8(Vars.LastStarPoleHit, ram.U8(Vars.SavedLastStarPoleHit));
        ram.Sonic.X = ram.U16(Vars.SavedXPos);
        ram.Sonic.Y = ram.U16(Vars.SavedYPos);
        ram.W16(Vars.RingCount, ram.U16(Vars.SavedRingCount));
        ram.W8(Vars.ExtraLifeFlags, ram.U8(Vars.SavedExtraLifeFlags));
        ram.W16(Vars.RingCount, 0);   // the original restores the rings, then clears them
        ram.W8(Vars.ExtraLifeFlags, 0);
        ram.W32(Vars.Timer, ram.U32(Vars.SavedTimer));
        ram.W8(Vars.TimerFrame, 0x3B);
        ram.W8(Vars.TimerSecond, ram.U8(Vars.TimerSecond) - 1);
        ram.Sonic.ArtTile = ram.U16(Vars.SavedArtTile);
        ram.Sonic.SetWord(0x3E, ram.U16(Vars.SavedSolidBits));
        ram.W8(Vars.DynamicResizeRoutine, ram.U8(Vars.SavedDynamicResizeRoutine));
        ram.W8(Vars.WaterRoutine, ram.U8(Vars.SavedWaterRoutine));
        ram.W16(Vars.CameraMaxYPos, ram.U16(Vars.SavedCameraMaxYPos));
        ram.W16(Vars.CameraMaxYPosTarget, ram.U16(Vars.SavedCameraMaxYPos));
        for (int i = 0; i < 8; i++) ram.W16(Vars.CameraXPos + i * 4, ram.U16(Vars.SavedCameraXPos + i * 2));
        if (ram.U8(Vars.WaterFlag) != 0)
        {
            ram.W16(Vars.WaterLevel2, ram.U16(Vars.SavedWaterLevel));
            ram.W8(Vars.WaterRoutine, ram.U8(Vars.SavedWaterRoutine));
            ram.W8(Vars.WaterFullscreenFlag, ram.U8(Vars.SavedWaterMove));
        }
        bool lockLeft = (sbyte)ram.U8(Vars.LastStarPoleHit) < 0;
        if (lockLeft) ram.W16(Vars.CameraMinXPos, ram.U16(Vars.SavedXPos) - 0xA0);
    }
}
