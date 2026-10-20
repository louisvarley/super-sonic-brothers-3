//! The SMB3 World 1-1 object ids (`Smb3Objects.cs`): blocks, Goombas/Paragoombas, Koopas/Paratroopas, piranha
//! plants and their fireballs.
//!
//! Behaviour follows SMB3's translated to Sonic rules (walking speeds, hops, plant timing). Sprite drawing is a
//! later milestone; the mapping frames and the on-screen flag (which object code reads) are kept exact.

use crate::collision::{self, CollisionSets};
use crate::objects::{
    display_or_mark_gone, find_free_slot, ids, move_and_fall, move_object, solid, sprites,
};
use crate::player::anim;
use crate::{vars, Obj, Ram};

pub const BLOCK_ID: u8 = ids::BLOCK;
pub const STOMP_COOLDOWN: u16 = 0x3A;

/// `Smb3.IsEnemy`.
pub fn is_enemy(id: u8) -> bool {
    (ids::GOOMBA..=ids::PIRANHA).contains(&id) || id == 0xEA
}

/// `Smb3.ContactBounds`: (half width, half height, y) used by `TouchResponse`.
pub fn contact_bounds(ram: &Ram, o: Obj) -> (i32, i32, i32) {
    if o.id(ram) == ids::PIRANHA {
        let direction = if (o.subtype(ram) & 4) == 0 { 1 } else { -1 };
        let height = if is_red(o.subtype(ram)) { 48 } else { 32 };
        let anchor = o.word(ram, 0x30) as i32;
        let exposed =
            (height - (o.y(ram) as i32 - anchor) as i16 as i32 * direction).clamp(0, height);
        return (8, exposed / 2, anchor - direction * ((exposed + 1) / 2));
    }
    if o.id(ram) == ids::KOOPA || o.id(ram) == ids::PARATROOPA {
        return (8, o.y_radius(ram) as i32, o.y(ram) as i32);
    }
    if o.id(ram) == 0xEA {
        return (8, 16, o.y(ram) as i32 - 8);
    }
    (8, 8, o.y(ram) as i32)
}

fn is_red(subtype: u8) -> bool {
    matches!(subtype & 3, 2 | 3)
}

fn shoots(subtype: u8) -> bool {
    matches!(subtype & 3, 1 | 2)
}

/// `Smb3.Init`.
fn init(ram: &mut Ram, o: Obj, mappings: u32, art_tile: u16, width: u8, collision: u8) {
    o.set_routine(ram, 2);
    o.set_mappings(ram, mappings);
    o.set_art_tile(ram, art_tile);
    o.set_render_flags(ram, 0x04);
    o.set_priority(ram, 4);
    o.set_width_pixels(ram, width);
    o.set_collision_flags(ram, collision);
}

/// `Smb3.FaceSonic`.
fn face_sonic(ram: &mut Ram, o: Obj) {
    let sonic = Obj::new(vars::MAIN_CHARACTER);
    if sonic.x(ram) >= o.x(ram) {
        o.set_status(ram, o.status(ram) | 1);
    } else {
        o.set_status(ram, o.status(ram) & !1);
    }
}

fn facing_right(ram: &Ram, o: Obj) -> bool {
    (o.status(ram) & 1) != 0
}

/// `Smb3.Tick`: the per-object frame counter at `$30` (and the stomp cooldown at `$3A`).
fn tick(ram: &mut Ram, o: Obj) -> i32 {
    if o.byte(ram, STOMP_COOLDOWN) != 0 {
        o.set_byte(ram, STOMP_COOLDOWN, o.byte(ram, STOMP_COOLDOWN) - 1);
    }
    let t = o.byte(ram, 0x30) as i32 + 1;
    o.set_byte(ram, 0x30, t as u8);
    t
}

fn set_flip(ram: &mut Ram, o: Obj) {
    let flags = (o.render_flags(ram) & !3) | (o.status(ram) & 1);
    o.set_render_flags(ram, flags);
}

