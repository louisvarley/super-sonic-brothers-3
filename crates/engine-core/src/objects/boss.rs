//! Sonic-zone bosses and their parts: Emerald Hill's drill car, Green Hill's wrecking-ball ship, Marble Zone's
//! lava ship, their explosions and Marble Zone's lava balls/fire.

use crate::collision::find_floor;
use crate::collision::CollisionSets;
use crate::objects::{
    delete_object, display_or_mark_gone, find_free_slot, find_free_slot_after, move_and_fall,
    move_object, out_of_range, sprites, touch,
};
use crate::player::Unported;
use crate::{vars, Obj, Ram};

const LEVEL_COORDS: u8 = 0x04;
const XFLIP: u8 = 0x01;
const YFLIP: u8 = 0x02;

pub fn random_number(ram: &mut Ram) -> u32 {
    let mut d1 = ram
        .u32(vars::RNG_SEED)
        .wrapping_add(ram.u32(vars::VINT_RUNCOUNT));
    if d1 == 0 {
        d1 = 0x2A6D_365A;
    }
    let d0 = d1;
    d1 = (d1 << 2).wrapping_add(d0);
    d1 = (d1 << 3).wrapping_add(d0);
    let mut d0 = (d0 & 0xFFFF_0000) | (d1 & 0xFFFF);
    d1 = d1.rotate_left(16);
    d0 = (d0 & 0xFFFF_0000) | d0.wrapping_add(d1) & 0xFFFF;
    d1 = (d1 & 0xFFFF_0000) | (d0 & 0xFFFF);
    d1 = d1.rotate_left(16);
    ram.w32(vars::RNG_SEED, d1);
    d0
}

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

fn object_ceiling_floor(ram: &mut Ram, sets: &CollisionSets, o: Obj) -> i32 {
    let y = (o.y(ram).wrapping_sub(o.y_radius(ram) as u16) ^ 0xF) as i32;
    ram.w8(vars::PRIMARY_ANGLE, 0);
    find_floor(
        ram,
        sets,
        y,
        o.x(ram) as i32,
        -0x10,
        0x0D,
        0x800,
        vars::PRIMARY_ANGLE,
    ) as i16 as i32
}

fn add_points(ram: &mut Ram, points: i32) {
    touch::add_points(ram, points);
}

// ================================================================== Emerald Hill boss ($56)

const EHZ_SHIP_MAPPINGS: u32 = 0x33D24;
const EHZ_CAR_MAPPINGS: u32 = 0x33C84;
const EHZ_PROPELLER_MAPPINGS: u32 = 0x33B9C;
const EHZ_ART_SHIP: u16 = 0x03A0;
const EHZ_ART_CAR: u16 = 0x0400;
const EHZ_ART_PROPELLER: u16 = 0x056C;
const EHZ_ARENA_LEFT: i32 = 0x28A0;
const EHZ_ARENA_RIGHT: i32 = 0x2B08;
const EHZ_CAR_START_X: u16 = 0x2AF0;
const EHZ_LANDING_X: u16 = 0x29D0;
const EHZ_LANDING_Y: u16 = 0x41E;
const EHZ_WHEEL_STOP_X: [u16; 3] = [0x29EC, 0x29C4, 0x29A4];
const EHZ_HITS: u8 = 8;
const EHZ_FLASH_FRAMES: i32 = 0x20;
const EHZ_DRIVE_SPEED: i16 = -0x200;

#[derive(Clone, Copy)]
struct EhzState {
    timer: i16,
    step: u8,
    flags: u8,
    wheel_sum: u16,
    link: u16,
    defeat_timer: i16,
    flash_timer: u8,
}

impl EhzState {
    fn new(ram: &Ram, o: Obj) -> Self {
        Self {
            timer: o.word(ram, 0x2A) as i16,
            step: o.byte(ram, 0x2C),
            flags: o.byte(ram, 0x2D),
            wheel_sum: o.word(ram, 0x2E),
            link: o.long(ram, 0x34) as u16,
            defeat_timer: o.word(ram, 0x3C) as i16,
            flash_timer: o.byte(ram, 0x3E),
        }
    }
    fn save(self, ram: &mut Ram, o: Obj) {
        o.set_word(ram, 0x2A, self.timer as u16);
        o.set_byte(ram, 0x2C, self.step);
        o.set_byte(ram, 0x2D, self.flags);
        o.set_word(ram, 0x2E, self.wheel_sum);
        o.set_long(ram, 0x34, 0xFFFF_0000 | self.link as u32);
        o.set_word(ram, 0x3C, self.defeat_timer as u16);
        o.set_byte(ram, 0x3E, self.flash_timer);
    }
    fn has(self, flag: u8) -> bool {
        self.flags & flag != 0
    }
    fn set(&mut self, flag: u8) {
        self.flags |= flag;
    }
    fn clear(&mut self, flag: u8) {
        self.flags &= !flag;
    }
}

pub fn ehz_boss(ram: &mut Ram, sets: &CollisionSets, o: Obj) -> Result<(), Unported> {
    match o.routine(ram) {
        0 => {
            create_ehz_boss(ram, o);
        }
        2 => update_ehz_main(ram, sets, o)?,
        4 => update_ehz_propeller(ram, o),
        6 => update_ehz_ground_car(ram, o),
        8 => update_ehz_wheel(ram, sets, o),
        0x0A => update_ehz_drill(ram, o),
        0x0C => update_ehz_propeller_reloaded(ram, o),
        0x0E => update_ehz_cockpit(ram, o),
        _ => {}
    }
    Ok(())
}

fn create_ehz_boss(ram: &mut Ram, o: Obj) {
    o.set_mappings(ram, EHZ_SHIP_MAPPINGS);
    o.set_art_tile(ram, 0x2000 | EHZ_ART_SHIP);
    o.set_render_flags(ram, o.render_flags(ram) | LEVEL_COORDS);
    o.set_subtype(ram, 0x81);
    o.set_x(ram, EHZ_LANDING_X);
    o.set_y(ram, 0x426);
    o.set_width_pixels(ram, 0x20);
    o.set_y_radius(ram, 0x14);
    o.set_priority(ram, 4);
    o.set_collision_flags(ram, 0x0F);
    o.set_collision_property(ram, EHZ_HITS);
    o.set_routine(ram, 2);
    o.set_word(ram, 0x30, o.x(ram));
    o.set_word(ram, 0x38, o.y(ram));
    let mut s = EhzState::new(ram, o);

    if let Some(top) = find_free_slot_after(ram, o) {
        let mut ts = EhzState::new(ram, top);
        ts.link = o.address();
        s.link = top.address();
        ts.save(ram, top);
        top.set_mappings(ram, EHZ_SHIP_MAPPINGS);
        top.set_art_tile(ram, EHZ_ART_SHIP);
        top.set_render_flags(ram, LEVEL_COORDS);
        top.set_width_pixels(ram, 0x20);
        top.set_priority(ram, 4);
        top.set_x_long(ram, o.x_long(ram));
        top.set_y_long(ram, o.y_long(ram));
        top.set_routine(ram, 0x0E);
        top.set_anim(ram, 1);
        top.set_render_flags(ram, o.render_flags(ram));
    }

    if let Some(car) = spawn_ehz_part(
        ram,
        o,
        EHZ_CAR_MAPPINGS,
        EHZ_ART_CAR,
        0x30,
        3,
        Some(EHZ_CAR_START_X),
    ) {
        car.set_y_radius(ram, 0x10);
        car.set_routine(ram, 6);
    }
    create_ehz_wheels(ram, o);
    o.set_word(ram, 0x38, o.word(ram, 0x38).wrapping_sub(8));
    o.set_x(ram, EHZ_CAR_START_X);
    o.set_y(ram, 0x2F8);
    if let Some(prop) = spawn_ehz_part(
        ram,
        o,
        EHZ_PROPELLER_MAPPINGS,
        0x2000 | EHZ_ART_PROPELLER,
        0x40,
        3,
        None,
    ) {
        let mut ps = EhzState::new(ram, prop);
        ps.timer = 0x1E;
        ps.save(ram, prop);
        prop.set_routine(ram, 4);
    }
    s.save(ram, o);
}

fn spawn_ehz_part(
    ram: &mut Ram,
    o: Obj,
    mappings: u32,
    art: u16,
    width: u8,
    priority: u8,
    x: Option<u16>,
) -> Option<Obj> {
    let part = find_free_slot_after(ram, o)?;
    part.set_id(ram, 0x56);
    let mut s = EhzState::new(ram, part);
    s.link = o.address();
    s.save(ram, part);
    part.set_mappings(ram, mappings);
    part.set_art_tile(ram, art);
    part.set_render_flags(ram, LEVEL_COORDS);
    part.set_width_pixels(ram, width);
    part.set_priority(ram, priority);
    match x {
        Some(x) => part.set_x(ram, x),
        None => part.set_x_long(ram, o.x_long(ram)),
    }
    part.set_y_long(ram, o.y_long(ram));
    Some(part)
}

fn create_ehz_wheels(ram: &mut Ram, o: Obj) {
    let wheels = [(0x1Ci32, 2u8, 4u8, 1u8), (-0x0C, 2, 4, 1), (-0x2C, 3, 6, 2)];
    for (i, (dx, priority, frame, anim)) in wheels.iter().enumerate() {
        let Some(wheel) = spawn_ehz_part(
            ram,
            o,
            EHZ_CAR_MAPPINGS,
            0x2000 | EHZ_ART_CAR,
            0x10,
            *priority,
            Some(EHZ_CAR_START_X),
        ) else {
            continue;
        };
        wheel.set_y_radius(ram, 0x10);
        wheel.set_x_radius(ram, 0x10);
        wheel.set_x(ram, wheel.x(ram).wrapping_add(*dx as u16));
        wheel.set_y(ram, wheel.y(ram).wrapping_add(0x0C));
        wheel.set_routine(ram, 8);
        wheel.set_mapping_frame(ram, *frame);
        wheel.set_anim(ram, *anim);
        let mut s = EhzState::new(ram, wheel);
        s.timer = 0x0A;
        s.save(ram, wheel);
        wheel.set_subtype(ram, i as u8);
    }
    if let Some(drill) = spawn_ehz_part(
        ram,
        o,
        EHZ_CAR_MAPPINGS,
        0x2000 | EHZ_ART_CAR,
        0x20,
        2,
        Some(EHZ_CAR_START_X),
    ) {
        drill.set_x(ram, drill.x(ram).wrapping_sub(0x36));
        drill.set_y(ram, drill.y(ram).wrapping_add(8));
        drill.set_routine(ram, 0x0A);
        drill.set_mapping_frame(ram, 1);
        drill.set_anim(ram, 0);
    }
}

