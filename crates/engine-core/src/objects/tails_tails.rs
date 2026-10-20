//! Object $05, Tails' tails (ROM 0x21076): follows Tails' slot, mirrors his angle/status/position and runs the
//! tails animation block (including its directional $FC command).

use crate::animation::TailsAnimation;
use crate::objects::sprites;
use crate::{vars, Obj, Ram};

pub fn tails_tails(ram: &mut Ram, o: Obj, anim: &TailsAnimation) {
    if o.routine(ram) == 0 {
        o.set_routine(ram, 2);
        o.set_mappings(ram, 0x7A6F6);
        o.set_art_tile(ram, 0x7B0);
        o.set_priority(ram, 2);
        o.set_width_pixels(ram, 0x18);
        o.set_render_flags(ram, 4);
    }
    let tails = Obj::new(o.word(ram, 0x3E));
    o.set_angle(ram, tails.angle(ram));
    o.set_status(ram, tails.status(ram));
    o.set_x(ram, tails.x(ram));
    o.set_y(ram, tails.y(ram));
    let art = o.art_tile(ram) & 0x7FFF;
    o.set_art_tile(
        ram,
        if (tails.art_tile(ram) as i16) < 0 {
            art | 0x8000
        } else {
            art
        },
    );
    let mut parent_anim = tails.anim(ram);
    if tails.pushing(ram) {
        parent_anim = 4;
    }
    if parent_anim != o.byte(ram, 0x30) {
        o.set_byte(ram, 0x30, parent_anim);
        o.set_anim(ram, anim.tails_animation_map(parent_anim));
    }
    anim.run_tails(ram, o, tails);
    sprites::display_sprite(ram, o);
    let _ = vars::TAILS_TAILS_LAST_LOADED_DPLC;
}
