//! Emerald Hill's object set and the common objects its act lists use (Sonic 2's column).
//!
//! The handlers follow the legacy `EmeraldHill/*.cs` and `Common/*.cs` files. Display-only state (mapping frames,
//! animation counters, art tiles) is written where the original writes it, but no sprite pieces are built yet; the
//! motion gate only depends on the physics, the players, the camera, the rings and the score.

use crate::assets::{BridgeData, SpringSlopes};
use crate::collision::CollisionSets;
use crate::math;
use crate::objects::{
    delete_object, display_or_mark_gone, find_free_slot, find_free_slot_after, move_and_fall,
    move_object, out_of_range, sprites, touch,
};
use crate::oscillation::Oscillation;
use crate::player::{self, Unported};
use crate::{vars, Obj, Ram};

const LEVEL_COORDS: u8 = 0x04;
const XFLIP: u8 = 0x01;
const MULTI_SPRITE: u8 = 0x40;

fn sbyte(v: u8) -> i32 {
    v as i8 as i32
}

// ------------------------------------------------------------------ Buzzer ($4B)

const BUZZER_ART: u16 = 0x3D2;
const BUZZER_MAPPINGS: u32 = 0x3154E;

pub fn buzzer(ram: &mut Ram, o: Obj) -> Result<(), Unported> {
    match o.routine(ram) {
        0 => {
            o.set_mappings(ram, BUZZER_MAPPINGS);
            o.set_art_tile(ram, BUZZER_ART);
            o.set_render_flags(ram, o.render_flags(ram) | LEVEL_COORDS);
            o.set_collision_flags(ram, 0x0A);
            o.set_priority(ram, 3);
            o.set_width_pixels(ram, 0x10);
            o.set_y_radius(ram, 0x10);
            o.set_x_radius(ram, 0x18);
            o.set_routine(ram, 2);
            if let Some(flame) = find_free_slot_after(ram, o) {
                flame.set_id(ram, 0x4B);
                flame.set_routine(ram, 4);
                flame.set_mappings(ram, BUZZER_MAPPINGS);
                flame.set_art_tile(ram, BUZZER_ART);
                flame.set_priority(ram, 4);
                flame.set_width_pixels(ram, 0x10);
                flame.set_status(ram, o.status(ram));
                flame.set_render_flags(ram, o.render_flags(ram));
                flame.set_anim(ram, 1);
                flame.set_long(ram, 0x2A, 0xFFFF_0000 | o.address() as u32);
                flame.set_x(ram, o.x(ram));
                flame.set_y(ram, o.y(ram));
                o.set_word(ram, 0x2E, 0x100);
                o.set_x_vel(ram, -0x100);
                if o.render_flags(ram) & XFLIP != 0 {
                    o.set_x_vel(ram, 0x100);
                }
            }
        }
        2 => {
            if o.routine_secondary(ram) == 0 {
                fly(ram, o);
            } else {
                shoot(ram, o);
            }
            display_or_mark_gone(ram, o);
        }
        4 => {
            let parent = o.long(ram, 0x2A) as u16;
            let bee = Obj::new(parent);
            if bee.id(ram) == 0 {
                delete_object(ram, o);
                return Ok(());
            }
            if bee.word(ram, 0x30) as i16 >= 0 {
                return Ok(());
            }
            o.set_x(ram, bee.x(ram));
            o.set_y(ram, bee.y(ram));
            o.set_status(ram, bee.status(ram));
            o.set_render_flags(ram, bee.render_flags(ram));
            display_or_mark_gone(ram, o);
        }
        6 => {
            move_object(ram, o);
            display_or_mark_gone(ram, o);
        }
        _ => {}
    }
    Ok(())
}

fn fly(ram: &mut Ram, o: Obj) {
    look_for_player(ram, o);
    let pause = o.word(ram, 0x30).wrapping_sub(1);
    o.set_word(ram, 0x30, pause);
    let pause_s = pause as i16;
    if pause_s == 0x0F {
        o.set_byte(ram, 0x32, 0);
        o.set_x_vel(ram, o.x_vel(ram).wrapping_neg());
        o.set_render_flags(ram, o.render_flags(ram) ^ XFLIP);
        o.set_status(ram, o.status(ram) ^ 0x01);
        o.set_word(ram, 0x2E, 0x100);
        return;
    }
    if pause_s >= 0 {
        return;
    }
    let timer = o.word(ram, 0x2E).wrapping_sub(1);
    o.set_word(ram, 0x2E, timer);
    if (timer as i16) > 0 {
        move_object(ram, o);
        return;
    }
    o.set_word(ram, 0x30, 0x1E);
}

fn look_for_player(ram: &mut Ram, o: Obj) {
    if o.byte(ram, 0x32) != 0 {
        return;
    }
    let player = if ram.u8(vars::VINT_RUNCOUNT_LOW) & 1 != 0 {
        Obj::new(vars::SIDEKICK)
    } else {
        Obj::new(vars::MAIN_CHARACTER)
    };
    let dx = (o.x(ram) as i32 - player.x(ram) as i32) as i16 as i32;
    let distance = dx.unsigned_abs();
    if !(0x28..=0x30).contains(&distance) {
        return;
    }
    let facing_right = o.render_flags(ram) & XFLIP != 0;
    let player_left = dx >= 0;
    if player_left == facing_right {
        return;
    }
    o.set_byte(ram, 0x32, 1);
    o.set_routine_secondary(ram, 2);
    o.set_anim(ram, 3);
    o.set_word(ram, 0x34, 0x32);
}

fn shoot(ram: &mut Ram, o: Obj) {
    let t = o.word(ram, 0x34).wrapping_sub(1);
    if (t as i16) < 0 {
        o.set_routine_secondary(ram, 0);
        return;
    }
    o.set_word(ram, 0x34, t);
    if t != 0x14 {
        return;
    }
    let Some(shot) = find_free_slot_after(ram, o) else {
        let x = Obj::new(vars::TAILS_TAILS).x(ram).wrapping_add(0x14);
        Obj::new(vars::TAILS_TAILS).set_x(ram, x);
        return;
    };
    shot.set_id(ram, 0x4B);
    shot.set_routine(ram, 6);
    shot.set_mappings(ram, BUZZER_MAPPINGS);
    shot.set_art_tile(ram, BUZZER_ART);
    shot.set_priority(ram, 4);
    shot.set_collision_flags(ram, 0x98);
    shot.set_width_pixels(ram, 0x10);
    shot.set_status(ram, o.status(ram));
    shot.set_render_flags(ram, o.render_flags(ram));
    shot.set_anim(ram, 2);
    shot.set_x(ram, o.x(ram));
    shot.set_y(ram, o.y(ram).wrapping_add(0x18));
    let mut dx = 0x0D;
    shot.set_y_vel(ram, 0x180);
    shot.set_x_vel(ram, -0x180);
    if shot.render_flags(ram) & XFLIP != 0 {
        shot.set_x_vel(ram, 0x180);
        dx = -0x0D;
    }
    shot.set_x(ram, shot.x(ram).wrapping_add(dx as u16));
}

// ------------------------------------------------------------------ Masher ($5C)

pub fn masher(ram: &mut Ram, o: Obj) {
    if o.routine(ram) == 0 {
        o.set_routine(ram, 2);
        o.set_mappings(ram, 0x316A6);
        o.set_art_tile(ram, 0x414);
        o.set_render_flags(ram, LEVEL_COORDS);
        o.set_priority(ram, 4);
        o.set_collision_flags(ram, 9);
        o.set_width_pixels(ram, 0x10);
        o.set_y_vel(ram, -0x400);
        o.set_word(ram, 0x30, o.y(ram));
    }
    let start_y = o.word(ram, 0x30);
    move_object(ram, o);
    o.set_y_vel(ram, o.y_vel(ram).wrapping_add(0x18));
    if start_y < o.y(ram) {
        o.set_y(ram, start_y);
        o.set_y_vel(ram, -0x500);
    }
    if start_y.wrapping_sub(0xC0) < o.y(ram) {
        o.set_anim(ram, 1);
    } else {
        o.set_anim(ram, if o.y_vel(ram) >= 0 { 2 } else { 0 });
    }
    display_or_mark_gone(ram, o);
}

// ------------------------------------------------------------------ Coconuts ($9D) and coconut ($98)

const COCONUTS_MAPPINGS: u32 = 0x3BF94;
const COCONUT_FALL_ROUTINE: u32 = 0x3B930;

