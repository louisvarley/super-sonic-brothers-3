//! `TouchResponse` (ROM 0x43776): Sonic's/Tails' side of object contact, `Touch_Rings` (0x1AE32),
//! `CollectRing` (0x14EC8), `HurtCharacter` (0x43A82), `KillCharacter` (0x43B44) and `AddPoints` (0x4529A).
//!
//! Objects are found through their `collision_flags` byte (`$20`): bits 6-7 select the response, bits 0-5 the
//! hitbox size. Sound is a no-op here (M6).

use crate::audio::sfx;
use crate::objects::smb3;
use crate::objects::{find_free_slot, ids, solid};
use crate::player::{self, Player};
use crate::{vars, Obj, Ram};

/// The routine an object goes to when touched (items, monitors: they read `TOUCHED_BY`).
pub const TOUCHED_ROUTINE: u8 = 4;
/// `$3E`: the slot of the player who touched it (for an enemy: the points index for its explosion).
pub const TOUCHED_BY: u16 = 0x3E;

const SIZE_MONITOR: u8 = 6;
const EXPLOSION_ID: u8 = 0x27;
const LOST_RINGS_ID: u8 = 0x37;
const SPIKES_ID: u8 = 0x36;
const MAX_RINGS: u16 = 999;

/// Hitbox half-sizes {width, height} per collision size index (0x43822, read from the ROM).
const SIZES: [(i32, i32); 64] = [
    (0x04, 0x04),
    (0x14, 0x14),
    (0x0C, 0x14),
    (0x14, 0x0C),
    (0x04, 0x10),
    (0x0C, 0x12),
    (0x10, 0x10),
    (0x06, 0x06),
    (0x18, 0x0C),
    (0x0C, 0x10),
    (0x10, 0x08),
    (0x08, 0x08),
    (0x14, 0x10),
    (0x14, 0x08),
    (0x0E, 0x0E),
    (0x18, 0x18),
    (0x28, 0x10),
    (0x10, 0x18),
    (0x08, 0x10),
    (0x20, 0x70),
    (0x40, 0x20),
    (0x80, 0x20),
    (0x20, 0x20),
    (0x08, 0x08),
    (0x04, 0x04),
    (0x20, 0x08),
    (0x0C, 0x0C),
    (0x08, 0x04),
    (0x18, 0x04),
    (0x28, 0x04),
    (0x04, 0x08),
    (0x04, 0x18),
    (0x04, 0x28),
    (0x04, 0x10),
    (0x18, 0x18),
    (0x0C, 0x18),
    (0x48, 0x08),
    (0x18, 0x28),
    (0x10, 0x04),
    (0x20, 0x02),
    (0x04, 0x40),
    (0x18, 0x80),
    (0x20, 0x10),
    (0x10, 0x20),
    (0x10, 0x30),
    (0x10, 0x40),
    (0x10, 0x50),
    (0x10, 0x02),
    (0x10, 0x01),
    (0x02, 0x08),
    (0x20, 0x1C),
    (0x47, 0xFA),
    (0xFF, 0x98),
    (0x34, 0x28),
    (0x00, 0x08),
    (0x36, 0x28),
    (0x00, 0x0C),
    (0x04, 0x42),
    (0x00, 0x08),
    (0x7A, 0x00),
    (0x1A, 0x28),
    (0x00, 0x16),
    (0x57, 0x05),
    (0x96, 0x45),
];

/// Enemy chain points / 10 (0x43A74): 100, 200, 500, 1000.
const CHAIN_POINTS: [i32; 4] = [10, 20, 50, 100];

