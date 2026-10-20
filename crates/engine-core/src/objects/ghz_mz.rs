//! Green Hill and Marble Zone objects that are not boss-specific.

use crate::collision::{find_floor, CollisionSets};
use crate::objects::{
    delete_object, display_or_mark_gone, find_free_slot, move_and_fall, move_object, out_of_range,
    sprites, touch,
};
use crate::player::{self, Unported};
use crate::{vars, Obj, Ram};

const LEVEL_COORDS: u8 = 0x04;
const XFLIP: u8 = 0x01;

fn object_floor(ram: &mut Ram, sets: &CollisionSets, o: Obj) -> i32 {
    let y = o.y(ram) as i32 + o.y_radius(ram) as i32;
    ram.w8(vars::PRIMARY_ANGLE, 0);
    find_floor(
        ram,
        sets,
        y,
        o.x(ram) as i32,
        0x10,
        0x0C,
        0,
        vars::PRIMARY_ANGLE,
    ) as i16 as i32
}

// ------------------------------------------------------------------ Swinging platform ($15)

pub fn swinging_platform(ram: &mut Ram, _sets: &CollisionSets, o: Obj) -> Result<(), Unported> {
    if o.routine(ram) == 0 {
        o.set_routine(ram, 2);
        o.set_mappings(ram, 0x12E28);
        o.set_art_tile(ram, 0x4380);
        o.set_render_flags(ram, LEVEL_COORDS);
        o.set_priority(ram, 3);
        o.set_width_pixels(ram, 0x18);
        o.set_y_radius(ram, 8);
        o.set_word(ram, 0x38, o.y(ram));
        o.set_word(ram, 0x3A, o.x(ram));
        o.set_word(ram, 0x26, 0x8000);
        o.set_subtype(ram, o.subtype(ram) & 0x70);
    }
    let old_x = o.x(ram) as i32;
    let angle = ram.u8(crate::oscillation::Oscillation::value(6));
    let (sin, cos) = crate::math::calc_sine(angle);
    let links = o.subtype(ram) & 0x0F;
    let axial = links as i32 * 16 + 8;
    o.set_x(
        ram,
        (o.word(ram, 0x3A) as i32 + ((cos as i32 * axial) >> 8)) as u16,
    );
    o.set_y(
        ram,
        (o.word(ram, 0x38) as i32 + ((sin as i32 * axial) >> 8)) as u16,
    );
    crate::objects::solid::platform_object(ram, o, o.width_pixels(ram) as i32, 0, old_x)?;
    sprites::display_sprite(ram, o);
    Ok(())
}

// ------------------------------------------------------------------ Crabmeat ($1F)

pub fn crabmeat(ram: &mut Ram, sets: &CollisionSets, o: Obj) -> Result<(), Unported> {
    if o.routine(ram) == 0 {
        o.set_y_radius(ram, 0x10);
        o.set_x_radius(ram, 8);
        o.set_mappings(ram, 0x152166);
        o.set_art_tile(
            ram,
            if ram.u8(vars::CURRENT_ZONE_AND_ACT) >= 0x15 {
                0x3BE
            } else {
                0x3C2
            },
        );
        o.set_render_flags(ram, LEVEL_COORDS);
        o.set_priority(ram, 3);
        o.set_collision_flags(ram, 6);
        o.set_width_pixels(ram, 0x15);
        move_and_fall(ram, o);
        let distance = object_floor(ram, sets, o);
        if distance >= 0 {
            return Ok(());
        }
        o.set_y(ram, (o.y(ram) as i32 + distance) as u16);
        o.set_y_vel(ram, 0);
        o.set_routine(ram, 2);
    }
    match o.routine(ram) {
        2 => {
            if o.routine_secondary(ram) == 0 {
                let timer = o.word(ram, 0x30).wrapping_sub(1);
                o.set_word(ram, 0x30, timer);
                if (timer as i16) < 0 {
                    let fire = o.byte(ram, 0x32) & 2 != 0;
                    o.set_byte(ram, 0x32, o.byte(ram, 0x32) ^ 2);
                    if fire && o.on_screen(ram) {
                        o.set_word(ram, 0x30, 0x3B);
                        o.set_anim(ram, 6);
                        for dx in [-0x10i32, 0x10] {
                            if let Some(m) = find_free_slot(ram) {
                                m.set_id(ram, 0x1F);
                                m.set_routine(ram, 6);
                                m.set_x(ram, o.x(ram).wrapping_add(dx as u16));
                                m.set_y(ram, o.y(ram));
                                m.set_x_vel(ram, if dx < 0 { -0x100 } else { 0x100 });
                            }
                        }
                        return Ok(());
                    }
                    o.set_routine_secondary(ram, 2);
                    o.set_word(ram, 0x30, 0x7F);
                    let was_flipped = o.status(ram) & XFLIP != 0;
                    o.set_status(ram, o.status(ram) ^ XFLIP);
                    o.set_x_vel(ram, if was_flipped { 0x80 } else { -0x80 });
                }
            } else {
                let timer = o.word(ram, 0x30).wrapping_sub(1);
                o.set_word(ram, 0x30, timer);
                if (timer as i16) < 0 {
                    o.set_routine_secondary(ram, 0);
                    o.set_word(ram, 0x30, 0x3B);
                    o.set_x_vel(ram, 0);
                } else {
                    move_object(ram, o);
                    let distance = object_floor(ram, sets, o);
                    let facing_left = o.status(ram) & XFLIP != 0;
                    let ahead_x = if facing_left {
                        o.x(ram).wrapping_sub(0x10)
                    } else {
                        o.x(ram).wrapping_add(0x10)
                    };
                    let ahead = object_floor_at(ram, sets, o, ahead_x as i32);
                    if !(-8..0x0C).contains(&distance) || !(-8..0x0C).contains(&ahead) {
                        o.set_routine_secondary(ram, 0);
                        o.set_word(ram, 0x30, 0x3B);
                        o.set_x_vel(ram, 0);
                    } else {
                        o.set_y(ram, (o.y(ram) as i32 + distance) as u16);
                    }
                }
            }
            display_or_mark_gone(ram, o);
        }
        4 => delete_object(ram, o),
        6 | 8 => {
            move_and_fall(ram, o);
            if ram.u16(vars::CAMERA_MAX_Y_POS).wrapping_add(0xE0) < o.y(ram) {
                delete_object(ram, o);
            } else {
                sprites::display_sprite(ram, o);
            }
        }
        _ => {}
    }
    Ok(())
}