/// `Smb3.Walk`: fall/land, walk at `speed`, turn at walls (and at ledges if asked).
fn walk(ram: &mut Ram, sets: &CollisionSets, o: Obj, speed: i32, turn_at_ledges: bool) -> bool {
    o.set_x_vel(
        ram,
        if facing_right(ram, o) {
            speed as i16
        } else {
            -(speed as i16)
        },
    );
    let previous_feet = o.y(ram) as i32 + o.y_radius(ram) as i32;
    move_and_fall(ram, o);
    let mut on_floor = false;
    if o.y_vel(ram) >= 0 {
        let d = check_floor_dist(ram, sets, o) as i16 as i32;
        if d <= 0 {
            o.set_y(ram, (o.y(ram) as i32 + d) as u16);
            o.set_y_vel(ram, 0);
            on_floor = true;
        }
        if let Some(top) = block_floor(ram, o.x(ram), previous_feet) {
            if o.y(ram) as i32 + o.y_radius(ram) as i32 >= top - 1 {
                o.set_y(ram, (top - 1 - o.y_radius(ram) as i32) as u16);
                o.set_y_vel(ram, 0);
                on_floor = true;
            }
        }
    }
    let wall = if facing_right(ram, o) {
        check_right_wall(ram, sets, o, 8)
    } else {
        check_left_wall(ram, sets, o, -8)
    };
    if (wall as i16) < 0 {
        o.set_status(ram, o.status(ram) ^ 1);
        let dx = if facing_right(ram, o) { 1 } else { -1 };
        o.set_x(ram, (o.x(ram) as i32 + dx) as u16);
    } else if turn_at_ledges && on_floor {
        let ahead = (o.x(ram) as i32 + if facing_right(ram, o) { 8 } else { -8 }) & 0xFFFF;
        let (mut dist, _) = check_floor(ram, sets, o, Some(ahead as u16));
        let feet = o.y(ram) as i32 + o.y_radius(ram) as i32;
        if let Some(top) = block_floor(ram, ahead as u16, feet) {
            dist = dist.min(top - 1 - feet);
        }
        if (dist as i16) >= 8 {
            o.set_status(ram, o.status(ram) ^ 1);
        }
    }
    on_floor
}

/// `Smb3.BlockFloor`: the nearest block top under `x`, if it is at or above `minimum_y`.
fn block_floor(ram: &Ram, x: u16, minimum_y: i32) -> Option<i32> {
    let mut nearest: Option<i32> = None;
    let mut slot = vars::FIRST_DYNAMIC_SLOT;
    while slot < vars::DYNAMIC_OBJECT_RAM_END {
        if ram.u8(slot) == BLOCK_ID {
            let block = Obj::new(slot);
            let top = block.y(ram) as i32 - 8;
            let dx = (block.x(ram).wrapping_sub(x) as i16).unsigned_abs() as i32;
            if dx < 8 && top >= minimum_y && nearest.map_or(true, |n| top < n) {
                nearest = Some(top);
            }
        }
        slot = slot.wrapping_add(solid::SLOT_SIZE);
    }
    nearest
}

/// `Smb3.Overlaps`.
fn overlaps(ram: &Ram, a: Obj, b: Obj, w: i32, h: i32) -> bool {
    ((a.x(ram).wrapping_sub(b.x(ram)) as i16).unsigned_abs() as i32) < w
        && ((a.y(ram).wrapping_sub(b.y(ram)) as i16).unsigned_abs() as i32) < h
}

/// `ObjectKit.CheckFloorDist` (0x22CF2): distance below the object's feet.
fn check_floor_dist(ram: &mut Ram, sets: &CollisionSets, o: Obj) -> i32 {
    let y = o.y(ram) as i32 + o.y_radius(ram) as i32;
    ram.w8(vars::PRIMARY_ANGLE, 0);
    collision::find_floor(
        ram,
        sets,
        y,
        o.x(ram) as i32,
        0x10,
        0x0C,
        0,
        vars::PRIMARY_ANGLE,
    )
}

/// `ObjectKit.CheckFloor` (0x22CF2): (distance, angle; 0 means "use the cardinal angle").
fn check_floor(ram: &mut Ram, sets: &CollisionSets, o: Obj, x: Option<u16>) -> (i32, i32) {
    let y = o.y(ram) as i32 + o.y_radius(ram) as i32;
    ram.w8(vars::PRIMARY_ANGLE, 0);
    let d1 = collision::find_floor(
        ram,
        sets,
        y,
        x.unwrap_or(o.x(ram)) as i32,
        0x10,
        0x0C,
        0,
        vars::PRIMARY_ANGLE,
    ) as i16 as i32;
    let mut d3 = ram.u8(vars::PRIMARY_ANGLE) as i32;
    if (d3 & 1) != 0 {
        d3 = 0;
    }
    (d1, d3)
}

