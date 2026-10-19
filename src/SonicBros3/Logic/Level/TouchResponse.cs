namespace SonicBros3.Logic;

/// <summary>
/// Sonic's side of object contact: TouchResponse (ROM 0x43776), Touch_Rings (0x1AE32), CollectRing (0x14EC8),
/// HurtCharacter (0x43A82), KillCharacter (0x43B44), AddPoints (0x4529A) and the free-slot finder (0x1BD56).
/// Objects are found through their collision_flags byte ($20): bits 6-7 select the response, bits 0-5 the hitbox size.
/// </summary>
public sealed class TouchResponse(Ram ram, ISoundRequests sound)
{
    /// <summary>
    /// Hitbox half-sizes {width, height} per collision size index (0x43822, read from the ROM). The table ends at
    /// 0x43888 (entry 51); higher indices read the following bytes, kept as-is for fidelity.
    /// </summary>
    private static readonly (int W, int H)[] Sizes =
    [
        (0x04, 0x04), (0x14, 0x14), (0x0C, 0x14), (0x14, 0x0C), (0x04, 0x10), (0x0C, 0x12), (0x10, 0x10), (0x06, 0x06),
        (0x18, 0x0C), (0x0C, 0x10), (0x10, 0x08), (0x08, 0x08), (0x14, 0x10), (0x14, 0x08), (0x0E, 0x0E), (0x18, 0x18),
        (0x28, 0x10), (0x10, 0x18), (0x08, 0x10), (0x20, 0x70), (0x40, 0x20), (0x80, 0x20), (0x20, 0x20), (0x08, 0x08),
        (0x04, 0x04), (0x20, 0x08), (0x0C, 0x0C), (0x08, 0x04), (0x18, 0x04), (0x28, 0x04), (0x04, 0x08), (0x04, 0x18),
        (0x04, 0x28), (0x04, 0x10), (0x18, 0x18), (0x0C, 0x18), (0x48, 0x08), (0x18, 0x28), (0x10, 0x04), (0x20, 0x02),
        (0x04, 0x40), (0x18, 0x80), (0x20, 0x10), (0x10, 0x20), (0x10, 0x30), (0x10, 0x40), (0x10, 0x50), (0x10, 0x02),
        (0x10, 0x01), (0x02, 0x08), (0x20, 0x1C), (0x47, 0xFA), (0xFF, 0x98), (0x34, 0x28), (0x00, 0x08), (0x36, 0x28),
        (0x00, 0x0C), (0x04, 0x42), (0x00, 0x08), (0x7A, 0x00), (0x1A, 0x28), (0x00, 0x16), (0x57, 0x05), (0x96, 0x45),
    ];

    /// <summary>Enemy chain points / 10 (0x43A74): 100, 200, 500, 1000.</summary>
    private static readonly int[] ChainPoints = [10, 20, 50, 100];

    /// <summary>The routine an object goes to when touched (items, monitors: they read <see cref="TouchedBy"/>).</summary>
    public const int TouchedRoutine = 4;
    /// <summary>$3E: the slot of the player who touched it (for an enemy: the points index for its explosion).</summary>
    public const int TouchedBy = 0x3E;
    // collision_flags bits 6-7: the kind of response.
    private const int KindEnemy = 0x00, KindItem = 0x40, KindHurts = 0x80, KindSpecial = 0xC0;
    private const int SizeMonitor = 6;
    private const int ExplosionId = 0x27, LostRingsId = 0x37, SpikesId = 0x36;
    private const int MaxRings = 999;

