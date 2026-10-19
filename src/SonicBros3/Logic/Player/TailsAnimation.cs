using System.Text.Json;

namespace SonicBros3.Logic;

/// <summary>
/// Tails_Animate (ROM 0x20C24): the same script format as Sonic's, with Tails' own speed-driven walk/run (three
/// speed sets), roll and push handling. The interpreter body (from 0x20C2A) is shared by his tails (object 05),
/// which uses its own script table and a directional command ($FC) that points the tails along his velocity.
/// Scripts are kept as raw blocks, as the code addresses some of them directly.
/// </summary>
public sealed class TailsAnimation : IPlayerAnimation
{
    private readonly Ram _ram;
    private readonly byte[] _block;
    private readonly int _table, _walk, _run, _fastest, _roll, _roll2, _push;

    /// <summary>The tails object's data: anim map (Tails' anim -> tails anim), scripts and the directional set.</summary>
    public byte[] TailsBlock { get; }
    public int TailsTable { get; }
    public int TailsDirectional { get; }

    public TailsAnimation(Ram ram, string dataDir)
    {
        _ram = ram;
        using (var doc = JsonDocument.Parse(S2Port.Extraction.DataFile.ReadAllText(Path.Combine(dataDir, "objects", "tails", "animations.json"))))
        {
            var r = doc.RootElement;
            _block = r.GetProperty("data").EnumerateArray().Select(b => (byte)b.GetInt32()).ToArray();
            _table = r.GetProperty("table").GetInt32();
            _walk = r.GetProperty("walk").GetInt32();
            _run = r.GetProperty("run").GetInt32();
            _fastest = r.GetProperty("fastest").GetInt32();
            _roll = r.GetProperty("roll").GetInt32();
            _roll2 = r.GetProperty("roll2").GetInt32();
            _push = r.GetProperty("push").GetInt32();
        }
        using (var doc = JsonDocument.Parse(S2Port.Extraction.DataFile.ReadAllText(Path.Combine(dataDir, "objects", "tails-tails", "animations.json"))))
        {
            var r = doc.RootElement;
            TailsBlock = r.GetProperty("data").EnumerateArray().Select(b => (byte)b.GetInt32()).ToArray();
            TailsTable = r.GetProperty("table").GetInt32();
            TailsDirectional = r.GetProperty("directional").GetInt32();
        }
    }

    public void Animate(SonicObject o) => Run(o, _block, _table, parent: null);

    /// <summary>The interpreter from 0x20C2A with a1 = the given table. parent is a2 for the tails' $FC command.</summary>
    public void Run(SonicObject o, byte[] block, int table, SonicObject? parent)
    {
        int anim = o.Anim;
        if (anim != o.PrevAnim)
        {
            o.PrevAnim = (byte)anim;
            o.AnimFrame = 0;
            o.AnimFrameDuration = 0;
            o.Pushing = false;
        }
        int script = table + (short)((block[table + anim * 2] << 8) | block[table + anim * 2 + 1]);
        int d0 = block[script];
        if ((sbyte)d0 >= 0)
        {
            o.RenderFlags = (o.RenderFlags & ~3) | (o.Status & 1);   // face the way the player faces
            o.AnimFrameDuration--;
            if ((sbyte)o.AnimFrameDuration >= 0) return;
            o.AnimFrameDuration = (byte)d0;
            Step(o, block, script);
            return;
        }
        o.AnimFrameDuration--;
        if ((sbyte)o.AnimFrameDuration >= 0) return;
        // 0x20CC2/0x20DCE: $FF walk/run, $FE roll; any other negative duration is the directional command (0x20E44).
        // (Pushing is reached from the walk code through status bit 5, not through a script byte.)
        switch (d0)
        {
            case 0xFF: WalkRun(o); return;
            case 0xFE: Roll(o); return;
            default: Directional(o, block, parent); return;
        }
    }

    /// <summary>0x20C72: show the next frame or run a command ($FF loop, $FE back, $FD switch).</summary>
    private static void Step(SonicObject o, byte[] block, int script)
    {
        int d1 = o.AnimFrame;
        int d0 = block[script + 1 + d1];
        if (d0 < 0xF0) { Show(d0); return; }
        switch (d0)
        {
            case 0xFF:
                o.AnimFrame = 0;
                Show(block[script + 1]);
                return;
            case 0xFE:
            {
                int back = block[script + 2 + d1];
                o.AnimFrame = (byte)(o.AnimFrame - back);
                Show(block[script + 1 + (byte)(d1 - back)]);
                return;
            }
            case 0xFD: o.Anim = block[script + 2 + d1]; return;
            default: return;
        }

        void Show(int frame)
        {
            o.MappingFrame = (byte)frame;
            o.AnimFrame++;
        }
    }

    /// <summary>0x20CC8: walk/run by speed (three sets), frame set by ground angle; flips via the flip angle.</summary>
    private void WalkRun(SonicObject o)
    {
        if (o.FlipAngle != 0) { Flip(o); return; }
        int d1 = 0;
        int d0 = o.Angle;
        if ((sbyte)d0 > 0) d0--;
        int d2 = o.Status & 1;
        if (d2 == 0) d0 = ~d0 & 0xFF;
        d0 = (d0 + 0x10) & 0xFF;
        if ((sbyte)d0 < 0) d1 = 3;
        o.RenderFlags = (o.RenderFlags & ~3) | (d2 ^ d1);
        if (o.Pushing) { PushBody(o); return; }
        d0 = (d0 >> 4) & 6;
        int speed = Math.Abs((int)o.Inertia) & 0xFFFF;
        if ((sbyte)o.StatusSecondary < 0) speed = (speed * 2) & 0xFFFF;
        int d3 = (d0 * 4) & 0xFF;
        int script = _walk;
        if (speed >= 0x600)
        {
            script = _run;
            int half = (d0 & 0xFF) >> 1;
            d0 = ((d0 + half) * 2) & 0xFF;
            d3 = d0;
            if (speed >= 0x700) script = _fastest;
        }
        int dur = -speed + 0x800;
        if ((short)dur < 0) dur = 0;
        o.AnimFrameDuration = (byte)((dur & 0xFFFF) >> 8);
        Step(o, _block, script);
        o.MappingFrame = (byte)(o.MappingFrame + d3);
    }

