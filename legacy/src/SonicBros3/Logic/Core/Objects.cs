namespace SonicBros3.Logic;

/// <summary>
/// RunObjects (ROM 0x19960) for the dynamic object slots, the shared object helpers, and the registry of ported
/// object implementations. Object types that are not ported yet are kept inert: they only run MarkObjGone so
/// they despawn like the original instead of filling every slot.
/// </summary>
public sealed class Objects
{
    public const int FirstDynamicSlot = 0xB400, DynamicSlots = 0x70, SlotSize = 0x40;

    /// <summary>An object slot's number (objects refer to each other by it: (address - $B000) / $40).</summary>
    public static int SlotIndex(int address) => ((address - Vars.MainCharacter) >> 6) & 0x7F;
    /// <summary>The slot with a given number.</summary>
    public static SonicObject Slot(Ram ram, int index) => new(ram, Vars.MainCharacter + (index << 6));

    private readonly Ram ram;
    /// <summary>Ported objects for the hack zones' object table (0x199CA, second pointer of each entry).</summary>
    private readonly Dictionary<int, Action<SonicObject>> _ported;
    /// <summary>Ported objects for Sonic 2's own zones' column (first pointer) where it differs from the hack zones'.</summary>
    private readonly Dictionary<int, Action<SonicObject>> _s2Ported = [];
    /// <summary>The object table (0x199CA): per id, the Sonic 2 zones' routine and the hack zones' routine.</summary>
    private readonly (int Base, byte[] Data) _table;

    /// <summary>The routine the object table gives an id in Sonic 2's zones (first) or the hack's (second).</summary>
    public int Routine(int id, bool hackZones)
    {
        int a = id * 8 + (hackZones ? 4 : 0);
        return (_table.Data[a] << 24) | (_table.Data[a + 1] << 16) | (_table.Data[a + 2] << 8) | _table.Data[a + 3];
    }

    /// <summary>
    /// The handler for an id in Sonic 2's zones: its own port, or the hack zones' port when both columns point at the
    /// same routine (the same code), else null (not ported).
    /// </summary>
    private Action<SonicObject>? S2Handler(int id)
    {
        if (_s2Ported.TryGetValue(id, out var h)) return h;
        return Routine(id, false) == Routine(id, true) && _ported.TryGetValue(id, out h) ? h : null;
    }

    /// <summary>Registers an object for Sonic 2's zones (the table's first column).</summary>
    public void RegisterS2(int id, Action<SonicObject> run) => _s2Ported[id] = run;

    public bool IsPortedS2(int id) => S2Handler(id) is not null;

    /// <summary>Whether an id is ported for the kind of zone being played ($FFE6).</summary>
    public bool IsPortedHere(int id) => ram.U8(Vars.HackZoneFlag) != 0 ? IsPorted(id) : IsPortedS2(id);