fn update_ehz_main(ram: &mut Ram, sets: &CollisionSets, o: Obj) -> Result<(), Unported> {
    let mut s = EhzState::new(ram, o);
    match o.routine_secondary(ram) {
        0 => {
            o.set_collision_flags(ram, 0);
            if o.x(ram) > EHZ_LANDING_X {
                o.set_x(ram, o.x(ram).wrapping_sub(1));
                o.set_y(ram, o.y(ram).wrapping_add(1));
            } else {
                o.set_x(ram, EHZ_LANDING_X);
                o.set_routine_secondary(ram, 2);
            }
        }
        2 => {
            if s.step == 0 {
                if o.y(ram) < EHZ_LANDING_Y {
                    o.set_y(ram, o.y(ram).wrapping_add(1));
                } else {
                    s.step = 2;
                    s.set(1);
                    s.timer = 60;
                }
            } else {
                s.timer -= 1;
                if s.timer < 0 {
                    o.set_x_vel(ram, EHZ_DRIVE_SPEED);
                    o.set_routine_secondary(ram, 4);
                    o.set_collision_flags(ram, 0x0F);
                    s.set(2);
                }
            }
        }
        4 => {
            ehz_handle_hits(ram, o, &mut s);
            ehz_turn_edges(ram, o);
            o.set_y(ram, (s.wheel_sum >> 1).wrapping_sub(0x14));
            s.wheel_sum = 0;
            o.set_x_long(
                ram,
                o.x_long(ram)
                    .wrapping_add(((o.x_vel(ram) as i32) << 8) as u32),
            );
        }
        6 => {
            s.defeat_timer -= 1;
            if s.defeat_timer < 0 {
                o.set_x_vel(ram, 0);
                o.set_routine_secondary(ram, 8);
                s.defeat_timer = -0x26;
                s.timer = 0x0C;
            } else {
                ehz_explosions(ram, o);
                move_and_fall(ram, o);
                let floor = object_floor(ram, sets, o);
                if floor < 0 {
                    o.set_y(ram, (o.y(ram) as i32 + floor) as u16);
                    o.set_y_vel(ram, 0);
                }
            }
        }
        8 => {
            s.timer -= 1;
            if s.timer < 0 {
                o.set_routine_secondary(ram, 0x0A);
                s.step = 0;
            }
        }
        0x0A if !ehz_fly_away(ram, o, &mut s) => return Ok(()),
        _ => {}
    }
    s.save(ram, o);
    sprites::display_sprite(ram, o);
    Ok(())
}

fn ehz_fly_away(ram: &mut Ram, o: Obj, s: &mut EhzState) -> bool {
    match s.step {
        0 => {
            s.clear(1);
            if let Some(prop) = find_free_slot_after(ram, o) {
                let mut ps = EhzState::new(ram, prop);
                ps.link = o.address();
                ps.save(ram, prop);
                prop.set_mappings(ram, EHZ_PROPELLER_MAPPINGS);
                prop.set_art_tile(ram, 0x2000 | EHZ_ART_PROPELLER);
                prop.set_render_flags(ram, LEVEL_COORDS);
                prop.set_width_pixels(ram, 0x20);
                prop.set_priority(ram, 3);
                prop.set_x_long(ram, o.x_long(ram));
                prop.set_y_long(ram, o.y_long(ram));
                prop.set_y(ram, prop.y(ram).wrapping_add(0x0C));
                prop.set_status(ram, o.status(ram));
                prop.set_render_flags(ram, o.render_flags(ram));
                prop.set_routine(ram, 0x0C);
                prop.set_anim(ram, 2);
                let mut ps = EhzState::new(ram, prop);
                ps.timer = 0x10;
                ps.save(ram, prop);
            }
            s.timer = 0x32;
            s.step = 2;
            ram.w8(vars::BOSS_DEFEATED_FLAG, 1);
        }
        2 => {
            s.timer -= 1;
            if s.timer < 0 {
                s.set(4);
                s.timer = 0x60;
                s.step = 4;
            }
        }
        4 => {
            s.timer -= 1;
            if s.timer >= 0 {
                o.set_y(ram, o.y(ram).wrapping_sub(1));
            } else {
                o.set_status(ram, o.status(ram) | XFLIP);
                o.set_render_flags(ram, o.render_flags(ram) | XFLIP);
                o.set_x(ram, o.x(ram).wrapping_add(6));
            }
            if ram.u16(vars::CAMERA_MAX_X_POS) < 0x2AB0 {
                let x = ram.u16(vars::CAMERA_MAX_X_POS).wrapping_add(2);
                ram.w16(vars::CAMERA_MAX_X_POS, x);
            } else if !o.on_screen(ram) {
                if s.link != 0 {
                    delete_object(ram, Obj::new(s.link));
                }
                delete_object(ram, o);
                return false;
            }
        }
        _ => {}
    }
    true
}

fn ehz_handle_hits(ram: &mut Ram, o: Obj, s: &mut EhzState) {
    if o.routine_secondary(ram) >= 6 {
        return;
    }
    if o.status(ram) & 0x80 != 0 {
        ehz_defeat(ram, o, s);
        return;
    }
    if o.collision_flags(ram) != 0 {
        return;
    }
    if s.flash_timer == 0 {
        s.flash_timer = EHZ_FLASH_FRAMES as u8;
    }
    let colour = vars::NORMAL_PALETTE + 0x22;
    let v = ram.u16(colour);
    ram.w16(colour, if v == 0 { 0x0EEE } else { 0 });
    s.flash_timer = s.flash_timer.wrapping_sub(1);
    if s.flash_timer == 0 {
        o.set_collision_flags(ram, 0x0F);
    }
}

fn ehz_defeat(ram: &mut Ram, o: Obj, s: &mut EhzState) {
    add_points(ram, 100);
    o.set_routine_secondary(ram, 6);
    o.set_x_vel(ram, 0);
    o.set_y_vel(ram, -0x180);
    s.defeat_timer = 0xB3;
    s.set(8);
    let cockpit = Obj::new(s.link);
    cockpit.set_anim(ram, 4);
    cockpit.set_mapping_frame(ram, 6);
}

fn ehz_turn_edges(ram: &mut Ram, o: Obj) {
    let x = o.x(ram) as i16 as i32;
    if x > EHZ_ARENA_LEFT && x < EHZ_ARENA_RIGHT {
        return;
    }
    o.set_status(ram, o.status(ram) ^ XFLIP);
    o.set_render_flags(ram, o.render_flags(ram) ^ XFLIP);
    o.set_x_vel(ram, o.x_vel(ram).wrapping_neg());
}

fn ehz_explosions(ram: &mut Ram, o: Obj) {
    if ram.u8(vars::VINT_RUNCOUNT_LOW) & 7 != 0 {
        return;
    }
    if let Some(explosion) = find_free_slot(ram) {
        explosion.set_id(ram, 0x58);
        explosion.set_x(ram, o.x(ram));
        explosion.set_y(ram, o.y(ram));
        let r = random_number(ram);
        explosion.set_x(
            ram,
            explosion
                .x(ram)
                .wrapping_add(((r & 0xFF) >> 2) as u16)
                .wrapping_sub(0x20),
        );
        explosion.set_y(
            ram,
            explosion
                .y(ram)
                .wrapping_add((((r >> 8) & 0xFF) >> 2) as u16)
                .wrapping_sub(0x20),
        );
    }
}

fn update_ehz_propeller(ram: &mut Ram, o: Obj) {
    let mut s = EhzState::new(ram, o);
    if o.routine_secondary(ram) == 0 {
        let car = Obj::new(s.link);
        if car.id(ram) != 0x56 {
            delete_object(ram, o);
            return;
        }
        if EhzState::new(ram, car).has(1) {
            o.set_anim(ram, 1);
            s.timer = 0x18;
            o.set_routine_secondary(ram, 2);
        }
        o.set_x(ram, car.x(ram));
        o.set_y(ram, car.y(ram));
        o.set_status(ram, car.status(ram));
        o.set_render_flags(ram, car.render_flags(ram));
    } else {
        s.timer -= 1;
        if s.timer < 0 {
            if s.timer <= -0x10 {
                delete_object(ram, o);
                return;
            }
            o.set_priority(ram, 4);
            o.set_y(ram, o.y(ram).wrapping_add(1));
            sprites::display_sprite(ram, o);
            s.save(ram, o);
            return;
        }
    }
    s.save(ram, o);
    sprites::display_sprite(ram, o);
}

fn update_ehz_propeller_reloaded(ram: &mut Ram, o: Obj) {
    let mut s = EhzState::new(ram, o);
    o.set_y(ram, o.y(ram).wrapping_sub(1));
    s.timer -= 1;
    if s.timer < 0 {
        o.set_routine(ram, 4);
    }
    s.save(ram, o);
    sprites::display_sprite(ram, o);
}

fn update_ehz_ground_car(ram: &mut Ram, o: Obj) {
    if o.routine_secondary(ram) == 0 {
        if ram.u16(vars::CAMERA_MIN_X_POS) >= 0x28F0 {
            if o.x(ram) > EHZ_LANDING_X {
                o.set_x(ram, o.x(ram).wrapping_sub(1));
            } else {
                o.set_x(ram, EHZ_LANDING_X);
                o.set_routine_secondary(ram, 2);
            }
        }
        sprites::display_sprite(ram, o);
        return;
    }
    let car = Obj::new(EhzState::new(ram, o).link);
    let flags = EhzState::new(ram, car);
    if flags.has(2) && !flags.has(4) {
        o.set_x(ram, car.x(ram));
        o.set_y(ram, car.y(ram).wrapping_add(8));
        o.set_status(ram, car.status(ram));
        if o.status(ram) & 0x80 == 0 {
            o.set_render_flags(ram, car.render_flags(ram));
        }
    }
    sprites::display_sprite(ram, o);
}

fn update_ehz_wheel(ram: &mut Ram, sets: &CollisionSets, o: Obj) {
    let mut s = EhzState::new(ram, o);
    match o.routine_secondary(ram) {
        0 => {
            if ram.u16(vars::CAMERA_MIN_X_POS) < 0x28F0 {
                return;
            }
            o.set_y_vel(ram, 0x100);
            let stop = EHZ_WHEEL_STOP_X[(o.subtype(ram) as usize).min(2)];
            if o.x(ram) > stop {
                o.set_x(ram, o.x(ram).wrapping_sub(1));
            } else {
                o.set_x(ram, stop);
                o.set_routine_secondary(ram, 2);
            }
            ehz_fall_to_floor(ram, sets, o);
            if o.routine_secondary(ram) != 0 {
                o.set_x_vel(ram, EHZ_DRIVE_SPEED);
            }
        }
        2 => {
            let car = Obj::new(s.link);
            if car.id(ram) != 0x56 {
                delete_object(ram, o);
                return;
            }
            if !EhzState::new(ram, car).has(2) {
                sprites::display_sprite(ram, o);
                return;
            }
            o.set_routine_secondary(ram, 4);
            if o.priority(ram) == 2 {
                ehz_add_wheel_sum(ram, o, car);
            }
        }
        4 => {
            let car = Obj::new(s.link);
            if car.id(ram) != 0x56 {
                delete_object(ram, o);
                return;
            }
            o.set_status(ram, car.status(ram));
            o.set_render_flags(ram, car.render_flags(ram));
            if o.status(ram) & 0x80 != 0 {
                o.set_routine_secondary(ram, 6);
            }
            ehz_turn_edges(ram, o);
            ehz_fall_to_floor(ram, sets, o);
            o.set_y_vel(ram, 0x100);
            if o.priority(ram) == 2 {
                ehz_add_wheel_sum(ram, o, car);
            }
        }
        6 => {
            s.timer -= 1;
            if s.timer >= 0 {
                return;
            }
            o.set_routine_secondary(ram, 8);
            s.timer = 0x0A;
            o.set_y_vel(ram, -0x300);
            if o.priority(ram) != 2 {
                o.set_x_vel(ram, o.x_vel(ram).wrapping_neg());
            }
        }
        8 => {
            s.timer -= 1;
            if s.timer >= 0 {
                return;
            }
            move_and_fall(ram, o);
            let floor = object_floor(ram, sets, o);
            if floor < 0 {
                o.set_y_vel(ram, -0x200);
                o.set_y(ram, (o.y(ram) as i32 + floor) as u16);
            }
            display_or_mark_gone(ram, o);
            s.save(ram, o);
            return;
        }
        _ => {}
    }
    s.save(ram, o);
    sprites::display_sprite(ram, o);
}

