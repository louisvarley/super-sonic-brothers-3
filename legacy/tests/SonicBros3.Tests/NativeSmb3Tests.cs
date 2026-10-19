using S2Port.Extraction;
using S2Port.Extraction.Graphics;
using S2Port.Extraction.Levels;
using S2Port.Extraction.Smb3;
using SonicBros3.Frontend;
using SonicBros3.Logic;

namespace SonicBros3.Tests;

public class NativeSmb3Tests
{
    private static ObjectContext Context(string data)
    {
        var world = new World(data, Smb3Levels.W11.Zone, 1);
        world.Ram.Bytes.AsSpan(Objects.FirstDynamicSlot, Objects.DynamicSlots * Objects.SlotSize).Clear();
        world.Ram.W32(Vars.CollisionAddr, 0xFFFF0000u | Vars.PrimaryCollision);
        return new(world.Ram, new Collision(world.Ram,
            LevelLoader.LoadCollisionSet(Path.Combine(data, "collision", "normal.json")),
            LevelLoader.LoadCollisionSet(Path.Combine(data, "collision", "alternate.json"))),
            world.Art, new NoSound());
    }

    [Theory]
    [InlineData(false, 0, -18)]
    [InlineData(false, -14, 0)]
    [InlineData(true, 0, -18)]
    [InlineData(true, 14, 0)]
    public void AirborneBall_BreaksPlainBrickOnTopAndSide(bool tails, int dx, int dy)
    {
        if (TestData.Dir is not { } data) return;
        var ctx = Context(data);
        var block = new SonicObject(ctx.Ram, Objects.FirstDynamicSlot)
            { Id = Logic.Smb3.Block, X = 328, Y = 280, Subtype = 0x67 };
        var player = tails ? ctx.Ram.Tails : ctx.Ram.Sonic;
        player.Routine = PlayerRoutine.Control;
        player.X = block.X + dx;
        player.Y = block.Y + dy;
        player.XRadius = 7; player.YRadius = 14;
        player.InAir = player.Rolling = true;
        player.Anim = PlayerAnim.Roll;
        new Smb3Block(ctx).Run(block);
        Assert.NotEqual(Logic.Smb3.Block, block.Id);
        Assert.Equal(PlayerRoutine.Control, player.Routine);
    }

    [Theory]
    [InlineData(0x67, false)]
    [InlineData(0x60, true)]
    [InlineData(0x68, true)]
    public void NonBallLandingAndContentBlocks_AreNotDestroyed(int subtype, bool rolling)
    {
        if (TestData.Dir is not { } data) return;
        var ctx = Context(data);
        var block = new SonicObject(ctx.Ram, Objects.FirstDynamicSlot)
            { Id = Logic.Smb3.Block, X = 328, Y = 280, Subtype = subtype };
        var p = ctx.Ram.Sonic;
        p.Routine = PlayerRoutine.Control;
        p.X = block.X; p.Y = block.Y - 18;
        p.XRadius = 7; p.YRadius = 14; p.InAir = true; p.Rolling = rolling;
        p.YVel = 0x100;
        new Smb3Block(ctx).Run(block);
        Assert.Equal(Logic.Smb3.Block, block.Id);
    }

