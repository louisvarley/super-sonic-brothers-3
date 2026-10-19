namespace SonicBros3.Logic;

/// <summary>
/// The force-roll trigger, object $06 in the hack's zones (ROM 0x251B0; Sonic 2's invisible pinball-mode switch).
///
/// A vertical line (or, with subtype bit 2, a horizontal one) reaching $20/$40/$80/$100 px each way (subtype bits
/// 0-1). A player crossing it gets pinball mode (always rolling, <see cref="SonicObject.SpindashFlag"/>) switched on
/// or off, depending on the crossing direction and the x-flip, and is put into a roll. Tails is skipped while the
/// CPU is flying him back in.
/// </summary>
public sealed class ForceRoll(ObjectContext ctx) : GameObject(ctx)
{
    private const int Init = 0, VerticalLine = 2, HorizontalLine = 4;
    private static readonly int[] Ranges = [0x20, 0x40, 0x80, 0x100];   // 0x2523C
    private const int SfxRoll = 0xBE;

    /// <summary>$32: how far the line reaches each way.</summary>
    private static int Range(SonicObject o) => o.Word(0x32);
    /// <summary>$34 (Sonic), $35 (Tails): 1 while that player is past the line.</summary>
    private const int SonicPast = 0x34, TailsPast = 0x35;

    public override void Run(SonicObject o)
    {
        switch (o.Routine)
        {
            case Init: Initialise(o); break;
            case VerticalLine: Check(o, horizontal: false); break;
            case HorizontalLine: Check(o, horizontal: true); break;
        }
        MarkObjGone(o);
    }

    private void Initialise(SonicObject o)
    {
        o.Routine = VerticalLine;
        o.Mappings = 0x23FAC;   // only drawn in debug mode
        o.ArtTile = 0x6BC;
        o.RenderFlags |= Render.LevelCoords;
        o.WidthPixels = 0x10;
        o.Priority = 5;
        int subtype = o.Subtype;
        bool horizontal = (subtype & 4) != 0;
        if (horizontal) o.Routine = HorizontalLine;
        o.MappingFrame = subtype & (horizontal ? 7 : 3);
        o.SetWord(0x32, Ranges[subtype & 3]);
        if (horizontal)
        {
            if (o.Y < Sonic.Y) o.SetByte(SonicPast, 1);
            if (o.Y < Tails.Y) o.SetByte(TailsPast, 1);
        }
        else
        {
            if (o.X < Sonic.X) o.SetByte(SonicPast, 1);
            if (o.X < Tails.X) o.SetByte(TailsPast, 1);
        }
        Check(o, horizontal);   // both inits fall straight into their check (0x25238 / 0x25278)
    }

    /// <summary>0x25278 / 0x2534A.</summary>
    private void Check(SonicObject o, bool horizontal)
    {
        if (Ram.U16(Vars.DebugPlacementMode) != 0) return;
        CheckPlayer(o, Sonic, SonicPast, horizontal);
        if (!horizontal && Ram.U16(Vars.TailsCpuRoutine) == 4) return;   // Tails flying back in
        CheckPlayer(o, Tails, TailsPast, horizontal);
    }

    private void CheckPlayer(SonicObject o, SonicObject player, int pastFlag, bool horizontal)
    {
        int line = horizontal ? o.Y : o.X;
        int pos = horizontal ? player.Y : player.X;
        int across = horizontal ? player.X : player.Y, centre = horizontal ? o.X : o.Y;
        bool flipped = (o.RenderFlags & Render.XFlip) != 0;
        bool enable;
        if (o.Byte(pastFlag) == 0)
        {
            if (line > pos) return;   // not crossed yet
            o.SetByte(pastFlag, 1);
            enable = !flipped;
        }
        else
        {
            if (line <= pos) return;  // not crossed back yet
            o.SetByte(pastFlag, 0);
            enable = flipped;
        }
        int range = Range(o);
        if (across < ((centre - range) & 0xFFFF) || across >= ((centre + range) & 0xFFFF)) return;
        if (!enable) { player.SpindashFlag = 0; return; }
        player.SpindashFlag = 1;
        StartRolling(player);
    }

    /// <summary>0x25318.</summary>
    private void StartRolling(SonicObject player)
    {
        if (player.Rolling) return;
        player.Rolling = true;
        player.YRadius = 0x0E;
        player.XRadius = 7;
        player.Anim = 2;   // rolling
        player.Y += 5;
        Ctx.Sound.PlaySound(SfxRoll);
    }
}
