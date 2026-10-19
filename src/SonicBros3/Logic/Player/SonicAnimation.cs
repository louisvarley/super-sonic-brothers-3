using System.Text.Json;

namespace SonicBros3.Logic;

/// <summary>
/// Sonic_Animate (ROM 0x1F138) with its scripts loaded from extracted/objects/sonic/animations.json.
/// Script byte 0 is the frame duration, or $FF/$FE/$FD for the speed-driven walk/run, roll and push animations.
/// Frame bytes follow; a byte >= $F0 is a command: $FF loop, $FE n back up n frames, $FD n switch to anim n.
/// Animate matters for physics: changing animation clears the "pushing" status bit.
/// </summary>
public sealed class SonicAnimation(Ram ram, SonicAnimation.Script[] normal, SonicAnimation.Script[] super) : IPlayerAnimation
{
    /// <summary>
    /// A script as a window into the raw animation block: indexing past its end reads the bytes that follow it,
    /// as the original does when the frame index outlives a script switch.
    /// </summary>
    public readonly struct Script(byte[] block, int start)
    {
        public byte this[int i] => block[start + i];
    }

    public static SonicAnimation Load(Ram ram, string dataDir)
    {
        using var doc = JsonDocument.Parse(S2Port.Extraction.DataFile.ReadAllText(Path.Combine(dataDir, "objects", "sonic", "animations.json")));
        var root = doc.RootElement;
        var block = root.GetProperty("data").EnumerateArray().Select(b => (byte)b.GetInt32()).ToArray();
        Script[] Table(int offset)
        {
            int count = ((block[offset] << 8) | block[offset + 1]) / 2;
            return Enumerable.Range(0, count)
                .Select(i => new Script(block, offset + (short)((block[offset + i * 2] << 8) | block[offset + i * 2 + 1]))).ToArray();
        }
        return new SonicAnimation(ram, Table(root.GetProperty("normalTable").GetInt32()), Table(root.GetProperty("superTable").GetInt32()));
    }

    private const int Walk = 0, Run = 1, Roll = 2, Roll2 = 3, Push = 4;

    public void Animate(SonicObject o)
    {
        bool isSuper = ram.U8(Vars.SuperSonicFlag) != 0;
        var table = isSuper ? super : normal;
        int anim = o.Anim;
        if (anim != o.PrevAnim)
        {
            o.PrevAnim = (byte)anim;
            o.AnimFrame = 0;
            o.AnimFrameDuration = 0;
            o.Pushing = false;
        }
        var script = table[anim];
        byte duration = script[0];
        if ((sbyte)duration >= 0)
        {
            SetFlipFromStatus(o);
            if ((sbyte)--o.AnimFrameDuration >= 0) return;
            o.AnimFrameDuration = duration;
            Step(o, script);
            return;
        }
        if (duration == 0xFF) { WalkRun(o, table, isSuper); return; }

        // $FE (roll) and $FD (push) share a first timer decrement (0x1F36E).
        if ((sbyte)--o.AnimFrameDuration >= 0) return;
        if (duration == 0xFE) RollAnim(o, table);
        else PushAnim(o, table);
    }

    private void SetFlipFromStatus(SonicObject o) =>
        o.RenderFlags = (o.RenderFlags & ~3) | (o.Status & 1);   // face the way the player faces

    /// <summary>Anim_Do (0x1F192): show the next frame or execute a command byte.</summary>
    private static void Step(SonicObject o, Script script)
    {
        int d1 = o.AnimFrame;
        byte d0 = script[1 + d1];
        if (d0 < 0xF0) { Show(o, d0); return; }
        switch (d0)
        {
            case 0xFF:
                o.AnimFrame = 0;
                Show(o, script[1]);
                return;
            case 0xFE:
            {
                int back = script[2 + d1];
                o.AnimFrame = (byte)(o.AnimFrame - back);
                Show(o, script[1 + (byte)(d1 - back)]);
                return;
            }
            case 0xFD:
                o.Anim = script[2 + d1];
                return;
        }
    }

    private static void Show(SonicObject o, byte frame)
    {
        o.MappingFrame = frame;
        o.AnimFrame++;
    }