fn load_sub_object(ram: &mut Ram, o: Obj) -> Result<(), Unported> {
    let data = match o.subtype(ram) {
        0x1E => (COCONUTS_MAPPINGS, 0x03EEu16, 0x04u8, 5u8, 0x0Cu8, 0x09u8),
        0x20 => (COCONUTS_MAPPINGS, 0x03EE, 0x84, 4, 0x08, 0x8B),
        subtype => {
            return Err(Unported::new(
                0x3A814,
                format!("LoadSubObject entry for subtype {subtype:02X}"),
            ))
        }
    };
    o.set_mappings(ram, data.0);
    o.set_art_tile(ram, data.1);
    o.set_render_flags(ram, o.render_flags(ram) | data.2);
    o.set_priority(ram, data.3);
    o.set_width_pixels(ram, data.4);
    o.set_collision_flags(ram, data.5);
    o.set_routine(ram, o.routine(ram).wrapping_add(2));
    Ok(())
}

pub fn coconuts(ram: &mut Ram, o: Obj) -> Result<(), Unported> {
    match o.routine(ram) {
        0 => {
            load_sub_object(ram, o)?;
            o.set_byte(ram, 0x2A, 0x10);
            return Ok(());
        }
        2 => wait(ram, o),
        4 => {
            let timer = o.byte(ram, 0x2A).wrapping_sub(1);
            o.set_byte(ram, 0x2A, timer);
            if timer == 0 {
                o.set_routine(ram, 2);
                o.set_byte(ram, 0x2A, 0x10);
            } else {
                move_object(ram, o);
            }
        }
        6 => {
            if o.routine_secondary(ram) == 0 {
                aim(ram, o);
            } else {
                after_throw(ram, o);
            }
        }
        _ => {}
    }
    display_or_mark_gone(ram, o);
    Ok(())
}

fn wait(ram: &mut Ram, o: Obj) {
    let (_, player_right, _, dx) = orientation_to_player(ram, o);
    o.set_render_flags(ram, o.render_flags(ram) & !XFLIP);
    o.set_status(ram, o.status(ram) & !0x01);
    if player_right {
        o.set_render_flags(ram, o.render_flags(ram) | XFLIP);
        o.set_status(ram, o.status(ram) | 0x01);
    }
    let near = ((dx + 0x60) & 0xFFFF) < 0xC0;
    if near {
        if o.byte(ram, 0x2E) == 0 {
            o.set_routine(ram, 6);
            o.set_mapping_frame(ram, 1);
            o.set_byte(ram, 0x2A, 8);
            o.set_byte(ram, 0x2E, 0x20);
            return;
        }
        let delay = o.byte(ram, 0x2E).wrapping_sub(1);
        o.set_byte(ram, 0x2E, delay);
    }
    let timer = o.byte(ram, 0x2A).wrapping_sub(1);
    o.set_byte(ram, 0x2A, timer);
    if sbyte(timer) >= 0 {
        return;
    }
    o.set_routine(ram, 4);
    next_climb_step(ram, o);
}

const CLIMB_STEPS: [(u8, u8); 6] = [
    (0xFF, 0x20),
    (0x01, 0x18),
    (0xFF, 0x10),
    (0x01, 0x28),
    (0xFF, 0x20),
    (0x01, 0x10),
];

fn next_climb_step(ram: &mut Ram, o: Obj) {
    let mut step = o.word(ram, 0x2C);
    if step >= CLIMB_STEPS.len() as u16 * 2 {
        step = 0;
    }
    o.set_word(ram, 0x2C, step + 2);
    let (speed, frames) = CLIMB_STEPS[(step / 2) as usize];
    o.set_byte(ram, 0x12, speed);
    o.set_byte(ram, 0x2A, frames);
}

fn aim(ram: &mut Ram, o: Obj) {
    let timer = o.byte(ram, 0x2A).wrapping_sub(1);
    o.set_byte(ram, 0x2A, timer);
    if sbyte(timer) >= 0 {
        return;
    }
    o.set_routine_secondary(ram, 2);
    o.set_byte(ram, 0x2A, 8);
    o.set_mapping_frame(ram, 2);
    throw_coconut(ram, o);
}

fn after_throw(ram: &mut Ram, o: Obj) {
    let timer = o.byte(ram, 0x2A).wrapping_sub(1);
    o.set_byte(ram, 0x2A, timer);
    if sbyte(timer) >= 0 {
        return;
    }
    o.set_routine_secondary(ram, 0);
    o.set_routine(ram, 4);
    o.set_byte(ram, 0x2A, 8);
    next_climb_step(ram, o);
}

fn orientation_to_player(ram: &Ram, o: Obj) -> (Obj, bool, bool, i32) {
    let sonic = Obj::new(vars::MAIN_CHARACTER);
    let tails = Obj::new(vars::SIDEKICK);
    let mut player = sonic;
    let mut dx = (o.x(ram) as i32 - sonic.x(ram) as i32) as i16 as i32;
    let dx_tails = (o.x(ram) as i32 - tails.x(ram) as i32) as i16 as i32;
    if (dx.unsigned_abs() & 0xFFFF) > (dx_tails.unsigned_abs() & 0xFFFF) {
        player = tails;
        dx = dx_tails;
    }
    (player, dx < 0, o.y(ram) < player.y(ram), dx)
}

fn throw_coconut(ram: &mut Ram, o: Obj) {
    let Some(c) = find_free_slot(ram) else {
        return;
    };
    c.set_id(ram, 0x98);
    c.set_mapping_frame(ram, 3);
    c.set_subtype(ram, 0x20);
    c.set_x(ram, o.x(ram));
    c.set_y(ram, o.y(ram).wrapping_sub(13));
    let facing_right = o.render_flags(ram) & XFLIP != 0;
    let (dx, speed) = if facing_right {
        (-0x0B, 0x100)
    } else {
        (0x0B, -0x100)
    };
    c.set_x(ram, c.x(ram).wrapping_add(dx as u16));
    c.set_x_vel(ram, speed);
    c.set_y_vel(ram, -0x100);
    c.set_long(ram, 0x2A, COCONUT_FALL_ROUTINE);
}

pub fn coconut(ram: &mut Ram, o: Obj) -> Result<(), Unported> {
    if o.routine(ram) == 0 {
        load_sub_object(ram, o)?;
        return Ok(());
    }
    if !o.on_screen(ram) {
        delete_object(ram, o);
        return Ok(());
    }
    if o.long(ram, 0x2A) != COCONUT_FALL_ROUTINE {
        return Err(Unported::new(
            o.long(ram, 0x2A),
            "object $98 projectile routine",
        ));
    }
    o.set_y_vel(ram, o.y_vel(ram).wrapping_add(0x20));
    move_object(ram, o);
    display_or_mark_gone(ram, o);
    Ok(())
}

// ------------------------------------------------------------------ Spiral ($06)

pub fn spiral(
    ram: &mut Ram,
    o: Obj,
    data: Option<&crate::assets::SpiralData>,
) -> Result<(), Unported> {
    let Some(data) = data else {
        return Err(Unported::new(0x25518, "spiral data missing"));
    };
    if o.routine(ram) == 0 {
        o.set_routine(ram, 2);
        o.set_width_pixels(ram, 0xD0);
        if o.subtype(ram) & 0x80 != 0 {
            return Err(Unported::new(0x2585C, "the vertical spiral"));
        }
    } else if o.routine(ram) != 2 {
        return Err(Unported::new(0x2585C, "the vertical spiral"));
    }
    check_spiral_player(ram, o, Obj::new(vars::MAIN_CHARACTER), 3, data);
    check_spiral_player(ram, o, Obj::new(vars::SIDEKICK), 4, data);
    if ram.u16(vars::TWO_PLAYER_MODE) == 0 && out_of_range(ram, o.x(ram)) {
        delete_object(ram, o);
    }
    Ok(())
}

fn check_spiral_player(
    ram: &mut Ram,
    o: Obj,
    player: Obj,
    riding_bit: u8,
    data: &crate::assets::SpiralData,
) {
    if ram.bit(o.address().wrapping_add(0x22), riding_bit) {
        ride_spiral(ram, o, player, riding_bit, data);
        return;
    }
    if player.in_air(ram) {
        return;
    }
    let dx = (player.x(ram) as i32 - o.x(ram) as i32) as i16 as i32;
    let from_object = player.on_object(ram);
    let at_end = if player.x_vel(ram) >= 0 {
        if from_object {
            (-0xC0..=-0xB0).contains(&dx)
        } else {
            (-0xD0..=-0xC0).contains(&dx)
        }
    } else if from_object {
        (0xB0..=0xC0).contains(&dx)
    } else {
        (0xC0..=0xD0).contains(&dx)
    };
    if !at_end {
        return;
    }
    let dy = player.y(ram).wrapping_sub(o.y(ram)).wrapping_sub(0x10) as i32 & 0xFFFF;
    if dy >= 0x30 {
        return;
    }
    if !from_object && player.obj_control(ram) != 0 {
        return;
    }
    crate::objects::solid::set_ride(ram, o, player, riding_bit as i32);
}