/// `ObjectKit.CheckRightWall` (0x22DF8).
fn check_right_wall(ram: &mut Ram, sets: &CollisionSets, o: Obj, d3: i32) -> i32 {
    ram.w8(vars::PRIMARY_ANGLE, 0);
    collision::find_wall(
        ram,
        sets,
        o.y(ram) as i32,
        (o.x(ram) as i32 + d3) & 0xFFFF,
        0x10,
        0x0D,
        0,
        vars::PRIMARY_ANGLE,
    ) as i16 as i32
}

/// `ObjectKit.CheckLeftWall` (0x22F7E).
fn check_left_wall(ram: &mut Ram, sets: &CollisionSets, o: Obj, d3: i32) -> i32 {
    ram.w8(vars::PRIMARY_ANGLE, 0);
    collision::find_wall(
        ram,
        sets,
        o.y(ram) as i32,
        (o.x(ram) as i32 + d3) & 0xFFFF,
        -0x10,
        0x0D,
        0x400,
        vars::PRIMARY_ANGLE,
    ) as i16 as i32
}

// ------------------------------------------------------------------ $E0 block

/// `$E0`: a ? block or brick (subtype = the SMB3 tile). Solid.
pub fn block(ram: &mut Ram, sets: &CollisionSets, o: Obj) {
    if o.routine(ram) == 0 {
        init(ram, o, 0xF00000, 0x4340, 8, 0);
        o.set_word(ram, 0x30, o.y(ram)); // resting y
        o.set_byte(ram, 0x32, 0); // bump timer
        let coins = if o.subtype(ram) == 0x6D { 10 } else { 0 };
        o.set_byte(ram, 0x33, coins);
    }
    let tile = o.subtype(ram);
    let brick = (0x67..=0x70).contains(&tile);
    if o.byte(ram, 0x34) == 0 {
        let frame =
            (if brick { 4 } else { 0 }) + ((ram.u16(vars::LEVEL_FRAME_COUNTER) >> 3) & 3) as u8;
        o.set_mapping_frame(ram, frame);
    } else {
        o.set_mapping_frame(ram, 8);
    }

    // The bump: up 4 px and back over 8 frames.
    let bump = o.byte(ram, 0x32);
    if bump != 0 {
        o.set_byte(ram, 0x32, bump - 1);
        let rest_y = o.word(ram, 0x30) as i32;
        let dy = if bump > 4 {
            8 - bump as i32
        } else {
            bump as i32
        };
        o.set_y(ram, (rest_y - dy) as u16);
    } else {
        o.set_y(ram, o.word(ram, 0x30));
    }

    let ball_contact = tile == 0x67
        && o.byte(ram, 0x34) == 0
        && bump == 0
        && (ball_touches(ram, o, Obj::new(vars::MAIN_CHARACTER))
            || ball_touches(ram, o, Obj::new(vars::SIDEKICK)));
    if ball_contact {
        break_block(ram, o);
        return;
    }

    let contact = solid::solid_object(ram, sets, o, 0x1B, 8, 9, o.x(ram) as i32);
    let hit_below = (contact.d6 & ((1 << 18) | (1 << 19))) != 0;
    let sonic = Obj::new(vars::MAIN_CHARACTER);
    let rolled_into =
        tile == 0x67 && (contact.d6 & (1 << 16)) != 0 && sonic.anim(ram) == anim::ROLL;
    if (hit_below || rolled_into) && o.byte(ram, 0x34) == 0 && bump == 0 {
        let tails_only = (contact.d6 & (1 << 19)) != 0 && (contact.d6 & (1 << 18)) == 0;
        let player = if tails_only {
            Obj::new(vars::SIDEKICK)
        } else {
            sonic
        };
        if tile == 0x67 {
            break_block(ram, o);
            return;
        }
        o.set_byte(ram, 0x32, 8);
        give(ram, o, player, tile);
    }
    display_or_mark_gone(ram, o);
}

fn ball_touches(ram: &Ram, block: Obj, p: Obj) -> bool {
    p.routine(ram) < 4
        && p.rolling(ram)
        && ((p.x(ram).wrapping_sub(block.x(ram)) as i16).unsigned_abs() as i32)
            < p.x_radius(ram) as i32 + 8
        && ((p.y(ram).wrapping_sub(block.y(ram)) as i16).unsigned_abs() as i32)
            < p.y_radius(ram) as i32 + 8
}