    public Objects(ObjectContext ctx)
    {
        ram = ctx.Ram;
        _table = ctx.Art.DataTable("object-table.json");
        var monitor = new Monitor(ctx);
        var contents = new MonitorContents(ctx);
        var explosion = new Explosion(ctx);
        var motobug = new Motobug(ctx);
        var animal = new Animal(ctx);
        var points = new Points(ctx);
        var lostRings = new LostRings(ctx, this);
        var bridge = new Bridge(ctx);
        var tailsTails = ctx.TailsAnimation is { } ta ? new TailsTails(ctx, ta, ctx.TailsSprites, ctx.Vram) : null;
        _ported = new()
        {
            [0x03] = o => PlaneSwitcher.Step(ram, o),
            [0x26] = monitor.Run,
            [0x27] = explosion.Run,
            [0x2E] = contents.Run,
            [0x28] = animal.Run,
            [0x29] = points.Run,
            [0x37] = lostRings.Run,
            [0x11] = bridge.Run,
            [0x40] = motobug.Run,
        };
        if (tailsTails is not null) _ported[0x05] = tailsTails.Run;
        _ported[0x08] = new Dust(ctx).Run;
        _ported[0x36] = new Spikes(ctx).Run;
        _ported[0x41] = new Spring(ctx).Run;
        _ported[0x18] = new Platform(ctx).Run;
        _ported[0x1C] = new Scenery(ctx).Run;
        _ported[0x1A] = new Ledge(ctx).Run;
        _ported[0x3B] = new Rock(ctx).Run;
        _ported[0x3C] = new Wall(ctx).Run;
        _ported[0x15] = new SwingingPlatform(ctx).Run;
        _ported[0x44] = new EdgeWall(ctx).Run;
        _ported[0x1F] = new Crabmeat(ctx).Run;
        _ported[0x22] = new BuzzBomber(ctx).Run;
        _ported[0x23] = new Missile(ctx).Run;
        _ported[0x2B] = new Chopper(ctx).Run;
        _ported[0x42] = new Newtron(ctx).Run;
        _ported[0x49] = new WaterfallSound(ctx).Run;
        _ported[0x06] = new ForceRoll(ctx).Run;
        _ported[0x79] = new Starpost(ctx).Run;
        _ported[0x39] = new GameOver(ctx).Run;
        _ported[0x34] = new TitleCardObject(ctx).Run;
        // Marble Zone
        _ported[0x54] = new LavaMarker(ctx).Run;
        _ported[0x71] = new InvisibleBlock(ctx).Run;
        _ported[0x2F] = new GrassPlatform(ctx).Run;
        _ported[0x19] = new GrassFire(ctx).Run;
        _ported[0x13] = new LavaBallMaker(ctx).Run;
        _ported[0x14] = new LavaBall(ctx).Run;
        _ported[0x46] = new MarbleBrick(ctx).Run;
        _ported[0x30] = new GlassBlock(ctx).Run;
        _ported[0x31] = new ChainedStomper(ctx).Run;
        _ported[0x32] = new Button(ctx).Run;
        _ported[0x33] = new PushBlock(ctx).Run;
        _ported[0x52] = new MovingBlock(ctx).Run;
        _ported[0x55] = new Basaran(ctx, this).Run;
        _ported[0x78] = new Caterkiller(ctx).Run;
        _ported[0x4C] = new GeyserMaker(ctx).Run;
        _ported[0x4D] = new Geyser(ctx).Run;
        _ported[0x4E] = new LavaWall(ctx).Run;
        _ported[0x51] = new BreakableBlock(ctx).Run;
        _ported[0x53] = new CollapsingFloor(ctx).Run;
        _ported[0x73] = new MzBoss(ctx).Run;
        // The SMB3 Easter egg (zone $40).
        _ported[Smb3.Block] = new Smb3Block(ctx).Run;
        var goomba = new Smb3Goomba(ctx);
        _ported[Smb3.Goomba] = goomba.Run;
        _ported[Smb3.Paragoomba] = goomba.Run;
        var koopa = new Smb3Koopa(ctx);
        _ported[Smb3.Koopa] = koopa.Run;
        _ported[Smb3.Paratroopa] = koopa.Run;
        _ported[Smb3.Piranha] = new Smb3Piranha(ctx).Run;
        _ported[Smb3.Fireball] = new Smb3Fireball(ctx).Run;
        _ported[Smb3.GoombaPipe] = new Smb3GoombaPipe(ctx).Run;
        _ported[Smb3.MicroGoomba] = new Smb3MicroGoomba(ctx).Run;
        _ported[Smb3.BoomerangBro] = new Smb3BoomerangBro(ctx).Run;
        _ported[Smb3.Boomerang] = new Smb3Boomerang(ctx).Run;
        _ported[0x74] = new BossFire(ctx).Run;
        _ported[0x4B] = new GiantRing(ctx).Run;
        _ported[0xDE] = new GiantRingFlash(ctx).Run;
        _ported[0x7D] = o => Objects.DeleteObject(ram, o);   // hack zones: DeleteObject (0x1A0C2)
        _ported[0x25] = new Ring(ctx).Run;
        // In the SMB3 zone the signpost's slot holds SMB3's goal roulette instead.
        var signpost = new Signpost(ctx);
        var goal = new RouletteGoal(ctx);
        _ported[0x0D] = o => { if (Camera.IsEasterEgg(ctx.Ram.U8(Vars.CurrentZoneAndAct))) goal.Run(o); else signpost.Run(o); };
        _ported[RouletteGoal.TextId] = goal.Run;
        _ported[0x3E] = new EggPrison(ctx).Run;
        _ported[0x38] = new Shield(ctx).Run;
        _ported[0x35] = new InvincibilityStars(ctx).Run;
        _ported[0x3A] = new Results(ctx).Run;
        _ported[0x17] = new Helix(ctx).Run;
        _ported[0x3D] = new GhzBoss(ctx).Run;
        _ported[0x48] = new WreckingBall(ctx).Run;
        _ported[0x58] = new BossExplosion(ctx).Run;
        _ported[0x3F] = o => Objects.DeleteObject(ram, o);   // hack zones: DeleteObject (0x1A0C2)

        // Sonic 2's zones (the object table's first column), where it differs from the hack's.
        // Emerald Hill
        RegisterS2(0x49, new Waterfall(ctx).Run);
        RegisterS2(0x36, new SpikesS2(ctx).Run);
        RegisterS2(0x1C, new SceneryS2(ctx).Run);
        RegisterS2(0x4B, new Buzzer(ctx).Run);
        RegisterS2(0x5C, new Masher(ctx).Run);
        RegisterS2(0x9D, new Coconuts(ctx).Run);
        RegisterS2(0x98, new Coconut(ctx).Run);
        RegisterS2(0x06, new Spiral(ctx).Run);
        RegisterS2(0x56, new EhzBoss(ctx).Run);
        RegisterS2(0xDD, _ported[0x4B]);   // the giant ring (the same code, 0x150FE)
    }

