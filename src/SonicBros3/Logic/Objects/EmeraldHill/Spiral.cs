namespace SonicBros3.Logic;

/// <summary>
/// The spiral (corkscrew), object $06 in Sonic 2's zones (ROM 0x25518; in the hack's zones $06 is the force-roll
/// trigger, <see cref="ForceRoll"/>). It is invisible: a player who runs into one of its ends at ground level is made to
/// ride it (RideObject_SetRide); while riding fast enough ($600), the player's height follows the spiral's profile and
/// their flip angle turns with it; slower, airborne or past either end, they fall off. Only the horizontal spiral
/// (Emerald Hill's) is ported; subtype bit 7 (the vertical one, 0x2585C) is not.
/// </summary>
public sealed class Spiral(ObjectContext ctx) : GameObject(ctx)
{
    private const int Init = 0, Horizontal = 2, Vertical = 4;
    private const int HalfWidth = 0xD0, MinSpeed = 0x600;

    /// <summary>The height of the path per pixel along the spiral (0x256BC, $1A0 bytes).</summary>
    private readonly byte[] _heights = ctx.Art.DataTable("spiral/heights.json").Data;
    /// <summary>The player's flip angle per 8 px along it (0x25688).</summary>
    private readonly byte[] _flipAngles = ctx.Art.DataTable("spiral/flip-angles.json").Data;

    public override void Run(SonicObject o)
    {
        switch (o.Routine)
        {
            case Init:
                // 0x2554E.
                o.Routine = Horizontal;
                o.WidthPixels = HalfWidth;
                if ((o.Subtype & 0x80) != 0)
                {
                    o.Routine = Vertical;
                    throw new UnportedException(0x2585C, "the vertical spiral");
                }
                goto case Horizontal;
            case Horizontal:
                CheckPlayer(o, Sonic, 3);
                CheckPlayer(o, Tails, 4);
                break;
            default: throw new UnportedException(0x2585C, "the vertical spiral");
        }
        // 0x25526: no display (invisible); deleted out of range.
        if (TwoPlayerMode) return;
        if (OutOfRange(o.X)) Delete(o);
    }

    /// <param name="ridingBit">The spiral's status bit for "this player rides it" (3 Sonic, 4 Tails).</param>
    private void CheckPlayer(SonicObject o, SonicObject player, int ridingBit)
    {
        if (Ram.Bit(o.StatusAddr, ridingBit)) { Ride(o, player, ridingBit); return; }
        if (player.InAir) return;
        int dx = (short)(player.X - o.X);
        // The entry points, just inside each end (a little further in when stepping off another object).
        bool fromObject = player.OnObject;
        bool atEnd = player.XVel >= 0
            ? fromObject ? dx >= -0xC0 && dx <= -0xB0 : dx >= -0xD0 && dx <= -0xC0
            : fromObject ? dx >= 0xB0 && dx <= 0xC0 : dx >= 0xC0 && dx <= 0xD0;
        if (!atEnd) return;
        int dy = (player.Y - o.Y - 0x10) & 0xFFFF;
        if (dy >= 0x30) return;
        if (!fromObject && player.ObjControl != 0) return;
        Ctx.Solid.SetRide(o, player, ridingBit);
    }

    /// <summary>0x25614.</summary>
    private void Ride(SonicObject o, SonicObject player, int ridingBit)
    {
        int speed = Math.Abs((int)player.Inertia) & 0xFFFF;
        int along = (player.X - o.X + HalfWidth) & 0xFFFF;
        bool fallOff = speed < MinSpeed || player.InAir || (short)along < 0 || along >= HalfWidth * 2;
        if (fallOff)
        {
            player.OnObject = false;
            Ram.Clear(o.StatusAddr, ridingBit);
            player.FlipsRemaining = 0;
            player.FlipSpeed = 4;
            return;
        }
        if (!player.OnObject) return;
        int height = (sbyte)_heights[along];
        player.Y = o.Y + height - (player.Byte(0x16) - 0x13);   // y_radius, as standing height
        player.FlipAngle = _flipAngles[(along >> 3) & 0x3F];
    }
}