fn ehz_fall_to_floor(ram: &mut Ram, sets: &CollisionSets, o: Obj) {
    move_and_fall(ram, o);
    let floor = object_floor(ram, sets, o);
    if floor < 0 {
        o.set_y(ram, (o.y(ram) as i32 + floor) as u16);
    }
}

fn ehz_add_wheel_sum(ram: &mut Ram, wheel: Obj, car: Obj) {
    let mut s = EhzState::new(ram, car);
    s.wheel_sum = s.wheel_sum.wrapping_add(wheel.y(ram));
    s.save(ram, car);
}

fn update_ehz_drill(ram: &mut Ram, o: Obj) {
    if o.routine_secondary(ram) == 0 {
        if ram.u16(vars::CAMERA_MIN_X_POS) >= 0x28F0 {
            if o.x(ram) > 0x299A {
                o.set_x(ram, o.x(ram).wrapping_sub(1));
            } else {
                o.set_x(ram, 0x299A);
                o.set_routine_secondary(ram, 2);
            }
        }
        sprites::display_sprite(ram, o);
        return;
    }
    let car = Obj::new(EhzState::new(ram, o).link);
    if car.id(ram) != 0x56 {
        delete_object(ram, o);
        return;
    }
    let mut flags = EhzState::new(ram, car);
    if flags.has(8) {
        let d = if o.status(ram) & XFLIP != 0 { 3 } else { -3 };
        o.set_x(ram, o.x(ram).wrapping_add(d as u16));
        sprites::display_sprite(ram, o);
        return;
    }
    if car.collision_property(ram) == 1 {
        let sonic = Obj::new(vars::MAIN_CHARACTER);
        let sonic_to_right = (o.x(ram) as i16 as i32 - sonic.x(ram) as i16 as i32) < 0;
        let facing_right = car.status(ram) & XFLIP != 0;
        if sonic_to_right == facing_right {
            flags.set(8);
            flags.save(ram, car);
        }
    }
    if !flags.has(2) {
        sprites::display_sprite(ram, o);
        return;
    }
    o.set_collision_flags(ram, 0x8B);
    o.set_x(ram, car.x(ram));
    o.set_y(ram, car.y(ram).wrapping_add(0x10));
    o.set_status(ram, car.status(ram));
    o.set_render_flags(ram, car.render_flags(ram));
    let d = if o.status(ram) & XFLIP != 0 {
        0x36
    } else {
        -0x36
    };
    o.set_x(ram, o.x(ram).wrapping_add(d as u16));
    sprites::display_sprite(ram, o);
}

fn update_ehz_cockpit(ram: &mut Ram, o: Obj) {
    let car = Obj::new(EhzState::new(ram, o).link);
    o.set_x_long(ram, car.x_long(ram));
    o.set_y_long(ram, car.y_long(ram));
    o.set_status(ram, car.status(ram));
    o.set_render_flags(ram, car.render_flags(ram));
    if EhzState::new(ram, car).flash_timer == (EHZ_FLASH_FRAMES - 1) as u8 {
        o.set_anim(ram, 2);
    }
    let sonic = Obj::new(vars::MAIN_CHARACTER);
    let tails = Obj::new(vars::SIDEKICK);
    if (sonic.routine(ram) == 4 || tails.routine(ram) == 4) && o.anim(ram) != 2 {
        o.set_anim(ram, 3);
    }
    sprites::display_sprite(ram, o);
}

// ================================================================== Green Hill boss ($3D) and wrecking ball ($48)

const GHZ_MAPPINGS: u32 = 0x152A10;
const GHZ_ARENA_X: u16 = 0x2A00;
const GHZ_HOVER_Y: u16 = 0x338;
const GHZ_FINAL_MAX_X: u16 = 0x2AC0;
const GHZ_HITS: u8 = 8;
const GHZ_FLASH_FRAMES: i32 = 0x20;

#[derive(Clone, Copy)]
struct BossState {
    base_x: u16,
    base_y: u16,
    parent: u16,
    timer: i16,
    flash_timer: u8,
    hover_angle: u8,
}

impl BossState {
    fn new(ram: &Ram, o: Obj) -> Self {
        Self {
            base_x: o.word(ram, 0x30),
            base_y: o.word(ram, 0x38),
            parent: o.long(ram, 0x34) as u16,
            timer: o.word(ram, 0x3C) as i16,
            flash_timer: o.byte(ram, 0x3E),
            hover_angle: o.byte(ram, 0x3F),
        }
    }
    fn save(self, ram: &mut Ram, o: Obj) {
        o.set_word(ram, 0x30, self.base_x);
        o.set_word(ram, 0x38, self.base_y);
        o.set_long(ram, 0x34, 0xFFFF_0000 | self.parent as u32);
        o.set_word(ram, 0x3C, self.timer as u16);
        o.set_byte(ram, 0x3E, self.flash_timer);
        o.set_byte(ram, 0x3F, self.hover_angle);
    }
    fn move_by_velocity(mut self, ram: &mut Ram, o: Obj) -> Self {
        o.set_long(
            ram,
            0x30,
            (o.long(ram, 0x30) as i32).wrapping_add((o.x_vel(ram) as i32) << 8) as u32,
        );
        o.set_long(
            ram,
            0x38,
            (o.long(ram, 0x38) as i32).wrapping_add((o.y_vel(ram) as i32) << 8) as u32,
        );
        self.base_x = o.word(ram, 0x30);
        self.base_y = o.word(ram, 0x38);
        self
    }
}

pub fn ghz_boss(ram: &mut Ram, sets: &CollisionSets, o: Obj) -> Result<(), Unported> {
    match o.routine(ram) {
        0 => {
            create_ghz_parts(ram, o);
            update_ghz_ship(ram, sets, o)?;
        }
        2 => update_ghz_ship(ram, sets, o)?,
        4 => update_ghz_face(ram, o),
        6 => update_ghz_flame(ram, o),
        _ => {}
    }
    Ok(())
}

fn create_ghz_parts(ram: &mut Ram, o: Obj) {
    let parts = [(2u8, 0u8), (4, 1), (6, 7)];
    let mut part = o;
    for (i, (routine, anim)) in parts.iter().enumerate() {
        if i > 0 {
            let Some(slot) = find_free_slot_after(ram, o) else {
                break;
            };
            part = slot;
        }
        part.set_routine(ram, *routine);
        part.set_id(ram, 0x3D);
        part.set_x(ram, o.x(ram));
        part.set_y(ram, o.y(ram));
        part.set_mappings(ram, GHZ_MAPPINGS);
        part.set_art_tile(ram, 0x3B0);
        part.set_render_flags(ram, LEVEL_COORDS);
        part.set_width_pixels(ram, 0x20);
        part.set_priority(ram, 3);
        part.set_anim(ram, *anim);
        let mut s = BossState::new(ram, part);
        s.parent = o.address();
        s.save(ram, part);
    }
    let mut s = BossState::new(ram, o);
    s.base_x = o.x(ram);
    s.base_y = o.y(ram);
    s.save(ram, o);
    o.set_collision_flags(ram, 0x0F);
    o.set_collision_property(ram, GHZ_HITS);
}

fn update_ghz_ship(ram: &mut Ram, _sets: &CollisionSets, o: Obj) -> Result<(), Unported> {
    let mut s = BossState::new(ram, o);
    match o.routine_secondary(ram) {
        0 => {
            o.set_y_vel(ram, 0x100);
            s = s.move_by_velocity(ram, o);
            if s.base_y == GHZ_HOVER_Y {
                o.set_y_vel(ram, 0);
                o.set_routine_secondary(ram, 2);
            }
            ghz_hover(ram, o, &mut s);
        }
        2 => {
            o.set_x_vel(ram, -0x100);
            o.set_y_vel(ram, -0x40);
            s = s.move_by_velocity(ram, o);
            if s.base_x == GHZ_ARENA_X {
                o.set_x_vel(ram, 0);
                o.set_y_vel(ram, 0);
                o.set_routine_secondary(ram, 4);
                if let Some(ball) = find_free_slot_after(ram, o) {
                    ball.set_id(ram, 0x48);
                    ball.set_x(ram, s.base_x);
                    ball.set_y(ram, s.base_y);
                    let mut bs = BossState::new(ram, ball);
                    bs.parent = o.address();
                    bs.save(ram, ball);
                }
                s.timer = 0x77;
            }
            ghz_hover(ram, o, &mut s);
        }
        4 => {
            s.timer -= 1;
            if s.timer < 0 {
                o.set_routine_secondary(ram, 6);
                s.timer = if s.base_x == GHZ_ARENA_X { 0x7F } else { 0x3F };
                let speed = if s.base_x == GHZ_ARENA_X { 0x40 } else { 0x100 };
                o.set_x_vel(
                    ram,
                    if o.status(ram) & XFLIP != 0 {
                        speed
                    } else {
                        -speed
                    },
                );
            }
            ghz_hover(ram, o, &mut s);
        }
        6 => {
            s.timer -= 1;
            if s.timer < 0 {
                o.set_status(ram, o.status(ram) ^ XFLIP);
                s.timer = 0x3F;
                o.set_routine_secondary(ram, 4);
                o.set_x_vel(ram, 0);
            } else {
                s = s.move_by_velocity(ram, o);
            }
            ghz_hover(ram, o, &mut s);
        }
        8 => {
            s.timer -= 1;
            if s.timer >= 0 {
                ghz_explosions(ram, o);
            } else {
                o.set_status(ram, (o.status(ram) | XFLIP) & !0x80);
                o.set_x_vel(ram, 0);
                o.set_routine_secondary(ram, 0x0A);
                s.timer = -0x26;
                if ram.u8(vars::BOSS_DEFEATED_FLAG) == 0 {
                    ram.w8(vars::BOSS_DEFEATED_FLAG, 1);
                }
            }
        }
        0x0A => {
            s.timer += 1;
            let t = s.timer;
            if t == 0 {
                o.set_y_vel(ram, 0);
            } else if t < 0 {
                o.set_y_vel(ram, o.y_vel(ram).wrapping_add(0x18));
            } else if t < 0x30 {
                o.set_y_vel(ram, o.y_vel(ram).wrapping_sub(8));
            } else if t == 0x30 {
                o.set_y_vel(ram, 0);
            } else if t >= 0x38 {
                o.set_routine_secondary(ram, 0x0C);
            }
            s = s.move_by_velocity(ram, o);
            ghz_hover(ram, o, &mut s);
        }
        0x0C => {
            o.set_x_vel(ram, 0x400);
            o.set_y_vel(ram, -0x40);
            if ram.u16(vars::CAMERA_MAX_X_POS) == GHZ_FINAL_MAX_X {
                if !o.on_screen(ram) {
                    delete_object(ram, o);
                    return Ok(());
                }
            } else {
                let x = ram.u16(vars::CAMERA_MAX_X_POS).wrapping_add(2);
                ram.w16(vars::CAMERA_MAX_X_POS, x);
                let x = ram.u16(vars::TAILS_MAX_X_POS).wrapping_add(2);
                ram.w16(vars::TAILS_MAX_X_POS, x);
            }
            s = s.move_by_velocity(ram, o);
            ghz_hover(ram, o, &mut s);
        }
        _ => {}
    }
    s.save(ram, o);
    o.set_render_flags(ram, (o.render_flags(ram) & !3) | (o.status(ram) & 3));
    sprites::display_sprite(ram, o);
    Ok(())
}