/// Main entry for a player. Returns 0 = no hit, -1 = hurt/killed.
pub fn run(ram: &mut Ram, o: Obj) -> i32 {
    touch_rings(ram, o);
    if ram.u8(vars::CURRENT_ZONE_AND_ACT) == 0x0C {
        return 0; // CNZ bumpers (0x43ED4) are not ported; not reachable in the SMB3 zones.
    }
    let boss = ram.u8(vars::CURRENT_BOSS_ID);
    if boss > 2 {
        return 0; // boss hitbox remaps (0x43C58) are a later milestone
    }

    let d2 = (o.x(ram) as i32 - 8) & 0xFFFF;
    let mut d5 = (o.y_radius(ram) as i32 - 3) as u8 as i32;
    let mut d3 = (o.y(ram) as i32 - d5) & 0xFFFF;
    if o.mapping_frame(ram) == 0x4D {
        d3 = (d3 + 0x0C) & 0xFFFF;
        d5 = 0x0A;
    }
    let d4 = 0x10;
    d5 *= 2;

    for slot in 0..0x70 {
        let obj = Obj::new(vars::FIRST_DYNAMIC_SLOT.wrapping_add(slot * solid::SLOT_SIZE));
        let flags = obj.collision_flags(ram);
        if flags == 0 {
            continue;
        }
        if smb3::is_enemy(obj.id(ram)) && obj.byte(ram, smb3::STOMP_COOLDOWN) != 0 {
            continue;
        }
        if boss != 0 && (flags & 0x3F) == 0 {
            continue;
        }
        let (mut w, mut h) = SIZES[(flags & 0x3F) as usize];
        let mut enemy_y = obj.y(ram) as i32;
        if smb3::is_enemy(obj.id(ram)) {
            let bounds = smb3::contact_bounds(ram, obj);
            w = bounds.0;
            h = bounds.1;
            enemy_y = bounds.2;
        }

        // Horizontal overlap (unsigned word arithmetic, as the original's carry tests).
        let dx = ((obj.x(ram) as i32 - w) & 0xFFFF) - d2;
        if (dx as i16) >= 0 {
            if dx > d4 {
                continue;
            }
        } else if dx + 2 * w + 0x10000 <= 0xFFFF {
            continue;
        }
        // Vertical overlap.
        let dy = ((enemy_y - h) & 0xFFFF) - d3;
        let d0 = if (dy as i16) >= 0 {
            if dy > d5 {
                continue;
            }
            dy
        } else {
            let sum = dy + 0x10000 + 2 * h;
            if sum <= 0xFFFF {
                continue;
            }
            sum & 0xFFFF
        };
        return respond(ram, o, obj, flags, d0, d2, d4, d5);
    }
    0
}

/// `Touch_ChkValue` (0x43914).
#[allow(clippy::too_many_arguments)] // the original's d0/d2/d4/d5 registers
fn respond(ram: &mut Ram, o: Obj, obj: Obj, flags: u8, d0: i32, d2: i32, d4: i32, d5: i32) -> i32 {
    match flags & 0xC0 {
        0x00 => return touch_enemy(ram, o, obj),
        0xC0 => return touch_special(ram, o, obj, flags & 0x3F, d0, d2, d4, d5),
        0x80 => return hurt(ram, o, obj),
        _ => {}
    }
    // KindItem.
    if (flags & 0x3F) == SIZE_MONITOR {
        return touch_monitor(ram, o, obj);
    }
    // A collectable: hand it to the object, unless Sonic was just hurt (the original reads Sonic's timer even
    // when Tails touched it).
    let timer = Obj::new(vars::MAIN_CHARACTER).invulnerable_time(ram);
    if timer >= 0x5A {
        return 0;
    }
    obj.set_routine(ram, TOUCHED_ROUTINE);
    obj.set_word(ram, TOUCHED_BY, o.address());
    0
}