fn ride_spiral(
    ram: &mut Ram,
    o: Obj,
    player: Obj,
    riding_bit: u8,
    data: &crate::assets::SpiralData,
) {
    let speed = (player.inertia(ram) as i32).unsigned_abs() & 0xFFFF;
    let along = player.x(ram).wrapping_sub(o.x(ram)).wrapping_add(0xD0) as i32 & 0xFFFF;
    let fall_off = speed < 0x600 || player.in_air(ram) || (along as i16) < 0 || along >= 0xD0 * 2;
    if fall_off {
        player.set_on_object(ram, false);
        ram.clear_bit(o.address().wrapping_add(0x22), riding_bit);
        player.set_flips_remaining(ram, 0);
        player.set_flip_speed(ram, 4);
        return;
    }
    if !player.on_object(ram) {
        return;
    }
    let height = data.heights.get(along as usize).copied().unwrap_or(0) as i8 as i32;
    let y = o.y(ram) as i32 + height - (player.byte(ram, 0x16) as i32 - 0x13);
    player.set_y(ram, y as u16);
    player.set_flip_angle(
        ram,
        data.flip_angles
            .get(((along >> 3) & 0x3F) as usize)
            .copied()
            .unwrap_or(0),
    );
}

// ------------------------------------------------------------------ Waterfall ($49)

pub fn waterfall(ram: &mut Ram, o: Obj) {
    if o.routine(ram) == 0 {
        o.set_routine(ram, 2);
        o.set_mappings(ram, 0x24C5E);
        o.set_art_tile(ram, 0x239E);
        o.set_render_flags(ram, LEVEL_COORDS);
        o.set_width_pixels(ram, 0x20);
        o.set_word(ram, 0x30, o.x(ram));
        o.set_priority(ram, 0);
        o.set_y_radius(ram, -128);
        o.set_render_flags(ram, o.render_flags(ram) | 0x10);
    }
    let mut near = false;
    for n in 0..2 {
        let p = player(n);
        let dx = if o.x(ram) >= p.x(ram) {
            o.x(ram).wrapping_sub(p.x(ram))
        } else {
            p.x(ram).wrapping_sub(o.x(ram))
        };
        if dx < 0x40 {
            near = true;
        }
    }
    o.set_mapping_frame(ram, o.subtype(ram).wrapping_add(near as u8));
    display_or_mark_gone(ram, o);
}

// ------------------------------------------------------------------ Monitor ($26) and contents ($2E)

pub fn monitor(ram: &mut Ram, sets: &CollisionSets, o: Obj) -> Result<(), Unported> {
    match o.routine(ram) {
        0 => {
            o.set_routine(ram, 2);
            o.set_y_radius(ram, 0x0E);
            o.set_x_radius(ram, 0x0E);
            o.set_mappings(ram, 0x15C7A);
            o.set_art_tile(ram, 0x680);
            o.set_render_flags(ram, LEVEL_COORDS);
            o.set_priority(ram, 3);
            o.set_width_pixels(ram, 0x0F);
            let entry = crate::objects::respawn_entry(o, ram);
            ram.clear_bit(entry, 7);
            if ram.bit(entry, 0) {
                o.set_routine(ram, 8);
                o.set_mapping_frame(ram, 0x0B);
                return Ok(());
            }
            o.set_collision_flags(ram, 0x46);
            let anim = if ram.u16(vars::TWO_PLAYER_MODE) != 0 {
                9
            } else {
                o.subtype(ram)
            };
            o.set_anim(ram, anim);
            monitor_update(ram, sets, o)?;
        }
        2 => monitor_update(ram, sets, o)?,
        4 => monitor_break(ram, o)?,
        6 => {
            sprites::display_sprite(ram, o);
        }
        8 => display_or_mark_gone(ram, o),
        _ => {}
    }
    Ok(())
}

fn monitor_update(ram: &mut Ram, sets: &CollisionSets, o: Obj) -> Result<(), Unported> {
    if o.routine_secondary(ram) != 0 {
        move_and_fall(ram, o);
        let y = o.y(ram) as i32 + o.y_radius(ram) as i32;
        ram.w8(vars::PRIMARY_ANGLE, 0);
        let distance = crate::collision::find_floor(
            ram,
            sets,
            y,
            o.x(ram) as i32,
            0x10,
            0x0C,
            0,
            vars::PRIMARY_ANGLE,
        ) as i16 as i32;
        if distance < 0 {
            o.set_y(ram, (o.y(ram) as i32 + distance) as u16);
            o.set_y_vel(ram, 0);
            o.set_routine_secondary(ram, 0);
        }
    }
    for (n, bit) in [(0u8, 3u8), (1, 4)] {
        let p = player(n as usize);
        if ram.bit(o.address().wrapping_add(0x22), bit) {
            if crate::objects::solid::still_on(ram, o, p, 0x1A, bit as i32) {
                crate::objects::solid::move_on_platform(ram, o, p, o.x(ram) as i32, 0x10);
            }
        } else {
            let solid = if n == 0 {
                p.anim(ram) != player::anim::ROLL
            } else {
                ram.u16(vars::TWO_PLAYER_MODE) == 0 || p.anim(ram) != player::anim::ROLL
            };
            if solid {
                let mut d6 = bit as i32;
                crate::objects::solid::solid_one(
                    ram,
                    sets,
                    o,
                    p,
                    0x1A,
                    0x0F,
                    0x10,
                    o.x(ram) as i32,
                    &mut d6,
                )?;
            }
        }
    }
    display_or_mark_gone(ram, o);
    Ok(())
}

fn monitor_break(ram: &mut Ram, o: Obj) -> Result<(), Unported> {
    let riders = o.status(ram) & 0x78;
    if riders != 0 {
        for (bit, p) in [
            (0x08u8, player(0)),
            (0x20, player(0)),
            (0x10, player(1)),
            (0x40, player(1)),
        ] {
            if riders & bit != 0 {
                let status = (p.status(ram) & 0xD7) | 2;
                p.set_status(ram, status);
            }
        }
    }
    o.set_status(ram, 0);
    o.set_routine(ram, 6);
    o.set_collision_flags(ram, 0);
    let broken_by = o.word(ram, 0x3E);
    if let Some(c) = find_free_slot(ram) {
        c.set_id(ram, 0x2E);
        c.set_x(ram, o.x(ram));
        c.set_y(ram, o.y(ram));
        c.set_anim(ram, o.anim(ram));
        c.set_word(ram, 0x3E, broken_by);
    }
    if let Some(e) = find_free_slot(ram) {
        e.set_id(ram, 0x27);
        e.set_routine(ram, 2);
        e.set_x(ram, o.x(ram));
        e.set_y(ram, o.y(ram));
    }
    ram.w8(crate::objects::respawn_entry(o, ram), 0);
    o.set_anim(ram, 0x0A);
    sprites::display_sprite(ram, o);
    Ok(())
}

pub fn monitor_contents(ram: &mut Ram, o: Obj) -> Result<(), Unported> {
    match o.routine(ram) {
        0 => {
            o.set_routine(ram, 2);
            o.set_art_tile(ram, 0x8680);
            o.set_render_flags(ram, 0x24);
            o.set_priority(ram, 3);
            o.set_width_pixels(ram, 8);
            o.set_y_vel(ram, -0x300);
            if ram.u16(vars::TWO_PLAYER_MODE) != 0 {
                return Err(Unported::new(0x157DE, "2-player random monitor"));
            }
            let frame = o.anim(ram).wrapping_add(1);
            o.set_mapping_frame(ram, frame);
            contents_rise(ram, o)?;
            sprites::display_sprite(ram, o);
        }
        2 => {
            contents_rise(ram, o)?;
            sprites::display_sprite(ram, o);
        }
        4 => {
            let linger = o.word(ram, 0x1E).wrapping_sub(1);
            o.set_word(ram, 0x1E, linger);
            if (linger as i16) < 0 {
                delete_object(ram, o);
            } else {
                sprites::display_sprite(ram, o);
            }
        }
        _ => {}
    }
    Ok(())
}