    /// <summary>Walk/run (0x1F1E2): frame set chosen by ground angle, speed by inertia.</summary>
    private void WalkRun(SonicObject o, Script[] table, bool isSuper)
    {
        if (o.FlipAngle != 0) { FlipAnim(o); return; }
        int d1 = 0;
        int d0 = o.Angle;
        if ((sbyte)d0 > 0) d0--;
        int d2 = o.Status & 1;
        if (d2 == 0) d0 = ~d0 & 0xFF;
        d0 = (d0 + 0x10) & 0xFF;
        if ((sbyte)d0 < 0) d1 = 3;
        o.RenderFlags = (o.RenderFlags & ~3) | (d2 ^ d1);
        if (o.Pushing) { PushAnimFromWalk(o, table); return; }
        d0 = (d0 >> 4) & 6;
        int speed = Math.Abs((int)o.Inertia) & 0xFFFF;
        if ((sbyte)o.StatusSecondary < 0) speed = (speed * 2) & 0xFFFF;
        if (isSuper) throw new UnportedException(0x1F296, "Super Sonic walk/run animation");

        Script script = table[Run];
        if (speed < 0x600) { script = table[Walk]; d0 = (d0 * 2) & 0xFF; }
        d0 = (d0 * 2) & 0xFF;
        int d3 = d0;
        int index = o.AnimFrame;
        byte frame = script[1 + index];
        if (frame == 0xFF) { o.AnimFrame = 0; frame = script[1]; }
        o.MappingFrame = (byte)(frame + d3);
        if ((sbyte)--o.AnimFrameDuration >= 0) return;
        int dur = -speed + 0x800;
        if ((short)dur < 0) dur = 0;
        o.AnimFrameDuration = (byte)((dur & 0xFFFF) >> 8);
        o.AnimFrame++;
    }

    /// <summary>0x1F308: corkscrew/flip frames from the flip angle.</summary>
    private static void FlipAnim(SonicObject o)
    {
        int d0 = o.FlipAngle;
        if ((o.Status & 1) == 0)
        {
            o.RenderFlags &= 0xFC;
            d0 = (d0 + 0x0B) & 0xFF;
        }
        else
        {
            o.RenderFlags &= 0xFC;
            if (o.FlipTurned != 0) { o.RenderFlags |= 1; d0 = (d0 + 0x0B) & 0xFF; }
            else { o.RenderFlags |= 3; d0 = (-d0 + 0x8F) & 0xFF; }
        }
        o.MappingFrame = (byte)(d0 / 0x16 + 0x5F);
        o.AnimFrameDuration = 0;
    }

    /// <summary>Roll (0x1F37A): faster script above $600, frame time from inertia.</summary>
    private void RollAnim(SonicObject o, Script[] table)
    {
        int speed = Math.Abs((int)o.Inertia) & 0xFFFF;
        var script = speed >= 0x600 ? table[Roll2] : table[Roll];
        if (o.Id != 1) throw new UnportedException(0x1F39A, "Tails roll animation");
        int dur = -speed + 0x400;
        if ((short)dur < 0) dur = 0;
        o.AnimFrameDuration = (byte)((dur & 0xFFFF) >> 8);
        SetFlipFromStatus(o);
        Step(o, script);
    }

    /// <summary>Push (0x1F3D4), reached directly from Animate after one timer decrement.</summary>
    private void PushAnim(SonicObject o, Script[] table)
    {
        if ((sbyte)--o.AnimFrameDuration >= 0) return;
        PushBody(o, table);
    }

    /// <summary>Push reached from the walk code (status bit 5): its single timer decrement.</summary>
    private void PushAnimFromWalk(SonicObject o, Script[] table)
    {
        if ((sbyte)--o.AnimFrameDuration >= 0) return;
        PushBody(o, table);
    }

    private void PushBody(SonicObject o, Script[] table)
    {
        int d2 = o.Inertia;
        if (d2 >= 0) d2 = -d2;
        d2 += 0x800;
        if ((short)d2 < 0) d2 = 0;
        o.AnimFrameDuration = (byte)((d2 & 0xFFFF) >> 6);
        SetFlipFromStatus(o);
        Step(o, table[Push]);
    }
}
