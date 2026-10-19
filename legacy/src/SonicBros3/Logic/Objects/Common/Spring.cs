namespace SonicBros3.Logic;

/// <summary>
/// Springs, object $41 (ROM 0x1C604).
///
/// Subtype bits:
///   4-6  direction: 0 up, 1 sideways, 2 down, 3 diagonally up, 4 diagonally down
///   1    yellow: weaker (-$A00 instead of -$1000)
///   0    the player twirls
///   7    the other velocity component is zeroed
///   2-3  switch the player's collision layer (4 = layer A, 8 = layer B)
/// </summary>
public sealed class Spring(ObjectContext ctx) : GameObject(ctx)
{
    public const uint Mappings = 0x1CD98, MappingsYellow = 0x1CDAE;
    private readonly byte[] _anims = ctx.Art["spring"].Animations!;

    private const int Init = 0, Up = 2, Sideways = 4, Down = 6, DiagonalUp = 8, DiagonalDown = 0x0A;
    // The spring's bounce animations.
    private const int AnimUp = 1, AnimSideways = 3, AnimDiagonal = 5;
    private const int PlayerAnimSpring = 0x10, PlayerAnimWalk = 0;
    private const int RedStrength = 0xF000, YellowStrength = 0xF600;   // -$1000, -$A00
    private const int SfxSpring = 0xCC;

    /// <summary>$30: the launch speed (negative).</summary>
    private static short Strength(SonicObject o) => (short)o.Word(0x30);

    private sbyte[]? _slopeUp, _slopeDown;

    public override void Run(SonicObject o)
    {
        switch (o.Routine)
        {
            case Init: Initialise(o); break;
            case Up: UpdateUp(o); break;
            case Sideways: UpdateSideways(o); break;
            case Down: UpdateDown(o); break;
            case DiagonalUp: UpdateDiagonal(o, up: true); break;
            case DiagonalDown: UpdateDiagonal(o, up: false); break;
        }
        DisplayOrMarkGone(o);
    }

    private void Initialise(SonicObject o)
    {
        o.Routine = Up;
        o.Mappings = Mappings;
        o.ArtTile = 0x45C;
        o.RenderFlags |= Render.LevelCoords;
        o.WidthPixels = 0x10;
        o.Priority = 4;
        int subtype = o.Subtype;
        switch ((subtype >> 4) & 7)
        {
            case 1:
                o.Routine = Sideways;
                o.Anim = 2;
                o.MappingFrame = 3;
                o.ArtTile = 0x470;
                o.WidthPixels = 8;
                break;
            case 2:
                o.Routine = Down;
                o.MappingFrame = 6;
                o.Status |= ObjStatus.YFlip;
                break;
            case 3:
                o.Routine = DiagonalUp;
                o.Anim = 4;
                o.MappingFrame = 7;
                o.ArtTile = 0x43C;
                break;
            case 4:
                o.Routine = DiagonalDown;
                o.Anim = 4;
                o.MappingFrame = 0x0A;
                o.ArtTile = 0x43C;
                o.Status |= ObjStatus.YFlip;
                break;
        }
        bool yellow = (subtype & 2) != 0;
        o.SetWord(0x30, yellow ? YellowStrength : RedStrength);
        if (yellow)
        {
            o.ArtTile |= 0x2000;   // palette line 2
            o.Mappings = MappingsYellow;
        }
    }

    /// <summary>Restart the spring's bounce animation (anim and prev_anim as one word).</summary>
    private static void Bounce(SonicObject o, int anim) => o.SetWord(0x1C, anim << 8);

    private static SonicObject[] Players(Ram ram) => [ram.Sonic, ram.Tails];

    /// <summary>0x1C6FC.</summary>
    private void UpdateUp(SonicObject o)
    {
        var players = Players(Ram);
        for (int n = 0; n < 2; n++)
        {
            int standingBit = 3 + n;
            Ctx.Solid.SolidAlwaysOne(o, players[n], 0x1B, 8, 0x10, o.X, ref standingBit);
            if (Ram.Bit(o.StatusAddr, 3 + n)) LaunchUp(o, players[n]);
        }
        ObjectKit.Animate(Ram, o, _anims);
    }