// ------------------------------------------------------------------ Newtron ($42)

pub fn newtron(ram: &mut Ram, sets: &CollisionSets, o: Obj) -> Result<(), Unported> {
    if o.routine(ram) == 0 {
        o.set_routine(ram, 2);
        o.set_mappings(ram, 0x1524FE);
        o.set_art_tile(ram, 0x500);
        o.set_render_flags(ram, LEVEL_COORDS);
        o.set_priority(ram, 4);
        o.set_width_pixels(ram, 0x14);
        o.set_y_radius(ram, 0x10);
        o.set_x_radius(ram, 8);
    }
    match o.routine(ram) {
        2 => {
            match o.routine_secondary(ram) {
                0 => {
                    let sonic = Obj::new(vars::MAIN_CHARACTER);
                    o.set_status(ram, o.status(ram) | XFLIP);
                    if sonic.x(ram) < o.x(ram) {
                        o.set_status(ram, o.status(ram) & !XFLIP);
                    }
                    let dx = (sonic.x(ram) as i16)
                        .wrapping_sub(o.x(ram) as i16)
                        .unsigned_abs();
                    if dx < 0x80 {
                        o.set_routine_secondary(ram, 2);
                        if o.subtype(ram) != 0 {
                            o.set_routine_secondary(ram, 8);
                        }
                    }
                }
                2 => {
                    if o.mapping_frame(ram) >= 4 {
                        move_and_fall(ram, o);
                        let distance = object_floor(ram, sets, o);
                        if distance < 0 {
                            o.set_y(ram, (o.y(ram) as i32 + distance) as u16);
                            o.set_y_vel(ram, 0);
                            o.set_routine_secondary(ram, 4);
                            o.set_collision_flags(ram, 0x0D);
                            o.set_x_vel(
                                ram,
                                if o.status(ram) & XFLIP != 0 {
                                    0x200
                                } else {
                                    -0x200
                                },
                            );
                        }
                    }
                }
                4 => {
                    move_object(ram, o);
                    let distance = object_floor(ram, sets, o);
                    if !(-8..0x0C).contains(&distance) {
                        o.set_routine_secondary(ram, 6);
                    } else {
                        o.set_y(ram, (o.y(ram) as i32 + distance) as u16);
                    }
                }
                6 => move_object(ram, o),
                8 if o.byte(ram, 0x32) == 0 => {
                    o.set_byte(ram, 0x32, 1);
                    if let Some(m) = find_free_slot(ram) {
                        let right = o.status(ram) & XFLIP != 0;
                        m.set_id(ram, 0x23);
                        m.set_subtype(ram, 1);
                        m.set_x(
                            ram,
                            o.x(ram).wrapping_add(if right { 0x14 } else { 0xFFEC }),
                        );
                        m.set_y(ram, o.y(ram).wrapping_sub(8));
                        m.set_x_vel(ram, if right { 0x200 } else { -0x200 });
                        m.set_status(ram, o.status(ram));
                    }
                }
                8 => {}
                _ => {}
            }
            display_or_mark_gone(ram, o);
        }
        4 => delete_object(ram, o),
        _ => {}
    }
    Ok(())
}

// ------------------------------------------------------------------ Basaran ($55)

pub fn basaran(ram: &mut Ram, o: Obj) -> Result<(), Unported> {
    if o.routine(ram) == 0 {
        o.set_routine(ram, 2);
        o.set_mappings(ram, 0x1553E8);
        o.set_art_tile(ram, 0x8518);
        o.set_render_flags(ram, LEVEL_COORDS);
        o.set_y_radius(ram, 0x0C);
        o.set_priority(ram, 2);
        o.set_collision_flags(ram, 0x0B);
        o.set_width_pixels(ram, 0x10);
    }
    let sonic = Obj::new(vars::MAIN_CHARACTER);
    match o.routine_secondary(ram) {
        0 => {
            let dx = (sonic.x(ram) as i16)
                .wrapping_sub(o.x(ram) as i16)
                .unsigned_abs();
            o.set_status(ram, o.status(ram) | XFLIP);
            if sonic.x(ram) < o.x(ram) {
                o.set_status(ram, o.status(ram) & !XFLIP);
            }
            o.set_word(ram, 0x36, sonic.y(ram));
            let dy = sonic.y(ram).wrapping_sub(o.y(ram));
            if dx < 0x80 && sonic.y(ram) >= o.y(ram) && dy < 0x80 {
                o.set_routine_secondary(ram, 2);
            }
        }
        2 => {
            move_object(ram, o);
            o.set_y_vel(ram, o.y_vel(ram).wrapping_add(0x18));
            let target = o.word(ram, 0x36);
            if target < o.y(ram) {
                if !o.on_screen(ram) {
                    o.clear(ram);
                    return Ok(());
                }
            } else if target - o.y(ram) < 0x10 {
                o.set_x_vel(
                    ram,
                    if o.status(ram) & XFLIP != 0 {
                        0x100
                    } else {
                        -0x100
                    },
                );
                o.set_y_vel(ram, 0);
                o.set_routine_secondary(ram, 4);
            }
        }
        4 => {
            move_object(ram, o);
            let dx = (sonic.x(ram) as i16)
                .wrapping_sub(o.x(ram) as i16)
                .unsigned_abs();
            if dx >= 0x80 {
                o.set_routine_secondary(ram, 6);
            }
        }
        6 => {
            move_object(ram, o);
            o.set_y_vel(ram, o.y_vel(ram).wrapping_sub(0x18));
            if o.y_vel(ram) <= 0 {
                o.set_x_vel(ram, 0);
                o.set_y_vel(ram, 0);
                o.set_routine_secondary(ram, 0);
            }
        }
        _ => {}
    }
    display_or_mark_gone(ram, o);
    Ok(())
}

// ------------------------------------------------------------------ Moving block ($52)