    public bool IsPorted(int id) => _ported.ContainsKey(id);

    public HashSet<int> UnportedSeen { get; } = [];

    /// <summary>RunObjects' loop counter d7 for the slot being run ($8F at $B000, counting down); some objects read it.</summary>
    public int SlotCounter { get; private set; }

    /// <summary>Runs every occupied slot after the players ($B080-), in slot order.</summary>
    /// <param name="playerDying">
    /// Sonic's routine was 6 or more when RunObjects started (0x1997E): then only the first and last 16 slots run,
    /// and the dynamic slots in between are just redrawn (if they were on screen), freezing the level.
    /// </param>
    public void RunAfterPlayers(bool playerDying = false)
    {
        for (int a = Vars.TitleCardZoneName; a < Vars.ObjectRamEnd; a += SlotSize)   // $90 slots from $B000
        {
            int id = ram.U8(a);
            if (id == 0) continue;
            var o = new SonicObject(ram, a);
            if (playerDying && ram.U16(Vars.TwoPlayerMode) == 0)
            {
                if (a >= FirstDynamicSlot && a < Vars.DynamicObjectRamEnd)
                {
                    if (o.OnScreen) SpriteBuilder.DisplaySprite(ram, o);   // 0x199B8
                    continue;
                }
                SlotCounter = a < FirstDynamicSlot ? 0x0F - SlotIndex(a) : 0x0F - ((a - Vars.DynamicObjectRamEnd) >> 6);
            }
            else SlotCounter = 0x8F - SlotIndex(a);
            var step = ram.U8(Vars.HackZoneFlag) != 0 ? _ported.GetValueOrDefault(id) : S2Handler(id);
            if (step is not null) step(o);
            else { UnportedSeen.Add(id); MarkObjGone(ram, o); }
        }
    }

    /// <summary>Runs the occupied slots in [start, end) (the title card loop, where nothing else exists yet).</summary>
    public void RunRange(int start, int end)
    {
        for (int a = start; a < end; a += SlotSize)
        {
            int id = ram.U8(a);
            if (id == 0) continue;
            SlotCounter = 0x8F - SlotIndex(a);
            var step = ram.U8(Vars.HackZoneFlag) != 0 ? _ported.GetValueOrDefault(id) : S2Handler(id);
            if (step is not null) step(new SonicObject(ram, a));
        }
    }

    /// <summary>SingleObjLoad (0x1BD56): first free dynamic slot, or -1.</summary>
    public static int FindFreeSlot(Ram ram)
    {
        int count = ram.U16(Vars.TwoPlayerMode) != 0 ? 0x28 : DynamicSlots;
        for (int i = 0; i < count; i++)
        {
            int a = FirstDynamicSlot + i * SlotSize;
            if (ram.U8(a) == 0) return a;
        }
        return -1;
    }

    /// <summary>MarkObjGone (0x1A184): delete the object once it is more than $280 px outside the load window.</summary>
    public static void MarkObjGone(Ram ram, SonicObject o)
    {
        if (ram.U16(Vars.TwoPlayerMode) != 0) return;
        int d0 = ((o.X & 0xFF80) - ram.U16(Vars.CameraXPosCoarse)) & 0xFFFF;
        if (d0 <= 0x280) return;
        int index = o.RespawnIndex;
        if (index != 0) ram.Clear(Vars.ObjectRespawnTable + 2 + index, 7);
        DeleteObject(ram, o);
    }

    /// <summary>DeleteObject (0x1A22C): clear the whole slot.</summary>
    public static void DeleteObject(Ram ram, SonicObject o)
    {
        for (int i = 0; i < SlotSize; i++) ram.W8(o.Address + i, 0);
    }
}