fn ghz_hover(ram: &mut Ram, o: Obj, s: &mut BossState) {
    let (sin, _) = crate::math::calc_sine(s.hover_angle);
    o.set_y(ram, (s.base_y as i32 + (sin as i32 >> 6)) as u16);
    o.set_x(ram, s.base_x);
    s.hover_angle = s.hover_angle.wrapping_add(2);
    if o.routine_secondary(ram) >= 8 {
        return;
    }
    if o.status(ram) & 0x80 != 0 {
        add_points(ram, 100);
        o.set_routine_secondary(ram, 8);
        s.timer = 0xB3;
        return;
    }
    if o.collision_flags(ram) != 0 {
        return;
    }
    if s.flash_timer == 0 {
        s.flash_timer = GHZ_FLASH_FRAMES as u8;
    }
    let colour = vars::NORMAL_PALETTE + 0x22;
    let v = ram.u16(colour);
    ram.w16(colour, if v == 0 { 0x0EEE } else { 0 });
    s.flash_timer = s.flash_timer.wrapping_sub(1);
    if s.flash_timer == 0 {
        o.set_collision_flags(ram, 0x0F);
    }
}

fn update_ghz_face(ram: &mut Ram, o: Obj) {
    let ship = Obj::new(BossState::new(ram, o).parent);
    let phase = ship.routine_secondary(ram);
    let mut anim;
    if phase >= 0x0A {
        anim = 0x0A;
    } else {
        anim = if phase == 4 && BossState::new(ram, ship).base_x == GHZ_ARENA_X {
            4
        } else {
            1
        };
        if ship.collision_flags(ram) == 0 {
            anim = 5;
        } else if Obj::new(vars::MAIN_CHARACTER).routine(ram) == 4
            || Obj::new(vars::SIDEKICK).routine(ram) == 4
        {
            anim = 4;
        }
    }
    o.set_anim(ram, anim);
    if phase == 0x0C {
        o.set_anim(ram, 6);
        if !o.on_screen(ram) {
            delete_object(ram, o);
            return;
        }
    }
    ghz_follow(ram, o, ship);
}

fn update_ghz_flame(ram: &mut Ram, o: Obj) {
    let ship = Obj::new(BossState::new(ram, o).parent);
    let mut anim = 7;
    if ship.routine_secondary(ram) == 0x0C {
        anim = 0x0B;
        if !o.on_screen(ram) {
            delete_object(ram, o);
            return;
        }
    } else if ship.x_vel(ram) != 0 {
        anim = 8;
    }
    o.set_anim(ram, anim);
    ghz_follow(ram, o, ship);
}

fn ghz_follow(ram: &mut Ram, o: Obj, ship: Obj) {
    o.set_x(ram, ship.x(ram));
    o.set_y(ram, ship.y(ram));
    o.set_status(ram, ship.status(ram));
    o.set_render_flags(ram, (o.render_flags(ram) & !3) | (o.status(ram) & 3));
    sprites::display_sprite(ram, o);
}

fn ghz_explosions(ram: &mut Ram, o: Obj) {
    if ram.u8(vars::VINT_RUNCOUNT_LOW) & 7 != 0 {
        return;
    }
    if let Some(explosion) = find_free_slot(ram) {
        explosion.set_id(ram, 0x58);
        let r = random_number(ram);
        explosion.set_x(
            ram,
            o.x(ram)
                .wrapping_add(((r & 0xFF) >> 2) as u16)
                .wrapping_sub(0x20),
        );
        explosion.set_y(ram, o.y(ram).wrapping_add((((r >> 8) & 0xFF) >> 3) as u16));
    }
}

pub fn wrecking_ball(ram: &mut Ram, o: Obj) -> Result<(), Unported> {
    match o.routine(ram) {
        0 => create_wrecking_ball(ram, o),
        2 => extend_wrecking_ball(ram, o),
        4 => {
            follow_wrecking_ship(ram, o);
            wrecking_swing_step(ram, o);
            wrecking_place_parts(ram, o);
            sprites::display_sprite(ram, o);
        }
        6 => {
            if wrecking_ship_defeated(ram, o) {
                o.set_id(ram, 0x3F);
                o.set_routine(ram, 0);
            }
            sprites::display_sprite(ram, o);
        }
        8 => {
            update_wrecking_ball(ram, o);
        }
        _ => {}
    }
    Ok(())
}

fn create_wrecking_ball(ram: &mut Ram, o: Obj) {
    o.set_routine(ram, 2);
    o.set_word(ram, 0x26, 0x4080);
    o.set_word(ram, 0x3E, 0xFE00);
    o.set_mappings(ram, 0x152D98);
    o.set_art_tile(ram, 0x46C);
    let next_slot = o.address().wrapping_add(0x28);
    ram.w8(next_slot, 0);
    let mut next_slot = next_slot.wrapping_add(1);
    let mut part = o;
    for i in 0..6 {
        if i > 0 {
            let Some(slot) = find_free_slot_after(ram, o) else {
                break;
            };
            part = slot;
            part.set_x(ram, o.x(ram));
            part.set_y(ram, o.y(ram));
            part.set_id(ram, o.id(ram));
            part.set_routine(ram, 6);
            part.set_mappings(ram, 0x152FB0);
            part.set_art_tile(ram, 0x4380);
            part.set_mapping_frame(ram, 1);
            o.set_subtype(ram, o.subtype(ram).wrapping_add(1));
        }
        ram.w8(next_slot, part.slot_index());
        next_slot = next_slot.wrapping_add(1);
        part.set_render_flags(ram, LEVEL_COORDS);
        part.set_width_pixels(ram, 8);
        part.set_priority(ram, 6);
        part.set_long(ram, 0x34, o.long(ram, 0x34));
    }
    part.set_routine(ram, 8);
    part.set_mappings(ram, 0x1530B2);
    part.set_art_tile(ram, 0x4300);
    part.set_mapping_frame(ram, 1);
    part.set_priority(ram, 5);
    part.set_collision_flags(ram, 0x81);
}

fn wrecking_part(ram: &Ram, o: Obj, i: u8) -> Obj {
    Obj::from_slot(o.byte(ram, 0x29u16 + i as u16))
}

fn extend_wrecking_ball(ram: &mut Ram, o: Obj) {
    let count = o.subtype(ram) + 1;
    const RADII: [u8; 6] = [0, 0x10, 0x20, 0x30, 0x40, 0x60];
    let mut last = o;
    for i in 0..count {
        let part = wrecking_part(ram, o, i);
        last = part;
        let radius = RADII[i as usize];
        if part.byte(ram, 0x3C) != radius {
            part.set_byte(ram, 0x3C, part.byte(ram, 0x3C).wrapping_add(1));
        }
    }
    if last.byte(ram, 0x3C) == RADII[count as usize - 1]
        && Obj::new(BossState::new(ram, o).parent).routine_secondary(ram) == 6
    {
        o.set_routine(ram, 4);
    }
    let drop = o.word(ram, 0x32);
    if drop != 0x20 {
        o.set_word(ram, 0x32, drop + 1);
    }
    follow_wrecking_ship(ram, o);
    wrecking_place_parts(ram, o);
    sprites::display_sprite(ram, o);
}

fn follow_wrecking_ship(ram: &mut Ram, o: Obj) {
    let flicker = o.anim_frame(ram) as i32 + 0x20;
    o.set_anim_frame(ram, (flicker & 0xFF) as u8);
    if flicker > 0xFF {
        o.set_mapping_frame(ram, o.mapping_frame(ram) ^ 1);
    }
    let ship = Obj::new(BossState::new(ram, o).parent);
    o.set_word(ram, 0x3A, ship.x(ram));
    o.set_word(ram, 0x38, ship.y(ram).wrapping_add(o.word(ram, 0x32)));
    o.set_status(ram, ship.status(ram));
    if ship.status(ram) & 0x80 != 0 {
        o.set_id(ram, 0x3F);
        o.set_routine(ram, 0);
    }
}

fn wrecking_swing_step(ram: &mut Ram, o: Obj) {
    let mut angle = o.word(ram, 0x26);
    let mut speed = o.word(ram, 0x3E);
    let back = o.byte(ram, 0x3D) != 0;
    if !back {
        speed = speed.wrapping_add(8);
        o.set_word(ram, 0x3E, speed);
        angle = angle.wrapping_add(speed);
        o.set_word(ram, 0x26, angle);
        if speed == 0x200 {
            o.set_byte(ram, 0x3D, 1);
        }
    } else {
        speed = speed.wrapping_sub(8);
        o.set_word(ram, 0x3E, speed);
        angle = angle.wrapping_add(speed);
        o.set_word(ram, 0x26, angle);
        if speed == 0xFE00 {
            o.set_byte(ram, 0x3D, 0);
        }
    }
}

fn wrecking_place_parts(ram: &mut Ram, o: Obj) {
    let angle = (o.word(ram, 0x26) >> 8) as u8;
    let (sin, cos) = crate::math::calc_sine(angle);
    let count = o.subtype(ram) + 1;
    let anchor_y = o.word(ram, 0x38);
    let anchor_x = o.word(ram, 0x3A);
    for i in 0..count {
        let part = wrecking_part(ram, o, i);
        let r = part.byte(ram, 0x3C) as i32;
        part.set_y(ram, (((r * sin as i32) >> 8) + anchor_y as i32) as u16);
        part.set_x(ram, (((r * cos as i32) >> 8) + anchor_x as i32) as u16);
    }
}

fn update_wrecking_ball(ram: &mut Ram, o: Obj) {
    o.set_mapping_frame(ram, if o.mapping_frame(ram) == 0 { 1 } else { 0 });
    if wrecking_ship_defeated(ram, o) {
        o.set_collision_flags(ram, 0);
        ghz_explosions(ram, o);
        let r = o.byte(ram, 0x3C).wrapping_sub(1);
        o.set_byte(ram, 0x3C, r);
        if (r as i8) < 0 {
            o.set_id(ram, 0x3F);
            o.set_routine(ram, 0);
        }
    }
    sprites::display_sprite(ram, o);
}

