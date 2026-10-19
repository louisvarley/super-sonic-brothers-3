namespace SonicBros3.Logic;

/// <summary>
/// RingsManager (ROM 0x1AD00): the level's rings, without one object each.
///
/// The ring table (<see cref="Vars.RingPositions"/>) holds 6-byte entries {state, x, y}, sorted by x and ended by
/// $FFFF; it is laid out at level start (the setup routine 0x1B03A). A ring's state word: 0 = there; collected, its
/// high byte is a frame timer and its low byte the sparkle frame (4-7); gone = $FFFF. Each frame the collected
/// rings advance their sparkle, and the visible window [<see cref="Vars.RingStartAddr"/>,
/// <see cref="Vars.RingEndAddr"/>) slides with the camera (rings with x in [camera - 8, camera + $148)).
/// </summary>
public sealed class RingsManager(Ram ram)
{
    private const int EntrySize = 6;
    /// <summary>A just-collected ring's state: timer 6, sparkle frame 4.</summary>
    public const int Sparkling = 0x0604;
    private const int Gone = 0xFFFF;
    private const int WindowWidth = 0x150;

    private int X(int entry) => ram.U16(entry + 2);

    /// <summary>Writes the expanded, sorted ring table (the setup routine's output).</summary>
    public void Setup(IReadOnlyList<(int X, int Y)> rings)
    {
        ram.Bytes.AsSpan(Vars.RingPositions, 0x600).Clear();          // 0x1B01C
        ram.Bytes.AsSpan(Vars.RingConsumptionTable, 0x40).Clear();
        int entry = Vars.RingPositionsFirst;
        foreach (var (x, y) in rings)
        {
            ram.W16(entry, 0);
            ram.W16(entry + 2, x);
            ram.W16(entry + 4, y);
            entry += EntrySize;
        }
        ram.W32(entry, 0xFFFFFFFF);
        ram.W16(Vars.PerfectRingsLeft, rings.Count == 0 ? 0xFFFF : rings.Count);
        ram.W16(Vars.PerfectRingsFlag, 0);
    }

    public void Run()
    {
        if (ram.U8(Vars.RingsManagerRoutine) == 0) { Init(); return; }
        Update();
    }

    /// <summary>0x1AD12: the first window, from the camera.</summary>
    private void Init()
    {
        ram.W8(Vars.RingsManagerRoutine, ram.U8(Vars.RingsManagerRoutine) + 2);
        int left = LeftEdge();
        int entry = Vars.RingPositions;
        while (left > X(entry)) entry += EntrySize;
        ram.W16(Vars.RingStartAddr, entry);
        ram.W16(Vars.RingStartAddrP2, entry);
        int right = left + WindowWidth;
        while (right > X(entry)) entry += EntrySize;
        ram.W16(Vars.RingEndAddr, entry);
        ram.W16(Vars.RingEndAddrP2, entry);
    }

    private int LeftEdge()
    {
        int left = ram.U16(Vars.CameraXPos) - 8;
        return left <= 0 ? 1 : left;
    }

    /// <summary>0x1AD56: sparkles, then slide both ends of the window.</summary>
    private void Update()
    {
        // The collected rings' sparkles: a frame every 6 frames, gone after frame 7.
        int count = ram.U16(Vars.RingConsumptionTable);
        int list = Vars.RingConsumptionList;
        for (int n = 0; n < count; n++)
        {
            int entry;
            do { entry = ram.U16(list); list += 2; } while (entry == 0);
            int timer = (ram.U8(entry) - 1) & 0xFF;
            ram.W8(entry, timer);
            if (timer != 0) continue;
            ram.W8(entry, 6);
            ram.W8(entry + 1, ram.U8(entry + 1) + 1);
            if (ram.U8(entry + 1) != 8) continue;
            ram.W16(entry, Gone);
            ram.W16(list - 2, 0);
            ram.W16(Vars.RingConsumptionTable, ram.U16(Vars.RingConsumptionTable) - 1);
        }

        int left = LeftEdge();
        int start = ram.U16(Vars.RingStartAddr);
        while (left > X(start)) start += EntrySize;
        while (left <= X(start - EntrySize)) start -= EntrySize;
        ram.W16(Vars.RingStartAddr, start);
        int end = ram.U16(Vars.RingEndAddr);
        int right = left + WindowWidth;
        while (right > X(end)) end += EntrySize;
        while (right <= X(end - EntrySize)) end -= EntrySize;
        ram.W16(Vars.RingEndAddr, end);
        ram.W16(Vars.RingStartAddrP2, start);
        ram.W16(Vars.RingEndAddrP2, end);
    }
}

/// <summary>ChangeRingFrame (ROM 0x5EB0): the shared animation counters that rings and other objects use.</summary>
public static class SyncAnimations
{
    public static void Step(Ram ram)
    {
        // The helix spikes: a step back every 12 frames, through 8 frames.
        if (CountDown(ram, Vars.LogspikeAnimCounter, 0x0B))
            ram.W8(Vars.LogspikeAnimFrame, (ram.U8(Vars.LogspikeAnimFrame) - 1) & 7);
        // Rings: a step every 8 frames, through 4 frames.
        if (CountDown(ram, Vars.RingsAnimCounter, 7))
            ram.W8(Vars.RingsAnimFrame, (ram.U8(Vars.RingsAnimFrame) + 1) & 3);
        // Unused here: a step every 8 frames, through 6 frames.
        if (CountDown(ram, Vars.UnknownAnimCounter, 7))
        {
            int frame = ram.U8(Vars.UnknownAnimFrame) + 1;
            ram.W8(Vars.UnknownAnimFrame, frame >= 6 ? 0 : frame);
        }
        // Lost rings: spin slower as the timer runs out.
        int t = ram.U8(Vars.RingSpillAnimCounter);
        if (t != 0)
        {
            int acc = (t + ram.U16(Vars.RingSpillAnimAccum)) & 0xFFFF;
            ram.W16(Vars.RingSpillAnimAccum, acc);
            ram.W8(Vars.RingSpillAnimFrame, ((acc << 7) | (acc >> 9)) & 3);
            ram.W8(Vars.RingSpillAnimCounter, t - 1);
        }
    }

    /// <summary>Decrement a counter; when it goes below 0, reload it and return true.</summary>
    private static bool CountDown(Ram ram, int counter, int reload)
    {
        if ((sbyte)(ram.U8(counter) - 1) < 0) { ram.W8(counter, reload); return true; }
        ram.W8(counter, ram.U8(counter) - 1);
        return false;
    }
}