fn give(ram: &mut Ram, o: Obj, player: Obj, tile: u8) {
    match tile {
        0x63 | 0x65 | 0x6B | 0x64 | 0x6C => {
            ring_pop(ram, o);
            o.set_byte(ram, 0x34, 1);
        }
        0x6D => {
            ring_pop(ram, o);
            let left = o.byte(ram, 0x33).wrapping_sub(1);
            o.set_byte(ram, 0x33, left);
            if left == 0 {
                o.set_byte(ram, 0x34, 1);
            }
        }
        0x60 | 0x61 | 0x68 | 0x69 => {
            contents(ram, o, player, 6);
            o.set_byte(ram, 0x34, 1);
        }
        0x62 | 0x6A => {
            contents(ram, o, player, 7);
            o.set_byte(ram, 0x34, 1);
        }
        0x6E => {
            contents(ram, o, player, 1);
            o.set_byte(ram, 0x34, 1);
        }
        _ => o.set_byte(ram, 0x34, 1),
    }
}

fn ring_pop(ram: &mut Ram, o: Obj) {
    crate::objects::touch::collect_ring_for(ram, false);
    if let Some(slot) = find_free_slot(ram) {
        slot.set_id(ram, crate::objects::common_ids::POINTS);
        slot.set_x(ram, o.x(ram));
        slot.set_y(ram, o.y(ram).wrapping_sub(0x10));
        slot.set_mapping_frame(ram, 0);
    }
}

fn contents(ram: &mut Ram, o: Obj, player: Obj, monitor_item: u8) {
    if let Some(slot) = find_free_slot(ram) {
        slot.set_id(ram, 0x2E);
        slot.set_x(ram, o.x(ram));
        slot.set_y(ram, o.y(ram).wrapping_sub(0x10));
        slot.set_anim(ram, monitor_item);
        slot.set_word(ram, 0x3E, player.address());
    }
}

fn break_block(ram: &mut Ram, o: Obj) {
    crate::objects::touch::add_points(ram, 5);
    let index = o.respawn_index(ram);
    if index != 0 {
        let entry = crate::objects::respawn_entry(o, ram);
        ram.w8(entry, ram.u8(entry) | 0x80);
    }
    // Release a player standing on it.
    let sonic = Obj::new(vars::MAIN_CHARACTER);
    let tails = Obj::new(vars::SIDEKICK);
    if (o.status(ram) & 0x08) != 0 {
        sonic.set_status(ram, (sonic.status(ram) & !8) | 2);
    }
    if (o.status(ram) & 0x10) != 0 {
        tails.set_status(ram, (tails.status(ram) & !8) | 2);
    }
    if let Some(explosion) = find_free_slot(ram) {
        explosion.set_id(ram, crate::objects::common_ids::EXPLOSION);
        explosion.set_routine(ram, 2);
        explosion.set_x(ram, o.x(ram));
        explosion.set_y(ram, o.y(ram));
    }
    crate::objects::delete_object(ram, o);
}

// ------------------------------------------------------------------ $E1/$E2 Goomba

const EMERGE: u16 = 0x33;