fn wrecking_ship_defeated(ram: &mut Ram, o: Obj) -> bool {
    let ship = Obj::new(BossState::new(ram, o).parent);
    ship.status(ram) & 0x80 != 0
}

// ================================================================== Marble Zone boss ($73), lava ball ($14), fire ($74)

const MZ_BOSS_ID: u8 = 0x73;
const MZ_RIGHT_END: i32 = 0x1910;
const MZ_LEFT_END: i32 = 0x1830;
const MZ_LAVA_Y: i32 = 0x22C;
const MZ_FINAL_MAX_X: u16 = 0x1960;
const MZ_DIPPING: u8 = 2;
const MZ_HITS: u8 = 8;
const MZ_FLASH_FRAMES: i32 = 0x28;

pub fn mz_boss(ram: &mut Ram, sets: &CollisionSets, o: Obj) -> Result<(), Unported> {
    match o.routine(ram) {
        0 => {
            create_mz_parts(ram, o);
            update_mz_ship(ram, sets, o)?;
        }
        2 => update_mz_ship(ram, sets, o)?,
        4 => update_mz_face(ram, o),
        6 => update_mz_flame(ram, o),
        8 => update_mz_anchor(ram, o),
        _ => {}
    }
    Ok(())
}

fn create_mz_parts(ram: &mut Ram, o: Obj) {
    let mut s = BossState::new(ram, o);
    s.base_x = o.x(ram);
    s.base_y = o.y(ram);
    s.save(ram, o);
    o.set_collision_flags(ram, 0x0F);
    o.set_collision_property(ram, MZ_HITS);
    let parts = [(2u8, 0u8, 4u8), (4, 1, 4), (6, 7, 4), (8, 0, 3)];
    let mut part = o;
    for (i, (routine, anim, priority)) in parts.iter().enumerate() {
        if i > 0 {
            let Some(slot) = find_free_slot_after(ram, o) else {
                break;
            };
            part = slot;
            part.set_id(ram, MZ_BOSS_ID);
            part.set_x(ram, o.x(ram));
            part.set_y(ram, o.y(ram));
        }
        o.set_status(ram, o.status(ram) & !XFLIP);
        part.set_routine_secondary(ram, 0);
        part.set_routine(ram, *routine);
        part.set_anim(ram, *anim);
        part.set_priority(ram, *priority);
        part.set_mappings(ram, GHZ_MAPPINGS);
        part.set_art_tile(ram, 0x3B0);
        part.set_render_flags(ram, LEVEL_COORDS);
        part.set_width_pixels(ram, 0x20);
        let mut ps = BossState::new(ram, part);
        ps.parent = o.address();
        ps.save(ram, part);
    }
}

fn update_mz_ship(ram: &mut Ram, sets: &CollisionSets, o: Obj) -> Result<(), Unported> {
    let mut s = BossState::new(ram, o);
    match o.routine_secondary(ram) {
        0 => {
            let angle = s.hover_angle;
            s.hover_angle = angle.wrapping_add(2);
            let (sin, _) = crate::math::calc_sine(angle);
            o.set_y_vel(ram, (sin as i32 >> 2) as i16);
            o.set_x_vel(ram, -0x100);
            s.save(ram, o);
            s = s.move_by_velocity(ram, o);
            if s.base_x == MZ_RIGHT_END as u16 {
                o.set_routine_secondary(ram, 2);
                o.set_subtype(ram, 0);
                o.set_x_vel(ram, 0);
                o.set_y_vel(ram, 0);
            }
            let lava_timer = (random_number(ram) & 0xFF) as u8;
            o.set_byte(ram, 0x34, lava_timer);
            mz_hover(ram, o, &mut s);
        }
        2 => {
            if o.subtype(ram) & MZ_DIPPING == 0 {
                mz_sweep(ram, o, &mut s);
            } else {
                mz_dip(ram, o, &mut s);
            }
            o.set_subtype(ram, o.subtype(ram) & 6);
            mz_hover(ram, o, &mut s);
        }
        4 => {
            s.timer -= 1;
            if s.timer >= 0 {
                ghz_explosions(ram, o);
            } else {
                o.set_status(ram, (o.status(ram) | XFLIP) & !0x80);
                o.set_x_vel(ram, 0);
                o.set_routine_secondary(ram, 6);
                s.timer = -0x26;
                if ram.u8(vars::BOSS_DEFEATED_FLAG) == 0 {
                    ram.w8(vars::BOSS_DEFEATED_FLAG, 1);
                    o.set_y_vel(ram, 0);
                }
            }
        }
        6 => {
            s.timer += 1;
            let t = s.timer;
            if t == 0 {
                o.set_y_vel(ram, 0);
                s.timer = 0;
            } else if t < 0 {
                if (s.base_y as i32) < 0x270 {
                    o.set_y_vel(ram, o.y_vel(ram).wrapping_add(0x18));
                } else {
                    o.set_y_vel(ram, 0);
                    s.timer = 0;
                }
            } else if t < 0x30 {
                o.set_y_vel(ram, o.y_vel(ram).wrapping_sub(8));
            } else if t == 0x30 {
                o.set_y_vel(ram, 0);
            } else if t >= 0x38 {
                o.set_routine_secondary(ram, 8);
            }
            s.save(ram, o);
            s = s.move_by_velocity(ram, o);
            mz_hover(ram, o, &mut s);
        }
        8 => {
            o.set_x_vel(ram, 0x500);
            o.set_y_vel(ram, -0x40);
            if ram.u16(vars::CAMERA_MAX_X_POS) < MZ_FINAL_MAX_X {
                let x = ram.u16(vars::CAMERA_MAX_X_POS).wrapping_add(2);
                ram.w16(vars::CAMERA_MAX_X_POS, x);
                let x = ram.u16(vars::TAILS_MAX_X_POS).wrapping_add(2);
                ram.w16(vars::TAILS_MAX_X_POS, x);
            } else if !o.on_screen(ram) {
                delete_object(ram, o);
                return Ok(());
            }
            s = s.move_by_velocity(ram, o);
            mz_hover(ram, o, &mut s);
        }
        _ => {}
    }
    s.save(ram, o);
    o.set_render_flags(ram, (o.render_flags(ram) & !3) | (o.status(ram) & 3));
    sprites::display_sprite(ram, o);
    let _ = sets;
    Ok(())
}

fn mz_hover(ram: &mut Ram, o: Obj, s: &mut BossState) {
    o.set_y(ram, s.base_y);
    o.set_x(ram, s.base_x);
    if o.routine_secondary(ram) >= 4 {
        return;
    }
    if o.status(ram) & 0x80 != 0 {
        add_points(ram, 100);
        o.set_routine_secondary(ram, 4);
        s.timer = 0xB4;
        o.set_x_vel(ram, 0);
        return;
    }
    if o.collision_flags(ram) != 0 {
        return;
    }
    if s.flash_timer == 0 {
        s.flash_timer = MZ_FLASH_FRAMES as u8;
    }
    let colour = vars::NORMAL_PALETTE + 0x22;
    let v = ram.u16(colour);
    ram.w16(colour, if v == 0 { 0x0EEE } else { 0 });
    s.flash_timer = s.flash_timer.wrapping_sub(1);
    if s.flash_timer == 0 {
        o.set_collision_flags(ram, 0x0F);
    }
}

fn mz_sweep(ram: &mut Ram, o: Obj, s: &mut BossState) {
    let facing_right = o.status(ram) & XFLIP != 0;
    if o.x_vel(ram) == 0 {
        if (s.base_y as i32) != MZ_LAVA_Y {
            o.set_y_vel(
                ram,
                if (s.base_y as i32) < MZ_LAVA_Y {
                    0x40
                } else {
                    -0x40
                },
            );
            s.save(ram, o);
            *s = s.move_by_velocity(ram, o);
            return;
        }
        o.set_x_vel(ram, if facing_right { 0x200 } else { -0x200 });
        o.set_y_vel(ram, 0x100);
    }
    if s.flash_timer < 0x18 {
        s.save(ram, o);
        *s = s.move_by_velocity(ram, o);
        o.set_y_vel(ram, o.y_vel(ram).wrapping_sub(4));
    }
    let timer = o.byte(ram, 0x34).wrapping_sub(1);
    o.set_byte(ram, 0x34, timer);
    if (timer as i8) < 0 {
        if let Some(ball) = find_free_slot(ram) {
            ball.set_id(ram, 0x14);
            ball.set_y(ram, 0x2E8);
            let r = random_number(ram);
            ball.set_x(ram, ((r & 0xFFFF) % 0x50) as u16 + 0x1878);
            ball.set_word(ram, 0x28, 0xFF);
        }
        let t = (random_number(ram) & 0x1F) as u8 + 0x40;
        o.set_byte(ram, 0x34, t);
    }
    if facing_right {
        if (s.base_x as i16 as i32) < MZ_RIGHT_END {
            return;
        }
        s.base_x = MZ_RIGHT_END as u16;
    } else {
        if (s.base_x as i16 as i32) > MZ_LEFT_END {
            return;
        }
        s.base_x = MZ_LEFT_END as u16;
    }
    o.set_x_vel(ram, 0);
    o.set_y_vel(
        ram,
        if (s.base_y as i32) < MZ_LAVA_Y {
            0x180
        } else {
            -0x180
        },
    );
    o.set_subtype(ram, o.subtype(ram).wrapping_add(2));
}

fn mz_dip(ram: &mut Ram, o: Obj, s: &mut BossState) {
    s.save(ram, o);
    *s = s.move_by_velocity(ram, o);
    if (s.base_y as i16).wrapping_sub(MZ_LAVA_Y as i16) > 0 {
        return;
    }
    if o.y_vel(ram) != 0 {
        o.set_y_vel(ram, 0);
        s.timer = 0x50;
        o.set_status(ram, o.status(ram) ^ XFLIP);
        if let Some(fire) = find_free_slot(ram) {
            fire.set_x(ram, s.base_x);
            fire.set_y(ram, s.base_y.wrapping_add(0x18));
            fire.set_id(ram, 0x74);
            fire.set_subtype(ram, 1);
        }
    }
    s.timer -= 1;
    if s.timer == 0 {
        o.set_subtype(ram, o.subtype(ram).wrapping_add(2));
    }
}

fn update_mz_face(ram: &mut Ram, o: Obj) {
    let ship = Obj::new(BossState::new(ram, o).parent);
    let phase = ship.routine_secondary(ram);
    let mut anim = 1;
    let mut react = false;
    if phase == 2 {
        let dipping_still = ship.subtype(ram) & MZ_DIPPING != 0 && ship.y_vel(ram) == 0;
        react = !dipping_still;
        if dipping_still {
            anim = 4;
        }
    } else if phase == 0 {
        react = true;
    } else {
        anim = 0x0A;
    }
    if react {
        if ship.collision_flags(ram) == 0 {
            anim = 5;
        } else if Obj::new(vars::MAIN_CHARACTER).routine(ram) == 4
            || Obj::new(vars::SIDEKICK).routine(ram) == 4
        {
            anim = 4;
        }
    }
    o.set_anim(ram, anim);
    if phase == 8 {
        o.set_anim(ram, 6);
        if !o.on_screen(ram) {
            delete_object(ram, o);
            return;
        }
    }
    mz_follow(ram, o, ship);
}

