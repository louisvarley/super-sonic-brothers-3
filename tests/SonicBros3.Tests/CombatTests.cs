using SonicBros3.Logic;
using Smb3Enemy = SonicBros3.Logic.Smb3;

namespace SonicBros3.Tests;

public class CombatTests
{
    private static (Ram Ram, SonicObject Player, SonicObject Enemy, TouchResponse Touch) Contact(
        int id, int hits = 0, bool above = false)
    {
        var ram = new Ram();
        ram.W8(Vars.CurrentZoneAndAct, 0x40);
        ram.W32(Vars.NextExtraLifeScore, 5000);
        var player = ram.Sonic;
        player.Id = 1;
        player.Routine = PlayerRoutine.Control;
        player.X = 100;
        player.Y = above ? 82 : 100;
        player.YRadius = 14;
        player.XRadius = 7;
        player.XVel = 0x600;
        player.YVel = above ? (short)0x100 : (short)0;
        player.InAir = above;
        var enemy = new SonicObject(ram, Objects.FirstDynamicSlot)
        {
            Id = id, X = 100, Y = 100, YRadius = 16, Routine = 2,
            CollisionFlags = 0x0B, CollisionProperty = hits,
        };
        return (ram, player, enemy, new TouchResponse(ram, new NoSound()));
    }

    [Theory]
    [InlineData(Smb3Enemy.Goomba, 0)]
    [InlineData(Smb3Enemy.Paragoomba, 1)]
    [InlineData(Smb3Enemy.Koopa, 1)]
    [InlineData(Smb3Enemy.Paratroopa, 1)]
    [InlineData(Smb3Enemy.BoomerangBro, 0)]
    public void GroundSpin_DefeatsEnemyInOneHitWithoutLosingMomentum(int id, int hits)
    {
        var (_, player, enemy, touch) = Contact(id, hits);
        player.Rolling = true;
        player.Anim = PlayerAnim.Roll;
        Assert.Equal(0, touch.Run(player));
        Assert.Equal(0x27, enemy.Id);
        Assert.Equal(0, enemy.CollisionFlags);
        Assert.Equal(0x600, player.XVel);
        Assert.Equal(0, player.YVel);
        Assert.Equal(PlayerRoutine.Control, player.Routine);
    }

    [Theory]
    [InlineData(Smb3Enemy.Paragoomba)]
    [InlineData(Smb3Enemy.Koopa)]
    [InlineData(Smb3Enemy.Paratroopa)]
    public void Stomp_PreservesMarioStageAndForwardSpeed(int id)
    {
        var (_, player, enemy, touch) = Contact(id, 1, above: true);
        player.Rolling = true;
        player.Anim = PlayerAnim.Roll;
        touch.Run(player);
        Assert.Equal(id, enemy.Id);
        Assert.Equal(0, enemy.CollisionProperty);
        Assert.NotEqual(0, enemy.Status & ObjStatus.Destroyed);
        Assert.Equal(0x600, player.XVel);
        Assert.Equal(-0x400, player.YVel);
        Assert.Equal(0, enemy.CollisionFlags);
        Assert.Equal(8, enemy.Byte(Smb3Enemy.StompCooldown));
        Assert.Equal(PlayerRoutine.Control, player.Routine);
    }

    [Fact]
    public void FallingOntoEnemyWithoutSpin_IsStillAMarioStomp()
    {
        var (_, player, enemy, touch) = Contact(Smb3Enemy.Koopa, 1, above: true);
        player.Anim = PlayerAnim.Walk;
        touch.Run(player);
        Assert.NotEqual(0, enemy.Status & ObjStatus.Destroyed);
        Assert.Equal(-0x400, player.YVel);
        Assert.Equal(PlayerRoutine.Control, player.Routine);
    }

    [Theory]
    [InlineData(PlayerAnim.Spindash, false)]
    [InlineData(PlayerAnim.Walk, true)]
    public void SpindashOrInvincibility_BypassesWingRemoval(int animation, bool invincible)
    {
        var (_, player, enemy, touch) = Contact(Smb3Enemy.Paratroopa, 1);
        player.Anim = animation;
        player.Invincible = invincible;
        touch.Run(player);
        Assert.Equal(0x27, enemy.Id);
        Assert.Equal(0x600, player.XVel);
    }

    [Fact]
    public void AirborneSideSpin_DefeatsRatherThanStomps()
    {
        var (_, player, enemy, touch) = Contact(Smb3Enemy.Paratroopa, 1);
        player.InAir = true;
        player.Rolling = true;
        player.Anim = PlayerAnim.Roll;
        player.YVel = -0x200;
        touch.Run(player);
        Assert.Equal(0x27, enemy.Id);
        Assert.Equal(-0x200, player.YVel);
        Assert.Equal(0x600, player.XVel);
    }

    [Fact]
    public void WalkingIntoEnemy_StillHurts()
    {
        var (_, player, _, touch) = Contact(Smb3Enemy.Goomba);
        touch.Run(player);
        Assert.Equal(PlayerRoutine.Dead, player.Routine);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RestingShell_IsSafeToKickButSpinDestroysIt(bool spin)
    {
        var (_, player, enemy, touch) = Contact(Smb3Enemy.Koopa);
        enemy.RoutineSecondary = 2;
        enemy.YRadius = 8;
        if (spin) player.Anim = PlayerAnim.Roll;
        touch.Run(player);
        Assert.Equal(spin ? 0x27 : Smb3Enemy.Koopa, enemy.Id);
        Assert.Equal(PlayerRoutine.Control, player.Routine);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void PlantContact_MatchesVisibleHeadNotPipeAnchor(int subtype)
    {
        var (_, player, enemy, touch) = Contact(Smb3Enemy.Piranha);
        enemy.Subtype = subtype;
        enemy.SetWord(0x30, enemy.Y);
        int direction = (subtype & 4) == 0 ? 1 : -1;
        int height = (subtype & 3) is 2 or 3 ? 48 : 32;
        player.Y = enemy.Y - direction * (height - 8);
        player.Anim = PlayerAnim.Roll;
        touch.Run(player);
        Assert.Equal(0x27, enemy.Id);
    }

    [Fact]
    public void PlantContact_DoesNotExtendIntoThePipe()
    {
        var (_, player, enemy, touch) = Contact(Smb3Enemy.Piranha);
        enemy.SetWord(0x30, enemy.Y);
        player.Y = enemy.Y + 12;
        touch.Run(player);
        Assert.Equal(PlayerRoutine.Control, player.Routine);
    }

    [Fact]
    public void NonSmb3MultiHitEnemy_KeepsOriginalResponse()
    {
        var (_, player, enemy, touch) = Contact(0x40, 2);
        player.Anim = PlayerAnim.Roll;
        touch.Run(player);
        Assert.Equal(0x40, enemy.Id);
        Assert.Equal(1, enemy.CollisionProperty);
        Assert.Equal(-0x600, player.XVel);
    }
}