/// `$E1` Goomba / `$E2` Paragoomba.
pub fn goomba(ram: &mut Ram, sets: &CollisionSets, o: Obj) {
    let winged = o.id(ram) == ids::PARAGOOMBA;
    if winged && o.subtype(ram) == 1 && (o.status(ram) & 0x80) == 0 {
        fly(ram, sets, o);
        return;
    }
    if !winged && o.byte(ram, EMERGE) != 0 {
        if o.routine(ram) == 0 {
            init(ram, o, 0xF01000, 0x6340, 8, 0);
            o.set_y_radius(ram, 8);
            o.set_x_radius(ram, 8);
        }
        o.set_byte(ram, EMERGE, o.byte(ram, EMERGE) - 1);
        o.set_x_vel(ram, if facing_right(ram, o) { 0x80 } else { -0x80 });
        move_object(ram, o);
        let t = tick(ram, o);
        o.set_mapping_frame(ram, ((t >> 3) & 1) as u8);
        if o.byte(ram, EMERGE) == 0 {
            o.set_collision_flags(ram, 0x0B);
        }
        set_flip(ram, o);
        display_or_mark_gone(ram, o);
        return;
    }
    if o.routine(ram) == 0 {
        let art = if winged { 0xF02000 } else { 0xF01000 };
        init(ram, o, art, 0x6340, 8, 0x0B);
        o.set_y_radius(ram, 8);
        o.set_x_radius(ram, 8);
        if winged {
            o.set_collision_property(ram, 1); // takes a hit before it is destroyed
        }
        face_sonic(ram, o);
    }
    if winged && (o.status(ram) & 0x80) != 0 {
        // Wings knocked off: carry on as a plain Goomba.
        o.set_id(ram, ids::GOOMBA);
        o.set_status(ram, o.status(ram) & !0x80);
        o.set_mappings(ram, 0xF01000);
        o.set_collision_flags(ram, 0x0B);
    }
    let on_floor = walk(ram, sets, o, 0x80, false);
    let t = tick(ram, o);
    if o.id(ram) == ids::PARAGOOMBA {
        if on_floor {
            let hops = o.byte(ram, 0x31);
            o.set_y_vel(ram, if hops == 3 { -0x500 } else { -0x200 });
            o.set_byte(ram, 0x31, (hops + 1) & 3);
            face_sonic(ram, o);
        }
        o.set_mapping_frame(ram, (3 + ((t >> 3) & 1)) as u8);
    } else {
        o.set_mapping_frame(ram, ((t >> 3) & 1) as u8);
    }
    if o.collision_flags(ram) == 0 {
        o.set_collision_flags(ram, 0x0B);
    }
    set_flip(ram, o);
    display_or_mark_gone(ram, o);
}

/// The micro goomba dropper's flight (1-2's Paragoomba subtype 1).
fn fly(ram: &mut Ram, sets: &CollisionSets, o: Obj) {
    let _ = sets;
    if o.routine(ram) == 0 {
        init(ram, o, 0xF02000, 0x6340, 8, 0x0B);
        o.set_y_radius(ram, 8);
        o.set_x_radius(ram, 8);
        o.set_collision_property(ram, 1);
        face_sonic(ram, o);
        o.set_routine_secondary(ram, 0);
    }
    let t = tick(ram, o);
    if o.routine_secondary(ram) == 0 {
        let sonic = Obj::new(vars::MAIN_CHARACTER);
        if (sonic.y(ram).wrapping_sub(o.y(ram)) as i16) >= 0x38 {
            o.set_routine_secondary(ram, 2);
            o.set_byte(ram, 0x34, 0x80);
            o.set_y_vel(ram, 0);
        } else {
            o.set_y_vel(ram, -0x100);
        }
    } else {
        let timer = o.byte(ram, 0x34).wrapping_sub(1);
        o.set_byte(ram, 0x34, timer);
        if (timer as i8) <= 0 {
            o.set_routine_secondary(ram, 0);
        }
        let drops = o.byte(ram, 0x35).wrapping_add(1);
        o.set_byte(ram, 0x35, drops);
        if (drops & 0x1F) == 0 {
            if let Some(m) = find_free_slot(ram) {
                m.set_id(ram, ids::MICRO_GOOMBA);
                m.set_x(ram, o.x(ram).wrapping_add(4));
                m.set_y(ram, o.y(ram).wrapping_add(4));
            }
        }
        o.set_y_vel(ram, if (drops & 0x10) == 0 { 0x80 } else { -0x80 });
    }
    let limit = if facing_right(ram, o) { 0x140 } else { -0x140 };
    if o.x_vel(ram) as i32 != limit {
        o.set_x_vel(
            ram,
            o.x_vel(ram)
                .wrapping_add(if limit > 0 { 0x10 } else { -0x10 }),
        );
    }
    if (t & 0x3F) == 0 {
        face_sonic(ram, o);
    }
    move_object(ram, o);
    o.set_mapping_frame(ram, (3 + ((t >> 2) & 1)) as u8);
    if o.collision_flags(ram) == 0 {
        o.set_collision_flags(ram, 0x0B);
    }
    set_flip(ram, o);
    display_or_mark_gone(ram, o);
}

// ------------------------------------------------------------------ $E3/$E4 Koopa

const WALKING: u8 = 0;
const SHELL: u8 = 2;
const SLIDING: u8 = 4;
const COOLDOWN: u16 = 0x32;