fn update_mz_flame(ram: &mut Ram, o: Obj) {
    let ship = Obj::new(BossState::new(ram, o).parent);
    let mut anim = 7;
    if ship.routine_secondary(ram) as i8 >= 8 {
        anim = 0x0B;
        if !o.on_screen(ram) {
            delete_object(ram, o);
            return;
        }
    } else if ship.x_vel(ram) != 0 {
        anim = 8;
    }
    o.set_anim(ram, anim);
    mz_follow(ram, o, ship);
}

fn update_mz_anchor(ram: &mut Ram, o: Obj) {
    let ship = Obj::new(BossState::new(ram, o).parent);
    if ship.routine_secondary(ram) == 8 && !o.on_screen(ram) {
        delete_object(ram, o);
        return;
    }
    o.set_mappings(ram, 0x152D98);
    o.set_art_tile(ram, 0x46C);
    o.set_mapping_frame(ram, 4);
    mz_follow(ram, o, ship);
}

fn mz_follow(ram: &mut Ram, o: Obj, ship: Obj) {
    o.set_x(ram, ship.x(ram));
    o.set_y(ram, ship.y(ram));
    o.set_status(ram, ship.status(ram));
    o.set_render_flags(ram, (o.render_flags(ram) & !3) | (o.status(ram) & 3));
    sprites::display_sprite(ram, o);
}

pub fn lava_ball(ram: &mut Ram, sets: &CollisionSets, o: Obj) -> Result<(), Unported> {
    if !matches!(o.routine(ram), 0 | 2) {
        delete_object(ram, o);
        return Ok(());
    }
    if o.routine(ram) == 0 {
        o.set_routine(ram, 2);
        o.set_y_radius(ram, 8);
        o.set_x_radius(ram, 8);
        o.set_mappings(ram, 0x1563C0);
        o.set_art_tile(ram, 0x345);
        o.set_render_flags(ram, LEVEL_COORDS);
        o.set_priority(ram, if o.byte(ram, 0x29) != 0 { 5 } else { 3 });
        o.set_collision_flags(ram, 0x8B);
        o.set_word(ram, 0x30, o.y(ram));
        const SPEEDS: [i16; 9] = [
            -0x400, -0x500, -0x600, -0x700, -0x200, 0x200, -0x200, 0x200, 0,
        ];
        o.set_y_vel(ram, *SPEEDS.get(o.subtype(ram) as usize).unwrap_or(&0));
        o.set_width_pixels(ram, 8);
        if o.subtype(ram) >= 6 {
            o.set_width_pixels(ram, 0x10);
            o.set_anim(ram, 2);
            o.set_x_vel(ram, o.y_vel(ram));
            o.set_y_vel(ram, 0);
        }
    }
    match o.subtype(ram) {
        0..=3 => {
            o.set_y_vel(ram, o.y_vel(ram).wrapping_add(0x18));
            if o.word(ram, 0x30) < o.y(ram) {
                o.set_routine(ram, o.routine(ram).wrapping_add(2));
            }
            o.set_status(
                ram,
                (o.status(ram) & !YFLIP) | if o.y_vel(ram) < 0 { YFLIP } else { 0 },
            );
        }
        4 => {
            o.set_status(ram, o.status(ram) | YFLIP);
            if object_ceiling_floor(ram, sets, o) < 0 {
                o.set_subtype(ram, 8);
                o.set_anim(ram, 1);
                o.set_y_vel(ram, 0);
            }
        }
        5 => {
            o.set_status(ram, o.status(ram) & !YFLIP);
            if object_floor(ram, sets, o) < 0 {
                o.set_subtype(ram, 8);
                o.set_anim(ram, 1);
                o.set_y_vel(ram, 0);
            }
        }
        6 => {
            o.set_status(ram, o.status(ram) | XFLIP);
            if wall_below(ram, sets, o, -8) < 0 {
                o.set_subtype(ram, 8);
                o.set_anim(ram, 3);
                o.set_x_vel(ram, 0);
            }
        }
        7 => {
            o.set_status(ram, o.status(ram) & !XFLIP);
            if wall_below(ram, sets, o, 8) < 0 {
                o.set_subtype(ram, 8);
                o.set_anim(ram, 3);
                o.set_x_vel(ram, 0);
            }
        }
        _ => {}
    }
    move_object(ram, o);
    if out_of_range(ram, o.x(ram)) {
        delete_object(ram, o);
        return Ok(());
    }
    sprites::display_sprite(ram, o);
    Ok(())
}

fn wall_below(ram: &mut Ram, sets: &CollisionSets, o: Obj, offset: i32) -> i32 {
    let y = o.y(ram) as i32;
    let x = o.x(ram) as i32 + offset;
    let (d, _) = crate::collision::floor_dist(ram, sets, y, x, 0x0C);
    d
}

pub fn boss_fire(ram: &mut Ram, sets: &CollisionSets, o: Obj) -> Result<(), Unported> {
    match o.routine(ram) {
        0 => {
            let dropped = o.subtype(ram) != 0;
            o.set_y_radius(ram, 8);
            o.set_x_radius(ram, 8);
            o.set_mappings(ram, 0x1563C0);
            o.set_art_tile(ram, 0x345);
            o.set_render_flags(ram, LEVEL_COORDS);
            o.set_priority(ram, 5);
            o.set_word(ram, 0x38, o.y(ram));
            o.set_width_pixels(ram, 8);
            o.set_routine(ram, 2);
            if !dropped {
                o.set_collision_flags(ram, 0x8B);
                o.set_routine(ram, 4);
                fire_burn(ram, o);
            } else {
                o.set_byte(ram, 0x29, 0x1E);
                o.set_anim(ram, 0);
                fire_move(ram, sets, o)?;
            }
        }
        2 => fire_move(ram, sets, o)?,
        4 => fire_burn(ram, o),
        _ => delete_object(ram, o),
    }
    sprites::display_sprite(ram, o);
    Ok(())
}

fn fire_move(ram: &mut Ram, sets: &CollisionSets, o: Obj) -> Result<(), Unported> {
    match o.routine_secondary(ram) {
        0 => {
            o.set_status(ram, o.status(ram) | 2);
            let t = o.byte(ram, 0x29).wrapping_sub(1);
            o.set_byte(ram, 0x29, t);
            if (t as i8) >= 0 {
            } else {
                o.set_collision_flags(ram, 0x8B);
                o.set_subtype(ram, 0);
                o.set_y_vel(ram, o.y_vel(ram).wrapping_add(0x18));
                o.set_status(ram, o.status(ram) & !2);
                if object_floor(ram, sets, o) < 0 {
                    o.set_routine_secondary(ram, 2);
                }
            }
        }
        2 => {
            o.set_y(ram, o.y(ram).wrapping_sub(2));
            o.set_art_tile(ram, o.art_tile(ram) | 0x8000);
            o.set_x_vel(ram, 0xA0);
            o.set_y_vel(ram, 0);
            o.set_word(ram, 0x30, o.x(ram));
            o.set_word(ram, 0x38, o.y(ram));
            o.set_byte(ram, 0x29, 3);
            if let Some(copy) = find_free_slot_after(ram, o) {
                copy.set_id(ram, 0x74);
                copy.set_x(ram, o.x(ram));
                copy.set_y(ram, o.y(ram));
                copy.set_x_vel(ram, -0xA0);
                copy.set_y_vel(ram, 0);
                copy.set_routine(ram, 2);
                copy.set_routine_secondary(ram, 4);
                copy.set_subtype(ram, 0);
                copy.set_collision_flags(ram, 0x8B);
                copy.set_width_pixels(ram, 8);
                copy.set_y_radius(ram, 8);
                copy.set_x_radius(ram, 8);
                copy.set_mappings(ram, 0x1563C0);
                copy.set_art_tile(ram, 0x345);
                copy.set_render_flags(ram, LEVEL_COORDS);
                copy.set_priority(ram, 5);
                copy.set_word(ram, 0x30, copy.x(ram));
                copy.set_word(ram, 0x38, copy.y(ram));
                copy.set_byte(ram, 0x29, 3);
            }
            o.set_routine_secondary(ram, 4);
        }
        4 => fire_run(ram, sets, o)?,
        6 => fire_drop_off(ram, sets, o)?,
        _ => {}
    }
    move_object(ram, o);
    if o.y(ram) > 0x2E8 {
        delete_object(ram, o);
    }
    Ok(())
}

fn fire_run(ram: &mut Ram, sets: &CollisionSets, o: Obj) -> Result<(), Unported> {
    if object_floor(ram, sets, o) >= 0 {
        o.set_routine_secondary(ram, 6);
        return Ok(());
    }
    let x = o.x(ram);
    if x as i16 as i32 > 0x1940 {
        o.set_routine(ram, o.routine(ram).wrapping_add(2));
        return Ok(());
    }
    let last = o.word(ram, 0x30);
    if x != last && (x & 0x10) != (last & 0x10) {
        if let Some(fire) = find_free_slot_after(ram, o) {
            fire.set_x(ram, o.x(ram));
            fire.set_y(ram, o.y(ram));
            fire.set_id(ram, 0x74);
            fire.set_word(ram, 0x28, 0x67);
        }
        o.set_word(ram, 0x32, o.x(ram));
    }
    o.set_word(ram, 0x30, o.x(ram));
    Ok(())
}

fn fire_drop_off(ram: &mut Ram, sets: &CollisionSets, o: Obj) -> Result<(), Unported> {
    o.set_status(ram, o.status(ram) & !2);
    o.set_y_vel(ram, o.y_vel(ram).wrapping_add(0x24));
    let dx = (o.x(ram) as i16)
        .wrapping_sub(o.word(ram, 0x32) as i16)
        .unsigned_abs();
    if dx == 0x12 {
        o.set_art_tile(ram, o.art_tile(ram) & 0x7FFF);
    }
    if object_floor(ram, sets, o) >= 0 {
        return Ok(());
    }
    let t = o.byte(ram, 0x29).wrapping_sub(1);
    o.set_byte(ram, 0x29, t);
    if t == 0 {
        delete_object(ram, o);
        return Ok(());
    }
    o.set_y_vel(ram, 0);
    o.set_x(ram, o.word(ram, 0x32));
    o.set_y(ram, o.word(ram, 0x38));
    o.set_art_tile(ram, o.art_tile(ram) | 0x8000);
    o.set_routine_secondary(ram, 4);
    Ok(())
}

fn fire_burn(ram: &mut Ram, o: Obj) {
    o.set_art_tile(ram, o.art_tile(ram) | 0x8000);
    let t = o.byte(ram, 0x29).wrapping_sub(1);
    o.set_byte(ram, 0x29, t);
    if t == 0 {
        o.set_anim(ram, 1);
        o.set_y(ram, o.y(ram).wrapping_sub(4));
        o.set_collision_flags(ram, 0);
    }
}