    /// <summary>Main entry for Sonic (a0 = $B000). Returns d0: 0 = no hit, -1 = hurt/killed.</summary>
    public int Run(SonicObject o)
    {
        TouchRings(o);
        if (ram.U8(Vars.CurrentZoneAndAct) == 0x0C) throw new UnportedException(0x43ED4, "CNZ bumpers");
        // During boss fights the variant at 0x43888 is used: objects with hitbox size 0 are skipped, and size $F
        // can be remapped per boss (0x43C4A; bosses 1 and 2, Green Hill and Emerald Hill, leave it alone).
        int boss = ram.U8(Vars.CurrentBossId);
        if (boss > 2) throw new UnportedException(0x43C58, $"collision remap for boss {boss}");

        int d2 = (o.X - 8) & 0xFFFF;
        int d5 = (byte)(o.YRadius - 3);
        int d3 = (o.Y - d5) & 0xFFFF;
        if (o.MappingFrame == 0x4D) { d3 = (d3 + 0x0C) & 0xFFFF; d5 = 0x0A; }
        const int d4 = 0x10;
        d5 *= 2;

        for (int slot = 0; slot < Objects.DynamicSlots; slot++)
        {
            var obj = new SonicObject(ram, Objects.FirstDynamicSlot + slot * Objects.SlotSize);
            int flags = obj.CollisionFlags;
            if (flags == 0) continue;
            if (Smb3.IsEnemy(obj.Id) && obj.Byte(Smb3.StompCooldown) != 0) continue;
            if (boss != 0 && (flags & 0x3F) == 0) continue;
            var (w, h) = Sizes[flags & 0x3F];
            int enemyY = obj.Y;
            if (Smb3.IsEnemy(obj.Id)) (w, h, enemyY) = Smb3.ContactBounds(obj);

            // Horizontal overlap (unsigned word arithmetic, as the original's carry tests).
            int dx = (((obj.X - w) & 0xFFFF) - d2);
            if (dx >= 0) { if (dx > d4) continue; }
            else if (dx + 2 * w + 0x10000 <= 0xFFFF) continue;   // add.w d1,d0 / bcs: needs a carry out
            // Vertical overlap.
            int dy = (((enemyY - h) & 0xFFFF) - d3);
            int d0;
            if (dy >= 0) { if (dy > d5) continue; d0 = dy; }
            else
            {
                int sum = dy + 0x10000 + 2 * h;
                if (sum <= 0xFFFF) continue;
                d0 = sum & 0xFFFF;
            }
            return Respond(o, obj, flags, d0, d2, d4, d5);
        }
        return 0;
    }

    /// <summary>Touch_ChkValue (0x43914).</summary>
    private int Respond(SonicObject o, SonicObject obj, int flags, int d0, int d2, int d4, int d5)
    {
        switch (flags & 0xC0)
        {
            case KindEnemy: return TouchEnemy(o, obj);
            case KindSpecial: return TouchSpecial(o, obj, flags & 0x3F, d0, d2, d4, d5);
            case KindHurts: return Hurt(o, obj);
        }
        // KindItem.
        if ((flags & 0x3F) == SizeMonitor) return TouchMonitor(o, obj);
        // A collectable: hand it to the object, unless Sonic was just hurt.
        int timer = ram.Sonic.InvulnerableTime;
        if (timer >= 0x5A) return 0;
        obj.Routine = TouchedRoutine;
        obj.SetWord(TouchedBy, o.Address);
        return 0;
    }

    /// <summary>Touch_Monitor (0x4395E).</summary>
    private int TouchMonitor(SonicObject o, SonicObject mon)
    {
        if (o.YVel < 0)
        {
            int d0 = (o.Y - 0x10) & 0xFFFF;
            if (d0 < mon.Y) return 0;
            o.YVel = (short)-o.YVel;
            mon.YVel = -0x180;
            if (mon.RoutineSecondary == 0) mon.RoutineSecondary = 4;
            return 0;
        }
        if (o.Address != Vars.MainCharacter && ram.U16(Vars.TwoPlayerMode) == 0) return 0;   // 0x4398A: Tails can't break monitors in 1P
        if (o.Anim != PlayerAnim.Roll) return 0;
        o.YVel = (short)-o.YVel;
        mon.Routine = Monitor.Breaking;
        mon.SetWord(TouchedBy, o.Address);
        return 0;
    }

