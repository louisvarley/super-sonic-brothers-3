namespace SonicBros3.Logic;

/// <summary>
/// The lava ball launcher, object $13 (ROM 0x153924, Sonic 1's Marble Zone). Invisible; every $1E-$B4 frames
/// (subtype high nibble) it throws a lava ball (<see cref="LavaBall"/>, with the subtype's low nibble) if it is on
/// screen.
/// </summary>
public sealed class LavaBallMaker(ObjectContext ctx) : GameObject(ctx)
{
    private static readonly byte[] Rates = [0x1E, 0x3C, 0x5A, 0x78, 0x96, 0xB4];
    private const int LavaBallId = 0x14;

    /// <summary>$1F: frames between lava balls (the countdown is anim_frame_duration, $1E).</summary>
    private const int Rate = 0x1F;

    public override void Run(SonicObject o)
    {
        if (o.Routine == 0)
        {
            o.Routine = 2;
            int rate = (o.Subtype >> 4) & 0xF;
            o.SetByte(Rate, rate < Rates.Length ? Rates[rate] : 0);
            o.AnimFrameDuration = o.Byte(Rate);
            o.Subtype &= 0x0F;
        }
        o.AnimFrameDuration--;
        if (o.AnimFrameDuration == 0)
        {
            o.AnimFrameDuration = o.Byte(Rate);
            if (IsOnScreen(o) && Spawn() is { } ball)
            {
                ball.Id = LavaBallId;
                ball.X = o.X;
                ball.Y = o.Y;
                ball.Subtype = o.Subtype;
            }
        }
        if (OutOfRangeS1(o.X)) o.Clear();
    }

    /// <summary>0x153992: inside the screen (the camera's 320 x 224).</summary>
    private bool IsOnScreen(SonicObject o)
    {
        int dx = (short)(o.X - Ram.U16(Vars.CameraXPos));
        if (dx < 0 || dx >= 0x140) return false;
        int dy = (short)(o.Y - Ram.U16(Vars.CameraYPos));
        return dy >= 0 && dy < 0xE0;
    }
}