    /// <summary>0x1C746.</summary>
    private void LaunchUp(SonicObject o, SonicObject player)
    {
        Bounce(o, AnimUp);
        player.Y += 8;
        player.YVel = Strength(o);
        player.InAir = true;     // in the air
        player.OnObject = false;   // not on an object
        player.Anim = PlayerAnimSpring;
        player.Routine = 2;
        int subtype = o.Subtype;
        if ((subtype & 0x80) != 0) player.XVel = 0;
        Twirl(player, subtype, 0, 1);
        SwitchLayer(player, subtype);
        Ctx.Sound.PlaySound(SfxSpring);
    }

    /// <summary>The twirl set-up shared by every direction; the number of flips differs per direction.</summary>
    private static void Twirl(SonicObject player, int subtype, int flipsYellow, int flipsRed)
    {
        if ((subtype & 1) == 0) return;
        player.Inertia = 1;
        player.FlipAngle = 1;
        player.Anim = PlayerAnimWalk;
        player.FlipsRemaining = flipsYellow;
        player.FlipSpeed = flipsYellow == 0 ? 4 : 8;
        if ((subtype & 2) == 0) player.FlipsRemaining = flipsRed;
        if (player.FacingLeft)
        {
            player.FlipAngle = -player.FlipAngle;
            player.Inertia = (short)-player.Inertia;
        }
    }

    private static void SwitchLayer(SonicObject player, int subtype)
    {
        switch (subtype & 0x0C)
        {
            case 4: player.TopSolidBit = 0x0C; player.LrbSolidBit = 0x0D; break;
            case 8: player.TopSolidBit = 0x0E; player.LrbSolidBit = 0x0F; break;
        }
    }

    /// <summary>0x1C7EC: pushing against the spring's face launches the player away.</summary>
    private void UpdateSideways(SonicObject o)
    {
        var players = Players(Ram);
        for (int n = 0; n < 2; n++)
        {
            int standingBit = 3 + n;
            var player = players[n];
            Ctx.Solid.SolidAlwaysOne(o, player, 0x13, 0x0E, 0x0F, o.X, ref standingBit);
            bool pushing = Ram.Bit(o.StatusAddr, 5 + n);
            if (!pushing) continue;
            // Only the face side launches.
            int side = o.Status;
            if (o.X >= player.X) side ^= 1;
            if ((side & 1) == 0) LaunchSideways(o, player);
        }
        LaunchApproaching(o);
        ObjectKit.Animate(Ram, o, _anims);
    }

    /// <summary>0x1C86A.</summary>
    private void LaunchSideways(SonicObject o, SonicObject player)
    {
        Bounce(o, AnimSideways);
        player.XVel = Strength(o);
        player.X += 8;
        player.FacingLeft = true;
        if ((o.Status & ObjStatus.XFlip) == 0)
        {
            player.FacingLeft = false;
            player.X -= 0x10;
            player.XVel = (short)-player.XVel;
        }
        player.MoveLock = 0x0F;
        player.Inertia = player.XVel;
        if (!player.Rolling) player.Anim = PlayerAnimWalk;
        int subtype = o.Subtype;
        if ((subtype & 0x80) != 0) player.YVel = 0;
        Twirl(player, subtype, 1, 3);
        SwitchLayer(player, subtype);
        Ram.Clear(o.StatusAddr, 5);
        Ram.Clear(o.StatusAddr, 6);
        player.Pushing = false;   // not pushing
        Ctx.Sound.PlaySound(SfxSpring);
    }

    /// <summary>0x1C942: a grounded player running at the spring's face is launched before touching it.</summary>
    private void LaunchApproaching(SonicObject o)
    {
        if (o.Anim == AnimSideways) return;
        bool flipped = (o.Status & ObjStatus.XFlip) != 0;
        int left = flipped ? (o.X - 0x28) & 0xFFFF : o.X;
        int right = flipped ? o.X : (o.X + 0x28) & 0xFFFF;
        int top = (o.Y - 0x18) & 0xFFFF, bottom = (o.Y + 0x18) & 0xFFFF;
        var players = Players(Ram);
        for (int n = 0; n < 2; n++)
        {
            var player = players[n];
            if (player.InAir) { if (n == 0) continue; return; }
            int towards = flipped ? -player.Inertia : player.Inertia;
            bool inRange = (short)towards >= 0 && player.X >= left && player.X < right && player.Y >= top && player.Y < bottom;
            if (inRange) LaunchSideways(o, player);
        }
    }