    /// <summary>Touch_Enemy (0x439AE): destroy it when attacking, otherwise get hurt.</summary>
    private int TouchEnemy(SonicObject o, SonicObject en)
    {
        bool nativeEnemy = Smb3.IsEnemy(en.Id);
        bool stomp = nativeEnemy && o.InAir && o.YVel >= 0 &&
            (short)(o.Y - Smb3.ContactBounds(en).Y) < 0 &&
            !o.Invincible && o.Anim != PlayerAnim.Spindash;
        bool attacking = o.Invincible || o.Anim == PlayerAnim.Spindash || o.Anim == PlayerAnim.Roll;
        if (nativeEnemy)
        {
            attacking |= o.Rolling || stomp;
            // A resting shell is kicked by its own handler, not a damaging enemy.
            if (!attacking && en.Id == Smb3.Koopa && en.RoutineSecondary == 2) return 0;
            if (stomp && (en.CollisionProperty != 0 || en.Id == Smb3.Koopa && en.RoutineSecondary == 4))
            {
                o.YVel = (short)-Math.Max((int)o.YVel, 0x400);
                en.CollisionFlags = 0;
                en.SetByte(Smb3.StompCooldown, 8);
                if (en.CollisionProperty != 0) en.CollisionProperty--;
                if (en.CollisionProperty == 0) en.Status |= ObjStatus.Destroyed;
                return 0;
            }
        }
        if (!attacking) return Hurt(o, en);

        if ((en.RenderFlags & Render.MultiSprite) != 0)
        {
            // A multi-sprite enemy counts its hits at $32.
            if (en.Byte(0x32) == 0) return 0;
            BounceOff(o, en);
            en.SetByte(0x32, en.Byte(0x32) - 1);
            return 0;
        }
        int hits = en.CollisionProperty;
        if (hits != 0 && !nativeEnemy)
        {
            // A boss (or a winged enemy): one hit off.
            BounceOff(o, en);
            en.CollisionProperty = hits - 1;
            if (hits - 1 == 0) en.Status |= ObjStatus.Destroyed;
            return 0;
        }

        // Destroy (0x43A0C): points by the chain of enemies hit without landing.
        en.Status |= ObjStatus.Destroyed;
        int chain = ram.U16(Vars.ChainBonusCounter);
        ram.W16(Vars.ChainBonusCounter, chain + 2);
        int index = chain >= 6 ? 6 : chain;
        en.SetWord(TouchedBy, index);   // the explosion's animal shows these points
        int points = ChainPoints[index / 2];
        if (ram.U16(Vars.ChainBonusCounter) >= 0x20)
        {
            points = 1000;
            en.SetWord(TouchedBy, 0x0A);
        }
        AddPoints(points);
        en.Id = ExplosionId;
        en.Routine = 0;
        if (nativeEnemy)
        {
            en.CollisionFlags = 0;
            if (stomp) o.YVel = (short)-Math.Max((int)o.YVel, 0x400);
            return 0;
        }
        if (o.YVel < 0) { o.YVel = (short)(o.YVel + 0x100); return 0; }
        if (o.Y >= en.Y) { o.YVel = (short)(o.YVel - 0x100); return 0; }
        o.YVel = (short)-o.YVel;
        return 0;
    }

    private void BounceOff(SonicObject o, SonicObject en)
    {
        o.XVel = (short)-o.XVel;
        o.YVel = (short)-o.YVel;
        en.CollisionFlags = 0;
    }

    /// <summary>Touch_Special (0x43B94): bosses and other objects that count hits or need extra checks.</summary>
    private int TouchSpecial(SonicObject o, SonicObject obj, int type, int d0, int d2, int d4, int d5)
    {
        switch (type)
        {
            case 0x06 or 0x0A or 0x14 or 0x15 or 0x16 or 0x17 or 0x18:
                obj.CollisionProperty += o.Address == Vars.MainCharacter ? 1 : 2;   // 0x43C1E: Tails adds 2
                return 0;
            case 0x07:
                obj.CollisionProperty = 2;
                return TouchEnemy(o, obj);
            case 0x0B:
                obj.Status |= ObjStatus.Destroyed;       // 0x43A7C, then hurt
                return Hurt(o, obj);
            case 0x0C:
            {
                int dd5 = (d5 - d0) & 0xFFFF;
                if (dd5 >= 8) return TouchEnemy(o, obj);
                int x = (obj.X - 4) & 0xFFFF;
                if (obj.FacingLeft) x = (x - 0x10) & 0xFFFF;
                int dx = x - d2;
                bool hurt = dx >= 0 ? dx <= d4 : dx + 0x18 >= 0;
                return hurt ? Hurt(o, obj) : TouchEnemy(o, obj);
            }
            case 0x1A:
                obj.CollisionProperty = 0xFF;
                return TouchEnemy(o, obj);
            case 0x21:
                obj.CollisionProperty++;
                return 0;
            default:
                return 0;
        }
    }