/// `Touch_Monitor` (0x4395E).
fn touch_monitor(ram: &mut Ram, o: Obj, mon: Obj) -> i32 {
    if o.y_vel(ram) < 0 {
        let d0 = (o.y(ram) as i32 - 0x10) & 0xFFFF;
        if d0 < mon.y(ram) as i32 {
            return 0;
        }
        o.set_y_vel(ram, o.y_vel(ram).wrapping_neg());
        mon.set_y_vel(ram, -0x180);
        if mon.routine_secondary(ram) == 0 {
            mon.set_routine_secondary(ram, 4);
        }
        return 0;
    }
    if o.address() != vars::MAIN_CHARACTER && ram.u16(vars::TWO_PLAYER_MODE) == 0 {
        return 0;
    }
    if o.anim(ram) != player::anim::ROLL {
        return 0;
    }
    o.set_y_vel(ram, o.y_vel(ram).wrapping_neg());
    mon.set_routine(ram, crate::objects::common::MONITOR_BREAKING);
    mon.set_word(ram, TOUCHED_BY, o.address());
    0
}

/// `Touch_Enemy` (0x439AE): destroy it when attacking, otherwise get hurt.
fn touch_enemy(ram: &mut Ram, o: Obj, en: Obj) -> i32 {
    let native_enemy = smb3::is_enemy(en.id(ram));
    let stomp = native_enemy
        && o.in_air(ram)
        && o.y_vel(ram) >= 0
        && ((o.y(ram) as i32 - smb3::contact_bounds(ram, en).2) as i16) < 0
        && !o.invincible(ram)
        && o.anim(ram) != player::anim::SPINDASH;
    let mut attacking = o.invincible(ram)
        || o.anim(ram) == player::anim::SPINDASH
        || o.anim(ram) == player::anim::ROLL;
    if native_enemy {
        attacking |= o.rolling(ram) || stomp;
        if !attacking && en.id(ram) == ids::KOOPA && en.routine_secondary(ram) == 2 {
            return 0;
        }
        if stomp
            && (en.collision_property(ram) != 0
                || (en.id(ram) == ids::KOOPA && en.routine_secondary(ram) == 4))
        {
            o.set_y_vel(ram, -(o.y_vel(ram) as i32).max(0x400) as i16);
            en.set_collision_flags(ram, 0);
            en.set_byte(ram, smb3::STOMP_COOLDOWN, 8);
            if en.collision_property(ram) != 0 {
                en.set_collision_property(ram, en.collision_property(ram) - 1);
            }
            if en.collision_property(ram) == 0 {
                en.set_status(ram, en.status(ram) | 0x80);
            }
            sfx::mario(ram, sfx::mario::KICK);
            return 0;
        }
    }
    if !attacking {
        return hurt(ram, o, en);
    }

    if (en.render_flags(ram) & 0x40) != 0 {
        // A multi-sprite enemy counts its hits at $32.
        if en.byte(ram, 0x32) == 0 {
            return 0;
        }
        bounce_off(ram, o, en);
        en.set_byte(ram, 0x32, en.byte(ram, 0x32) - 1);
        return 0;
    }
    let hits = en.collision_property(ram);
    if hits != 0 && !native_enemy {
        bounce_off(ram, o, en);
        en.set_collision_property(ram, hits - 1);
        if hits - 1 == 0 {
            en.set_status(ram, en.status(ram) | 0x80);
        }
        return 0;
    }

    // Destroy (0x43A0C): points by the chain of enemies hit without landing.
    en.set_status(ram, en.status(ram) | 0x80);
    let chain = ram.u16(vars::CHAIN_BONUS_COUNTER);
    ram.w16(vars::CHAIN_BONUS_COUNTER, chain.wrapping_add(2));
    let index = if chain >= 6 { 6 } else { chain };
    en.set_word(ram, TOUCHED_BY, index);
    let mut points = CHAIN_POINTS[(index / 2) as usize];
    if ram.u16(vars::CHAIN_BONUS_COUNTER) >= 0x20 {
        points = 1000;
        en.set_word(ram, TOUCHED_BY, 0x0A);
    }
    add_points(ram, points);
    en.set_id(ram, EXPLOSION_ID);
    en.set_routine(ram, 0);
    if native_enemy {
        sfx::mario(ram, sfx::mario::KICK);
        en.set_collision_flags(ram, 0);
        if stomp {
            o.set_y_vel(ram, -(o.y_vel(ram) as i32).max(0x400) as i16);
        }
        return 0;
    }
    if o.y_vel(ram) < 0 {
        o.set_y_vel(ram, o.y_vel(ram).wrapping_add(0x100));
        return 0;
    }
    if o.y(ram) >= en.y(ram) {
        o.set_y_vel(ram, o.y_vel(ram).wrapping_sub(0x100));
        return 0;
    }
    o.set_y_vel(ram, o.y_vel(ram).wrapping_neg());
    0
}

