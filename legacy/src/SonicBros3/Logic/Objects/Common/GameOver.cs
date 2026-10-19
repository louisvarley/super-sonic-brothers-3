namespace SonicBros3.Logic;

/// <summary>
/// "GAME" / "OVER" (or "TIME" / "OVER"), object $39 (ROM 0x17256). Once the game-over art (PLC 3) is loaded, each
/// half slides from its screen edge to x $120 at 16 px per frame. Then the first half waits $2D0 frames (or for A, B
/// or C) and ends the game:
/// - after a time over, the act restarts (<see cref="Vars.LevelInactiveFlag"/>);
/// - otherwise the game goes to the continue screen (continues left: game mode $14) or back to the SEGA screen
///   (mode 0). Neither screen is ported; the session watches <see cref="Vars.GameMode"/>.
/// The halves are screen-space sprites (render flags 0; y in the x_sub word, $0A).
/// </summary>
public sealed class GameOver(ObjectContext ctx) : GameObject(ctx)
{
    public const uint Mappings = 0x1820E;
    private const int WaitForArt = 0, Slide = 2, Wait = 4;
    private const int FinalX = 0x120, SlideSpeed = 0x10, WaitFrames = 0x2D0;
    public const int ModeContinue = 0x14, ModeSega = 0x00;

    /// <summary>The second half ("OVER") has an odd frame; only the first half ends the game.</summary>
    private static bool IsFirstHalf(SonicObject o) => (o.MappingFrame & 1) == 0;

    public override void Run(SonicObject o)
    {
        switch (o.Routine)
        {
            case WaitForArt:
                if (Ram.U32(Vars.PlcBuffer) != 0) return;   // the PLC queue is still decompressing
                o.Routine = Slide;
                o.X = IsFirstHalf(o) ? 0x50 : 0x1F0;
                o.XSub = 0xF0;                               // screen y
                o.Mappings = Mappings;
                o.ArtTile = 0x84DE;
                o.RenderFlags = 0;
                o.Priority = 0;
                goto case Slide;
            case Slide:
                if (o.X == FinalX)
                {
                    o.SetWord(0x1E, WaitFrames);
                    o.Routine = Wait;
                    return;
                }
                o.X += o.X < FinalX ? SlideSpeed : -SlideSpeed;
                Display(o);
                return;
            default:
                if (IsFirstHalf(o)) WaitThenEnd(o);
                else Display(o);
                return;
        }
    }

    /// <summary>0x172D0.</summary>
    private void WaitThenEnd(SonicObject o)
    {
        const int ButtonsAbc = 0x70;
        bool pressed = ((Ram.U8(Vars.Ctrl1Press) | Ram.U8(Vars.Ctrl2Press)) & ButtonsAbc) != 0;
        int timer = o.Word(0x1E);
        if (!pressed && timer != 0)
        {
            o.SetWord(0x1E, timer - 1);
            Display(o);
            return;
        }
        if (Ram.U8(Vars.TimedMode) != 0) throw new UnportedException(0x172FC, "game over in the hack's timed mode");
        if (Ram.U8(Vars.TimeOverFlag) != 0 || Ram.U8(Vars.TimeOverFlagP2) != 0)
        {
            Ram.W32(Vars.SavedTimer, 0);
            Ram.W16(Vars.LevelInactiveFlag, 1);
        }
        else if (Ram.U8(Vars.ContinueCount) != 0) Ram.W8(Vars.GameMode, ModeContinue);
        else
        {
            Ram.W8(Vars.CurrentSpecialAct, 1);
            Ram.W8(Vars.GameMode, ModeSega);
        }
        if (TwoPlayerMode) throw new UnportedException(0x17342, "2-player game over");
        Display(o);
    }
}