pub fn moving_block(ram: &mut Ram, _sets: &CollisionSets, o: Obj) -> Result<(), Unported> {
    if o.routine(ram) == 0 {
        o.set_routine(ram, 2);
        o.set_mappings(ram, 0x154C3E);
        o.set_art_tile(ram, 0x4500);
        o.set_render_flags(ram, LEVEL_COORDS);
        o.set_priority(ram, 4);
        let size = ((o.subtype(ram) >> 3) & 0x1E) as usize / 2;
        const SIZES: [(u8, u8); 5] = [(0x10, 0), (0x20, 1), (0x20, 2), (0x40, 3), (0x30, 4)];
        let (width, frame) = SIZES.get(size).copied().unwrap_or((0x10, 0));
        o.set_width_pixels(ram, width);
        o.set_mapping_frame(ram, frame);
        o.set_word(ram, 0x30, o.x(ram));
        o.set_word(ram, 0x32, o.y(ram));
        o.set_subtype(ram, o.subtype(ram) & 0x0F);
    }
    let old_x = o.x(ram) as i32;
    match o.subtype(ram) {
        1 => {
            let swing = ram.u8(crate::oscillation::Oscillation::value(3)) as i32;
            o.set_x(ram, (o.word(ram, 0x30) as i32 - swing) as u16);
        }
        8 => {
            let swing = ram.u8(crate::oscillation::Oscillation::value(7)) as i32;
            o.set_y(ram, (o.word(ram, 0x32) as i32 - swing) as u16);
        }
        _ => {}
    }
    crate::objects::solid::platform_object(ram, o, o.width_pixels(ram) as i32, 9, old_x)?;
    if out_of_range(ram, o.word(ram, 0x30)) {
        o.clear(ram);
    } else {
        sprites::display_sprite(ram, o);
    }
    Ok(())
}

// ------------------------------------------------------------------ Push block ($33) and collapsing floor ($53)

pub fn push_block(ram: &mut Ram, sets: &CollisionSets, o: Obj) -> Result<(), Unported> {
    if o.routine(ram) == 0 {
        o.set_routine(ram, 2);
        o.set_y_radius(ram, 0x0F);
        o.set_x_radius(ram, 0x0F);
        o.set_mappings(ram, 0x1549A8);
        o.set_art_tile(ram, 0x4500);
        o.set_render_flags(ram, LEVEL_COORDS);
        o.set_priority(ram, 3);
        o.set_width_pixels(ram, if o.subtype(ram) & 7 == 0 { 0x10 } else { 0x40 });
        o.set_word(ram, 0x34, o.x(ram));
        o.set_word(ram, 0x36, o.y(ram));
    }
    crate::objects::solid::solid_object(
        ram,
        sets,
        o,
        o.width_pixels(ram) as i32 + 0x0B,
        0x10,
        0x11,
        o.x(ram) as i32,
    )?;
    if out_of_range(ram, o.x(ram)) {
        o.clear(ram);
    } else {
        sprites::display_sprite(ram, o);
    }
    Ok(())
}

pub fn collapsing_floor(ram: &mut Ram, sets: &CollisionSets, o: Obj) -> Result<(), Unported> {
    if o.routine(ram) == 0 {
        o.set_routine(ram, 2);
        o.set_render_flags(ram, LEVEL_COORDS);
        o.set_width_pixels(ram, 0x38);
        o.set_y_radius(ram, 0x20);
        o.set_priority(ram, 4);
    }
    if o.routine(ram) == 2 {
        crate::objects::solid::solid_object(ram, sets, o, 0x2B, 0x20, 0x21, o.x(ram) as i32)?;
        if o.status(ram) & 0x18 != 0 {
            o.set_routine(ram, 4);
            o.set_word(ram, 0x30, 0x1E);
        }
    } else {
        let t = o.word(ram, 0x30).wrapping_sub(1);
        o.set_word(ram, 0x30, t);
        if t == 0 {
            move_and_fall(ram, o);
            if !o.on_screen(ram) {
                delete_object(ram, o);
            } else {
                sprites::display_sprite(ram, o);
            }
        } else {
            crate::objects::solid::solid_object(ram, sets, o, 0x2B, 0x20, 0x21, o.x(ram) as i32)?;
            sprites::display_sprite(ram, o);
        }
    }
    Ok(())
}

// ------------------------------------------------------------------ Geysers ($4C/$4D) and lava wall ($4E)

pub fn geyser_maker(ram: &mut Ram, o: Obj) {
    if o.routine(ram) == 0 {
        o.set_routine(ram, 2);
        o.set_mappings(ram, 0x155746);
        o.set_art_tile(ram, 0xE3A8);
        o.set_render_flags(ram, LEVEL_COORDS);
        o.set_priority(ram, 1);
        o.set_width_pixels(ram, 0x38);
        o.set_word(ram, 0x34, 0x78);
        o.set_word(ram, 0x32, 0x78);
    }
    if o.routine(ram) == 2 {
        let timer = o.word(ram, 0x32).wrapping_sub(1);
        o.set_word(ram, 0x32, timer);
        if (timer as i16) < 0 {
            o.set_word(ram, 0x32, o.word(ram, 0x34));
            let sonic = Obj::new(vars::MAIN_CHARACTER);
            if sonic.y(ram) >= o.y(ram) && sonic.y(ram) < o.y(ram).wrapping_sub(0x170) {
                if let Some(g) = find_free_slot(ram) {
                    g.set_id(ram, 0x4D);
                    g.set_x(ram, o.x(ram));
                    g.set_y(ram, o.y(ram));
                    g.set_subtype(ram, o.subtype(ram));
                    g.set_long(ram, 0x3C, 0xFFFF_0000 | o.address() as u32);
                }
            }
        }
    }
    if out_of_range(ram, o.x(ram)) {
        o.clear(ram);
    }
}

pub fn geyser(ram: &mut Ram, o: Obj) {
    if o.routine(ram) == 0 {
        o.set_routine(ram, 2);
        o.set_render_flags(ram, LEVEL_COORDS);
        o.set_priority(ram, 1);
        o.set_width_pixels(ram, 0x38);
        o.set_y_radius(ram, 0x40);
        o.set_collision_flags(ram, 0x96);
        o.set_word(ram, 0x30, 0x40);
    }
    let timer = o.word(ram, 0x30).wrapping_sub(1);
    o.set_word(ram, 0x30, timer);
    if timer == 0 {
        let maker = Obj::new(o.long(ram, 0x3C) as u16);
        if maker.id(ram) != 0 {
            maker.set_routine(ram, 0x0A);
        }
        o.clear(ram);
        return;
    }
    sprites::display_sprite(ram, o);
}