fn contents_rise(ram: &mut Ram, o: Obj) -> Result<(), Unported> {
    if o.y_vel(ram) < 0 {
        move_object(ram, o);
        o.set_y_vel(ram, o.y_vel(ram).wrapping_add(0x18));
        return Ok(());
    }
    o.set_routine(ram, 4);
    o.set_word(ram, 0x1E, 0x1D);
    let p = Obj::new(o.word(ram, 0x3E));
    let sonic = p.address() == vars::MAIN_CHARACTER;
    let monitors = if sonic {
        vars::MONITORS_BROKEN
    } else {
        vars::MONITORS_BROKEN_P2
    };
    let anim = o.anim(ram);
    match anim {
        0 | 3 => return Err(Unported::new(0x1587C, "Eggman monitor (hurt)")),
        1 | 2 => {
            let n = ram.u16(monitors).wrapping_add(1);
            ram.w16(monitors, n);
            let n = ram.u16(monitors).wrapping_add(1);
            ram.w16(monitors, n);
            let lives = if sonic {
                vars::LIFE_COUNT
            } else {
                vars::LIFE_COUNT_P2
            };
            let update = if sonic {
                vars::UPDATE_HUD_LIVES
            } else {
                vars::UPDATE_HUD_LIVES_P2
            };
            ram.w8(lives, ram.u8(lives).wrapping_add(1));
            ram.w8(update, ram.u8(update).wrapping_add(1));
        }
        4 => {
            let n = ram.u16(monitors).wrapping_add(1);
            ram.w16(monitors, n);
            let rings = if sonic {
                vars::RING_COUNT
            } else {
                vars::RING_COUNT_P2
            };
            let update = if sonic {
                vars::UPDATE_HUD_RINGS
            } else {
                vars::UPDATE_HUD_RINGS_P2
            };
            let life_flags = if sonic {
                vars::EXTRA_LIFE_FLAGS
            } else {
                vars::EXTRA_LIFE_FLAGS_P2
            };
            let collected = if sonic {
                vars::RINGS_COLLECTED
            } else {
                vars::RINGS_COLLECTED_P2
            };
            let c = (ram.u16(collected).wrapping_add(10)).min(999);
            ram.w16(collected, c);
            let r = (ram.u16(rings).wrapping_add(10)).min(999);
            ram.w16(rings, r);
            ram.w8(update, ram.u8(update) | 1);
            if r >= 100
                && (!ram.test_set(life_flags, 1) || (r >= 200 && !ram.test_set(life_flags, 2)))
            {
                ram.w8(vars::LIFE_COUNT, ram.u8(vars::LIFE_COUNT).wrapping_add(1));
                ram.w8(
                    vars::UPDATE_HUD_LIVES,
                    ram.u8(vars::UPDATE_HUD_LIVES).wrapping_add(1),
                );
            }
        }
        5 => {
            let n = ram.u16(monitors).wrapping_add(1);
            ram.w16(monitors, n);
            p.set_has_speed_shoes(ram, true);
            p.set_speed_shoes_time(ram, 0x4B0);
            let main_character = sonic && ram.u16(vars::PLAYER_MODE) != 2;
            let top = if main_character {
                vars::SONIC_TOP_SPEED
            } else {
                vars::TAILS_TOP_SPEED
            };
            ram.w16(top, 0xC00);
            ram.w16(top.wrapping_add(2), 0x18);
            ram.w16(top.wrapping_add(4), 0x80);
        }
        6 => {
            let n = ram.u16(monitors).wrapping_add(1);
            ram.w16(monitors, n);
            p.set_has_shield(ram, true);
            let shield = Obj::new(if o.byte(ram, 0x3F) != 0 {
                vars::TAILS_SHIELD
            } else {
                vars::SONIC_SHIELD
            });
            shield.set_id(ram, 0x38);
            shield.set_word(ram, 0x3E, p.address());
        }
        7 => {
            let n = ram.u16(monitors).wrapping_add(1);
            ram.w16(monitors, n);
            if ram.u8(vars::SUPER_SONIC_FLAG) != 0 {
                return Ok(());
            }
            p.set_invincible(ram, true);
            p.set_invincibility_time(ram, 0x4B0);
            let stars = Obj::new(if o.byte(ram, 0x3F) != 0 {
                vars::TAILS_INVINCIBILITY_STARS
            } else {
                vars::SONIC_INVINCIBILITY_STARS
            });
            stars.set_id(ram, 0x35);
            stars.set_word(ram, 0x3E, p.address());
        }
        8 => return Err(Unported::new(0x159EA, "teleport monitor")),
        _ => {
            let n = ram.u16(monitors).wrapping_add(1);
            ram.w16(monitors, n);
        }
    }
    Ok(())
}

// ------------------------------------------------------------------ Scenery ($1C)

pub fn scenery_s2(ram: &mut Ram, o: Obj) -> Result<(), Unported> {
    if o.routine(ram) == 0 {
        o.set_routine(ram, 2);
        let table = [
            (0x0001_4440u32, 0x43FDu16, 4u8, 6u8),
            (0x0101_4440, 0x43FD, 4, 6),
            (0x0101_26F8, 0x43B6, 4, 1),
        ];
        let Some(&(frame_and_mappings, art, width, priority)) = table.get(o.subtype(ram) as usize)
        else {
            return Err(Unported::new(
                0x14046 + o.subtype(ram) as u32 * 8,
                format!("scenery subtype {} in Sonic 2's zones", o.subtype(ram)),
            ));
        };
        o.set_mapping_frame(ram, (frame_and_mappings >> 24) as u8);
        o.set_mappings(ram, frame_and_mappings);
        o.set_art_tile(ram, art);
        o.set_render_flags(ram, o.render_flags(ram) | LEVEL_COORDS);
        o.set_width_pixels(ram, width);
        o.set_priority(ram, priority);
    }
    display_or_mark_gone(ram, o);
    Ok(())
}

// ------------------------------------------------------------------ Spikes ($36, Sonic 2)

const SPIKES_SIZES: [(u8, u8); 8] = [
    (0x10, 0x10),
    (0x20, 0x10),
    (0x30, 0x10),
    (0x40, 0x10),
    (0x10, 0x10),
    (0x10, 0x20),
    (0x10, 0x30),
    (0x10, 0x40),
];

pub fn spikes_s2(ram: &mut Ram, sets: &CollisionSets, o: Obj) -> Result<(), Unported> {
    match o.routine(ram) {
        0 => {
            o.set_routine(ram, 2);
            o.set_mappings(ram, 0x191A6);
            o.set_art_tile(ram, 0x2434);
            o.set_render_flags(ram, o.render_flags(ram) | LEVEL_COORDS);
            o.set_priority(ram, 4);
            let size = (o.subtype(ram) >> 4) as usize;
            o.set_subtype(ram, o.subtype(ram) & 0x0F);
            let (width, height) = SPIKES_SIZES.get(size).copied().unwrap_or((0x10, 0x10));
            o.set_width_pixels(ram, width);
            o.set_y_radius(ram, height as i8);
            o.set_mapping_frame(ram, size as u8);
            if size >= 4 {
                o.set_routine(ram, 4);
                o.set_art_tile(ram, 0x242C);
            }
            if o.status(ram) & 0x02 != 0 {
                o.set_routine(ram, 6);
            }
            o.set_word(ram, 0x30, o.x(ram));
            o.set_word(ram, 0x32, o.y(ram));
        }
        2 => {
            spikes_move(ram, o)?;
            let half = o.y_radius(ram) as u8 as i32;
            crate::objects::solid::solid_object(
                ram,
                sets,
                o,
                o.width_pixels(ram) as i32 + 0x0B,
                half,
                half + 1,
                o.x(ram) as i32,
            )?;
            if o.status(ram) & 0x08 != 0 {
                spikes_hurt(ram, o, Obj::new(vars::MAIN_CHARACTER));
            }
            if o.status(ram) & 0x10 != 0 {
                spikes_hurt(ram, o, Obj::new(vars::SIDEKICK));
            }
            mark_gone2(ram, o);
        }
        4 => {
            let previous_x = o.x(ram) as i32;
            spikes_move(ram, o)?;
            let half = o.y_radius(ram) as u8 as i32;
            let r = crate::objects::solid::solid_object(
                ram,
                sets,
                o,
                o.width_pixels(ram) as i32 + 0x0B,
                half,
                half + 1,
                previous_x,
            )?;
            let contact = (r.d6 >> 16) & 0xFFFF;
            if contact & 1 != 0 {
                spikes_hurt(ram, o, Obj::new(vars::MAIN_CHARACTER));
                o.set_status(ram, o.status(ram) & !0x20);
            }
            if contact & 2 != 0 {
                spikes_hurt(ram, o, Obj::new(vars::SIDEKICK));
                o.set_status(ram, o.status(ram) & !0x40);
            }
            mark_gone2(ram, o);
        }
        6 => {
            spikes_move(ram, o)?;
            let half = o.y_radius(ram) as u8 as i32;
            let r = crate::objects::solid::solid_object(
                ram,
                sets,
                o,
                o.width_pixels(ram) as i32 + 0x0B,
                half,
                half + 1,
                o.x(ram) as i32,
            )?;
            let contact = (r.d6 >> 16) & 0xFFFF;
            if contact & 4 != 0 {
                spikes_hurt(ram, o, Obj::new(vars::MAIN_CHARACTER));
            }
            if contact & 8 != 0 {
                spikes_hurt(ram, o, Obj::new(vars::SIDEKICK));
            }
            mark_gone2(ram, o);
        }
        _ => {}
    }
    Ok(())
}

