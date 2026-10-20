//! Common objects the 1-1 object list and the players create: the rings spilled by a hurt player ($37), the
//! explosion ($27), the freed animal ($28) and the points popup ($29).
//!
//! Their art/mappings pointers are set as the original sets them (the numbers are RAM state), but sprite drawing is
//! a later milestone: only the on-screen flag (set by `BuildSprites`) and the object's own variables matter here.

use crate::collision::CollisionSets;
use crate::math;
use crate::objects::{
    delete_object, display_or_mark_gone, find_free_slot, move_object, sprites, touch,
};
use crate::{vars, Obj, Ram};

/// `Monitor.Breaking`: the routine a broken monitor goes to (not used by the SMB3 1-1 list).
pub const MONITOR_BREAKING: u8 = 4;

const LOST_RINGS_ID: u8 = 0x37;
const LOST_RINGS_MAPPINGS: u32 = 0x152C4;
const COLLECTED: u8 = 4;
const SPARKLE: u8 = 6;
const GONE: u8 = 8;
const MAX_RINGS: i32 = 32;
const GRAVITY: i16 = 0x18;

/// `LostRings` (0x14F78): the rings a hurt player drops.
pub fn lost_rings(ram: &mut Ram, sets: &CollisionSets, o: Obj, slot_counter: i32) {
    match o.routine(ram) {
        0 => {
            scatter_rings(ram, o);
            bounce(ram, sets, o, slot_counter);
        }
        2 => bounce(ram, sets, o, slot_counter),
        COLLECTED => {
            o.set_routine(ram, SPARKLE);
            o.set_collision_flags(ram, 0);
            o.set_priority(ram, 1);
            touch::collect_ring_for(ram, is_tails(ram, o));
            // The legacy falls through into Sparkle to display the first sparkle frame.
            sprites::display_sprite(ram, o);
        }
        SPARKLE => {
            // ObjectKit.Animate overwrites the mapping frame from the shared spill animation; only the frame matters.
            o.set_mapping_frame(ram, ram.u8(vars::RING_SPILL_ANIM_FRAME));
            sprites::display_sprite(ram, o);
        }
        GONE => delete_object(ram, o),
        _ => {}
    }
}

fn is_tails(ram: &Ram, o: Obj) -> bool {
    o.byte(ram, 0x3F) != 0
}

/// 0x14F90: one ring per ring held (up to 32), fanned out in pairs mirrored left and right.
fn scatter_rings(ram: &mut Ram, o: Obj) {
    let tails = is_tails(ram, o);
    let mut left = (ram.u16(if tails {
        vars::RING_COUNT_P2
    } else {
        vars::RING_COUNT
    }) as i32)
        .min(MAX_RINGS)
        - 1;
    let mut d4 = 0x288i32;
    let mut x_speed = 0i32;
    let mut y_speed = 0i32;
    let origin_x = o.x(ram);
    let origin_y = o.y(ram);
    let mut ring = o;
    loop {
        ring.set_id(ram, LOST_RINGS_ID);
        ring.set_routine(ram, ring.routine(ram).wrapping_add(2));
        ring.set_y_radius(ram, 8);
        ring.set_x_radius(ram, 8);
        ring.set_x(ram, origin_x);
        ring.set_y(ram, origin_y);
        ring.set_mappings(ram, LOST_RINGS_MAPPINGS);
        ring.set_art_tile(ram, 0x26BC);
        ring.set_render_flags(ram, 0x80 | 0x04);
        ring.set_priority(ram, 3);
        ring.set_collision_flags(ram, 0x47);
        ring.set_width_pixels(ram, 8);
        ram.w8(vars::RING_SPILL_ANIM_COUNTER, 0xFF);
        if (d4 as i16) >= 0 {
            let (sin, cos) = math::calc_sine((d4 & 0xFF) as u8);
            let shift = (d4 & 0xFFFF) >> 8;
            x_speed = ((sin as i32) << shift) & 0xFFFF;
            y_speed = ((cos as i32) << shift) & 0xFFFF;
            let angle = (d4 & 0xFF) + 0x10;
            d4 = (d4 & 0xFF00) | (angle & 0xFF);
            if angle > 0xFF {
                let next = (d4 & 0xFFFF) - 0x80;
                d4 = if next < 0 { 0x288 } else { next };
            }
        }
        ring.set_x_vel(ram, x_speed as i16);
        ring.set_y_vel(ram, y_speed as i16);
        x_speed = (-x_speed) & 0xFFFF;
        d4 = (-d4) & 0xFFFF;
        if left == 0 {
            break;
        }
        left -= 1;
        let Some(slot) = find_free_slot(ram) else {
            break;
        };
        ring = slot;
    }
    ram.w16(
        if tails {
            vars::RING_COUNT_P2
        } else {
            vars::RING_COUNT
        },
        0,
    );
    ram.w8(
        if tails {
            vars::UPDATE_HUD_RINGS_P2
        } else {
            vars::UPDATE_HUD_RINGS
        },
        0x80,
    );
    ram.w8(
        if tails {
            vars::EXTRA_LIFE_FLAGS_P2
        } else {
            vars::EXTRA_LIFE_FLAGS
        },
        0,
    );
}