pub fn lava_wall(ram: &mut Ram, sets: &CollisionSets, o: Obj) -> Result<(), Unported> {
    if o.routine(ram) == 0 {
        o.set_routine(ram, 4);
        o.set_mappings(ram, 0x155D46);
        o.set_art_tile(ram, 0x63A8);
        o.set_render_flags(ram, LEVEL_COORDS);
        o.set_width_pixels(ram, 0x50);
        o.set_priority(ram, 1);
        o.set_collision_flags(ram, 0x94);
    }
    if o.routine(ram) == 4 {
        let sonic = Obj::new(vars::MAIN_CHARACTER);
        let dx = (sonic.x(ram) as i16)
            .wrapping_sub(o.x(ram) as i16)
            .unsigned_abs();
        let dy = (sonic.y(ram) as i16)
            .wrapping_sub(o.y(ram) as i16)
            .unsigned_abs();
        if dx < 0xC0 && dy < 0x60 {
            o.set_byte(ram, 0x36, 1);
        } else if o.byte(ram, 0x36) != 0 {
            o.set_routine(ram, 2);
            o.set_x_vel(ram, 0x180);
        }
    } else if o.routine(ram) == 2 {
        crate::objects::solid::solid_object(ram, sets, o, 0x2B, 0x18, 0x19, o.x(ram) as i32)?;
        move_object(ram, o);
        if o.x(ram) == 0x6A0 {
            o.set_x_vel(ram, 0);
            o.set_byte(ram, 0x36, 0);
            o.set_routine(ram, 4);
        }
    }
    display_or_mark_gone(ram, o);
    Ok(())
}

fn object_floor_at(ram: &mut Ram, sets: &CollisionSets, o: Obj, x: i32) -> i32 {
    let y = o.y(ram) as i32 + o.y_radius(ram) as i32;
    ram.w8(vars::PRIMARY_ANGLE, 0);
    find_floor(ram, sets, y, x, 0x10, 0x0C, 0, vars::PRIMARY_ANGLE) as i16 as i32
}

pub fn motobug(ram: &mut Ram, sets: &CollisionSets, o: Obj) -> Result<(), Unported> {
    match o.routine(ram) {
        0 => {
            o.set_mappings(ram, 0x151A58);
            o.set_art_tile(ram, 0x555);
            o.set_render_flags(ram, LEVEL_COORDS);
            o.set_priority(ram, 4);
            o.set_width_pixels(ram, 0x14);
            if o.anim(ram) != 0 {
                o.set_routine(ram, 4);
                sprites::display_sprite(ram, o);
                return Ok(());
            }
            o.set_y_radius(ram, 0x0E);
            o.set_x_radius(ram, 8);
            o.set_collision_flags(ram, 0x0C);
            move_and_fall(ram, o);
            let distance = object_floor(ram, sets, o);
            if distance >= 0 {
                return Ok(());
            }
            o.set_y(ram, (o.y(ram) as i32 + distance) as u16);
            o.set_y_vel(ram, 0);
            o.set_routine(ram, 2);
            o.set_status(ram, o.status(ram) ^ XFLIP);
        }
        2 => {
            if o.routine_secondary(ram) == 0 {
                motobug_wait(ram, o);
            } else {
                motobug_drive(ram, sets, o);
            }
            display_or_mark_gone(ram, o);
        }
        4 => {
            // Exhaust animation ends by deleting the object in the original.
            display_or_mark_gone(ram, o);
        }
        6 => delete_object(ram, o),
        _ => {}
    }
    Ok(())
}

fn motobug_wait(ram: &mut Ram, o: Obj) {
    let timer = o.word(ram, 0x30).wrapping_sub(1);
    o.set_word(ram, 0x30, timer);
    if (timer as i16) >= 0 {
        return;
    }
    o.set_routine_secondary(ram, 2);
    o.set_anim(ram, 1);
    let was_flipped = o.status(ram) & XFLIP != 0;
    o.set_status(ram, o.status(ram) ^ XFLIP);
    o.set_x_vel(ram, if was_flipped { -0x100 } else { 0x100 });
}

fn motobug_drive(ram: &mut Ram, sets: &CollisionSets, o: Obj) {
    move_object(ram, o);
    let distance = object_floor(ram, sets, o);
    if !(-8..0x0C).contains(&distance) {
        o.set_routine_secondary(ram, 0);
        o.set_word(ram, 0x30, 0x3B);
        o.set_x_vel(ram, 0);
        o.set_anim(ram, 0);
        return;
    }
    o.set_y(ram, (o.y(ram) as i32 + distance) as u16);
    let timer = o.byte(ram, 0x33).wrapping_sub(1);
    o.set_byte(ram, 0x33, timer);
    if (timer as i8) >= 0 {
        return;
    }
    o.set_byte(ram, 0x33, 0x0F);
    if let Some(puff) = find_free_slot(ram) {
        puff.set_id(ram, 0x40);
        puff.set_x(ram, o.x(ram));
        puff.set_y(ram, o.y(ram));
        puff.set_status(ram, o.status(ram));
        puff.set_anim(ram, 2);
    }
}

// ------------------------------------------------------------------ Chopper ($2B)

pub fn chopper(ram: &mut Ram, o: Obj) {
    if o.routine(ram) == 0 {
        o.set_routine(ram, 2);
        o.set_mappings(ram, 0x152314);
        o.set_art_tile(ram, 0x43D);
        o.set_render_flags(ram, LEVEL_COORDS);
        o.set_priority(ram, 4);
        o.set_collision_flags(ram, 9);
        o.set_width_pixels(ram, 0x10);
        o.set_y_vel(ram, -0x700);
        o.set_word(ram, 0x30, o.y(ram));
    }
    let start_y = o.word(ram, 0x30);
    move_object(ram, o);
    o.set_y_vel(ram, o.y_vel(ram).wrapping_add(0x18));
    if start_y < o.y(ram) {
        o.set_y(ram, start_y);
        o.set_y_vel(ram, -0x700);
    }
    display_or_mark_gone(ram, o);
}

// ------------------------------------------------------------------ Edge wall ($44)

pub fn edge_wall(ram: &mut Ram, sets: &CollisionSets, o: Obj) {
    if o.routine(ram) == 0 {
        o.set_routine(ram, 2);
        o.set_mappings(ram, 0x15187A);
        o.set_art_tile(ram, 0x434C);
        o.set_render_flags(ram, o.render_flags(ram) | LEVEL_COORDS);
        o.set_width_pixels(ram, 8);
        o.set_priority(ram, 6);
        o.set_mapping_frame(ram, o.subtype(ram) & !0x10);
        if o.subtype(ram) & 0x10 != 0 {
            o.set_routine(ram, 4);
        }
    }
    if o.routine(ram) == 2 {
        let _ =
            crate::objects::solid::solid_object(ram, sets, o, 0x13, 0x28, 0x29, o.x(ram) as i32);
    }
    sprites::display_sprite(ram, o);
    if out_of_range(ram, o.x(ram)) {
        delete_object(ram, o);
    }
}