    [Theory]
    [InlineData(0xA0, false, false)]
    [InlineData(0xA1, false, true)]
    [InlineData(0xA2, false, false)]
    [InlineData(0xA3, false, true)]
    [InlineData(0xA4, true, false)]
    [InlineData(0xA5, true, true)]
    [InlineData(0xA6, true, false)]
    [InlineData(0xA7, true, true)]
    public void Plants_OnlySourceFireVariantsShoot(int sourceId, bool fires, bool ceiling)
    {
        if (TestData.Dir is not { } data) return;
        var placement = Smb3ObjectConverter.Convert(new(sourceId, 20, 20))!;
        var ctx = Context(data);
        var plant = new SonicObject(ctx.Ram, Objects.FirstDynamicSlot)
        {
            Id = placement.Id, X = placement.X, Y = placement.Y, Subtype = placement.Subtype,
        };
        ctx.Ram.Sonic.X = plant.X + 128;
        ctx.Ram.Sonic.Y = plant.Y - 128;
        var handler = new Smb3Piranha(ctx);
        var expectedArt = (placement.Subtype & 3) switch
        {
            Smb3Plant.Green => Logic.Smb3.ArtPiranha,
            Smb3Plant.GreenFire => Logic.Smb3.ArtPiranhaFire,
            Smb3Plant.Red => Logic.Smb3.ArtPiranhaRed,
            _ => Logic.Smb3.ArtVenus,
        };
        int shots = 0;
        for (int frame = 0; frame < 1000; frame++)
        {
            handler.Run(plant);
            Assert.Equal(expectedArt, plant.Mappings);
            Assert.Equal(ceiling, (plant.RenderFlags & 2) != 0);
            for (int slot = Objects.FirstDynamicSlot + Objects.SlotSize; slot < Vars.DynamicObjectRamEnd; slot += Objects.SlotSize)
                if (ctx.Ram.U8(slot) == Logic.Smb3.Fireball)
                {
                    shots++;
                    var fireball = new SonicObject(ctx.Ram, slot);
                    Assert.True(ceiling ? fireball.Y > plant.Word(0x30) : fireball.Y < plant.Word(0x30));
                    Objects.DeleteObject(ctx.Ram, fireball);
                }
        }
        if (fires) Assert.True(shots >= 3, $"Fire plant ${sourceId:X2} only fired {shots} times.");
        else Assert.Equal(0, shots);
    }

    [Theory]
    [InlineData("smb3-koopa-green", 2)]
    [InlineData("smb3-koopa-red", 2)]
    [InlineData("smb3-paratroopa", 4)]
    public void TurtleOpaqueFeet_MatchFloorProbeInEveryFrame(string name, int walkFrames)
    {
        if (TestData.Dir is not { } data) return;
        using var stream = DataFile.OpenRead(Path.Combine(data, "zones", "zone_40", "objects.png"));
        var image = IndexedPng.Read(stream);
        var sprites = SpriteData.Load(data, name);
        for (int f = 0; f < sprites.Frames.Count; f++)
        {
            int bottom = int.MinValue;
            foreach (var piece in sprites.Frames[f])
            for (int y = 0; y < 16; y++)
            for (int x = 0; x < 8; x++)
            {
                int sy = (piece.Pattern & 0x1000) != 0 ? 15 - y : y;
                int sx = (piece.Pattern & 0x800) != 0 ? 7 - x : x;
                int tile = (piece.Pattern & 0x7FF) + sy / 8;
                int px = tile % 16 * 8 + sx, py = tile / 16 * 8 + sy % 8;
                if ((image.Pixels[py * image.Width + px] & 15) != 0)
                    bottom = Math.Max(bottom, piece.Y + y);
            }
            Assert.Equal(f < walkFrames ? 16 : 8, bottom);
        }
    }

    [Fact]
    public void Turtle_StandsFlushOnRaisedTerrainBox()
    {
        if (TestData.Dir is not { } data) return;
        var ctx = Context(data);
        var placement = Smb3ObjectConverter.Convert(new(0x6D, 36, 22))!;
        var turtle = new SonicObject(ctx.Ram, Objects.FirstDynamicSlot)
        {
            Id = placement.Id, X = placement.X, Y = placement.Y, YRadius = 16,
        };
        for (int frame = 0; frame < 60; frame++) Logic.Smb3.Walk(ctx.Ram, ctx.Collision, turtle, 0, false);
        Assert.Equal(384 - 1, turtle.Y + turtle.YRadius);
        Assert.Equal(0, ObjectKit.CheckFloor(ctx.Ram, ctx.Collision, turtle).Distance);
    }