/// `$E3` Koopa Troopa and `$E4` Paratroopa.
pub fn koopa(ram: &mut Ram, sets: &CollisionSets, o: Obj) {
    let winged = o.id(ram) == ids::PARATROOPA;
    let red = !winged && o.subtype(ram) != 0;
    if o.routine(ram) == 0 {
        let art = if winged {
            0xF05000
        } else if red {
            0xF04000
        } else {
            0xF03000
        };
        init(ram, o, art, 0x6340, 8, 0x0B);
        o.set_y_radius(ram, 0x10);
        o.set_x_radius(ram, 8);
        o.set_collision_property(ram, 1);
        face_sonic(ram, o);
    }
    let t = tick(ram, o);
    match o.routine_secondary(ram) {
        WALKING => {
            if (o.status(ram) & 0x80) != 0 {
                o.set_status(ram, o.status(ram) & !0x80);
                if winged {
                    o.set_id(ram, ids::KOOPA);
                    o.set_subtype(ram, 0);
                    o.set_mappings(ram, 0xF03000);
                    o.set_collision_property(ram, 1);
                    o.set_collision_flags(ram, 0x0B);
                } else {
                    to_shell(ram, o);
                }
            } else {
                let on_floor = walk(ram, sets, o, 0x80, red);
                if winged {
                    if on_floor {
                        o.set_y_vel(ram, -0x380);
                    } else {
                        o.set_y_vel(ram, o.y_vel(ram).wrapping_sub(0x10));
                    }
                    o.set_mapping_frame(ram, (2 + ((t >> 2) & 1)) as u8);
                } else {
                    o.set_mapping_frame(ram, ((t >> 3) & 1) as u8);
                }
                if o.collision_flags(ram) == 0 {
                    o.set_collision_flags(ram, 0x0B);
                }
            }
        }
        SHELL => {
            walk(ram, sets, o, 0, false);
            if o.byte(ram, COOLDOWN) != 0 {
                o.set_byte(ram, COOLDOWN, o.byte(ram, COOLDOWN) - 1);
            } else {
                let sonic = Obj::new(vars::MAIN_CHARACTER);
                if overlaps(ram, o, sonic, 0x14, 0x18) {
                    kick(ram, o, sonic);
                }
            }
        }
        SLIDING => {
            if (o.status(ram) & 0x80) != 0 {
                o.set_status(ram, o.status(ram) & !0x80);
                to_shell(ram, o);
            } else {
                if o.byte(ram, COOLDOWN) != 0 {
                    o.set_byte(ram, COOLDOWN, o.byte(ram, COOLDOWN) - 1);
                    if o.byte(ram, COOLDOWN) == 0 {
                        o.set_collision_flags(ram, 0x0B);
                    }
                }
                walk(ram, sets, o, 0x400, false);
                knock_out_others(ram, o);
            }
        }
        _ => {}
    }
    set_flip(ram, o);
    display_or_mark_gone(ram, o);
}

fn to_shell(ram: &mut Ram, o: Obj) {
    o.set_routine_secondary(ram, SHELL);
    o.set_collision_flags(ram, 0x0B);
    o.set_byte(ram, COOLDOWN, 16);
    o.set_x_vel(ram, 0);
    o.set_y(ram, (o.y(ram) as i32 + o.y_radius(ram) as i32 - 8) as u16);
    o.set_y_radius(ram, 8);
    let frame = if o.id(ram) == ids::PARATROOPA { 4 } else { 2 };
    o.set_mapping_frame(ram, frame);
}

fn kick(ram: &mut Ram, o: Obj, sonic: Obj) {
    o.set_routine_secondary(ram, SLIDING);
    if sonic.x(ram) < o.x(ram) {
        o.set_status(ram, o.status(ram) | 1);
    } else {
        o.set_status(ram, o.status(ram) & !1);
    }
    o.set_byte(ram, COOLDOWN, 12);
}

fn knock_out_others(ram: &mut Ram, o: Obj) {
    let mut a = vars::FIRST_DYNAMIC_SLOT;
    while a < vars::TAILS_TAILS {
        if a != o.address() && is_enemy(ram.u8(a)) {
            let other = Obj::new(a);
            if overlaps(ram, o, other, 0x10, 0x14) {
                crate::objects::touch::add_points(ram, 10);
                other.set_id(ram, crate::objects::common_ids::EXPLOSION);
                other.set_routine(ram, 0);
                other.set_collision_flags(ram, 0);
            }
        }
        a = a.wrapping_add(solid::SLOT_SIZE);
    }
}

// ------------------------------------------------------------------ $E5 piranha / $E6 fireball

const RISING: u8 = 0;
const OUT: u8 = 2;
const SINKING: u8 = 4;
const HIDDEN: u8 = 6;