fn bounce_off(ram: &mut Ram, o: Obj, en: Obj) {
    o.set_x_vel(ram, o.x_vel(ram).wrapping_neg());
    o.set_y_vel(ram, o.y_vel(ram).wrapping_neg());
    en.set_collision_flags(ram, 0);
}

/// `Touch_Special` (0x43B94): bosses and other objects that count hits or need extra checks.
#[allow(clippy::too_many_arguments)] // the original's d0/d2/d4/d5 registers
fn touch_special(
    ram: &mut Ram,
    o: Obj,
    obj: Obj,
    type_: u8,
    d0: i32,
    d2: i32,
    d4: i32,
    d5: i32,
) -> i32 {
    match type_ {
        0x06 | 0x0A | 0x14 | 0x15 | 0x16 | 0x17 | 0x18 => {
            let add = if o.address() == vars::MAIN_CHARACTER {
                1
            } else {
                2
            };
            obj.set_collision_property(ram, obj.collision_property(ram).wrapping_add(add));
            0
        }
        0x07 => {
            obj.set_collision_property(ram, 2);
            touch_enemy(ram, o, obj)
        }
        0x0B => {
            obj.set_status(ram, obj.status(ram) | 0x80);
            hurt(ram, o, obj)
        }
        0x0C => {
            let dd5 = d5.wrapping_sub(d0) & 0xFFFF;
            if dd5 >= 8 {
                return touch_enemy(ram, o, obj);
            }
            let mut x = (obj.x(ram) as i32 - 4) & 0xFFFF;
            if obj.facing_left(ram) {
                x = (x - 0x10) & 0xFFFF;
            }
            let dx = x - d2;
            let touching = if dx >= 0 { dx <= d4 } else { dx + 0x18 >= 0 };
            if touching {
                hurt(ram, o, obj)
            } else {
                touch_enemy(ram, o, obj)
            }
        }
        0x1A => {
            obj.set_collision_property(ram, 0xFF);
            touch_enemy(ram, o, obj)
        }
        0x21 => {
            obj.set_collision_property(ram, obj.collision_property(ram).wrapping_add(1));
            0
        }
        _ => 0,
    }
}

/// `HurtCharacter` (0x43A82).
pub fn hurt(ram: &mut Ram, o: Obj, source: Obj) -> i32 {
    if o.invincible(ram) {
        return -1;
    }
    if o.invulnerable_time(ram) != 0 {
        return -1;
    }
    sfx::sonic(
        ram,
        if source.id(ram) == SPIKES_ID {
            sfx::ids::HURT_BY_SPIKES
        } else {
            sfx::ids::HURT
        },
    );
    let rings = ram.u16(vars::RING_COUNT);
    let loses_rings = o.address() == vars::MAIN_CHARACTER;
    if loses_rings && !o.has_shield(ram) {
        if rings == 0 {
            return kill(ram, o, source);
        }
        if let Some(slot) = find_free_slot(ram) {
            let lost = slot;
            lost.set_id(ram, LOST_RINGS_ID);
            lost.set_x(ram, o.x(ram));
            lost.set_y(ram, o.y(ram));
            lost.set_word(ram, 0x3E, o.address());
        }
    }
    if loses_rings {
        o.set_has_shield(ram, false);
    }
    o.set_routine(ram, player::routine::HURT);
    Player::reset_on_floor_part2(ram, o);
    o.set_in_air(ram, true);
    o.set_y_vel(ram, -0x400);
    o.set_x_vel(ram, -0x200);
    if o.underwater(ram) {
        o.set_y_vel(ram, -0x200);
        o.set_x_vel(ram, -0x100);
    }
    if o.x(ram) >= source.x(ram) {
        o.set_x_vel(ram, o.x_vel(ram).wrapping_neg());
    }
    o.set_inertia(ram, 0);
    o.set_anim(ram, player::anim::HURT2);
    o.set_invulnerable_time(ram, 0x78);
    let _ = source.id(ram) == SPIKES_ID;
    -1
}

