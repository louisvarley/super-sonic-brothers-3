namespace SonicBros3.Logic;

/// <summary>
/// The player's dust and splash effects, object $08 (ROM 0x21BA4): one per player ($D100 for Sonic, $D140 for
/// Tails). The animation says which effect shows: 0 none, 1 water splash, 2 spindash dust (follows the player while
/// they charge), 3 skid puff.
/// Routine <see cref="SkidEmitter"/> is set by the player while skidding: every 4 frames it spawns a puff at the
/// player's feet. Tiles are loaded per frame through DPLCs, to the VRAM address at $3C.
/// </summary>
public sealed class Dust(ObjectContext ctx) : GameObject(ctx)
{
    public const uint Mappings = 0x21DDE;
    private readonly SpriteData _data = ctx.Art["dust"];

    private const int Init = 0, Main = 2, Gone = 4;
    public const int SkidEmitter = 6;
    private const int AnimNone = 0, AnimSplash = 1, AnimSpindash = 2, AnimSkid = 3;
    private const int PlayerAnimSkid = 0x0D;

    /// <summary>The effect's own variables.</summary>
    private readonly struct State(SonicObject o)
    {
        /// <summary>$30: the frame whose tiles are loaded.</summary>
        public int LoadedFrame { get => o.Byte(0x30); set => o.SetByte(0x30, value); }
        /// <summary>$32: frames until the next skid puff.</summary>
        public int PuffTimer { get => o.Byte(0x32); set => o.SetByte(0x32, value); }
        /// <summary>$34: set for Tails' effect (drawn 4 px higher: Tails is shorter).</summary>
        public bool ForTails { get => o.Byte(0x34) != 0; set => o.SetByte(0x34, value ? 1 : 0); }
        /// <summary>$3C: the VRAM address its tiles load to.</summary>
        public int VramAddress { get => o.Word(0x3C); set => o.SetWord(0x3C, value); }
        /// <summary>$3E: the player's slot.</summary>
        public int PlayerSlot { get => o.Word(0x3E); set => o.SetWord(0x3E, value); }
    }

    public override void Run(SonicObject o)
    {
        var s = new State(o);
        switch (o.Routine)
        {
            case Init: Initialise(o, s); Animate(o, s); break;
            case Main: Animate(o, s); break;
            case Gone: Delete(o); break;
            case SkidEmitter: Skid(o, s); break;
        }
    }

    private void Initialise(SonicObject o, State s)
    {
        o.Routine = Main;
        o.Mappings = Mappings;
        o.RenderFlags |= Render.LevelCoords;
        o.Priority = 1;
        o.WidthPixels = 0x10;
        o.ArtTile = 0x49C;
        s.PlayerSlot = Vars.MainCharacter;
        s.VramAddress = 0x9380;
        if (o.Address == PlayerProfile.Sonic.Dust) return;
        s.ForTails = true;
        if (Ram.U16(Vars.PlayerMode) == 2) return;   // Tails alone: he is the main character
        o.ArtTile = 0x48C;
        s.PlayerSlot = Vars.Sidekick;
        s.VramAddress = 0x9180;
    }

    /// <summary>0x21C14.</summary>
    private void Animate(SonicObject o, State s)
    {
        var player = new SonicObject(Ram, s.PlayerSlot);
        switch (o.Anim)
        {
            case AnimSplash:   // at the water surface
                o.Y = Ram.U16(Vars.WaterLevel1);
                if (o.PrevAnim == AnimNone)
                {
                    o.X = player.X;
                    o.Status = 0;
                    o.ArtTile &= 0x7FFF;
                }
                break;
            case AnimSpindash:
                if (player.AirLeft < 0x0C || player.Routine >= 4 || player.SpindashFlag == 0) { o.Anim = AnimNone; return; }
                o.X = player.X;
                o.Y = player.Y;
                o.Status = player.Status & ObjStatus.XFlip;
                if (s.ForTails) o.Y -= 4;
                if (o.PrevAnim == AnimNone)
                {
                    o.ArtTile &= 0x7FFF;
                    if ((short)player.ArtTile < 0) o.ArtTile |= 0x8000;   // match the player's priority
                }
                break;
            case AnimSkid:
                if (player.AirLeft < 0x0C) { o.Anim = AnimNone; return; }
                break;
        }
        ObjectKit.Animate(Ram, o, _data.Animations!);
        LoadDplc(o, s);
        Display(o);
    }

    /// <summary>0x21CCE: while the player skids, spawn a puff every 4 frames.</summary>
    private void Skid(SonicObject o, State s)
    {
        var player = new SonicObject(Ram, s.PlayerSlot);
        if (player.Anim != PlayerAnimSkid)
        {
            o.Routine = Main;
            s.PuffTimer = 0;
            return;
        }
        s.PuffTimer--;
        if ((sbyte)s.PuffTimer < 0)
        {
            s.PuffTimer = 3;
            if (Spawn() is { } puff)
            {
                puff.Id = o.Id;
                puff.X = player.X;
                puff.Y = player.Y + 0x10;
                if (s.ForTails) puff.Y -= 4;
                puff.Status = 0;
                puff.Anim = AnimSkid;
                puff.Routine = Main;
                puff.Mappings = o.Mappings;
                puff.RenderFlags = o.RenderFlags;
                puff.Priority = 1;
                puff.WidthPixels = 4;
                puff.ArtTile = o.ArtTile & 0x7FFF;
                new State(puff).PlayerSlot = s.PlayerSlot;
                if ((short)player.ArtTile < 0) puff.ArtTile |= 0x8000;
            }
        }
        LoadDplc(o, s);
    }

    /// <summary>0x21D64: load the frame's tiles when it changes.</summary>
    private void LoadDplc(SonicObject o, State s)
    {
        int frame = o.MappingFrame;
        if (frame == s.LoadedFrame) return;
        s.LoadedFrame = frame;
        if (_data.Dplc is not { } dplc || Ctx.Vram is not { } vram) return;
        int dest = s.VramAddress;
        foreach (var (first, count) in dplc[frame])
        {
            _data.Art.AsSpan(first * 32, count * 32).CopyTo(vram.AsSpan(dest));
            dest += count * 32;
        }
    }
}
