//! `DisplaySprite` (0x1A23A) and the on-screen half of `BuildSprites` (0x1A354).
//!
//! Drawing itself (mappings → hardware sprite pieces, VRAM) is a later milestone; object code does read the
//! on-screen flag that `BuildSprites` sets (render flag bit 7), so the display lists and the per-object range test
//! are ported exactly.

use crate::{vars, Obj, Ram};

/// `DisplaySprite` (0x1A23A): queue in the priority's list.
pub fn display_sprite(ram: &mut Ram, o: Obj) {
    if o.id(ram) == 0 {
        return;
    }
    let list = vars::OBJECT_DISPLAY_LISTS.wrapping_add(((o.priority(ram) & 7) as u16) << 7);
    if ram.u16(list) >= 0x7E {
        return;
    }
    let len = ram.u16(list).wrapping_add(2);
    ram.w16(list, len);
    ram.w16(list.wrapping_add(len), o.address());
}

/// `DisplaySprite3` (0x1A27A): queue at a byte offset (a multiple of $80).
pub fn display_sprite_at(ram: &mut Ram, o: Obj, list_offset: u16) {
    let list = vars::OBJECT_DISPLAY_LISTS.wrapping_add(list_offset);
    if ram.u16(list) >= 0x7E {
        return;
    }
    let len = ram.u16(list).wrapping_add(2);
    ram.w16(list, len);
    ram.w16(list.wrapping_add(len), o.address());
}

/// The `BuildSprites` lists: each object's on-screen flag is recomputed from the camera copy.
pub fn build_sprites(ram: &mut Ram) {
    for list_index in 0..8u16 {
        let list = vars::OBJECT_DISPLAY_LISTS + list_index * 0x80;
        let mut d6 = 2u16;
        while ram.u16(list) != 0 {
            // Invariant: `display_sprite` never lets a list exceed $7E bytes.
            if ram.u16(list) > 0x7E {
                break;
            }
            let address = ram.u16(list.wrapping_add(d6));
            let o = Obj::new(address);
            if o.id(ram) != 0 {
                draw_object(ram, o);
            }
            d6 = d6.wrapping_add(2);
            let len = ram.u16(list);
            ram.w16(list, len.wrapping_sub(2));
        }
    }
}

/// `DrawObject`'s range test and on-screen flag (0x1A3C0); the piece writer is not needed yet.
fn draw_object(ram: &mut Ram, o: Obj) {
    o.set_render_flags(ram, o.render_flags(ram) & 0x7F);
    let d4 = o.render_flags(ram);
    if (d4 & 0x0C) == 0 {
        // Screen-fixed object: its position is already in sprite coordinates.
        o.set_render_flags(ram, d4 | 0x80);
        return;
    }
    let cam_x = ram.u16(vars::CAMERA_X_POS_COPY);
    let cam_y = ram.u16(vars::CAMERA_Y_POS_COPY);
    let (width, height) = if (d4 & 0x40) != 0 {
        // Multi-sprite: $0E is the half width; $14 the half height when render flag bit 4 is set.
        (o.byte(ram, 0x0E) as i32, o.byte(ram, 0x14) as i32)
    } else {
        (o.width_pixels(ram) as i32, o.y_radius(ram) as u8 as i32)
    };
    let d3 = o.x(ram).wrapping_sub(cam_x) as i16 as i32;
    if ((d3 + width) as i16) < 0 {
        return;
    }
    if (d3 - width) as i16 >= 0x140 {
        return;
    }
    if (d4 & 0x10) != 0 {
        let d2 = o.y(ram).wrapping_sub(cam_y) as i16 as i32;
        if ((d2 + height) as i16) < 0 {
            return;
        }
        if (d2 - height) as i16 >= 0xE0 {
            return;
        }
    } else {
        let d2 = (o.y(ram).wrapping_sub(cam_y).wrapping_add(0x80)) & 0x7FF;
        if !(0x60..0x180).contains(&(d2 as i32)) {
            return;
        }
    }
    o.set_render_flags(ram, d4 | 0x80);
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn display_and_build_set_the_on_screen_flag_from_the_camera_copy() {
        let mut ram = Ram::new();
        let o = Obj::new(vars::MAIN_CHARACTER);
        o.set_id(&mut ram, 1);
        o.set_x(&mut ram, 100);
        o.set_y(&mut ram, 100);
        o.set_width_pixels(&mut ram, 0x18);
        o.set_render_flags(&mut ram, 0x04);
        ram.w16(vars::CAMERA_X_POS_COPY, 80);
        ram.w16(vars::CAMERA_Y_POS_COPY, 80);
        display_sprite(&mut ram, o);
        assert_eq!(ram.u16(vars::OBJECT_DISPLAY_LISTS), 2, "one queued address");
        build_sprites(&mut ram);
        assert!(o.on_screen(&ram), "inside the 320x224 view");
        assert_eq!(
            ram.u16(vars::OBJECT_DISPLAY_LISTS),
            0,
            "the list is consumed"
        );

        // Moving the camera copy far away clears the flag on the next build.
        ram.w16(vars::CAMERA_X_POS_COPY, 0x400);
        display_sprite(&mut ram, o);
        build_sprites(&mut ram);
        assert!(!o.on_screen(&ram));
    }
}