pub fn buzz_bomber(ram: &mut Ram, o: Obj, animations: &[u8]) {
    if o.routine(ram) == 0 {
        o.set_routine(ram, 2);
        o.set_mappings(ram, 0x151C88);
        let zone = ram.u8(vars::CURRENT_ZONE_AND_ACT);
        o.set_art_tile(
            ram,
            if zone < 0x13 {
                0x406
            } else if zone < 0x15 {
                0x298
            } else {
                0x53D
            },
        );
        o.set_render_flags(ram, LEVEL_COORDS);
        o.set_priority(ram, 3);
        o.set_collision_flags(ram, 8);
        o.set_width_pixels(ram, 0x18);
    }
    match o.routine(ram) {
        2 => {
            if o.routine_secondary(ram) == 0 {
                buzz_wait(ram, o);
            } else {
                buzz_fly(ram, o);
            }
            sprites::animate_object(ram, o, animations);
            display_or_mark_gone(ram, o);
        }
        4 => delete_object(ram, o),
        _ => {}
    }
}

fn buzz_wait(ram: &mut Ram, o: Obj) {
    let timer = o.word(ram, 0x32).wrapping_sub(1);
    o.set_word(ram, 0x32, timer);
    if (timer as i16) >= 0 {
        return;
    }
    if o.byte(ram, 0x34) & 2 != 0 {
        if let Some(missile) = find_free_slot(ram) {
            let right = o.status(ram) & XFLIP != 0;
            missile.set_id(ram, 0x23);
            missile.set_x(
                ram,
                o.x(ram).wrapping_add(if right { 0x18 } else { 0xFFE8 }),
            );
            missile.set_y(ram, o.y(ram).wrapping_add(0x1C));
            missile.set_y_vel(ram, 0x200);
            missile.set_x_vel(ram, if right { 0x200 } else { -0x200 });
            missile.set_status(ram, o.status(ram));
            missile.set_word(ram, 0x32, 0x0E);
            missile.set_long(ram, 0x3C, 0xFFFF_0000 | o.address() as u32);
        }
        o.set_byte(ram, 0x34, 1);
        o.set_word(ram, 0x32, 0x3B);
        return;
    }
    o.set_routine_secondary(ram, 2);
    o.set_word(ram, 0x32, 0x7F);
    o.set_x_vel(
        ram,
        if o.status(ram) & XFLIP != 0 {
            0x400
        } else {
            -0x400
        },
    );
}

fn buzz_fly(ram: &mut Ram, o: Obj) {
    let timer = o.word(ram, 0x32).wrapping_sub(1);
    o.set_word(ram, 0x32, timer);
    if (timer as i16) < 0 {
        o.set_byte(ram, 0x34, 0);
        o.set_status(ram, o.status(ram) ^ XFLIP);
        o.set_word(ram, 0x32, 0x3B);
        o.set_routine_secondary(ram, 0);
        o.set_x_vel(ram, 0);
        return;
    }
    move_object(ram, o);
    if o.byte(ram, 0x34) != 0 {
        return;
    }
    let sonic = Obj::new(vars::MAIN_CHARACTER);
    let distance = ((sonic.x(ram) as i32 - o.x(ram) as i32) as i16).unsigned_abs();
    if distance >= 0x60 || !o.on_screen(ram) {
        return;
    }
    o.set_byte(ram, 0x34, 2);
    o.set_word(ram, 0x32, 0x1D);
    o.set_routine_secondary(ram, 0);
    o.set_x_vel(ram, 0);
}

pub fn missile(ram: &mut Ram, o: Obj) {
    match o.routine(ram) {
        0 => {
            let t = o.word(ram, 0x32).wrapping_sub(1);
            o.set_word(ram, 0x32, t);
            if (t as i16) >= 0 {
                return;
            }
            o.set_routine(ram, 2);
            o.set_mappings(ram, 0x151ED4);
            o.set_render_flags(ram, LEVEL_COORDS);
            o.set_priority(ram, 3);
            o.set_width_pixels(ram, 8);
            o.set_status(ram, o.status(ram) & 3);
            if o.subtype(ram) != 0 {
                o.set_routine(ram, 8);
                o.set_collision_flags(ram, 0x87);
            }
        }
        2 => {
            // The flare animation would advance to Flying; without art, go straight to the flying phase.
            o.set_routine(ram, 4);
            o.set_collision_flags(ram, 0x87);
        }
        4 => {
            if o.status(ram) & 0x80 != 0 {
                delete_object(ram, o);
                return;
            }
            o.set_collision_flags(ram, 0x87);
            move_object(ram, o);
            if ram.u16(vars::CAMERA_MAX_Y_POS).wrapping_add(0xE0) < o.y(ram) {
                delete_object(ram, o);
                return;
            }
            sprites::display_sprite(ram, o);
        }
        8 => {
            if !o.on_screen(ram) {
                delete_object(ram, o);
                return;
            }
            move_object(ram, o);
            sprites::display_sprite(ram, o);
        }
        _ => delete_object(ram, o),
    }
}

pub fn caterkiller(
    ram: &mut Ram,
    sets: &CollisionSets,
    o: Obj,
    wave: &[u8],
) -> Result<(), Unported> {
    match o.routine(ram) {
        0 => create_caterkiller(ram, sets, o)?,
        2 => caterkiller_head(ram, sets, o, wave),
        4 | 8 => caterkiller_follow(ram, sets, o),
        6 => caterkiller_middle(ram, sets, o, wave),
        0x0A => delete_object(ram, o),
        0x0C => caterkiller_bounce(ram, sets, o),
        _ => {}
    }
    Ok(())
}

fn caterkiller_floor(ram: &mut Ram, sets: &CollisionSets, o: Obj) -> i32 {
    object_floor(ram, sets, o)
}

fn create_caterkiller(ram: &mut Ram, sets: &CollisionSets, o: Obj) -> Result<(), Unported> {
    o.set_y_radius(ram, 7);
    o.set_x_radius(ram, 8);
    move_and_fall(ram, o);
    let distance = caterkiller_floor(ram, sets, o);
    if distance >= 0 {
        return Ok(());
    }
    o.set_y(ram, (o.y(ram) as i32 + distance) as u16);
    o.set_y_vel(ram, 0);
    o.set_routine(ram, 2);
    o.set_mappings(ram, 0x1550C8);
    o.set_art_tile(ram, 0x2444);
    let flags = (o.render_flags(ram) & 3) | LEVEL_COORDS;
    o.set_render_flags(ram, flags);
    o.set_status(ram, flags);
    o.set_priority(ram, 4);
    o.set_width_pixels(ram, 8);
    o.set_collision_flags(ram, 0x0B);
    let mut x = o.x(ram);
    let spacing: i32 = if o.status(ram) & XFLIP != 0 {
        -0x0C
    } else {
        0x0C
    };
    let mut routine = 4u8;
    let mut in_front = o.address();
    for i in 0..3u16 {
        let Some(segment) = find_free_slot_after(ram, o) else {
            caterkiller_go_away(ram, o);
            return Ok(());
        };
        segment.set_id(ram, 0x78);
        segment.set_routine(ram, routine);
        routine += 2;
        segment.set_mappings(ram, o.mappings(ram));
        segment.set_art_tile(ram, o.art_tile(ram));
        segment.set_priority(ram, 5);
        segment.set_width_pixels(ram, 8);
        segment.set_collision_flags(ram, 0xCB);
        x = x.wrapping_add(spacing as u16);
        segment.set_x(ram, x);
        segment.set_y(ram, o.y(ram));
        segment.set_status(ram, o.status(ram));
        segment.set_render_flags(ram, o.status(ram));
        segment.set_mapping_frame(ram, 8);
        segment.set_long(ram, 0x3C, 0xFFFF_0000 | in_front as u32);
        segment.set_byte(ram, 0x3C, (4 + i as u8 * 4) & 0x0F);
        in_front = segment.address();
    }
    o.set_byte(ram, 0x2A, 7);
    o.set_byte(ram, 0x3C, 0);
    caterkiller_head(ram, sets, o, &[]);
    Ok(())
}

fn caterkiller_go_away(ram: &mut Ram, o: Obj) {
    let index = o.respawn_index(ram);
    if index != 0 {
        let entry = vars::OBJECT_RESPAWN_TABLE + 2 + index as u16;
        ram.w8(entry, ram.u8(entry) & 0x7F);
    }
    o.set_routine(ram, 0x0A);
    o.set_collision_flags(ram, 0);
}

fn caterkiller_head(ram: &mut Ram, sets: &CollisionSets, o: Obj, wave: &[u8]) {
    if o.status(ram) & 0x80 != 0 {
        caterkiller_split(ram, sets, o);
        return;
    }
    if o.routine_secondary(ram) == 0 {
        caterkiller_wait(ram, sets, o);
    } else {
        caterkiller_inch(ram, sets, o);
    }
    let mut motion = o.byte(ram, 0x2B);
    if motion & 0x80 != 0 {
        let i = (o.angle(ram) & 0x7F) as usize;
        o.set_angle(ram, o.angle(ram).wrapping_add(4));
        let rise = wave.get(i).copied().unwrap_or(0);
        if (rise as i8) < 0 {
            motion &= 0x7F;
            o.set_byte(ram, 0x2B, motion);
        } else {
            o.set_mapping_frame(ram, rise.wrapping_add(motion & 0x10));
        }
    }
    if out_of_range(ram, o.x(ram)) {
        caterkiller_go_away(ram, o);
        return;
    }
    sprites::display_sprite(ram, o);
}

fn caterkiller_wait(ram: &mut Ram, sets: &CollisionSets, o: Obj) {
    let timer = o.byte(ram, 0x2A).wrapping_sub(1);
    o.set_byte(ram, 0x2A, timer);
    if (timer as i8) >= 0 {
        return;
    }
    o.set_routine_secondary(ram, 2);
    o.set_byte(ram, 0x2A, 0x10);
    o.set_x_vel(ram, -0xC0);
    o.set_inertia(ram, 0x40);
    let was_pushing = o.byte(ram, 0x2B) & 0x10 != 0;
    let mut motion = o.byte(ram, 0x2B) ^ 0x10;
    if !was_pushing {
        o.set_x_vel(ram, 0);
        o.set_inertia(ram, o.inertia(ram).wrapping_neg());
    }
    motion |= 0x80;
    o.set_byte(ram, 0x2B, motion);
    caterkiller_inch(ram, sets, o);
}

fn caterkiller_inch(ram: &mut Ram, sets: &CollisionSets, o: Obj) {
    let timer = o.byte(ram, 0x2A).wrapping_sub(1);
    o.set_byte(ram, 0x2A, timer);
    if (timer as i8) < 0 {
        o.set_routine_secondary(ram, 0);
        o.set_byte(ram, 0x2A, 7);
        o.set_x_vel(ram, 0);
        o.set_inertia(ram, 0);
        return;
    }
    let speed = o.x_vel(ram);
    if speed == 0 {
        return;
    }
    if !caterkiller_advance(ram, o, speed) {
        return;
    }
    let distance = caterkiller_floor(ram, sets, o);
    if !(-8..0x0C).contains(&distance) {
        caterkiller_turn(ram, o, true);
        return;
    }
    o.set_y(ram, (o.y(ram) as i32 + distance) as u16);
    let k = o.byte(ram, 0x3C);
    o.set_byte(ram, 0x3C, k.wrapping_add(1) & 0x0F);
    o.set_byte(ram, 0x2C + k as u16, distance as u8);
}

