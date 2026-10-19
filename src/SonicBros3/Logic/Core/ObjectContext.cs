namespace SonicBros3.Logic;

/// <summary>What object code can reach: RAM, terrain collision, the solid-object routines, sprite data and sound.</summary>
public sealed class ObjectContext(Ram ram, Collision collision, ObjectArt art, ISoundRequests sound)
{
    public Ram Ram { get; } = ram;
    public Collision Collision { get; } = collision;
    public Solid Solid { get; } = new(ram);
    public ObjectArt Art { get; } = art;
    public ISoundRequests Sound { get; } = sound;
    /// <summary>HurtCharacter and friends, for objects that hurt players directly (spikes).</summary>
    public TouchResponse Touch { get; } = new(ram, sound);
    public TailsAnimation? TailsAnimation { get; init; }
    public SpriteData? TailsSprites { get; init; }
    public byte[]? Vram { get; init; }
    /// <summary>LoadPLC: art requested at run time goes straight into VRAM.</summary>
    public Action<int>? LoadPlc { get; init; }
    /// <summary>0x43F50: copy the giant ring flash's art to VRAM.</summary>
    public Action? LoadFlashArt { get; init; }
    /// <summary>DeformBgLayer (0xDCD6), which the title card calls as it uncovers the level.</summary>
    public Action? DeformBgLayer { get; init; }
    /// <summary>The PLCs the title card queues as it leaves (0x171FC).</summary>
    public Action? AfterTitleCard { get; init; }
    /// <summary>Obj34's zone-name frame for a zone (0x17238).</summary>
    public Func<int, int> TitleCardNameFrame { get; init; } = _ => 0;
    /// <summary>The SMB3 goal's end: the card won (<see cref="RouletteGoal"/>).</summary>
    public Action<int>? GoalReached { get; set; }
}

/// <summary>A ported object type. State lives in the object's RAM slot; implementations hold no per-instance state.</summary>
public abstract class GameObject(ObjectContext ctx)
{
    protected ObjectContext Ctx { get; } = ctx;
    protected Ram Ram => Ctx.Ram;
    protected SonicObject Sonic => Ctx.Ram.Sonic;
    protected SonicObject Tails => Ctx.Ram.Tails;

    public abstract void Run(SonicObject o);

    /// <summary>DisplaySprite: queue the object for drawing this frame (in its priority's list).</summary>
    protected void Display(SonicObject o) => SpriteBuilder.DisplaySprite(Ram, o);
    /// <summary>DeleteObject: clear the slot.</summary>
    protected void Delete(SonicObject o) => Objects.DeleteObject(Ram, o);
    /// <summary>A free slot after this one (SingleObjLoad), or null when all are taken.</summary>
    protected SonicObject? Spawn() => Objects.FindFreeSlot(Ram) is var a and >= 0 ? new SonicObject(Ram, a) : null;

    /// <summary>The zone being played (the high byte of <see cref="Vars.CurrentZoneAndAct"/>).</summary>
    protected int CurrentZone => Ram.U8(Vars.CurrentZoneAndAct);
    protected bool TwoPlayerMode => Ram.U16(Vars.TwoPlayerMode) != 0;

    /// <summary>
    /// The despawn test objects share: is x more than $280 px past the left edge of the object manager's window
    /// (the camera's x rounded down to $80, minus $80)? The subtraction wraps, so far left of it counts too.
    /// </summary>
    protected bool OutOfRange(int x) => (((x & 0xFF80) - Ram.U16(Vars.CameraXPosCoarse)) & 0xFFFF) > 0x280;

    /// <summary>Sonic 1's despawn test (its objects in the hack): the window starts at the camera's x - $80, rounded down.</summary>
    protected bool OutOfRangeS1(int x) => ObjectKit.OutOfRangeS1(Ram, x);

    /// <summary>MarkObjGone: delete the object once out of range (and let it load again when the camera returns).</summary>
    protected void MarkObjGone(SonicObject o) => Objects.MarkObjGone(Ram, o);
    /// <summary>MarkObjGone's variant that also displays the object while it is in range.</summary>
    protected void DisplayOrMarkGone(SonicObject o) => ObjectKit.MarkObjGoneDisplay(Ram, o);
}