// ------------------------------------------------------------------ Rock ($3B), Wall ($3C), Scenery ($1C)

pub fn rock(ram: &mut Ram, sets: &CollisionSets, o: Obj) -> Result<(), Unported> {
    if o.routine(ram) == 0 {
        o.set_routine(ram, 2);
        o.set_art_tile(ram, 0x63AA);
        o.set_mappings(ram, 0x19604);
        o.set_render_flags(ram, LEVEL_COORDS);
        o.set_width_pixels(ram, 0x13);
        o.set_priority(ram, 4);
    }
    crate::objects::solid::solid_object(ram, sets, o, 0x1B, 0x10, 0x10, o.x(ram) as i32)?;
    if out_of_range(ram, o.x(ram)) {
        delete_object(ram, o);
    } else {
        sprites::display_sprite(ram, o);
    }
    Ok(())
}

pub fn wall(ram: &mut Ram, sets: &CollisionSets, o: Obj) -> Result<(), Unported> {
    if o.routine(ram) == 0 {
        o.set_routine(ram, 2);
        o.set_mappings(ram, 0x19886);
        o.set_art_tile(ram, 0x4340);
        o.set_render_flags(ram, LEVEL_COORDS);
        o.set_width_pixels(ram, 0x10);
        o.set_priority(ram, 4);
        o.set_mapping_frame(ram, o.subtype(ram));
    }
    if o.routine(ram) == 2 {
        crate::objects::solid::solid_object(ram, sets, o, 0x1B, 0x20, 0x20, o.x(ram) as i32)?;
    } else if o.routine(ram) == 4 {
        move_object(ram, o);
        o.set_y_vel(ram, o.y_vel(ram).wrapping_add(0x70));
    }
    display_or_mark_gone(ram, o);
    Ok(())
}

pub fn scenery(ram: &mut Ram, o: Obj) -> Result<(), Unported> {
    if o.routine(ram) == 0 {
        o.set_routine(ram, 2);
        let subtype = o.subtype(ram);
        if subtype != 3 {
            return Err(Unported::new(0x14166, format!("scenery subtype {subtype}")));
        }
        o.set_mappings(ram, 0x12710);
        o.set_art_tile(ram, 0x438E);
        o.set_render_flags(ram, o.render_flags(ram) | LEVEL_COORDS);
        o.set_mapping_frame(ram, 1);
        o.set_width_pixels(ram, 0x10);
        o.set_priority(ram, 1);
        o.set_collision_flags(ram, 0);
    }
    if out_of_range(ram, o.x(ram)) {
        delete_object(ram, o);
    } else {
        sprites::display_sprite(ram, o);
    }
    Ok(())
}

// ------------------------------------------------------------------ Spikes ($36, Sonic 1)

pub fn spikes(ram: &mut Ram, sets: &CollisionSets, o: Obj) -> Result<(), Unported> {
    if o.routine(ram) == 0 {
        o.set_routine(ram, 2);
        o.set_mappings(ram, 0x194D4);
        o.set_art_tile(
            ram,
            if ram.u8(vars::CURRENT_ZONE_AND_ACT) >= 0x17 {
                0x550
            } else {
                0x578
            },
        );
        o.set_render_flags(ram, o.render_flags(ram) | LEVEL_COORDS);
        o.set_priority(ram, 4);
        let subtype = o.subtype(ram);
        o.set_subtype(ram, subtype & 0x0F);
        const SIZES: [(u8, u8); 6] = [
            (0, 0x14),
            (1, 0x10),
            (2, 0x04),
            (3, 0x1C),
            (4, 0x40),
            (5, 0x10),
        ];
        let (frame, width) = SIZES[(subtype >> 4) as usize];
        o.set_mapping_frame(ram, frame);
        o.set_width_pixels(ram, width);
        o.set_word(ram, 0x30, o.x(ram));
        o.set_word(ram, 0x32, o.y(ram));
    }
    spikes_move(ram, o)?;
    for (n, bit) in [(0u8, 3u8), (1, 4)] {
        if n == 1 && !Obj::new(vars::SIDEKICK).on_screen(ram) {
            continue;
        }
        spikes_check(
            ram,
            sets,
            o,
            Obj::new(if n == 0 {
                vars::MAIN_CHARACTER
            } else {
                vars::SIDEKICK
            }),
            bit,
        )?;
    }
    sprites::display_sprite(ram, o);
    if out_of_range(ram, o.word(ram, 0x30)) {
        delete_object(ram, o);
    }
    Ok(())
}

fn spikes_check(
    ram: &mut Ram,
    sets: &CollisionSets,
    o: Obj,
    player: Obj,
    bit: u8,
) -> Result<(), Unported> {
    let frame = o.mapping_frame(ram);
    if frame == 1 || frame == 5 {
        let half_height = if frame == 5 { 4 } else { 0x14 };
        let mut d6 = bit as i32;
        let side = crate::objects::solid::solid_one(
            ram,
            sets,
            o,
            player,
            0x1B,
            half_height,
            half_height + 1,
            o.x(ram) as i32,
            &mut d6,
        )?;
        if ram.bit(o.address().wrapping_add(0x22), bit) {
            return Ok(());
        }
        if side == 1 {
            spikes_hurt(ram, o, player);
        }
        return Ok(());
    }
    let mut d6 = bit as i32;
    let contact = crate::objects::solid::solid_one(
        ram,
        sets,
        o,
        player,
        o.width_pixels(ram) as i32 + 0x0B,
        0x10,
        0x11,
        o.x(ram) as i32,
        &mut d6,
    )?;
    if ram.bit(o.address().wrapping_add(0x22), bit) || contact < 0 {
        spikes_hurt(ram, o, player);
    }
    Ok(())
}

fn spikes_hurt(ram: &mut Ram, o: Obj, player: Obj) {
    if player.routine(ram) >= 4 {
        return;
    }
    undo_y_move(ram, player);
    if player.invincible(ram) || player.invulnerable_time(ram) != 0 || player.routine(ram) >= 4 {
        return;
    }
    undo_y_move(ram, player);
    let _ = touch::hurt(ram, player, o);
}

fn undo_y_move(ram: &mut Ram, player: Obj) {
    let y = (player.y_long(ram) as i32).wrapping_sub((player.y_vel(ram) as i32) << 8);
    player.set_y_long(ram, y as u32);
}

