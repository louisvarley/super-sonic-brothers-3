namespace SonicBros3.Logic;

/// <summary>
/// Green Hill's rotating spiked helix, object $17 (ROM 0x12ECA; Sonic 1's).
///
/// The placed object spawns one spike per 16 px: subtype = the count, centred on itself (it is one of them). It
/// records the spikes' slot numbers from $29, and deletes them when it goes out of range. Each spike's frame is the
/// shared counter <see cref="Vars.LogspikeAnimFrame"/> plus its phase ($3E), mod 8; only frame 0 (pointing up) hurts.
/// </summary>
public sealed class Helix(ObjectContext ctx) : GameObject(ctx)
{
    public const uint Mappings = 0x1300A;
    private const int Init = 0, Main = 2, Spike = 4;
    private const int Art = 0x4398, Spacing = 0x10, Frames = 8;
    private const int HurtingCollision = 0x84;

    /// <summary>$29 onward: the slot numbers of the spikes this one spawned.</summary>
    private const int SpikeSlots = 0x29;
    /// <summary>$3E: the spike's rotation phase.</summary>
    private static int Phase(SonicObject o) => o.Byte(0x3E);

    public override void Run(SonicObject o)
    {
        switch (o.Routine)
        {
            case Init: SpawnSpikes(o); MainLoop(o); break;
            case Main: MainLoop(o); break;
            case Spike: SetFrame(o); Display(o); break;
        }
    }

    private void SpawnSpikes(SonicObject o)
    {
        o.Routine = Main;
        SetupSpike(o);
        int count = o.Subtype;
        int nextSlot = o.Address + SpikeSlots - 1;   // the subtype byte doubles as the spawned count
        Ram.W8(nextSlot++, 0);
        int x = (o.X - ((count >> 1) << 4)) & 0xFFFF;
        int phase = 0;
        for (count -= 2; count >= 0; count--)
        {
            int slot = ObjectKit.FindFreeSlotAfter(Ram, o);
            if (slot < 0) return;
            o.Subtype++;
            Ram.W8(nextSlot++, Objects.SlotIndex(slot));
            var spike = new SonicObject(Ram, slot);
            spike.Routine = Spike;
            spike.Id = o.Id;
            spike.Y = o.Y;
            spike.X = x;
            SetupSpike(spike);
            spike.SetByte(0x3E, phase);
            phase = (phase + 1) % Frames;
            x = (x + Spacing) & 0xFFFF;
            if (x != o.X) continue;
            // The placed object is the spike at its own position.
            o.SetByte(0x3E, phase);
            phase = (phase + 1) % Frames;
            x = (x + Spacing) & 0xFFFF;
            o.Subtype++;
        }
    }

    private static void SetupSpike(SonicObject s)
    {
        s.Mappings = Mappings;
        s.ArtTile = Art;
        s.RenderFlags = Render.LevelCoords;
        s.Priority = 3;
        s.WidthPixels = 8;
    }

    /// <summary>0x12FA0.</summary>
    private void MainLoop(SonicObject o)
    {
        SetFrame(o);
        if (!OutOfRange(o.X)) { Display(o); return; }
        // Out of range: delete the spawned spikes too.
        for (int i = 0; i < o.Subtype - 1; i++)
            Delete(Objects.Slot(Ram, o.Byte(SpikeSlots + i)));
        Delete(o);
    }

    /// <summary>0x12FE2.</summary>
    private void SetFrame(SonicObject o)
    {
        int frame = (Ram.U8(Vars.LogspikeAnimFrame) + Phase(o)) % Frames;
        o.MappingFrame = frame;
        o.CollisionFlags = frame == 0 ? HurtingCollision : 0;
    }
}