    /// <summary>HurtCharacter (0x43A82).</summary>
    public int Hurt(SonicObject o, SonicObject source)
    {
        if (o.Invincible) return -1;
        if (o.InvulnerableTime != 0) return -1;
        int rings = ram.U16(Vars.RingCount);
        bool losesRings = o.Address == Vars.MainCharacter;   // 0x43A9C: in 1P Tails never drops rings
        if (!losesRings && ram.U16(Vars.TwoPlayerMode) != 0) throw new UnportedException(0x43AA8, "Tails hurt in 2-player mode");
        if (losesRings && !o.HasShield)
        {
            if (rings == 0) return Kill(o, source);
            int slot = FindFreeObject();
            if (slot >= 0)
            {
                var lostRings = new SonicObject(ram, slot);
                lostRings.Id = LostRingsId;
                lostRings.X = o.X;
                lostRings.Y = o.Y;
                lostRings.SetWord(0x3E, o.Address);
            }
        }
        if (losesRings) o.HasShield = false;   // lose the shield (skipped for Tails in 1P: 0x43AA6 -> 0x43ADC)
        o.Routine = PlayerRoutine.Hurt;
        Player.ResetOnFloorPart2(ram, o);
        o.InAir = true;
        o.YVel = -0x400;
        o.XVel = -0x200;
        if (o.Underwater) { o.YVel = -0x200; o.XVel = -0x100; }
        if (o.X >= source.X) o.XVel = (short)-o.XVel;
        o.Inertia = 0;
        o.Anim = PlayerAnim.Hurt2;
        o.InvulnerableTime = 0x78;
        sound.PlaySound(source.Id == SpikesId ? Sfx.HurtBySpikes : Sfx.Hurt);
        return -1;
    }

    /// <summary>KillCharacter (0x43B44).</summary>
    public int Kill(SonicObject o, SonicObject source)
    {
        if (ram.U16(Vars.DebugPlacementMode) != 0) return -1;   // debug mode
        o.StatusSecondary = 0;
        o.Routine = PlayerRoutine.Dead;
        Player.ResetOnFloorPart2(ram, o);
        o.InAir = true;
        o.YVel = -0x700;
        o.XVel = 0;
        o.Inertia = 0;
        o.Anim = PlayerAnim.Death;
        o.ArtTile |= 0x8000;   // drawn in front of everything
        sound.PlaySound(source.Id == SpikesId ? Sfx.HurtBySpikes : Sfx.Hurt);
        return -1;
    }

    /// <summary>SingleObjLoad (0x1BD56): first free dynamic object slot, or -1.</summary>
    public int FindFreeObject()
    {
        int count = ram.U16(Vars.TwoPlayerMode) != 0 ? 0x28 : Objects.DynamicSlots;
        for (int i = 0; i < count; i++)
        {
            int a = Objects.FirstDynamicSlot + i * Objects.SlotSize;
            if (ram.U8(a) == 0) return a;
        }
        return -1;
    }

    /// <summary>AddPoints (0x4529A), 1-player path: score in units of 10, extra life every 50000.</summary>
    public void AddPoints(int points)
    {
        if (ram.U16(Vars.TwoPlayerMode) != 0) throw new UnportedException(0x452D6, "AddPoints 2P");
        ram.W8(Vars.UpdateHudScore, 1);
        long score = ram.U32(Vars.Score) + (long)points;
        const int MaxScore = 999999;   // in tens
        if (score > MaxScore) score = MaxScore;
        ram.W32(Vars.Score, score);
        if (score >= ram.U32(Vars.NextExtraLifeScore))
        {
            ram.W32(Vars.NextExtraLifeScore, ram.U32(Vars.NextExtraLifeScore) + 5000);   // every 50000 points
            ram.W8(Vars.LifeCount, ram.U8(Vars.LifeCount) + 1);
            ram.W8(Vars.UpdateHudLives, ram.U8(Vars.UpdateHudLives) + 1);
            sound.PlayMusic(Music.ExtraLife);
        }
    }