fn spikes_hurt(ram: &mut Ram, o: Obj, player: Obj) {
    if player.invincible(ram) || player.invulnerable_time(ram) != 0 || player.routine(ram) >= 6 {
        return;
    }
    let y = (player.y_long(ram) as i32).wrapping_sub((player.y_vel(ram) as i32) << 8);
    player.set_y_long(ram, y as u32);
    let _ = touch::hurt(ram, player, o);
}

fn spikes_move(ram: &mut Ram, o: Obj) -> Result<(), Unported> {
    match o.subtype(ram) {
        0 => {}
        1 => {
            spikes_retract(ram, o);
            let y = o.byte(ram, 0x34) as u16 + o.word(ram, 0x32);
            o.set_y(ram, y);
        }
        2 => {
            spikes_retract(ram, o);
            let x = o.byte(ram, 0x34) as u16 + o.word(ram, 0x30);
            o.set_x(ram, x);
        }
        movement => {
            return Err(Unported::new(
                0x19104,
                format!("spikes movement type {movement}"),
            ))
        }
    }
    Ok(())
}

fn spikes_retract(ram: &mut Ram, o: Obj) {
    let wait = o.word(ram, 0x38);
    if wait != 0 {
        o.set_word(ram, 0x38, wait.wrapping_sub(1));
        return;
    }
    if o.word(ram, 0x36) != 0 {
        let d = o.word(ram, 0x34).wrapping_sub(0x800);
        if d < 0x8000 {
            o.set_word(ram, 0x34, d);
            return;
        }
        o.set_word(ram, 0x34, 0);
        o.set_word(ram, 0x36, 0);
        o.set_word(ram, 0x38, 60);
        return;
    }
    let r = o.word(ram, 0x34).wrapping_add(0x800);
    if r < 0x2000 {
        o.set_word(ram, 0x34, r);
        return;
    }
    o.set_word(ram, 0x34, 0x2000);
    o.set_word(ram, 0x36, 1);
    o.set_word(ram, 0x38, 60);
}

fn mark_gone2(ram: &mut Ram, o: Obj) {
    if ram.u16(vars::TWO_PLAYER_MODE) != 0 || !out_of_range(ram, o.word(ram, 0x30)) {
        sprites::display_sprite(ram, o);
        return;
    }
    let index = o.respawn_index(ram);
    if index != 0 {
        ram.clear_bit(vars::OBJECT_RESPAWN_TABLE + 2 + index as u16, 7);
    }
    delete_object(ram, o);
}

// ------------------------------------------------------------------ Platform ($18)

pub fn platform(ram: &mut Ram, sets: &CollisionSets, o: Obj) -> Result<(), Unported> {
    if o.routine(ram) == 0 {
        o.set_routine(ram, 2);
        let subtype = o.subtype(ram);
        let size = ((subtype >> 4) & 7) as usize;
        const SIZES: [(u8, u8); 5] = [(0x20, 0), (0x20, 1), (0x20, 2), (0x40, 3), (0x30, 4)];
        let (width, frame) = SIZES.get(size).copied().unwrap_or((0x20, 0));
        o.set_width_pixels(ram, width);
        o.set_mapping_frame(ram, frame);
        let zone = ram.u8(vars::CURRENT_ZONE_AND_ACT);
        if zone >= 0x11 {
            o.set_mappings(ram, 0x134C8);
            if subtype == 0x0A {
                o.set_mapping_frame(ram, frame.wrapping_add(1));
                o.set_width_pixels(ram, 0x20);
            }
        } else {
            o.set_mappings(ram, 0x13418);
        }
        if zone >= 0x15 {
            o.set_mappings(ram, 0x13540);
            o.set_width_pixels(ram, 0x20);
        }
        if zone == 0x0F {
            o.set_mappings(ram, 0x13470);
        }
        o.set_art_tile(ram, 0x4000);
        o.set_render_flags(ram, LEVEL_COORDS);
        o.set_priority(ram, 4);
        o.set_word(ram, 0x2C, o.y(ram));
        o.set_word(ram, 0x34, o.y(ram));
        o.set_word(ram, 0x32, o.x(ram));
        o.set_angle(ram, 0x80);
        o.set_subtype(ram, subtype & 0x0F);
        if subtype & 0x80 != 0 {
            o.set_routine(ram, 8);
            o.set_y_radius(ram, 0x30);
            o.set_render_flags(ram, o.render_flags(ram) | 0x10);
        }
    }
    match o.routine(ram) {
        2 => {
            platform_sag(ram, o);
            let old_x = o.x(ram) as i32;
            platform_move(ram, o);
            platform_bob(ram, o);
            crate::objects::solid::platform_object(ram, o, o.width_pixels(ram) as i32, 8, old_x)?;
            platform_display(ram, o);
        }
        4 => delete_object(ram, o),
        6 => {
            platform_move(ram, o);
            platform_bob(ram, o);
            platform_display(ram, o);
        }
        8 => {
            platform_sag(ram, o);
            let old_x = o.x(ram) as i32;
            platform_move(ram, o);
            platform_bob(ram, o);
            let half = o.y_radius(ram) as u8 as i32;
            crate::objects::solid::solid_object(
                ram,
                sets,
                o,
                o.width_pixels(ram) as i32 + 0x0B,
                half,
                half + 1,
                old_x,
            )?;
            platform_display(ram, o);
        }
        _ => {}
    }
    Ok(())
}

fn platform_sag(ram: &mut Ram, o: Obj) {
    let standing = o.status(ram) & 0x18;
    let sag = o.byte(ram, 0x38);
    if standing == 0 {
        if sag != 0 {
            o.set_byte(ram, 0x38, sag - 4);
        }
    } else if sag != 0x40 {
        o.set_byte(ram, 0x38, sag + 4);
    }
}

fn platform_move(ram: &mut Ram, o: Obj) {
    let angle = o.angle(ram);
    let swing = (angle as i32 - 0x40) as i8 as i32;
    let reverse = (-(angle as i32) + 0x40) as i8 as i32;
    let base_y = o.word(ram, 0x34);
    let base_x = o.word(ram, 0x32);
    match o.subtype(ram) & 0x0F {
        0 | 9 => return,
        1 => o.set_x(ram, base_x.wrapping_add(swing as u16)),
        5 => o.set_x(ram, base_x.wrapping_add(reverse as u16)),
        2 => o.set_word(ram, 0x2C, base_y.wrapping_add(swing as u16)),
        6 => o.set_word(ram, 0x2C, base_y.wrapping_add(reverse as u16)),
        0x0A => o.set_word(ram, 0x2C, base_y.wrapping_add((swing >> 1) as u16)),
        0x0B => o.set_word(ram, 0x2C, base_y.wrapping_add((reverse >> 1) as u16)),
        3 => {
            platform_wait_to_fall(ram, o);
            return;
        }
        4 => {
            platform_fall(ram, o);
            return;
        }
        7 => {
            platform_wait_for_switch(ram, o);
            return;
        }
        8 => {
            let y = o.word(ram, 0x2C).wrapping_sub(2);
            o.set_word(ram, 0x2C, y);
            if y == base_y.wrapping_sub(0x200) {
                o.set_subtype(ram, 0);
            }
            return;
        }
        _ => {}
    }
    o.set_angle(ram, ram.u8(Oscillation::value(6)));
}