/// `KillCharacter` (0x43B44).
pub fn kill(ram: &mut Ram, o: Obj, source: Obj) -> i32 {
    if ram.u16(vars::DEBUG_PLACEMENT_MODE) != 0 {
        return -1;
    }
    o.set_status_secondary(ram, 0);
    o.set_routine(ram, player::routine::DEAD);
    Player::reset_on_floor_part2(ram, o);
    o.set_in_air(ram, true);
    o.set_y_vel(ram, -0x700);
    o.set_x_vel(ram, 0);
    o.set_inertia(ram, 0);
    o.set_anim(ram, player::anim::DEATH);
    o.set_art_tile(ram, o.art_tile(ram) | 0x8000);
    sfx::sonic(
        ram,
        if source.id(ram) == SPIKES_ID {
            sfx::ids::HURT_BY_SPIKES
        } else {
            sfx::ids::HURT
        },
    );
    -1
}

/// `AddPoints` (0x4529A), 1-player path: score in units of 10, an extra life every 50000.
pub fn add_points(ram: &mut Ram, points: i32) {
    if ram.u16(vars::TWO_PLAYER_MODE) != 0 {
        return;
    }
    ram.w8(vars::UPDATE_HUD_SCORE, 1);
    let mut score = ram.u32(vars::SCORE) as i64 + points as i64;
    const MAX_SCORE: i64 = 999_999;
    if score > MAX_SCORE {
        score = MAX_SCORE;
    }
    ram.w32(vars::SCORE, score as u32);
    if score >= ram.u32(vars::NEXT_EXTRA_LIFE_SCORE) as i64 {
        ram.w32(
            vars::NEXT_EXTRA_LIFE_SCORE,
            ram.u32(vars::NEXT_EXTRA_LIFE_SCORE).wrapping_add(5000),
        );
        ram.w8(vars::LIFE_COUNT, ram.u8(vars::LIFE_COUNT).wrapping_add(1));
        ram.w8(
            vars::UPDATE_HUD_LIVES,
            ram.u8(vars::UPDATE_HUD_LIVES).wrapping_add(1),
        );
        sfx::music(ram, 0x98);
    }
}