    [Fact]
    public void Turtle_StandsOnConvertedBrick_AndKeepsFeetWhenBecomingShell()
    {
        if (TestData.Dir is not { } data) return;
        var ctx = Context(data);
        ctx.Ram.Sonic.X = 400;
        var block = new SonicObject(ctx.Ram, Objects.FirstDynamicSlot)
        {
            Id = Logic.Smb3.Block, X = 328, Y = 312,
        };
        var turtle = new SonicObject(ctx.Ram, Objects.FirstDynamicSlot + Objects.SlotSize)
        {
            Id = Logic.Smb3.Koopa, X = 328, Y = 240, YRadius = 16,
        };
        for (int frame = 0; frame < 90; frame++) Logic.Smb3.Walk(ctx.Ram, ctx.Collision, turtle, 0, false);
        Assert.Equal(block.Y - 9, turtle.Y + turtle.YRadius);
        int feet = turtle.Y + turtle.YRadius;
        turtle.Routine = 2;
        turtle.Status |= ObjStatus.Destroyed;
        new Smb3Koopa(ctx).Run(turtle);
        Assert.Equal(8, turtle.YRadius);
        Assert.Equal(feet, turtle.Y + turtle.YRadius);
    }

    [Fact]
    public void StompingWingedTurtle_RemovesWingsThenMakesShell_WithoutImmediateRehit()
    {
        if (TestData.Dir is not { } data) return;
        var ctx = Context(data);
        var turtle = new SonicObject(ctx.Ram, Objects.FirstDynamicSlot)
        {
            Id = Logic.Smb3.Paratroopa, X = 328, Y = 240,
        };
        var handler = new Smb3Koopa(ctx);
        handler.Run(turtle);
        void Stomp()
        {
            var player = ctx.Ram.Sonic;
            player.X = turtle.X;
            player.Y = turtle.Y - 18;
            player.YRadius = 14;
            player.InAir = true;
            player.Rolling = true;
            player.Anim = PlayerAnim.Roll;
            player.XVel = 0x300;
            player.YVel = 0x100;
            ctx.Touch.Run(player);
        }
        Stomp();
        handler.Run(turtle);
        Assert.Equal(Logic.Smb3.Koopa, turtle.Id);
        Assert.Equal(0, turtle.RoutineSecondary);
        Assert.Equal(1, turtle.CollisionProperty);
        ctx.Touch.Run(ctx.Ram.Sonic);
        Assert.Equal(Logic.Smb3.Koopa, turtle.Id);
        Assert.Equal(1, turtle.CollisionProperty);
        for (int i = 0; i < 8; i++) handler.Run(turtle);
        Stomp();
        handler.Run(turtle);
        Assert.Equal(2, turtle.RoutineSecondary);
        Assert.Equal(8, turtle.YRadius);
        Assert.Equal(0x300, ctx.Ram.Sonic.XVel);
        Assert.True(ctx.Ram.Sonic.YVel < 0);
    }

    [Fact]
    public void StompingSlidingShell_StopsItInsteadOfDestroyingIt()
    {
        if (TestData.Dir is not { } data) return;
        var ctx = Context(data);
        var turtle = new SonicObject(ctx.Ram, Objects.FirstDynamicSlot)
        {
            Id = Logic.Smb3.Koopa, X = 328, Y = 240,
        };
        var handler = new Smb3Koopa(ctx);
        handler.Run(turtle);
        turtle.RoutineSecondary = 4;
        turtle.YRadius = 8;
        turtle.CollisionProperty = 0;
        ctx.Ram.Sonic.X = turtle.X;
        ctx.Ram.Sonic.Y = turtle.Y - 18;
        ctx.Ram.Sonic.YRadius = 14;
        ctx.Ram.Sonic.InAir = true;
        ctx.Ram.Sonic.YVel = 0x200;
        ctx.Touch.Run(ctx.Ram.Sonic);
        handler.Run(turtle);
        Assert.Equal(Logic.Smb3.Koopa, turtle.Id);
        Assert.Equal(2, turtle.RoutineSecondary);
        Assert.Equal(0, turtle.XVel);
        Assert.True(ctx.Ram.Sonic.YVel < 0);
    }