fn platform_bob(ram: &mut Ram, o: Obj) {
    let (sin, _) = math::calc_sine(o.byte(ram, 0x38));
    let y = ((sin as i32 * 0x400) >> 16) + o.word(ram, 0x2C) as i32;
    o.set_y(ram, y as u16);
}

fn platform_wait_to_fall(ram: &mut Ram, o: Obj) {
    let timer = o.word(ram, 0x3A);
    if timer == 0 {
        if o.status(ram) & 0x18 != 0 {
            o.set_word(ram, 0x3A, 0x1E);
        }
        return;
    }
    let timer = timer.wrapping_sub(1);
    o.set_word(ram, 0x3A, timer);
    if timer == 0 {
        o.set_word(ram, 0x3A, 0x20);
        o.set_subtype(ram, o.subtype(ram).wrapping_add(1));
    }
}

fn platform_fall(ram: &mut Ram, o: Obj) {
    let timer = o.word(ram, 0x3A);
    if timer != 0 {
        let timer = timer.wrapping_sub(1);
        o.set_word(ram, 0x3A, timer);
        if timer == 0 {
            drop_riders(ram, o);
            o.set_routine(ram, 6);
        }
    }
    let bob = o
        .long(ram, 0x2C)
        .wrapping_add(((o.y_vel(ram) as i32) << 8) as u32);
    o.set_long(ram, 0x2C, bob);
    o.set_y_vel(ram, o.y_vel(ram).wrapping_add(0x38));
    let limit = ram.u16(vars::CAMERA_MAX_Y_POS).wrapping_add(0x120);
    if limit < o.word(ram, 0x2C) {
        o.set_routine(ram, 4);
    }
}

fn drop_riders(ram: &mut Ram, o: Obj) {
    for bit in [3u8, 4] {
        if ram.bit(o.address().wrapping_add(0x22), bit) {
            let p = Obj::new(if bit == 3 {
                vars::MAIN_CHARACTER
            } else {
                vars::SIDEKICK
            });
            p.set_in_air(ram, true);
            p.set_on_object(ram, false);
            p.set_routine(ram, 2);
            p.set_y_vel(ram, o.y_vel(ram));
            ram.clear_bit(o.address().wrapping_add(0x22), bit);
        }
    }
}

fn platform_wait_for_switch(ram: &mut Ram, o: Obj) {
    let timer = o.word(ram, 0x3A);
    if timer == 0 {
        let addr = vars::BUTTON_VINE_TRIGGER + (o.subtype(ram) >> 4) as u16;
        if ram.u8(addr) != 0 {
            o.set_word(ram, 0x3A, 0x3C);
        }
        return;
    }
    let timer = timer.wrapping_sub(1);
    o.set_word(ram, 0x3A, timer);
    if timer == 0 {
        o.set_subtype(ram, o.subtype(ram).wrapping_add(1));
    }
}

fn platform_display(ram: &mut Ram, o: Obj) {
    if ram.u16(vars::TWO_PLAYER_MODE) == 0 && out_of_range(ram, o.word(ram, 0x32)) {
        delete_object(ram, o);
    } else {
        sprites::display_sprite(ram, o);
    }
}

// ------------------------------------------------------------------ Spring ($41)

pub fn spring(
    ram: &mut Ram,
    sets: &CollisionSets,
    o: Obj,
    slopes: Option<&SpringSlopes>,
) -> Result<(), Unported> {
    match o.routine(ram) {
        0 => {
            o.set_routine(ram, 2);
            o.set_mappings(ram, 0x1CD98);
            o.set_art_tile(ram, 0x45C);
            o.set_render_flags(ram, o.render_flags(ram) | LEVEL_COORDS);
            o.set_width_pixels(ram, 0x10);
            o.set_priority(ram, 4);
            let subtype = o.subtype(ram);
            match (subtype >> 4) & 7 {
                1 => {
                    o.set_routine(ram, 4);
                    o.set_anim(ram, 2);
                    o.set_mapping_frame(ram, 3);
                    o.set_art_tile(ram, 0x470);
                    o.set_width_pixels(ram, 8);
                }
                2 => {
                    o.set_routine(ram, 6);
                    o.set_mapping_frame(ram, 6);
                    o.set_status(ram, o.status(ram) | 0x02);
                }
                3 => {
                    o.set_routine(ram, 8);
                    o.set_anim(ram, 4);
                    o.set_mapping_frame(ram, 7);
                    o.set_art_tile(ram, 0x43C);
                }
                4 => {
                    o.set_routine(ram, 0x0A);
                    o.set_anim(ram, 4);
                    o.set_mapping_frame(ram, 0x0A);
                    o.set_art_tile(ram, 0x43C);
                    o.set_status(ram, o.status(ram) | 0x02);
                }
                _ => {}
            }
            let yellow = subtype & 2 != 0;
            o.set_word(ram, 0x30, if yellow { 0xF600 } else { 0xF000 });
            if yellow {
                o.set_art_tile(ram, o.art_tile(ram) | 0x2000);
                o.set_mappings(ram, 0x1CDAE);
            }
        }
        2 => {
            for n in 0..2i32 {
                let p = player(n as usize);
                let mut d6 = 3 + n;
                crate::objects::solid::solid_always_one(
                    ram,
                    sets,
                    o,
                    p,
                    0x1B,
                    8,
                    0x10,
                    o.x(ram) as i32,
                    &mut d6,
                )?;
                if ram.bit(o.address().wrapping_add(0x22), (3 + n) as u8) {
                    launch_up(ram, o, p);
                }
            }
        }
        4 => {
            for n in 0..2i32 {
                let p = player(n as usize);
                let mut d6 = 3 + n;
                crate::objects::solid::solid_always_one(
                    ram,
                    sets,
                    o,
                    p,
                    0x13,
                    0x0E,
                    0x0F,
                    o.x(ram) as i32,
                    &mut d6,
                )?;
                if !ram.bit(o.address().wrapping_add(0x22), (5 + n) as u8) {
                    continue;
                }
                let mut side = o.status(ram) as i32;
                if o.x(ram) >= p.x(ram) {
                    side ^= 1;
                }
                if side & 1 == 0 {
                    launch_sideways(ram, o, p);
                }
            }
            launch_approaching(ram, o);
        }
        6 => {
            for n in 0..2i32 {
                let p = player(n as usize);
                let mut d6 = 3 + n;
                let contact = crate::objects::solid::solid_always_one(
                    ram,
                    sets,
                    o,
                    p,
                    0x1B,
                    8,
                    0x10,
                    o.x(ram) as i32,
                    &mut d6,
                )?;
                if contact == -2 {
                    launch_down(ram, o, p);
                }
            }
        }
        8 | 0x0A => {
            let up = o.routine(ram) == 8;
            let Some(slopes) = slopes else {
                return Err(Unported::new(0x1CD26, "diagonal spring slopes missing"));
            };
            let slope = if up { &slopes.up } else { &slopes.down };
            for n in 0..2i32 {
                let p = player(n as usize);
                let mut d6 = 3 + n;
                let contact = crate::objects::solid::sloped_solid_one(
                    ram,
                    o,
                    p,
                    0x1B,
                    0x10,
                    o.x(ram) as i32,
                    slope,
                    &mut d6,
                )?;
                let launch = if up {
                    ram.bit(o.address().wrapping_add(0x22), (3 + n) as u8)
                } else {
                    contact == -2
                };
                if launch {
                    launch_diagonal(ram, o, p, up);
                }
            }
        }
        _ => {}
    }
    display_or_mark_gone(ram, o);
    Ok(())
}

fn player(n: usize) -> Obj {
    Obj::new(if n == 0 {
        vars::MAIN_CHARACTER
    } else {
        vars::SIDEKICK
    })
}

fn spring_bounce(ram: &mut Ram, o: Obj, anim: u8) {
    o.set_word(ram, 0x1C, (anim as u16) << 8);
}

fn twirl(ram: &mut Ram, p: Obj, subtype: u8, yellow_flips: u8, red_flips: u8) {
    if subtype & 1 == 0 {
        return;
    }
    p.set_inertia(ram, 1);
    p.set_flip_angle(ram, 1);
    p.set_anim(ram, player::anim::WALK);
    p.set_flips_remaining(ram, yellow_flips);
    p.set_flip_speed(ram, if yellow_flips == 0 { 4 } else { 8 });
    if subtype & 2 == 0 {
        p.set_flips_remaining(ram, red_flips);
    }
    if p.facing_left(ram) {
        p.set_flip_angle(ram, (-(p.flip_angle(ram) as i32)) as u8);
        p.set_inertia(ram, -p.inertia(ram));
    }
}