/// `Touch_Rings` (0x1AE32): collect uncollected rings in the ring manager's window.
fn touch_rings(ram: &mut Ram, o: Obj) {
    let sonic = o.address() == vars::MAIN_CHARACTER;
    let mut a1 = ram.u16(if sonic {
        vars::RING_START_ADDR
    } else {
        vars::RING_START_ADDR_P2
    });
    let a2 = ram.u16(if sonic {
        vars::RING_END_ADDR
    } else {
        vars::RING_END_ADDR_P2
    });
    if a1 == a2 {
        return;
    }
    if o.invulnerable_time(ram) >= 0x5A {
        return;
    }
    let d2 = (o.x(ram) as i32 - 8) & 0xFFFF;
    let mut d5 = (o.y_radius(ram) as i32 - 3) as u8 as i32;
    let mut d3 = (o.y(ram) as i32 - d5) & 0xFFFF;
    if o.mapping_frame(ram) == 0x4D {
        d3 = (d3 + 0x0C) & 0xFFFF;
        d5 = 0x0A;
    }
    let d1 = 6;
    let d6 = 0x0C;
    let d4 = 0x10;
    d5 *= 2;
    while a1 != a2 {
        if ram.u16(a1) == 0 {
            let dx = ((ram.u16(a1.wrapping_add(2)) as i32 - d1) & 0xFFFF) - d2;
            let hit_x = if dx >= 0 {
                dx <= d4
            } else {
                dx + 0x10000 + d6 > 0xFFFF
            };
            let dy = ((ram.u16(a1.wrapping_add(4)) as i32 - d1) & 0xFFFF) - d3;
            let hit_y = if dy >= 0 {
                dy <= d5
            } else {
                dy + 0x10000 + d6 > 0xFFFF
            };
            if hit_x && hit_y {
                ram.w16(a1, crate::rings::SPARKLING);
                let left = ram.u16(vars::PERFECT_RINGS_LEFT);
                ram.w16(vars::PERFECT_RINGS_LEFT, left.wrapping_sub(1));
                collect_ring_for(ram, !sonic);
                let mut list = vars::RING_CONSUMPTION_LIST;
                while ram.u16(list) != 0 {
                    list = list.wrapping_add(2);
                }
                ram.w16(list, a1);
                let count = ram.u16(vars::RING_CONSUMPTION_TABLE);
                ram.w16(vars::RING_CONSUMPTION_TABLE, count.wrapping_add(1));
            }
        }
        a1 = a1.wrapping_add(6);
    }
}

/// `CollectRing` (0x14EC8) / Tails' copy (0x14F1E), which in 1-player mode also counts for Sonic.
pub fn collect_ring_for(ram: &mut Ram, tails: bool) {
    collect_ring_for_with(ram, tails, true);
}

/// Collects a ring without the Sonic ring SFX (SMB3's block coins play their own PCM instead).
pub fn collect_ring_for_silent(ram: &mut Ram, tails: bool) {
    collect_ring_for_with(ram, tails, false);
}