/// 0x15076.
fn bounce(ram: &mut Ram, sets: &CollisionSets, o: Obj, slot_counter: i32) {
    o.set_mapping_frame(ram, ram.u8(vars::RING_SPILL_ANIM_FRAME));
    move_object(ram, o);
    o.set_y_vel(ram, o.y_vel(ram).wrapping_add(GRAVITY));
    let check_floor = ((ram.u8(vars::VINT_RUNCOUNT_LOW) as i32 + slot_counter) & 7) == 0;
    if o.y_vel(ram) >= 0 && check_floor {
        if o.on_screen(ram) {
            let feet = o.y(ram) as i32 + o.y_radius(ram) as i32;
            ram.w8(vars::PRIMARY_ANGLE, 0);
            let distance = crate::collision::ring_find_floor(
                ram,
                sets,
                feet,
                o.x(ram) as i32,
                0x10,
                0x0C,
                0,
                vars::PRIMARY_ANGLE,
            ) as i16 as i32;
            if distance < 0 {
                // Land: bounce back up at 3/4 speed.
                o.set_y(ram, (o.y(ram) as i32 + distance) as u16);
                let half = o.y_vel(ram) >> 2;
                o.set_y_vel(ram, o.y_vel(ram).wrapping_sub(half));
                o.set_y_vel(ram, o.y_vel(ram).wrapping_neg());
            }
        } else if ram.u16(vars::TWO_PLAYER_MODE) != 0 {
            delete_object(ram, o);
            return;
        }
    }
    if ram.u8(vars::RING_SPILL_ANIM_COUNTER) == 0 {
        delete_object(ram, o);
        return;
    }
    let level_bottom = ram.u16(vars::CAMERA_MAX_Y_POS).wrapping_add(0xE0) as i32;
    if level_bottom < o.y(ram) as i32 {
        delete_object(ram, o);
        return;
    }
    sprites::display_sprite(ram, o);
}

/// `Explosion` (0x25096): starts at routine 0 when an enemy is destroyed.
pub fn explosion(ram: &mut Ram, o: Obj) {
    match o.routine(ram) {
        0 => {
            o.set_routine(ram, 2);
            if let Some(animal) = find_free_slot(ram) {
                animal.set_id(ram, crate::objects::common_ids::ANIMAL);
                animal.set_x(ram, o.x(ram));
                animal.set_y(ram, o.y(ram));
                animal.set_word(ram, 0x3E, o.word(ram, 0x3E));
            }
            init_explosion(ram, o);
        }
        2 => init_explosion(ram, o),
        COLLECTED => {
            let d = o.anim_frame_duration(ram).wrapping_sub(1);
            o.set_anim_frame_duration(ram, d);
            if (d as i8) < 0 {
                o.set_anim_frame_duration(ram, 7);
                o.set_mapping_frame(ram, o.mapping_frame(ram).wrapping_add(1));
                if o.mapping_frame(ram) == 5 {
                    delete_object(ram, o);
                    return;
                }
            }
            sprites::display_sprite(ram, o);
        }
        _ => {}
    }
}

fn init_explosion(ram: &mut Ram, o: Obj) {
    o.set_routine(ram, 4);
    o.set_mappings(ram, 0x25174);
    o.set_art_tile(ram, 0x5A4);
    o.set_render_flags(ram, 0x04);
    o.set_priority(ram, 1);
    o.set_collision_flags(ram, 0);
    o.set_width_pixels(ram, 0x0C);
    o.set_anim_frame_duration(ram, 3);
    o.set_mapping_frame(ram, 0);
}

/// `Animal` (0x14D5E): the animal freed by an explosion; a single rising hop.
pub fn animal(ram: &mut Ram, o: Obj) {
    if o.routine(ram) == 0 {
        if o.byte(ram, 0x38) != 0 {
            o.set_routine(ram, 0x1C);
            o.set_collision_flags(ram, 0);
            o.set_priority(ram, 0);
            o.set_render_flags(ram, 0x84);
            if o.on_screen(ram) {
                sprites::display_sprite(ram, o);
            }
            return;
        }
        o.set_routine(ram, 2);
        o.set_mappings(ram, 0x14C4A);
        o.set_art_tile(ram, 0x84AC);
        o.set_render_flags(ram, 0x04);
        o.set_priority(ram, 1);
        o.set_width_pixels(ram, 8);
        o.set_y_vel(ram, -0x300);
    }
    if o.routine(ram) == 0x1C {
        if !o.on_screen(ram) {
            delete_object(ram, o);
            return;
        }
        let delay = o.word(ram, 0x36).wrapping_sub(1);
        o.set_word(ram, 0x36, delay);
        if delay == 0 {
            o.set_routine(ram, 2);
            o.set_priority(ram, 1);
            o.set_y_vel(ram, -0x300);
        }
        sprites::display_sprite(ram, o);
        return;
    }
    if o.y_vel(ram) >= 0 {
        delete_object(ram, o);
        return;
    }
    move_object(ram, o);
    o.set_y_vel(ram, o.y_vel(ram).wrapping_add(0x18));
    sprites::display_sprite(ram, o);
}

/// `Points` (0x14DD2): the 10-point popup a ring block leaves.
pub fn points(ram: &mut Ram, o: Obj) {
    if o.routine(ram) == 0 {
        o.set_routine(ram, 2);
        o.set_mappings(ram, 0x14DD2);
        o.set_art_tile(ram, 0x84AC);
        o.set_render_flags(ram, 0x04);
        o.set_priority(ram, 1);
        o.set_width_pixels(ram, 8);
        o.set_y_vel(ram, -0x300);
    }
    if o.y_vel(ram) >= 0 {
        delete_object(ram, o);
        return;
    }
    move_object(ram, o);
    o.set_y_vel(ram, o.y_vel(ram).wrapping_add(0x18));
    sprites::display_sprite(ram, o);
}

/// `MarkObjGone` variant used by the SMB3 objects.
pub fn display_or_mark(ram: &mut Ram, o: Obj) {
    display_or_mark_gone(ram, o);
}