    [Fact]
    public void RedTurtle_TurnsAtConvertedBrickLedge()
    {
        if (TestData.Dir is not { } data) return;
        var ctx = Context(data);
        var block = new SonicObject(ctx.Ram, Objects.FirstDynamicSlot)
        {
            Id = Logic.Smb3.Block, X = 344, Y = 312,
        };
        var turtle = new SonicObject(ctx.Ram, Objects.FirstDynamicSlot + Objects.SlotSize)
        {
            Id = Logic.Smb3.Koopa, X = block.X + 5, Y = block.Y - 9 - 16,
            YRadius = 16, Status = Logic.Smb3.FacingRight,
        };
        Assert.True(Logic.Smb3.Walk(ctx.Ram, ctx.Collision, turtle, 0, true));
        Assert.False(Logic.Smb3.IsFacingRight(turtle));
        Assert.Equal(block.Y - 9, turtle.Y + turtle.YRadius);
    }

    [Fact]
    public void BoomerangBrother_ThrowsAProjectileWhichReturnsAndIsCaught()
    {
        if (TestData.Dir is not { } data) return;
        var ctx = Context(data);
        ctx.Ram.Sonic.X = 550;
        var bro = new SonicObject(ctx.Ram, Objects.FirstDynamicSlot)
        {
            Id = Logic.Smb3.BoomerangBro, X = 328, Y = 400,
        };
        var handler = new Smb3BoomerangBro(ctx);
        SonicObject? projectile = null;
        for (int frame = 0; frame < 140 && projectile is null; frame++)
        {
            handler.Run(bro);
            for (int slot = Objects.FirstDynamicSlot + Objects.SlotSize; slot < Vars.DynamicObjectRamEnd; slot += Objects.SlotSize)
                if (ctx.Ram.U8(slot) == Logic.Smb3.Boomerang) { projectile = new(ctx.Ram, slot); break; }
        }
        Assert.NotNull(projectile);
        int origin = projectile.X, farthest = origin, flightFrames = 0;
        bool reversed = false;
        var flight = new Smb3Boomerang(ctx);
        for (; flightFrames < 128 && projectile.Id != 0; flightFrames++)
        {
            flight.Run(projectile);
            if (projectile.Id != 0)
            {
                farthest = Math.Max(farthest, projectile.X);
                reversed |= projectile.XVel < 0;
            }
        }
        Assert.True(farthest >= origin + 24);
        Assert.True(reversed);
        Assert.True(flightFrames < 128, "The boomerang must be caught, not simply expire.");
        Assert.Equal(0, projectile.Id);
    }

    [Fact]
    public void World13_MapPanelStartsItsOwnZone()
    {
        if (TestData.Dir is not { } data) return;
        var screen = new WorldMapScreen(data, new MapProgress { Position = WorldMapScreen.Level13Tile }, false);
        for (int i = 0; i < 60; i++) screen.Step(0);
        screen.Step(0x40);
        for (int i = 0; i < 60 && screen.Result is null; i++) screen.Step(0);
        var start = Assert.IsType<ScreenResult.StartGame>(screen.Result);
        Assert.Equal(Smb3Levels.W13.Zone, start.Zone);
    }

    [Fact]
    public void World13_GoalClearsPanel3AndReturnsToMap()
    {
        if (TestData.Dir is not { } data) return;
        var flow = new GameFlow(data, null, GameFlow.Start.Level, (2350, 396), Smb3Levels.W13.Zone);
        for (int i = 0; i < 400; i++) flow.Step(0);
        for (int i = 0; i < 40; i++) flow.Step(0x08);
        for (int i = 0; i < 8; i++) flow.Step(0x48);
        for (int i = 0; i < 1200 && flow.Session is not null; i++)
        {
            flow.Step(0x08);
            Assert.True(flow.Session?.Stopped is null, flow.Session?.Stopped?.Message);
        }
        Assert.IsType<WorldMapScreen>(flow.Screen);
        Assert.Contains(WorldMapScreen.Level13Tile, flow.Progress.Cleared);
        Assert.Single(flow.Progress.Cards);
    }
}