    /// <summary>0x20D68: corkscrew/flip frames ($75 + angle / $16).</summary>
    private static void Flip(SonicObject o)
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
        o.MappingFrame = (byte)((d0 / 0x16 + 0x75) & 0xFF);
        o.AnimFrameDuration = 0;
    }

    /// <summary>0x20DD2: roll, faster script from $600.</summary>
    private void Roll(SonicObject o)
    {
        int speed = Math.Abs((int)o.Inertia) & 0xFFFF;
        int script = speed >= 0x600 ? _roll2 : _roll;
        int dur = -speed + 0x400;
        if ((short)dur < 0) dur = 0;
        o.AnimFrameDuration = (byte)((dur & 0xFFFF) >> 8);
        o.RenderFlags = (o.RenderFlags & ~3) | (o.Status & 1);   // face the way the player faces
        Step(o, _block, script);
    }

    /// <summary>0x20E12.</summary>
    private void PushBody(SonicObject o)
    {
        int d2 = o.Inertia;
        if (d2 >= 0) d2 = -d2;
        d2 += 0x800;
        if ((short)d2 < 0) d2 = 0;
        o.AnimFrameDuration = (byte)((d2 & 0xFFFF) >> 6);
        o.RenderFlags = (o.RenderFlags & ~3) | (o.Status & 1);   // face the way the player faces
        Step(o, _block, _push);
    }

    /// <summary>
    /// 0x20E44, the tails object's $FC command: point the tails along the parent's velocity (CalcAngle), 4 frames
    /// per direction set, 3 frames each.
    /// </summary>
    private void Directional(SonicObject o, byte[] block, SonicObject? parent)
    {
        if (parent is null) throw new UnportedException(0x20E44, "directional tails animation without a parent");
        int d0 = MdMath.CalcAngle(parent.XVel, parent.YVel);
        if (o.Id != 5) d0 = (d0 + _ram.U8(Vars.ObjectRamBlockIndices)) & 0xFF;
        int d1 = 0;
        int d2 = o.Status & 1;
        d0 = d2 == 0 ? ~d0 & 0xFF : (d0 + 0x80) & 0xFF;
        d0 = (d0 + 0x10) & 0xFF;
        if ((sbyte)d0 < 0) d1 = 3;
        o.RenderFlags = (o.RenderFlags & ~3) | (d2 ^ d1);
        int d3 = (d0 >> 3) & 0x0C;
        if (o.Id != 5) throw new UnportedException(0x20E98, "directional animation for other objects (table 0x15FD44)");
        o.AnimFrameDuration = 3;
        Step(o, block, TailsDirectional);
        o.MappingFrame = (byte)(o.MappingFrame + d3);
    }
}

/// <summary>
/// Object 05, Tails' tails (ROM 0x21076): follows Tails ($3E), mirrors his angle, status and position, picks its
/// animation from his (pushing = 4) through a map, animates with Tails_Animate's interpreter and loads its own
/// frame tiles to VRAM tile $7B0.
/// </summary>
public sealed class TailsTails(ObjectContext ctx, TailsAnimation anim, SpriteData? sprites, byte[]? vram) : GameObject(ctx)
{
    /// <summary>$30: the animation it last picked from Tails'.</summary>
    private const int LastParentAnim = 0x30;

    public override void Run(SonicObject o)
    {
        if (o.Routine == 0)
        {
            o.Routine = 2;
            o.Mappings = 0x7A6F6;
            o.ArtTile = 0x7B0;
            o.Priority = 2;
            o.WidthPixels = 0x18;
            o.RenderFlags = Render.LevelCoords;
        }
        var tails = new SonicObject(Ram, o.Word(0x3E));   // $3E: Tails' slot
        o.Angle = tails.Angle;
        o.Status = tails.Status;
        o.X = tails.X;
        o.Y = tails.Y;
        o.ArtTile &= 0x7FFF;
        if ((short)tails.ArtTile < 0) o.ArtTile |= 0x8000;   // Tails' priority
        int parentAnim = tails.Anim;
        if (tails.Pushing) parentAnim = PlayerAnim.Push;
        if (parentAnim != o.Byte(LastParentAnim))
        {
            o.SetByte(LastParentAnim, parentAnim);
            o.Anim = anim.TailsBlock[parentAnim];   // map at the block start (0x21114)
        }
        anim.Run(o, anim.TailsBlock, anim.TailsTable, tails);
        LoadDplc(o);
        Display(o);
    }

    /// <summary>0x20FFA: as LoadTailsDynPLC, to VRAM $F600 (tile $7B0), remembering the frame at $F7DF.</summary>
    private void LoadDplc(SonicObject o)
    {
        int frame = o.MappingFrame;
        if (frame == Ram.U8(Vars.TailsTailsLastLoadedDplc)) return;
        Ram.W8(Vars.TailsTailsLastLoadedDplc, frame);
        if (sprites?.Dplc is not { } dplc || vram is null) return;
        int dest = 0xF600;
        foreach (var (first, count) in dplc[frame])
        {
            sprites.Art.AsSpan(first * 32, count * 32).CopyTo(vram.AsSpan(dest));
            dest += count * 32;
        }
    }
}