/// `$E5` piranha plant.
pub fn piranha(ram: &mut Ram, sets: &CollisionSets, o: Obj) {
    let _ = sets;
    let kind = o.subtype(ram) & 3;
    let does_shoot = shoots(kind);
    let red = is_red(kind);
    let direction = if (o.subtype(ram) & 4) == 0 { 1 } else { -1 };
    let height = if red { 48 } else { 32 };
    if o.routine(ram) == 0 {
        let art = match kind {
            0 => 0xF06000,
            1 => 0xF0B000,
            2 => 0xF07000,
            _ => 0xF0A000,
        };
        init(ram, o, art, 0x6340, 8, 0x0B);
        o.set_x(ram, o.x(ram).wrapping_add(8));
        o.set_y(ram, o.y(ram).wrapping_sub(8));
        o.set_word(ram, 0x30, o.y(ram)); // pipe top
        o.set_y(ram, (o.y(ram) as i32 + height * direction) as u16);
        o.set_routine_secondary(ram, HIDDEN);
        o.set_word(ram, 0x34, 0x40);
    }
    let pipe_top = o.word(ram, 0x30) as i32;
    let mut timer = o.word(ram, 0x34) as i32 - 1;
    timer = timer.max(0);
    o.set_word(ram, 0x34, timer as u16);
    let sonic = Obj::new(vars::MAIN_CHARACTER);
    match o.routine_secondary(ram) {
        RISING => {
            o.set_y(ram, (o.y(ram) as i32 - direction) as u16);
            if (o.y(ram) as i32 - pipe_top) as i16 as i32 * direction <= 0 {
                o.set_routine_secondary(ram, OUT);
                o.set_word(ram, 0x34, if does_shoot { 0x60 } else { 0x30 });
            }
        }
        OUT => {
            if does_shoot && timer == 0x30 {
                spit(ram, o, sonic, direction, height);
            }
            if timer <= 0 {
                o.set_routine_secondary(ram, SINKING);
            }
        }
        SINKING => {
            o.set_y(ram, (o.y(ram) as i32 + direction) as u16);
            if (o.y(ram) as i32 - pipe_top) as i16 as i32 * direction >= height {
                o.set_routine_secondary(ram, HIDDEN);
                o.set_word(ram, 0x34, 0x60);
            }
        }
        HIDDEN => {
            let sonic_near =
                ((sonic.x(ram).wrapping_sub(o.x(ram)) as i16).unsigned_abs() as i32) + 0x1B < 0x37;
            if timer <= 0 && !sonic_near {
                o.set_routine_secondary(ram, RISING);
            }
        }
        _ => {}
    }
    // Only touchable when out of the pipe.
    let exposed = (o.y(ram) as i32 - pipe_top) as i16 as i32 * direction;
    o.set_collision_flags(ram, if exposed < height - 8 { 0x0B } else { 0 });
    o.set_mapping_frame(ram, ((ram.u16(vars::LEVEL_FRAME_COUNTER) >> 3) & 1) as u8);
    if does_shoot {
        face_sonic(ram, o);
    } else {
        o.set_status(ram, o.status(ram) & !1);
    }
    set_flip(ram, o);
    if direction < 0 {
        o.set_render_flags(ram, o.render_flags(ram) | 2);
    }
    display_or_mark_gone(ram, o);
}

fn spit(ram: &mut Ram, o: Obj, sonic: Obj, direction: i32, height: i32) {
    let Some(f) = find_free_slot(ram) else {
        return;
    };
    f.set_id(ram, ids::FIREBALL);
    let x = o.x(ram);
    f.set_x(ram, x);
    let y = (o.word(ram, 0x30) as i32 - (height - 8) * direction) as u16;
    f.set_y(ram, y);
    let dx = sonic.x(ram).wrapping_sub(x) as i16 as i32;
    let dy = sonic.y(ram).wrapping_sub(y) as i16 as i32;
    let steep = dy.abs() > dx.abs() / 2;
    f.set_x_vel(
        ram,
        ((if steep { 0xB0 } else { 0xE0 }) * if dx < 0 { -1 } else { 1 }) as i16,
    );
    f.set_y_vel(
        ram,
        ((if steep { 0xB0 } else { 0x50 }) * if dy < 0 { -1 } else { 1 }) as i16,
    );
}