    /// <summary>Touch_Rings (0x1AE32): collect uncollected rings in the ring manager's window.</summary>
    private void TouchRings(SonicObject o)
    {
        bool sonic = o.Address == Vars.MainCharacter;   // Tails uses his own copy of the window
        int a1 = ram.U16(sonic ? Vars.RingStartAddr : Vars.RingStartAddrP2), a2 = ram.U16(sonic ? Vars.RingEndAddr : Vars.RingEndAddrP2);
        if (a1 == a2) return;
        if (o.InvulnerableTime >= 0x5A) return;
        int d2 = (o.X - 8) & 0xFFFF;
        int d5 = (byte)(o.YRadius - 3);
        int d3 = (o.Y - d5) & 0xFFFF;
        if (o.MappingFrame == 0x4D) { d3 = (d3 + 0x0C) & 0xFFFF; d5 = 0x0A; }
        const int d1 = 6, d6 = 0x0C, d4 = 0x10;
        d5 *= 2;
        for (; a1 != a2; a1 = (a1 + 6) & 0xFFFF)
        {
            if (ram.U16(a1) != 0) continue;
            int dx = ((ram.U16(a1 + 2) - d1) & 0xFFFF) - d2;
            if (dx >= 0) { if (dx > d4) continue; }
            else if (dx + 0x10000 + d6 <= 0xFFFF) continue;
            int dy = ((ram.U16(a1 + 4) - d1) & 0xFFFF) - d3;
            if (dy >= 0) { if (dy > d5) continue; }
            else if (dy + 0x10000 + d6 <= 0xFFFF) continue;

            ram.W16(a1, RingsManager.Sparkling);
            ram.W16(Vars.PerfectRingsLeft, ram.U16(Vars.PerfectRingsLeft) - 1);
            CollectRingFor(ram, sound, tails: !sonic);
            int list = Vars.RingConsumptionList;  // the first free entry of the sparkle list
            while (ram.U16(list) != 0) list += 2;
            ram.W16(list, a1);
            ram.W16(Vars.RingConsumptionTable, ram.U16(Vars.RingConsumptionTable) + 1);
        }
    }

    /// <summary>CollectRing: Sonic's (0x14EC8), or Tails' (0x14F1E), which in 1-player mode also counts for Sonic.</summary>
    public static void CollectRingFor(Ram ram, ISoundRequests sound, bool tails = false)
    {
        if (tails)
        {
            if (ram.U16(Vars.RingsCollectedP2) < MaxRings) ram.W16(Vars.RingsCollectedP2, ram.U16(Vars.RingsCollectedP2) + 1);
            if (ram.U16(Vars.RingCountP2) < MaxRings) ram.W16(Vars.RingCountP2, ram.U16(Vars.RingCountP2) + 1);
            if (ram.U16(Vars.TwoPlayerMode) != 0) throw new UnportedException(0x14F3C, "Tails' ring count in 2-player mode");
        }
        else if (ram.U16(Vars.RingsCollected) < MaxRings) ram.W16(Vars.RingsCollected, ram.U16(Vars.RingsCollected) + 1);
        int sfx = Sfx.Ring;
        if (ram.U16(Vars.RingCount) < MaxRings)
        {
            ram.W16(Vars.RingCount, ram.U16(Vars.RingCount) + 1);
            ram.W8(Vars.UpdateHudRings, ram.U8(Vars.UpdateHudRings) | 1);
            int rings = ram.U16(Vars.RingCount);
            if (rings >= 100)
            {
                bool life = !ram.TestSet(Vars.ExtraLifeFlags, 1) || (rings >= 200 && !ram.TestSet(Vars.ExtraLifeFlags, 2));
                if (life)
                {
                    ram.W8(Vars.LifeCount, ram.U8(Vars.LifeCount) + 1);
                    ram.W8(Vars.UpdateHudLives, ram.U8(Vars.UpdateHudLives) + 1);
                    sfx = Music.ExtraLife;
                }
            }
        }
        sound.PlaySoundSecondary(sfx);   // jmp 0x150C: secondary SFX queue
    }
}