fn switch_layer(ram: &mut Ram, p: Obj, subtype: u8) {
    match subtype & 0x0C {
        4 => {
            p.set_top_solid_bit(ram, 0x0C);
            p.set_lrb_solid_bit(ram, 0x0D);
        }
        8 => {
            p.set_top_solid_bit(ram, 0x0E);
            p.set_lrb_solid_bit(ram, 0x0F);
        }
        _ => {}
    }
}

fn launch_up(ram: &mut Ram, o: Obj, p: Obj) {
    spring_bounce(ram, o, 1);
    p.set_y(ram, p.y(ram).wrapping_add(8));
    p.set_y_vel(ram, o.word(ram, 0x30) as i16);
    p.set_in_air(ram, true);
    p.set_on_object(ram, false);
    p.set_anim(ram, 0x10);
    p.set_routine(ram, 2);
    let subtype = o.subtype(ram);
    if subtype & 0x80 != 0 {
        p.set_x_vel(ram, 0);
    }
    twirl(ram, p, subtype, 0, 1);
    switch_layer(ram, p, subtype);
}

fn launch_sideways(ram: &mut Ram, o: Obj, p: Obj) {
    spring_bounce(ram, o, 3);
    p.set_x_vel(ram, o.word(ram, 0x30) as i16);
    p.set_x(ram, p.x(ram).wrapping_add(8));
    p.set_facing_left(ram, true);
    if o.status(ram) & 0x01 == 0 {
        p.set_facing_left(ram, false);
        p.set_x(ram, p.x(ram).wrapping_sub(0x10));
        p.set_x_vel(ram, p.x_vel(ram).wrapping_neg());
    }
    p.set_move_lock(ram, 0x0F);
    p.set_inertia(ram, p.x_vel(ram));
    if !p.rolling(ram) {
        p.set_anim(ram, player::anim::WALK);
    }
    let subtype = o.subtype(ram);
    if subtype & 0x80 != 0 {
        p.set_y_vel(ram, 0);
    }
    twirl(ram, p, subtype, 1, 3);
    switch_layer(ram, p, subtype);
    ram.clear_bit(o.address().wrapping_add(0x22), 5);
    ram.clear_bit(o.address().wrapping_add(0x22), 6);
    p.set_pushing(ram, false);
}

fn launch_approaching(ram: &mut Ram, o: Obj) {
    if o.anim(ram) == 3 {
        return;
    }
    let flipped = o.status(ram) & 0x01 != 0;
    let left = if flipped {
        o.x(ram).wrapping_sub(0x28)
    } else {
        o.x(ram)
    };
    let right = if flipped {
        o.x(ram)
    } else {
        o.x(ram).wrapping_add(0x28)
    };
    let top = o.y(ram).wrapping_sub(0x18);
    let bottom = o.y(ram).wrapping_add(0x18);
    for n in 0..2 {
        let p = player(n);
        if p.in_air(ram) {
            if n == 0 {
                continue;
            }
            return;
        }
        let towards = if flipped {
            -(p.inertia(ram) as i32)
        } else {
            p.inertia(ram) as i32
        };
        let in_range = (towards as i16) >= 0
            && p.x(ram) >= left
            && p.x(ram) < right
            && p.y(ram) >= top
            && p.y(ram) < bottom;
        if in_range {
            launch_sideways(ram, o, p);
        }
    }
}

fn launch_down(ram: &mut Ram, o: Obj, p: Obj) {
    spring_bounce(ram, o, 1);
    p.set_y(ram, p.y(ram).wrapping_sub(8));
    p.set_y_vel(ram, (o.word(ram, 0x30) as i16).wrapping_neg());
    let subtype = o.subtype(ram);
    if subtype & 0x80 != 0 {
        p.set_x_vel(ram, 0);
    }
    twirl(ram, p, subtype, 0, 1);
    switch_layer(ram, p, subtype);
    p.set_in_air(ram, true);
    p.set_on_object(ram, false);
    p.set_routine(ram, 2);
}

fn launch_diagonal(ram: &mut Ram, o: Obj, p: Obj, up: bool) {
    let flipped = o.status(ram) & 0x01 != 0;
    if up {
        if !flipped {
            if o.x(ram).wrapping_sub(4) >= p.x(ram) {
                return;
            }
        } else if o.x(ram).wrapping_add(4) < p.x(ram) {
            return;
        }
    }
    spring_bounce(ram, o, 5);
    let strength = o.word(ram, 0x30) as i16;
    p.set_y_vel(
        ram,
        if up {
            strength
        } else {
            strength.wrapping_neg()
        },
    );
    p.set_x_vel(ram, strength);
    p.set_y(ram, p.y(ram).wrapping_add(if up { 6 } else { 0xFFFA }));
    p.set_x(ram, p.x(ram).wrapping_add(6));
    p.set_facing_left(ram, true);
    if !flipped {
        p.set_facing_left(ram, false);
        p.set_x(ram, p.x(ram).wrapping_sub(0x0C));
        p.set_x_vel(ram, p.x_vel(ram).wrapping_neg());
    }
    p.set_in_air(ram, true);
    p.set_on_object(ram, false);
    if up {
        p.set_anim(ram, 0x10);
    }
    p.set_routine(ram, 2);
    let subtype = o.subtype(ram);
    twirl(ram, p, subtype, 1, 3);
    switch_layer(ram, p, subtype);
}

// ------------------------------------------------------------------ Bridge ($11)

pub fn bridge(ram: &mut Ram, o: Obj, data: Option<&BridgeData>) -> Result<(), Unported> {
    if o.render_flags(ram) & MULTI_SPRITE != 0 {
        sprites::display_sprite_at(ram, o, 0x180);
        return Ok(());
    }
    let Some(data) = data else {
        return Err(Unported::new(0x12202, "bridge sag data missing"));
    };
    if o.routine(ram) == 0 {
        o.set_routine(ram, 2);
        if ram.u8(vars::CURRENT_ZONE_AND_ACT) == 8 {
            return Err(Unported::new(0x1212A, "zone 8 bridge"));
        }
        let hack = ram.u8(vars::HACK_ZONE_FLAG) != 0;
        o.set_mappings(ram, if hack { 0x12710 } else { 0x126F8 });
        o.set_art_tile(ram, if hack { 0x438E } else { 0x43B6 });
        o.set_priority(ram, 3);
        o.set_render_flags(ram, LEVEL_COORDS);
        o.set_width_pixels(ram, 0x80);
        let y = o.y(ram);
        o.set_word(ram, 0x3C, y);
        let logs = o.subtype(ram);
        let mut x = o.x(ram).wrapping_sub((logs as u16 >> 1) << 4);
        let Some(first) = spawn_logs(ram, o, 8, &mut x, y) else {
            return Ok(());
        };
        let center = first.word(ram, 0x10 + 4 * 6).wrapping_sub(8);
        first.set_x(ram, center);
        o.set_long(ram, 0x30, 0xFFFF_0000 | first.address() as u32);
        let rest = logs as i32 - 8;
        if rest <= 0 {
            update_bridge(ram, o, data)?;
            return Ok(());
        }
        let Some(second) = spawn_logs(ram, o, rest as u8, &mut x, y) else {
            return Ok(());
        };
        let center = second
            .word(ram, 0x10u16.wrapping_add(rest as u16 * 3))
            .wrapping_sub(8);
        second.set_x(ram, center);
        o.set_long(ram, 0x34, 0xFFFF_0000 | second.address() as u32);
    }
    update_bridge(ram, o, data)
}

fn spawn_logs(ram: &mut Ram, o: Obj, count: u8, x: &mut u16, y: u16) -> Option<Obj> {
    let child = find_free_slot_after(ram, o)?;
    child.set_id(ram, o.id(ram));
    child.set_x(ram, o.x(ram));
    child.set_y(ram, y);
    child.set_mappings(ram, o.mappings(ram));
    child.set_art_tile(ram, o.art_tile(ram));
    child.set_render_flags(ram, o.render_flags(ram) | MULTI_SPRITE);
    child.set_byte(ram, 0x0E, 0x40);
    child.set_byte(ram, 0x0F, count);
    for i in 0..count as u16 {
        child.set_word(ram, 0x10 + i * 6, *x);
        child.set_word(ram, 0x12 + i * 6, y);
        child.set_word(ram, 0x14 + i * 6, 0);
        *x = x.wrapping_add(0x10);
    }
    Some(child)
}