    /// <summary>0x1C9FC: hitting the underside launches the player downward.</summary>
    private void UpdateDown(SonicObject o)
    {
        var players = Players(Ram);
        for (int n = 0; n < 2; n++)
        {
            int standingBit = 3 + n;
            int contact = Ctx.Solid.SolidAlwaysOne(o, players[n], 0x1B, 8, 0x10, o.X, ref standingBit);
            if (contact == -2) LaunchDown(o, players[n]);   // hit from below
        }
        ObjectKit.Animate(Ram, o, _anims);
    }

    /// <summary>0x1CA42.</summary>
    private void LaunchDown(SonicObject o, SonicObject player)
    {
        Bounce(o, AnimUp);
        player.Y -= 8;
        player.YVel = (short)-Strength(o);
        int subtype = o.Subtype;
        if ((subtype & 0x80) != 0) player.XVel = 0;
        Twirl(player, subtype, 0, 1);
        SwitchLayer(player, subtype);
        player.InAir = true;
        player.OnObject = false;
        player.Routine = 2;
        Ctx.Sound.PlaySound(SfxSpring);
    }

    /// <summary>
    /// The slopes of the diagonal springs' tops (0x1CD26 up, 0x1CD42 down): a height per 2 px. A flipped spring
    /// touched at exactly its right edge makes SlopedSolid index $7FFF past the table (not.w wraps); the original
    /// then reads that ROM byte, kept here from the extracted bytes at table + $7FFF.
    /// </summary>
    private sbyte[] Slope(bool up)
    {
        if ((up ? _slopeUp : _slopeDown) is { } cached) return cached;
        var (b, data) = Ctx.Art.DataTable("spring/diagonal-slopes.json");
        var (eb, edge) = Ctx.Art.DataTable("spring/diagonal-slopes-edge.json");
        int start = up ? 0x1CD26 : 0x1CD42;
        var slope = new sbyte[0x8000];
        for (int i = 0; i < 0x1C; i++) slope[i] = (sbyte)data[start - b + i];
        slope[0x7FFF] = (sbyte)edge[start + 0x7FFF - eb];
        if (up) _slopeUp = slope; else _slopeDown = slope;
        return slope;
    }

    /// <summary>
    /// 0x1CAE6 (up) / 0x1CC1A (down): diagonal springs. Up: a player standing on the raised side is launched up and
    /// away. Down: a player hitting it from below is launched down and away.
    /// </summary>
    private void UpdateDiagonal(SonicObject o, bool up)
    {
        var slope = Slope(up);
        var players = Players(Ram);
        for (int n = 0; n < 2; n++)
        {
            int standingBit = 3 + n;
            int contact = Ctx.Solid.SlopedSolidOne(o, players[n], 0x1B, 0x10, o.X, slope, ref standingBit);
            bool launch = up ? Ram.Bit(o.StatusAddr, 3 + n) : contact == -2;
            if (launch) LaunchDiagonal(o, players[n], up);
        }
        ObjectKit.Animate(Ram, o, _anims);
    }

    /// <summary>0x1CB30 / 0x1CC62.</summary>
    private void LaunchDiagonal(SonicObject o, SonicObject player, bool up)
    {
        bool flipped = (o.Status & ObjStatus.XFlip) != 0;
        if (up)
        {
            // Only from the spring's raised side.
            if (!flipped) { if (((o.X - 4) & 0xFFFF) >= player.X) return; }
            else if (((o.X + 4) & 0xFFFF) < player.X) return;
        }
        Bounce(o, AnimDiagonal);
        player.YVel = up ? Strength(o) : (short)-Strength(o);
        player.XVel = Strength(o);
        player.Y += up ? 6 : -6;
        player.X += 6;
        player.FacingLeft = true;
        if (!flipped)
        {
            player.FacingLeft = false;
            player.X -= 0x0C;
            player.XVel = (short)-player.XVel;
        }
        player.InAir = true;
        player.OnObject = false;
        if (up) player.Anim = PlayerAnimSpring;
        player.Routine = 2;
        int subtype = o.Subtype;
        Twirl(player, subtype, 1, 3);
        SwitchLayer(player, subtype);
        Ctx.Sound.PlaySound(SfxSpring);
    }
}