fn collect_ring_for_with(ram: &mut Ram, tails: bool, play_ring: bool) {
    if tails {
        if ram.u16(vars::RINGS_COLLECTED_P2) < MAX_RINGS {
            let v = ram.u16(vars::RINGS_COLLECTED_P2);
            ram.w16(vars::RINGS_COLLECTED_P2, v + 1);
        }
        if ram.u16(vars::RING_COUNT_P2) < MAX_RINGS {
            let v = ram.u16(vars::RING_COUNT_P2);
            ram.w16(vars::RING_COUNT_P2, v + 1);
        }
    } else if ram.u16(vars::RINGS_COLLECTED) < MAX_RINGS {
        let v = ram.u16(vars::RINGS_COLLECTED);
        ram.w16(vars::RINGS_COLLECTED, v + 1);
    }
    if ram.u16(vars::RING_COUNT) < MAX_RINGS {
        let v = ram.u16(vars::RING_COUNT);
        ram.w16(vars::RING_COUNT, v + 1);
        ram.w8(vars::UPDATE_HUD_RINGS, ram.u8(vars::UPDATE_HUD_RINGS) | 1);
        if ram.u16(vars::RING_COUNT) >= 100 {
            let mut life = !ram.test_set(vars::EXTRA_LIFE_FLAGS, 1);
            if !life && ram.u16(vars::RING_COUNT) >= 200 {
                life = !ram.test_set(vars::EXTRA_LIFE_FLAGS, 2);
            }
            if life {
                ram.w8(vars::LIFE_COUNT, ram.u8(vars::LIFE_COUNT).wrapping_add(1));
                ram.w8(
                    vars::UPDATE_HUD_LIVES,
                    ram.u8(vars::UPDATE_HUD_LIVES).wrapping_add(1),
                );
                sfx::music(ram, 0x98);
            }
        }
    }
    if play_ring {
        sfx::sonic(ram, sfx::ids::RING);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::collision::{CollisionSet, CollisionSets};

    fn sets() -> CollisionSets {
        let set = CollisionSet {
            name: "empty".into(),
            angles: vec![0; 256],
            heights: vec![0; 256 * 16],
            widths: vec![0; 256 * 16],
        };
        CollisionSets {
            normal: set.clone(),
            alternate: set,
        }
    }

    fn sonic(ram: &mut Ram) -> Obj {
        let o = Obj::new(vars::MAIN_CHARACTER);
        o.set_id(ram, 1);
        o.set_x(ram, 100);
        o.set_y(ram, 100);
        o.set_y_radius(ram, 0x13);
        o.set_x_radius(ram, 9);
        o.set_routine(ram, player::routine::CONTROL);
        o.set_anim(ram, player::anim::WALK);
        o
    }

    #[test]
    fn hurt_with_rings_drops_lost_rings_without_zeroing_the_count_yet() {
        let sets = sets();
        let mut ram = Ram::new();
        let o = sonic(&mut ram);
        ram.w16(vars::RING_COUNT, 1);
        let source = Obj::new(vars::FIRST_DYNAMIC_SLOT);
        source.set_id(&mut ram, 0xE1);
        source.set_x(&mut ram, 90);
        assert_eq!(hurt(&mut ram, o, source), -1);
        assert_eq!(o.routine(&ram), player::routine::HURT);
        assert_eq!(o.anim(&ram), player::anim::HURT2);
        assert_eq!(o.invulnerable_time(&ram), 0x78);
        assert!(o.in_air(&ram));
        assert_eq!(o.y_vel(&ram), -0x400);
        assert_eq!(o.x_vel(&ram), 0x200, "away from the source");
        assert_eq!(o.inertia(&ram), 0);
        assert_eq!(
            ram.u16(vars::RING_COUNT),
            1,
            "LostRings clears it when it runs"
        );

        let lost = Obj::new(vars::FIRST_DYNAMIC_SLOT + 0x40);
        assert_eq!(lost.id(&ram), 0x37);
        assert_eq!(lost.word(&ram, 0x3E), vars::MAIN_CHARACTER);
        crate::objects::common::lost_rings(&mut ram, &sets, lost, 0, &[]);
        assert_eq!(ram.u16(vars::RING_COUNT), 0);
        assert_eq!(lost.routine(&ram), 2);
    }

    #[test]
    fn hurt_without_rings_kills() {
        let mut ram = Ram::new();
        let o = sonic(&mut ram);
        let source = Obj::new(vars::FIRST_DYNAMIC_SLOT);
        source.set_id(&mut ram, 0xE1);
        source.set_x(&mut ram, 90);
        hurt(&mut ram, o, source);
        assert_eq!(o.routine(&ram), player::routine::DEAD);
        assert_eq!(o.anim(&ram), player::anim::DEATH);
        assert_eq!(o.y_vel(&ram), -0x700);
        assert_eq!(o.x_vel(&ram), 0);
        assert_ne!(o.art_tile(&ram) & 0x8000, 0);
    }

    #[test]
    fn points_grant_the_extra_life_at_the_threshold() {
        let mut ram = Ram::new();
        ram.w32(vars::SCORE, 4990);
        ram.w32(vars::NEXT_EXTRA_LIFE_SCORE, 5000);
        ram.w8(vars::LIFE_COUNT, 3);
        add_points(&mut ram, 20);
        assert_eq!(ram.u32(vars::SCORE), 5010);
        assert_eq!(ram.u32(vars::NEXT_EXTRA_LIFE_SCORE), 10000);
        assert_eq!(ram.u8(vars::LIFE_COUNT), 4);
    }

    #[test]
    fn the_100th_ring_grants_one_life_and_sets_the_flag() {
        let mut ram = Ram::new();
        ram.w16(vars::RING_COUNT, 99);
        ram.w8(vars::LIFE_COUNT, 3);
        collect_ring_for(&mut ram, false);
        assert_eq!(ram.u16(vars::RING_COUNT), 100);
        assert_eq!(ram.u8(vars::LIFE_COUNT), 4);
        assert!(ram.bit(vars::EXTRA_LIFE_FLAGS, 1));
    }
}