fn spikes_move(ram: &mut Ram, o: Obj) -> Result<(), Unported> {
    let kind = o.subtype(ram);
    if kind == 0 {
        return Ok(());
    }
    if kind > 2 {
        return Err(Unported::new(
            0x19432,
            format!("spikes movement type {kind}"),
        ));
    }
    spikes_retract(ram, o);
    if kind == 1 {
        o.set_y(
            ram,
            o.word(ram, 0x32).wrapping_add(o.byte(ram, 0x34) as u16),
        );
    } else {
        o.set_x(
            ram,
            o.word(ram, 0x30).wrapping_add(o.byte(ram, 0x34) as u16),
        );
    }
    Ok(())
}

fn spikes_retract(ram: &mut Ram, o: Obj) {
    let wait = o.word(ram, 0x38);
    if wait != 0 {
        o.set_word(ram, 0x38, wait - 1);
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

// ------------------------------------------------------------------ Force roll ($06)

pub fn force_roll(ram: &mut Ram, o: Obj) {
    if o.routine(ram) == 0 {
        o.set_routine(ram, 2);
        o.set_mappings(ram, 0x23FAC);
        o.set_art_tile(ram, 0x6BC);
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
        for (n, past) in [(0, 0x34), (1, 0x35)] {
            let p = Obj::new(if n == 0 {
                vars::MAIN_CHARACTER
            } else {
                vars::SIDEKICK
            });
            let mine = if horizontal { o.y(ram) } else { o.x(ram) };
            let theirs = if horizontal { p.y(ram) } else { p.x(ram) };
            if mine < theirs {
                o.set_byte(ram, past, 1);
            }
        }
        force_roll_check(ram, o, horizontal);
    } else {
        force_roll_check(ram, o, o.routine(ram) == 4);
    }
    crate::objects::mark_obj_gone(ram, o);
}

fn force_roll_check(ram: &mut Ram, o: Obj, horizontal: bool) {
    if ram.u16(vars::DEBUG_PLACEMENT_MODE) != 0 {
        return;
    }
    force_roll_player(ram, o, Obj::new(vars::MAIN_CHARACTER), 0x34, horizontal);
    if !horizontal && ram.u16(vars::TAILS_CPU_ROUTINE) == 4 {
        return;
    }
    force_roll_player(ram, o, Obj::new(vars::SIDEKICK), 0x35, horizontal);
}

fn force_roll_player(ram: &mut Ram, o: Obj, player: Obj, past_flag: u16, horizontal: bool) {
    let line = if horizontal { o.y(ram) } else { o.x(ram) };
    let pos = if horizontal {
        player.y(ram)
    } else {
        player.x(ram)
    };
    let across = if horizontal {
        player.x(ram)
    } else {
        player.y(ram)
    };
    let centre = if horizontal { o.x(ram) } else { o.y(ram) };
    let flipped = o.render_flags(ram) & XFLIP != 0;
    let enable = if o.byte(ram, past_flag) == 0 {
        if line > pos {
            return;
        }
        o.set_byte(ram, past_flag, 1);
        !flipped
    } else {
        if line <= pos {
            return;
        }
        o.set_byte(ram, past_flag, 0);
        flipped
    };
    let range = o.word(ram, 0x32);
    if across < centre.wrapping_sub(range) || across >= centre.wrapping_add(range) {
        return;
    }
    if !enable {
        player.set_spindash_flag(ram, 0);
        return;
    }
    player.set_spindash_flag(ram, 1);
    if !player.rolling(ram) {
        player.set_rolling(ram, true);
        player.set_y_radius(ram, 0x0E);
        player.set_x_radius(ram, 7);
        player.set_anim(ram, player::anim::ROLL);
        player.set_y(ram, player.y(ram).wrapping_add(5));
    }
}

// ------------------------------------------------------------------ Waterfall sound ($49)

pub fn waterfall_sound(ram: &mut Ram, o: Obj) {
    if o.routine(ram) == 0 {
        o.set_routine(ram, 2);
        o.set_render_flags(ram, LEVEL_COORDS);
    }
    if out_of_range(ram, o.x(ram)) {
        delete_object(ram, o);
    }
}

// ------------------------------------------------------------------ Ledge ($1A)

pub fn ledge(ram: &mut Ram, o: Obj, slope: &[i8], delays: &[u8]) -> Result<(), Unported> {
    if o.routine(ram) == 0 {
        o.set_routine(ram, 2);
        o.set_mappings(ram, 0x139FE);
        o.set_art_tile(ram, 0x4000);
        o.set_render_flags(ram, o.render_flags(ram) | LEVEL_COORDS);
        o.set_priority(ram, 4);
        o.set_byte(ram, 0x38, 7);
        o.set_mapping_frame(ram, o.subtype(ram));
        o.set_width_pixels(ram, 0x30);
        o.set_y_radius(ram, 0x38);
        o.set_render_flags(ram, o.render_flags(ram) | 0x10);
    }
    let mut timer = o.byte(ram, 0x38);
    let touched = o.byte(ram, 0x3A) != 0;
    if o.routine(ram) == 2 {
        if touched {
            if timer == 0 {
                // Break into fragments: for MOTION the original only drops riders here; keep the main object as a
                // falling fragment with its first delay.
                o.set_byte(ram, 0x3A, 1);
                o.set_routine(ram, 4);
                o.set_byte(ram, 0x38, delays.first().copied().unwrap_or(0));
                return Ok(());
            }
            timer -= 1;
            o.set_byte(ram, 0x38, timer);
        }
        if o.status(ram) & 0x18 != 0 {
            o.set_byte(ram, 0x3A, 1);
        }
        crate::objects::solid::sloped_platform(
            ram,
            o,
            o.width_pixels(ram) as i32,
            o.x(ram) as i32,
            slope,
        );
        display_or_mark_gone(ram, o);
    } else if o.routine(ram) == 4 {
        if timer == 0 {
            move_and_fall(ram, o);
            if !o.on_screen(ram) {
                delete_object(ram, o);
            } else {
                sprites::display_sprite(ram, o);
            }
        } else if !touched {
            timer -= 1;
            o.set_byte(ram, 0x38, timer);
            sprites::display_sprite(ram, o);
        } else {
            crate::objects::solid::sloped_platform(
                ram,
                o,
                o.width_pixels(ram) as i32,
                o.x(ram) as i32,
                slope,
            );
            timer -= 1;
            o.set_byte(ram, 0x38, timer);
            if timer == 0 {
                for p in [Obj::new(vars::MAIN_CHARACTER), Obj::new(vars::SIDEKICK)] {
                    if p.on_object(ram) {
                        p.set_on_object(ram, false);
                        p.set_pushing(ram, false);
                        p.set_prev_anim(ram, 1);
                    }
                }
            }
        }
    }
    Ok(())
}

// ------------------------------------------------------------------ Simple Marble Zone statics

pub fn lava_marker(ram: &mut Ram, o: Obj) {
    if o.routine(ram) == 0 {
        o.set_routine(ram, 2);
        let size = o.subtype(ram);
        let collision = [0x96u8, 0x94, 0x95];
        o.set_collision_flags(ram, *collision.get(size as usize).unwrap_or(&0));
        o.set_mappings(
            ram,
            if ram.u16(vars::DEBUG_PLACEMENT_MODE) != 0 {
                0x24E82
            } else {
                0x24E7A
            },
        );
        o.set_art_tile(ram, 0x8680);
        o.set_render_flags(ram, LEVEL_COORDS | 0x80);
        o.set_width_pixels(ram, 0x80);
        o.set_priority(ram, 4);
        o.set_mapping_frame(ram, size);
    }
    if ram.u16(vars::TWO_PLAYER_MODE) == 0 && out_of_range(ram, o.x(ram)) {
        o.clear(ram);
        return;
    }
    if ram.u16(vars::DEBUG_PLACEMENT_MODE) != 0 {
        sprites::display_sprite(ram, o);
    }
}

pub fn lava_ball_maker(ram: &mut Ram, o: Obj) {
    if o.routine(ram) == 0 {
        o.set_routine(ram, 2);
        const RATES: [u8; 6] = [0x1E, 0x3C, 0x5A, 0x78, 0x96, 0xB4];
        let rate = (o.subtype(ram) >> 4) as usize;
        o.set_byte(ram, 0x1F, *RATES.get(rate).unwrap_or(&0));
        o.set_anim_frame_duration(ram, o.byte(ram, 0x1F));
        o.set_subtype(ram, o.subtype(ram) & 0x0F);
    }
    let d = o.anim_frame_duration(ram).wrapping_sub(1);
    o.set_anim_frame_duration(ram, d);
    if d == 0 {
        o.set_anim_frame_duration(ram, o.byte(ram, 0x1F));
        let dx = (o.x(ram) as i32 - ram.u16(vars::CAMERA_X_POS) as i32) as i16 as i32;
        let dy = (o.y(ram) as i32 - ram.u16(vars::CAMERA_Y_POS) as i32) as i16 as i32;
        if (0..0x140).contains(&dx) && (0..0xE0).contains(&dy) {
            if let Some(ball) = find_free_slot(ram) {
                ball.set_id(ram, 0x14);
                ball.set_x(ram, o.x(ram));
                ball.set_y(ram, o.y(ram));
                ball.set_subtype(ram, o.subtype(ram));
            }
        }
    }
    if out_of_range(ram, o.x(ram)) {
        o.clear(ram);
    }
}

pub fn invisible_block(ram: &mut Ram, sets: &CollisionSets, o: Obj) -> Result<(), Unported> {
    if o.routine(ram) == 0 {
        o.set_routine(ram, 2);
        o.set_width_pixels(ram, 0x20);
        o.set_priority(ram, 4);
        o.set_render_flags(ram, LEVEL_COORDS);
    }
    crate::objects::solid::solid_object(ram, sets, o, 0x20, 0x20, 0x21, o.x(ram) as i32)?;
    Ok(())
}

pub fn glass_block(ram: &mut Ram, sets: &CollisionSets, o: Obj) -> Result<(), Unported> {
    if o.routine(ram) == 0 {
        o.set_routine(ram, 2);
        o.set_render_flags(ram, LEVEL_COORDS);
        o.set_width_pixels(ram, 0x20);
        o.set_y_radius(ram, 0x20);
        o.set_priority(ram, 4);
    }
    crate::objects::solid::solid_object(ram, sets, o, 0x2B, 0x20, 0x21, o.x(ram) as i32)?;
    // The original breaks most glass blocks on a hard hit; this is only needed once a trace reaches one.
    sprites::display_sprite(ram, o);
    Ok(())
}

pub fn marble_brick(ram: &mut Ram, sets: &CollisionSets, o: Obj) -> Result<(), Unported> {
    if o.routine(ram) == 0 {
        o.set_routine(ram, 2);
        o.set_render_flags(ram, LEVEL_COORDS);
        o.set_width_pixels(ram, 0x20);
        o.set_y_radius(ram, 0x20);
        o.set_priority(ram, 4);
    }
    crate::objects::solid::solid_object(ram, sets, o, 0x2B, 0x20, 0x21, o.x(ram) as i32)?;
    sprites::display_sprite(ram, o);
    Ok(())
}

pub fn breakable_block(ram: &mut Ram, sets: &CollisionSets, o: Obj) -> Result<(), Unported> {
    if o.routine(ram) == 0 {
        o.set_routine(ram, 2);
        o.set_render_flags(ram, LEVEL_COORDS);
        o.set_width_pixels(ram, 0x20);
        o.set_y_radius(ram, 0x20);
        o.set_priority(ram, 4);
    }
    crate::objects::solid::solid_object(ram, sets, o, 0x2B, 0x20, 0x21, o.x(ram) as i32)?;
    sprites::display_sprite(ram, o);
    Ok(())
}

// ------------------------------------------------------------------ Button ($32)

pub fn button(ram: &mut Ram, sets: &CollisionSets, o: Obj) -> Result<(), Unported> {
    if o.routine(ram) == 0 {
        o.set_routine(ram, 2);
        o.set_render_flags(ram, LEVEL_COORDS);
        o.set_width_pixels(ram, 0x10);
        o.set_priority(ram, 4);
        o.set_word(ram, 0x30, o.y(ram));
    }
    crate::objects::solid::solid_object(ram, sets, o, 0x1B, 8, 8, o.x(ram) as i32)?;
    o.set_y(ram, o.word(ram, 0x30));
    if o.status(ram) & 0x18 != 0 {
        o.set_y(ram, o.y(ram).wrapping_add(8));
        o.set_byte(ram, 0x34, 1);
    }
    display_or_mark_gone(ram, o);
    Ok(())
}

// ------------------------------------------------------------------ Chained stomper ($31)

pub fn chained_stomper(ram: &mut Ram, sets: &CollisionSets, o: Obj) -> Result<(), Unported> {
    if o.routine(ram) == 0 {
        o.set_routine(ram, 2);
        o.set_mappings(ram, 0x154182);
        o.set_art_tile(ram, 0x300);
        o.set_render_flags(ram, LEVEL_COORDS);
        o.set_priority(ram, 4);
        o.set_width_pixels(ram, 0x38);
        let subtype = o.subtype(ram);
        let kind = subtype & 0x0F;
        let limit = [0x7000u16, 0xA000, 0x5000, 0x7800, 0x3800, 0x5800, 0xB800]
            .get(kind as usize)
            .copied()
            .unwrap_or(0);
        o.set_word(ram, 0x34, limit);
        o.set_word(ram, 0x30, o.y(ram));
        if kind == 0 {
            o.set_word(ram, 0x32, limit);
        }
    }
    chained_move(ram, o);
    crate::objects::solid::solid_object(
        ram,
        sets,
        o,
        o.width_pixels(ram) as i32 + 0x0B,
        0x0C,
        0x0D,
        o.x(ram) as i32,
    )?;
    if o.status(ram) & 0x08 != 0 && o.byte(ram, 0x32) < 0x10 {
        let sonic = Obj::new(vars::MAIN_CHARACTER);
        let _ = touch::kill(ram, sonic, o);
    }
    if o.status(ram) & 0x10 != 0 && o.byte(ram, 0x32) < 0x0C {
        let tails = Obj::new(vars::SIDEKICK);
        let _ = touch::kill(ram, tails, o);
    }
    display_or_mark_gone(ram, o);
    Ok(())
}

fn chained_move(ram: &mut Ram, o: Obj) {
    let kind = o.subtype(ram) & 0x0F;
    match kind {
        0 => chained_switch(ram, o),
        1 | 2 | 4 | 6 => chained_drop_climb(ram, o),
        3 | 5 => {
            let sonic = Obj::new(vars::MAIN_CHARACTER);
            let dx = (sonic.x(ram) as i16)
                .wrapping_sub(o.x(ram) as i16)
                .unsigned_abs();
            if dx < 0x90 {
                o.set_subtype(ram, o.subtype(ram).wrapping_add(1));
            }
        }
        _ => {}
    }
    let y = o.word(ram, 0x30).wrapping_add(o.byte(ram, 0x32) as u16);
    o.set_y(ram, y);
}

fn chained_switch(ram: &mut Ram, o: Obj) {
    let switch = o.byte(ram, 0x3A);
    if ram.u8(vars::BUTTON_VINE_TRIGGER + switch as u16) == 0 {
        chained_drop(ram, o);
        return;
    }
    let extension = o.word(ram, 0x32);
    if extension != 0 {
        let up = extension.wrapping_sub(0x80);
        o.set_word(ram, 0x32, up);
    }
}

fn chained_drop_climb(ram: &mut Ram, o: Obj) {
    if o.word(ram, 0x36) != 0 {
        let wait = o.word(ram, 0x38);
        if wait != 0 {
            o.set_word(ram, 0x38, wait - 1);
            return;
        }
        let extension = o.word(ram, 0x32);
        let up = extension.wrapping_sub(0x80);
        if up < 0x8000 {
            o.set_word(ram, 0x32, up);
            return;
        }
        o.set_word(ram, 0x32, 0);
        o.set_y_vel(ram, 0);
        o.set_word(ram, 0x36, 0);
        return;
    }
    if chained_drop(ram, o) {
        o.set_word(ram, 0x36, 1);
        o.set_word(ram, 0x38, 0x3C);
    }
}

fn chained_drop(ram: &mut Ram, o: Obj) -> bool {
    let limit = o.word(ram, 0x34);
    let extension = o.word(ram, 0x32);
    if extension == limit {
        return false;
    }
    let speed = o.y_vel(ram);
    o.set_y_vel(ram, speed.wrapping_add(0x70));
    let next = extension.wrapping_add(speed as u16);
    o.set_word(ram, 0x32, next);
    if limit > next {
        return false;
    }
    o.set_word(ram, 0x32, limit);
    o.set_y_vel(ram, 0);
    true
}

// ------------------------------------------------------------------ Grass fire ($19)

pub fn grass_fire(ram: &mut Ram, o: Obj, heights: &[u8]) -> Result<(), Unported> {
    if o.routine(ram) == 0 {
        o.set_routine(ram, 2);
        o.set_mappings(ram, 0x1536E8);
        o.set_art_tile(ram, 0x345);
        o.set_word(ram, 0x2A, o.x(ram));
        o.set_render_flags(ram, LEVEL_COORDS);
        o.set_priority(ram, 1);
        o.set_collision_flags(ram, 0x8B);
        o.set_width_pixels(ram, 8);
        if o.subtype(ram) != 0 {
            o.set_routine(ram, 4);
        }
    }
    if o.routine(ram) == 2 {
        let start_x = o.word(ram, 0x2A);
        let along = o.x(ram).wrapping_sub(start_x).wrapping_add(0x0C);
        let profile = o.long(ram, 0x30) as usize;
        let height = heights
            .get(profile.wrapping_add((along >> 1) as usize))
            .copied()
            .unwrap_or(0) as i32;
        let base_y = o.word(ram, 0x2C) as i32;
        let sink = o.word(ram, 0x3C) as i32;
        let top_y = (base_y - height) & 0xFFFF;
        o.set_y(ram, (top_y + sink) as u16);
        if along < 0x84 {
            o.set_x_long(ram, o.x_long(ram).wrapping_add(0x10000));
            if along < 0x80 && (o.x_long(ram).wrapping_add(0x80000) & 0xFFFFF) == 0 {
                if let Some(piece) = find_free_slot(ram) {
                    piece.set_id(ram, 0x19);
                    piece.set_x(ram, o.x(ram));
                    piece.set_subtype(ram, 1);
                    piece.set_word(ram, 0x2C, top_y as u16);
                    piece.set_word(ram, 0x3C, sink as u16);
                    add_platform_fire(ram, o.long(ram, 0x38) as u16, piece);
                }
            }
        }
    } else if o.routine(ram) == 4 {
        let base_y = o.word(ram, 0x2C) as i32;
        let sink = o.word(ram, 0x3C) as i32;
        o.set_y(ram, (base_y + sink) as u16);
    }
    sprites::display_sprite(ram, o);
    Ok(())
}

fn add_platform_fire(ram: &mut Ram, platform: u16, fire: Obj) {
    if platform == 0 {
        return;
    }
    let platform = Obj::new(platform);
    let count = platform.byte(ram, 0x36);
    if count >= 6 {
        return;
    }
    platform.set_byte(ram, 0x36, count + 1);
    platform.set_byte(ram, 0x37 + count as u16, fire.slot_index());
}
