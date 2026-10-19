namespace SonicBros3.Logic;

/// <summary>A Sonic-rules adaptation of SMB3's alternating walk/stop brother and its paired throws.</summary>
public sealed class Smb3BoomerangBro(ObjectContext ctx) : GameObject(ctx)
{
    private const int Phase = 0x32, Timer = 0x34, ThrowCounter = 0x36, ThrowPose = 0x38;

    public override void Run(SonicObject o)
    {
        if (o.Routine == 0)
        {
            Smb3.Init(Ram, o, Smb3.ArtBoomerangBro, Smb3.ArtTileSprites, 8, 0x0B);
            o.XRadius = 8;
            o.YRadius = 8;
            o.SetWord(Timer, 0x10);
        }
        int tick = Smb3.Tick(o);
        int phase = o.Byte(Phase), timer = o.Word(Timer) - 1;
        if (timer <= 0)
        {
            phase = (phase + 1) & 3;
            o.SetByte(Phase, phase);
            timer = (phase & 1) != 0 ? 0x40 : 0x10;
        }
        o.SetWord(Timer, timer);
        bool walking = (phase & 1) != 0;
        if (phase == 1) o.Status |= Smb3.FacingRight;
        else if (phase == 3) o.Status &= ~Smb3.FacingRight;
        bool grounded = Smb3.Walk(Ram, Ctx.Collision, o, walking ? 0x80 : 0, false);
        if (grounded && tick == 256) o.YVel = -0x300;
        Smb3.FaceSonic(Ram, o);
        int pose = Math.Max(o.Byte(ThrowPose) - 1, 0);
        if (walking)
        {
            int counter = o.Byte(ThrowCounter);
            o.SetByte(ThrowCounter, counter + 1);
            if ((counter & 0xBF) == 0x1F && Spawn() is { } projectile)
            {
                projectile.Id = Smb3.Boomerang;
                projectile.X = o.X;
                projectile.Y = o.Y - 16;
                projectile.XVel = (short)(Smb3.IsFacingRight(o) ? 0x200 : -0x200);
                projectile.YVel = -0x160;
                projectile.SetWord(Smb3Boomerang.Owner, o.Address);
                projectile.SetByte(Smb3Boomerang.OwnerRespawn, o.RespawnIndex);
                pose = 0x0F;
            }
        }
        o.SetByte(ThrowPose, pose);
        o.MappingFrame = ((tick >> 3) & 1) + (pose > 0 ? 2 : 0);
        Smb3.SetFlip(o);
        DisplayOrMarkGone(o);
    }
}

/// <summary>A spinning, damaging arc which reverses horizontally and is caught by its owner on return.</summary>
public sealed class Smb3Boomerang(ObjectContext ctx) : GameObject(ctx)
{
    public const int Owner = 0x3C, OwnerRespawn = 0x3E;
    private const int Age = 0x30, Direction = 0x32;

    public override void Run(SonicObject o)
    {
        if (o.Routine == 0)
        {
            Smb3.Init(Ram, o, Smb3.ArtBoomerang, Smb3.ArtTileSprites, 4, 0x80 | 0x0B);
            o.SetWord(Direction, o.XVel < 0 ? -1 : 1);
        }
        int age = o.Word(Age) + 1;
        o.SetWord(Age, age);
        ObjectKit.Move(o);
        int direction = (short)o.Word(Direction);
        o.XVel = (short)Math.Clamp(o.XVel - direction * 0x10, -0x200, 0x200);
        o.YVel += 0x0B;
        o.MappingFrame = (age >> 2) & 3;
        int ownerAddress = o.Word(Owner);
        if (age > 32 && ownerAddress >= Objects.FirstDynamicSlot && ownerAddress < Vars.DynamicObjectRamEnd &&
            (ownerAddress - Objects.FirstDynamicSlot) % Objects.SlotSize == 0)
        {
            var owner = new SonicObject(Ram, ownerAddress);
            if (owner.Id == Smb3.BoomerangBro && owner.RespawnIndex == o.Byte(OwnerRespawn) &&
                Math.Abs((short)(o.X - owner.X)) < 16 && Math.Abs((short)(o.Y - (owner.Y - 16))) < 24)
            {
                Delete(o);
                return;
            }
        }
        if (age >= 128 || OutOfRange(o.X)) { Delete(o); return; }
        Display(o);
    }
}