fn caterkiller_advance(ram: &mut Ram, o: Obj, speed: i16) -> bool {
    let x = o.x_long(ram);
    let old = (x >> 16) as u16;
    let step = if o.status(ram) & XFLIP != 0 {
        -(speed as i32)
    } else {
        speed as i32
    };
    o.set_x_long(ram, x.wrapping_add((step << 8) as u32));
    o.x(ram) != old
}

fn caterkiller_turn(ram: &mut Ram, o: Obj, is_head: bool) {
    let mut history = o.byte(ram, 0x3C);
    if is_head {
        o.set_byte(ram, 0x2C + history as u16, 0x80);
    }
    let sub = o.word(ram, 0x0A);
    o.set_word(ram, 0x0A, sub.wrapping_neg());
    if sub != 0 && o.status(ram) & XFLIP != 0 && (is_head || o.x_vel(ram) == -0xC0) {
        o.set_x(ram, o.x(ram).wrapping_sub(1));
        history = history.wrapping_add(1);
        o.set_byte(ram, 0x2C + history as u16, 0);
    }
    o.set_status(ram, o.status(ram) ^ XFLIP);
    o.set_render_flags(ram, o.status(ram));
    history = history.wrapping_add(1) & 0x0F;
    o.set_byte(ram, 0x3C, history);
}

fn caterkiller_middle(ram: &mut Ram, sets: &CollisionSets, o: Obj, wave: &[u8]) {
    let front = Obj::new(o.word(ram, 0x3E));
    o.set_byte(ram, 0x2B, front.byte(ram, 0x2B));
    let motion = o.byte(ram, 0x2B);
    if motion & 0x80 != 0 {
        let i = (o.angle(ram) & 0x7F) as usize;
        o.set_angle(ram, o.angle(ram).wrapping_add(4));
        if (wave.get((i + 4) & 0xFF).copied().unwrap_or(0) as i8) < 0 {
            o.set_angle(ram, o.angle(ram).wrapping_add(4));
        }
        o.set_mapping_frame(ram, wave.get(i).copied().unwrap_or(0).wrapping_add(8));
    }
    caterkiller_follow(ram, sets, o);
}

fn caterkiller_follow(ram: &mut Ram, sets: &CollisionSets, o: Obj) {
    let front = Obj::new(o.word(ram, 0x3E));
    if o.status(ram) & 0x80 != 0 {
        front.set_status(ram, front.status(ram) | 0x80);
        caterkiller_split(ram, sets, o);
        return;
    }
    o.set_byte(ram, 0x2B, front.byte(ram, 0x2B));
    o.set_routine_secondary(ram, front.routine_secondary(ram));
    if o.routine_secondary(ram) != 0 {
        o.set_inertia(ram, front.inertia(ram));
        o.set_x_vel(ram, front.x_vel(ram).wrapping_add(o.inertia(ram)));
        if caterkiller_advance(ram, o, o.x_vel(ram)) {
            let k = o.byte(ram, 0x3C);
            let change = front.byte(ram, 0x2C + k as u16);
            if change == 0x80 {
                o.set_byte(ram, 0x2C + k as u16, 0x80);
                caterkiller_turn(ram, o, false);
            } else {
                o.set_y(ram, (o.y(ram) as i32 + change as i8 as i32) as u16);
                o.set_byte(ram, 0x3C, k.wrapping_add(1) & 0x0F);
                o.set_byte(ram, 0x2C + k as u16, change);
            }
        }
    }
    if front.routine(ram) == 0x0C {
        front.set_status(ram, front.status(ram) | 0x80);
        caterkiller_split(ram, sets, o);
        return;
    }
    if front.id(ram) == 0x27 || front.routine(ram) == 0x0A {
        o.set_routine(ram, 0x0A);
        o.set_collision_flags(ram, 0);
    }
    let _ = sets;
    sprites::display_sprite(ram, o);
}

fn caterkiller_split(ram: &mut Ram, sets: &CollisionSets, o: Obj) {
    let routine = o.routine(ram);
    const SPEEDS: [i16; 4] = [-0x200, -0x180, 0x180, 0x200];
    let mut speed = if (2..=8).contains(&routine) {
        SPEEDS[((routine - 2) / 2) as usize]
    } else {
        0
    };
    if o.status(ram) & XFLIP != 0 {
        speed = speed.wrapping_neg();
    }
    o.set_x_vel(ram, speed);
    o.set_y_vel(ram, -0x400);
    o.set_routine(ram, 0x0C);
    o.set_mapping_frame(ram, o.mapping_frame(ram) & 0xF8);
    caterkiller_bounce(ram, sets, o);
}

fn caterkiller_bounce(ram: &mut Ram, sets: &CollisionSets, o: Obj) {
    move_and_fall(ram, o);
    if o.y_vel(ram) >= 0 {
        let distance = caterkiller_floor(ram, sets, o);
        if distance < 0 {
            o.set_y(ram, (o.y(ram) as i32 + distance) as u16);
            o.set_y_vel(ram, -0x400);
        }
    }
    if !o.on_screen(ram) {
        caterkiller_go_away(ram, o);
        return;
    }
    sprites::display_sprite(ram, o);
}

pub fn grass_platform(
    ram: &mut Ram,
    sets: &CollisionSets,
    o: Obj,
    heights: &[u8],
) -> Result<(), Unported> {
    if o.routine(ram) == 0 {
        o.set_routine(ram, 2);
        o.set_mappings(ram, 0x153500);
        o.set_art_tile(ram, 0xC000);
        o.set_render_flags(ram, LEVEL_COORDS);
        o.set_priority(ram, 5);
        o.set_word(ram, 0x2C, o.y(ram));
        o.set_word(ram, 0x2A, o.x(ram));
        let t = ((o.subtype(ram) >> 4) & 7) as usize;
        let (offset, frame, width) = match t {
            1 => (0x78usize, 1u8, 0x40u8),
            2 => (0x4C, 2, 0x20),
            _ => (0, 0, 0x40),
        };
        o.set_long(ram, 0x30, offset as u32);
        o.set_mapping_frame(ram, frame);
        o.set_width_pixels(ram, width);
        o.set_subtype(ram, o.subtype(ram) & 0x0F);
        o.set_y_radius(ram, 0x40);
        o.set_render_flags(ram, o.render_flags(ram) | 0x10);
    }
    let old_x = o.x(ram) as i32;
    grass_move(ram, o);
    let half_width = o.width_pixels(ram) as i32 + 0x0B;
    let half_height = if o.mapping_frame(ram) == 2 {
        0x30
    } else {
        0x20
    };
    crate::objects::solid::solid_object(
        ram,
        sets,
        o,
        half_width,
        half_height,
        half_height + 1,
        old_x,
    )?;
    let offset = o.long(ram, 0x30) as usize;
    let profile: Vec<i8> = (0..0x80)
        .map(|i| heights.get(offset + i).copied().unwrap_or(0) as i8)
        .collect();
    crate::objects::solid::sloped_platform(ram, o, half_width, o.x(ram) as i32, &profile);
    let stood = o.status(ram) & 0x18 != 0;
    o.set_routine_secondary(ram, if stood { 2 } else { 0 });
    sprites::display_sprite(ram, o);
    if out_of_range(ram, o.word(ram, 0x2A)) {
        delete_object(ram, o);
    }
    Ok(())
}

fn add_grass_fire(ram: &mut Ram, platform: Obj, fire: Obj) {
    let count = platform.byte(ram, 0x36);
    if count >= 6 {
        return;
    }
    platform.set_byte(ram, 0x36, count + 1);
    platform.set_byte(ram, 0x37 + count as u16, fire.slot_index());
}

fn grass_move(ram: &mut Ram, o: Obj) {
    let kind = o.subtype(ram) & 7;
    if kind == 0 {
        return;
    }
    if kind == 5 {
        let mut sink = o.byte(ram, 0x34) as i32;
        if o.routine_secondary(ram) == 0 {
            sink = (sink - 2).max(0);
        } else {
            sink = (sink + 4).min(0x40);
        }
        o.set_byte(ram, 0x34, sink as u8);
        let (sin, _) = crate::math::calc_sine(sink as u8);
        let depth = (sin as u16 >> 4) as i32;
        o.set_y(ram, (o.word(ram, 0x2C) as i32 + depth) as u16);
        if sink == 0x20 && o.byte(ram, 0x35) == 0 {
            o.set_byte(ram, 0x35, 1);
            if let Some(fire) = crate::objects::find_free_slot_after(ram, o) {
                fire.set_id(ram, 0x19);
                fire.set_x(ram, o.x(ram).wrapping_sub(0x40));
                fire.set_word(ram, 0x2C, o.word(ram, 0x2C).wrapping_add(8).wrapping_sub(3));
                fire.set_long(ram, 0x30, o.long(ram, 0x30));
                fire.set_long(ram, 0x38, 0xFFFF_0000 | o.address() as u32);
                add_grass_fire(ram, o, fire);
            }
        }
        let count = o.byte(ram, 0x36);
        for i in 0..count {
            let fire = Obj::from_slot(o.byte(ram, 0x37 + i as u16));
            fire.set_word(ram, 0x3C, depth as u16);
        }
        return;
    }
    if kind > 5 {
        return;
    }
    let swing = ram.u8(crate::oscillation::Oscillation::value((kind - 1) as usize)) as i32;
    let range = match kind {
        1 => 0x20,
        2 => 0x30,
        3 => 0x40,
        _ => 0x60,
    };
    let swing = if o.subtype(ram) & 8 != 0 {
        -swing + range
    } else {
        swing
    };
    o.set_y(ram, (o.word(ram, 0x2C) as i32 - swing) as u16);
}

pub fn boss_explosion(ram: &mut Ram, o: Obj) {
    if o.routine(ram) == 0 {
        o.set_routine(ram, 2);
        o.set_mappings(ram, 0x3176E);
        o.set_art_tile(ram, 0x8580);
        o.set_render_flags(ram, LEVEL_COORDS);
        o.set_priority(ram, 0);
        o.set_collision_flags(ram, 0);
        o.set_width_pixels(ram, 0x0C);
        o.set_anim_frame_duration(ram, 7);
        o.set_mapping_frame(ram, 0);
        return;
    }
    let d = o.anim_frame_duration(ram).wrapping_sub(1);
    o.set_anim_frame_duration(ram, d);
    if (d as i8) < 0 {
        o.set_anim_frame_duration(ram, 7);
        o.set_mapping_frame(ram, o.mapping_frame(ram).wrapping_add(1));
        if o.mapping_frame(ram) == 7 {
            delete_object(ram, o);
            return;
        }
    }
    sprites::display_sprite(ram, o);
}