fn update_bridge(ram: &mut Ram, o: Obj, data: &BridgeData) -> Result<(), Unported> {
    let standing = o.status(ram) & 0x18;
    let mut bend = true;
    let mut sag = o.byte(ram, 0x3E);
    if standing == 0 {
        if sag == 0 {
            bend = false;
        } else {
            sag -= 4;
            o.set_byte(ram, 0x3E, sag);
        }
    } else {
        if standing & 0x10 != 0 {
            let sonic_log = o.byte(ram, 0x3F);
            let tails_log = o.byte(ram, 0x3B);
            if sonic_log != tails_log {
                o.set_byte(
                    ram,
                    0x3F,
                    if sonic_log >= tails_log {
                        sonic_log - 1
                    } else {
                        sonic_log + 1
                    },
                );
            }
        }
        if sag != 0x40 {
            o.set_byte(ram, 0x3E, sag + 4);
        }
    }
    if bend {
        bend_bridge(ram, o, data);
    }
    let logs = o.subtype(ram) as i32;
    let half_width = logs * 8 + 8;
    let width = logs * 16;
    stand_on_bridge(ram, o, 4, 0x3B, half_width as u16, width as u16);
    stand_on_bridge(ram, o, 3, 0x3F, half_width as u16, width as u16);
    if ram.u16(vars::TWO_PLAYER_MODE) == 0 && out_of_range(ram, o.x(ram)) {
        let first = o.long(ram, 0x30) as u16;
        if first != 0 {
            delete_object(ram, Obj::new(first));
        }
        let second = o.long(ram, 0x34) as u16;
        if logs > 8 && second != 0 {
            delete_object(ram, Obj::new(second));
        }
        delete_object(ram, o);
    }
    Ok(())
}

fn stand_on_bridge(
    ram: &mut Ram,
    o: Obj,
    standing_bit: u8,
    log_var: u16,
    half_width: u16,
    width: u16,
) {
    let p = Obj::new(if standing_bit == 3 {
        vars::MAIN_CHARACTER
    } else {
        vars::SIDEKICK
    });
    if !ram.bit(o.address().wrapping_add(0x22), standing_bit) {
        if p.y_vel(ram) >= 0 {
            let along = p.x(ram).wrapping_sub(o.x(ram)).wrapping_add(half_width);
            if (along as i16) >= 0 && along < width {
                crate::objects::solid::chk_y_range(
                    ram,
                    o,
                    p,
                    o.y(ram).wrapping_sub(8),
                    standing_bit as i32,
                );
            }
        }
        if ram.bit(o.address().wrapping_add(0x22), standing_bit) {
            let along = p.x(ram).wrapping_sub(o.x(ram)).wrapping_add(half_width);
            o.set_byte(ram, log_var, (along >> 4) as u8);
        }
        return;
    }
    let x = p.x(ram).wrapping_sub(o.x(ram)).wrapping_add(half_width);
    if p.in_air(ram) || (x as i16) < 0 || x >= width {
        p.set_on_object(ram, false);
        ram.clear_bit(o.address().wrapping_add(0x22), standing_bit);
        return;
    }
    let log = x >> 4;
    o.set_byte(ram, log_var, log as u8);
    let child = if log < 8 {
        o.long(ram, 0x30) as u16
    } else {
        o.long(ram, 0x34) as u16
    };
    if child != 0 {
        let child = Obj::new(child);
        let log_y = child.word(ram, 0x12 + (log % 8) * 6);
        p.set_y(
            ram,
            log_y.wrapping_sub(8).wrapping_sub(p.byte(ram, 0x16) as u16),
        );
    }
}

fn bend_bridge(ram: &mut Ram, o: Obj, data: &BridgeData) {
    let (sin, _) = math::calc_sine(o.byte(ram, 0x3E));
    let sag = sin as i32 & 0xFFFF;
    let logs = o.subtype(ram) as i32;
    let standing = o.byte(ram, 0x3F) as i32;
    let key = standing + (logs << 4);
    let index = key - (data.depression_first_subtype << 4);
    let depth = data
        .depression
        .get(index.max(0) as usize)
        .copied()
        .unwrap_or(0) as i32;
    let mut profile = ((key & 0x0F) << 4) as usize;
    let base_y = o.word(ram, 0x3C);
    let first = o.long(ram, 0x30) as u16;
    let second = o.long(ram, 0x34) as u16;
    let mut entry = first.wrapping_add(0x12);
    let mut end = first.wrapping_add(0x42);
    let mut writes = 0usize;

    let mut write = |amount: u8| {
        let y = ((amount as u32 + 1) & 0xFFFF).wrapping_mul(depth as u32 & 0xFFFF);
        let y = (y & 0xFFFF).wrapping_mul(sag as u32);
        ram.w16(entry, ((y >> 16) as u16).wrapping_add(base_y));
        entry = entry.wrapping_add(6);
        if entry == end {
            entry = second.wrapping_add(0x12);
            end = 0xFFFF;
        }
        writes += 1;
    };

    for _ in 0..=standing {
        write(data.bend.get(profile).copied().unwrap_or(0));
        profile += 1;
    }
    let after = (-(standing + 1 - logs) & 0xFF) as i8 as i32;
    if after < 0 {
        return;
    }
    profile = ((after << 4) + after) as usize;
    for _ in (0..after).rev() {
        profile = profile.saturating_sub(1);
        write(data.bend.get(profile).copied().unwrap_or(0));
    }
    let _ = writes;
}

// ------------------------------------------------------------------ PlaneSwitcher ($03)

pub fn plane_switcher(ram: &mut Ram, o: Obj) {
    match o.routine(ram) {
        0 => {
            o.set_routine(ram, 2);
            o.set_mappings(ram, 0x23FAC);
            o.set_art_tile(ram, 0x26BC);
            o.set_render_flags(ram, o.render_flags(ram) | LEVEL_COORDS);
            o.set_width_pixels(ram, 0x10);
            o.set_priority(ram, 5);
            let subtype = o.subtype(ram);
            let horizontal = subtype & 4 != 0;
            if horizontal {
                o.set_routine(ram, 4);
            }
            o.set_mapping_frame(ram, subtype & if horizontal { 7 } else { 3 });
            o.set_word(ram, 0x32, [0x20, 0x40, 0x80, 0x100][(subtype & 3) as usize]);
            for n in 0..2u8 {
                let p = player(n as usize);
                let mine = if horizontal { o.y(ram) } else { o.x(ram) };
                let theirs = if horizontal { p.y(ram) } else { p.x(ram) };
                if mine < theirs {
                    o.set_byte(ram, 0x34 + n as u16, 1);
                }
            }
            check_plane(ram, o, horizontal);
        }
        2 => check_plane(ram, o, false),
        4 => check_plane(ram, o, true),
        _ => {}
    }
    crate::objects::mark_obj_gone(ram, o);
}

fn check_plane(ram: &mut Ram, o: Obj, horizontal: bool) {
    if ram.u16(vars::DEBUG_PLACEMENT_MODE) != 0 {
        return;
    }
    let line = if horizontal { o.y(ram) } else { o.x(ram) };
    let centre = if horizontal { o.x(ram) } else { o.y(ram) };
    let range = o.word(ram, 0x32);
    let subtype = o.subtype(ram);
    for n in 0..2u16 {
        let p = player(n as usize);
        let along = if horizontal { p.y(ram) } else { p.x(ram) };
        let across = if horizontal { p.x(ram) } else { p.y(ram) } as i16;
        let was_past = o.byte(ram, 0x34 + n) != 0;
        if !was_past {
            if line > along {
                continue;
            }
            o.set_byte(ram, 0x34 + n, 1);
        } else {
            if line <= along {
                continue;
            }
            o.set_byte(ram, 0x34 + n, 0);
        }
        if across < (centre.wrapping_sub(range)) as i16
            || across >= (centre.wrapping_add(range)) as i16
        {
            continue;
        }
        if subtype & 0x80 != 0 && p.in_air(ram) {
            continue;
        }
        let forward = !was_past;
        if o.render_flags(ram) & XFLIP == 0 {
            let layer_b = subtype & if forward { 0x08 } else { 0x10 } != 0;
            p.set_top_solid_bit(ram, if layer_b { 0x0E } else { 0x0C });
            p.set_lrb_solid_bit(ram, if layer_b { 0x0F } else { 0x0D });
        }
        let mut art = p.art_tile(ram) & 0x7FFF;
        if subtype & if forward { 0x20 } else { 0x40 } != 0 {
            art |= 0x8000;
        }
        p.set_art_tile(ram, art);
    }
}