/// `$E6` piranha fireball.
pub fn fireball(ram: &mut Ram, o: Obj) {
    if o.routine(ram) == 0 {
        init(ram, o, 0xF08000, 0x6340, 4, 0x80 | 0x0B);
    }
    move_object(ram, o);
    let t = tick(ram, o);
    o.set_mapping_frame(ram, ((t >> 2) & 3) as u8);
    if t > 30 && !o.on_screen(ram) {
        crate::objects::delete_object(ram, o);
        return;
    }
    sprites::display_sprite(ram, o);
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::collision::CollisionSet;

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

    #[test]
    fn a_coin_block_gives_a_ring_and_a_points_popup() {
        let mut ram = Ram::new();
        let o = Obj::new(vars::FIRST_DYNAMIC_SLOT);
        o.set_id(&mut ram, ids::BLOCK);
        o.set_subtype(&mut ram, 0x63);
        o.set_x(&mut ram, 100);
        o.set_y(&mut ram, 100);
        let player = Obj::new(vars::MAIN_CHARACTER);
        give(&mut ram, o, player, 0x63);
        assert_eq!(ram.u16(vars::RING_COUNT), 1);
        assert_eq!(o.byte(&ram, 0x34), 1, "used");
        let points = Obj::new(vars::FIRST_DYNAMIC_SLOT + 0x40);
        assert_eq!(points.id(&ram), crate::objects::common_ids::POINTS);
    }

    #[test]
    fn the_bump_raises_the_block_for_four_frames_and_back() {
        let sets = sets();
        let mut ram = Ram::new();
        let o = Obj::new(vars::FIRST_DYNAMIC_SLOT);
        o.set_id(&mut ram, ids::BLOCK);
        o.set_subtype(&mut ram, 0x63);
        o.set_x(&mut ram, 400);
        o.set_y(&mut ram, 100);
        o.set_routine(&mut ram, 2);
        o.set_word(&mut ram, 0x30, 100);
        o.set_byte(&mut ram, 0x32, 8);
        o.set_render_flags(&mut ram, 0x84);
        block(&mut ram, &sets, o);
        assert_eq!(o.y(&ram), 100);
        assert_eq!(o.byte(&ram, 0x32), 7);
        block(&mut ram, &sets, o);
        assert_eq!(o.y(&ram), 99, "bump 7 -> 1 px up");
        block(&mut ram, &sets, o);
        assert_eq!(o.y(&ram), 98, "bump 6 -> 2 px up");
    }

    #[test]
    fn goombas_start_facing_sonic() {
        let mut ram = Ram::new();
        let o = Obj::new(vars::FIRST_DYNAMIC_SLOT);
        let sonic = Obj::new(vars::MAIN_CHARACTER);
        sonic.set_x(&mut ram, 32);
        o.set_x(&mut ram, 100);
        face_sonic(&mut ram, o);
        assert_eq!(o.status(&ram) & 1, 0);
        sonic.set_x(&mut ram, 200);
        face_sonic(&mut ram, o);
        assert_eq!(o.status(&ram) & 1, 1);
    }

    #[test]
    fn a_piranha_stays_hidden_while_sonic_is_within_27_px() {
        let sets = sets();
        let mut ram = Ram::new();
        let sonic = Obj::new(vars::MAIN_CHARACTER);
        sonic.set_x(&mut ram, 32);
        sonic.set_y(&mut ram, 396);
        let o = Obj::new(vars::FIRST_DYNAMIC_SLOT);
        o.set_id(&mut ram, ids::PIRANHA);
        o.set_subtype(&mut ram, 0);
        o.set_x(&mut ram, 360);
        o.set_y(&mut ram, 376);
        o.set_routine(&mut ram, 2);
        o.set_word(&mut ram, 0x30, 368);
        o.set_word(&mut ram, 0x34, 0);
        o.set_routine_secondary(&mut ram, HIDDEN);
        o.set_render_flags(&mut ram, 0x84);
        piranha(&mut ram, &sets, o);
        assert_eq!(o.routine_secondary(&ram), RISING, "far away: it rises");

        o.set_routine_secondary(&mut ram, HIDDEN);
        o.set_word(&mut ram, 0x34, 0);
        sonic.set_x(&mut ram, 368);
        piranha(&mut ram, &sets, o);
        assert_eq!(
            o.routine_secondary(&ram),
            HIDDEN,
            "|dx| = 0: stays in the pipe"
        );
    }
}
