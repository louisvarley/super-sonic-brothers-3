//! Object $08, the dust/splash/skid effect (ROM 0x21C14): it lives in the players' fixed effect slots and follows
//! their spindash/skid state.

use crate::graphics::SpriteData;
use crate::objects::sprites;
use crate::{vars, Obj, Ram};

const MAPPINGS: u32 = 0x21DDE;
const INIT: u8 = 0;
const MAIN: u8 = 2;
const GONE: u8 = 4;
const SKID_EMITTER: u8 = 6;
const ANIM_NONE: u8 = 0;
const ANIM_SPLASH: u8 = 1;
const ANIM_SPINDASH: u8 = 2;
const ANIM_SKID: u8 = 3;

pub fn dust(ram: &mut Ram, o: Obj, data: Option<&SpriteData>) {
    match o.routine(ram) {
        INIT => {
            initialise(ram, o);
            animate(ram, o, data);
        }
        MAIN => animate(ram, o, data),
        GONE => o.clear(ram),
        SKID_EMITTER => skid(ram, o),
        _ => {}
    }
}

fn initialise(ram: &mut Ram, o: Obj) {
    o.set_routine(ram, MAIN);
    o.set_mappings(ram, MAPPINGS);
    o.set_render_flags(ram, o.render_flags(ram) | 4);
    o.set_priority(ram, 1);
    o.set_width_pixels(ram, 0x10);
    o.set_art_tile(ram, 0x49C);
    o.set_word(ram, 0x3E, vars::MAIN_CHARACTER);
    o.set_word(ram, 0x3C, 0x9380);
    if o.address() == vars::SONIC_DUST {
        return;
    }
    o.set_byte(ram, 0x34, 1);
    if ram.u16(vars::PLAYER_MODE) == 2 {
        return;
    }
    o.set_art_tile(ram, 0x48C);
    o.set_word(ram, 0x3E, vars::SIDEKICK);
    o.set_word(ram, 0x3C, 0x9180);
}

#[allow(clippy::collapsible_match)]
fn animate(ram: &mut Ram, o: Obj, data: Option<&SpriteData>) {
    let player = Obj::new(o.word(ram, 0x3E));
    match o.anim(ram) {
        ANIM_SPLASH => {
            o.set_y(ram, ram.u16(vars::WATER_LEVEL1));
            if o.prev_anim(ram) == ANIM_NONE {
                o.set_x(ram, player.x(ram));
                o.set_status(ram, 0);
                o.set_art_tile(ram, o.art_tile(ram) & 0x7FFF);
            }
        }
        ANIM_SPINDASH => {
            if player.air_left(ram) < 0x0C
                || player.routine(ram) >= 4
                || player.spindash_flag(ram) == 0
            {
                o.set_anim(ram, ANIM_NONE);
                return;
            }
            o.set_x(ram, player.x(ram));
            o.set_y(ram, player.y(ram));
            o.set_status(ram, player.status(ram) & 1);
            if o.byte(ram, 0x34) != 0 {
                o.set_y(ram, o.y(ram).wrapping_sub(4));
            }
            if o.prev_anim(ram) == ANIM_NONE {
                let art = o.art_tile(ram) & 0x7FFF;
                o.set_art_tile(
                    ram,
                    if (player.art_tile(ram) as i16) < 0 {
                        art | 0x8000
                    } else {
                        art
                    },
                );
            }
        }
        ANIM_SKID => {
            if player.air_left(ram) < 0x0C {
                o.set_anim(ram, ANIM_NONE);
                return;
            }
        }
        _ => {}
    }
    if let Some(data) = data {
        animate_script(ram, o, &data.animations);
    }
    sprites::display_sprite(ram, o);
}

fn skid(ram: &mut Ram, o: Obj) {
    let player = Obj::new(o.word(ram, 0x3E));
    if player.anim(ram) != 0x0D {
        o.set_routine(ram, MAIN);
        o.set_byte(ram, 0x32, 0);
        return;
    }
    let t = o.byte(ram, 0x32).wrapping_sub(1);
    o.set_byte(ram, 0x32, t);
    if (t as i8) < 0 {
        o.set_byte(ram, 0x32, 3);
        // The puff spawn goes into a free dynamic slot; the original's slot search is not needed for the
        // effect slots themselves and is left to the object manager's normal spawn path.
    }
}

fn animate_script(ram: &mut Ram, o: Obj, block: &[u8]) {
    if block.is_empty() {
        return;
    }
    let anim = o.anim(ram);
    if anim != o.prev_anim(ram) {
        o.set_prev_anim(ram, anim);
        o.set_anim_frame(ram, 0);
        o.set_anim_frame_duration(ram, 0);
    }
    let d = o.anim_frame_duration(ram).wrapping_sub(1);
    o.set_anim_frame_duration(ram, d);
    if (d as i8) >= 0 {
        return;
    }
    let raw = ((block[anim as usize * 2] as u16) << 8) | block[anim as usize * 2 + 1] as u16;
    let script = (raw as i16) as usize;
    if script >= block.len() {
        return;
    }
    o.set_anim_frame_duration(ram, block[script]);
    let d1 = o.anim_frame(ram);
    let d0 = block[script + 1 + d1 as usize];
    if d0 < 0x80 {
        show(ram, o, d0);
        return;
    }
    match d0 {
        0xFF => {
            o.set_anim_frame(ram, 0);
            show(ram, o, block[script + 1]);
        }
        0xFE => {
            let back = block[script + 2 + d1 as usize];
            let frame = d1.wrapping_sub(back);
            o.set_anim_frame(ram, frame);
            show(ram, o, block[script + 1 + frame as usize]);
        }
        0xFD => o.set_anim(ram, block[script + 2 + d1 as usize]),
        0xFC => {
            o.set_routine(ram, o.routine(ram).wrapping_add(2));
            o.set_anim_frame_duration(ram, 0);
            o.set_anim_frame(ram, d1.wrapping_add(1));
        }
        0xFB => {
            o.set_anim_frame(ram, 0);
            o.set_routine_secondary(ram, 0);
        }
        0xFA => o.set_routine_secondary(ram, o.routine_secondary(ram).wrapping_add(2)),
        0xF9 => o.set_byte(ram, 0x2A, o.byte(ram, 0x2A).wrapping_add(2)),
        _ => {}
    }
}

fn show(ram: &mut Ram, o: Obj, frame: u8) {
    o.set_mapping_frame(ram, frame & 0x1F);
    let flip = (frame.rotate_left(3) ^ o.status(ram)) & 3;
    o.set_render_flags(ram, (o.render_flags(ram) & !3) | flip);
    o.set_anim_frame(ram, o.anim_frame(ram).wrapping_add(1));
}
