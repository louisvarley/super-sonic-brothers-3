using SonicBros3.Logic;
using S2Port.Extraction.Levels;

namespace SonicBros3.Runtime;

/// <summary>
/// Data-driven animated level tiles, following the generic interpreter at ROM 0x443EC:
/// each frame the script's timer is decremented; when it underflows, the next frame's tiles are copied to VRAM
/// and the timer is reloaded from the frame's duration. Custom per-zone code (HTZ, CNZ, ARZ, zones 13/14) is not
/// included here.
/// </summary>
public sealed class AnimatedTiles
{
    /// <summary>Per-script state in 68k RAM, as the original keeps it: {timer, next frame} from $FFFFF7F0.</summary>

    private readonly LevelData _level;
    private readonly Logic.Ram _ram;

    public AnimatedTiles(LevelData level, Logic.Ram ram)
    {
        _level = level;
        _ram = ram;
    }

    /// <summary>Runs one frame. Returns true if VRAM changed.</summary>
    public bool Step()
    {
        if (_level.Vram is null) return false;
        bool changed = Magma();
        for (int i = 0; i < _level.AnimatedTiles.Count; i++)
        {
            // subq.b #1,(a3) / bcc: only act when the timer underflows.
            int a = Vars.AnimCounters + i * 2;
            int timer = _ram.U8(a);
            _ram.W8(a, timer - 1);
            if (timer != 0) continue;
            var script = _level.AnimatedTiles[i];
            int frame = _ram.U8(a + 1);
            if (frame >= script.Frames.Count) frame = 0;
            _ram.W8(a + 1, frame + 1);
            _ram.W8(a, script.Frames[frame].Duration);
            int offset = script.Frames[frame].TileOffset;
            script.Art.AsSpan(offset * 32, script.TilesPerFrame * 32).CopyTo(_level.Vram.AsSpan(script.VramTile * 32));
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// Marble Zone's magma (0x4423A), every other frame: the frame before the lava-surface script's next one (its
    /// counter at $F7F1) is copied as 4 columns of 8x32 px into 16 tiles, each column starting (($FE68 + 4 * column) mod
    /// 16) bytes into the 16-byte rows (the shift wraps), so the magma ripples with the oscillating value.
    /// </summary>
    private bool Magma()
    {
        if (_level.MagmaArt is not { } art) return false;
        int timer = _ram.U8(Vars.AnimCounters + 0x4);
        _ram.W8(Vars.AnimCounters + 0x4, timer - 1);
        if ((sbyte)(timer - 1) >= 0) return false;
        _ram.W8(Vars.AnimCounters + 0x4, 1);
        int frame = (_ram.U8(Vars.AnimCounters + 0x1) - 1) & 3;
        _ram.W8(Vars.AnimCounters + 0x5, _ram.U8(Vars.AnimCounters + 0x5) + 1);
        int shift = _ram.U8(Oscillation.Value(2));
        int dest = _level.MagmaVramTile * 32;
        for (int col = 0; col < 4; col++, shift += 4)
        {
            int k = (shift * 2 & 0x1E) >> 1;
            for (int row = 0; row < 32; row++)
            {
                int src = frame * 0x200 + row * 16;
                for (int i = 0; i < 4; i++) _level.Vram[dest++] = art[src + ((k + i) & 15)];
            }
        }
        return true;
    }
}
