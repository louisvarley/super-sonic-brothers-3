//! `BuildSprites` (0x1A354): HUD, object display lists, rings and the SAT at `$F800`.

use crate::graphics::{HudData, MappingPiece, SpriteBank, SpriteData};
use crate::{vars, Obj, Ram};

pub fn build_sprites(
    ram: &mut Ram,
    bank: &SpriteBank,
    hud: &HudData,
    rings: &SpriteData,
) -> Result<(), crate::player::Unported> {
    if ram.u16(vars::TWO_PLAYER_MODE) != 0 {
        return Err(crate::player::Unported::new(0x1A6C6, "BuildSprites 2P"));
    }
    let mut a2 = vars::SPRITE_TABLE;
    let mut d5 = 0u8;
    let mut first_list = 0;
    if ram.u8(vars::LEVEL_STARTED_FLAG) != 0 {
        hud_build(ram, hud, &mut a2, &mut d5);
        if ram.u8(vars::SPRITE_COUNT + 1) != 0 {
            draw_list(ram, bank, vars::OBJECT_DISPLAY_LISTS, &mut a2, &mut d5)?;
            first_list = 1;
        }
        build_rings(ram, rings, &mut a2, &mut d5);
    }
    for n in first_list..8u16 {
        draw_list(
            ram,
            bank,
            vars::OBJECT_DISPLAY_LISTS + n * 0x80,
            &mut a2,
            &mut d5,
        )?;
    }
    ram.w8(vars::SPRITE_COUNT, d5);
    if d5 == 0x50 {
        ram.w8(a2.wrapping_sub(5), 0);
    } else {
        ram.w32(a2, 0);
    }
    Ok(())
}

fn hud_build(ram: &mut Ram, hud: &HudData, a2: &mut u16, d5: &mut u8) {
    let blink_off = ram.bit(vars::LEVEL_FRAME_COUNTER + 1, 3);
    let time_warning = if ram.u8(vars::TIMED_MODE) == 0 {
        ram.u8(vars::TIMER_MINUTE) == 9
    } else {
        ram.u32(vars::TIMER) < 0x600
    };
    let mut frame = 0usize;
    if ram.u16(vars::RING_COUNT) == 0 {
        if !blink_off {
            frame = 1 + usize::from(time_warning) * 2;
        }
    } else if !blink_off && time_warning {
        frame = 2;
    }
    let pieces = hud.sprites.frames.get(frame).cloned().unwrap_or_default();
    write_static(ram, &pieces, 0x108, 0x90, 0x86CA, a2, d5);
}

fn build_rings(ram: &mut Ram, rings: &SpriteData, a2: &mut u16, d5: &mut u8) {
    let mut a0 = ram.u16(vars::RING_START_ADDR);
    let end = ram.u16(vars::RING_END_ADDR);
    let cam_x = ram.u16(vars::CAMERA_X_POS) as i32;
    let cam_y = ram.u16(vars::CAMERA_Y_POS) as i32;
    while a0 != end {
        if (ram.u16(a0) as i16) < 0 {
            a0 = a0.wrapping_add(6);
            continue;
        }
        let d3 = ((ram.u16(a0 + 2) as i32 - cam_x + 0x80) & 0xFFFF) as u16;
        let mut d2 = ((ram.u16(a0 + 4) as i32 - cam_y) & 0x7FF) + 8;
        if d2 < 0xF0 {
            d2 += 0x78;
            let mut frame = ram.u8(a0 + 1) as usize;
            if frame == 0 {
                frame = ram.u8(vars::RINGS_ANIM_FRAME) as usize;
            }
            if let Some(p) = rings.frames.get(frame).and_then(|f| f.first()) {
                ram.w16(*a2, (p.y as i32 + d2) as u16);
                ram.w8(*a2 + 2, p.size);
                *d5 = d5.wrapping_add(1);
                ram.w8(*a2 + 3, *d5);
                ram.w16(*a2 + 4, p.pattern.wrapping_add(0x26BC));
                ram.w16(*a2 + 6, p.x.wrapping_add(d3 as i16) as u16);
                *a2 = a2.wrapping_add(8);
            }
        }
        a0 = a0.wrapping_add(6);
    }
}

fn draw_list(
    ram: &mut Ram,
    bank: &SpriteBank,
    list: u16,
    a2: &mut u16,
    d5: &mut u8,
) -> Result<(), crate::player::Unported> {
    let mut d6 = 2u16;
    while ram.u16(list) != 0 {
        let o = Obj::new(ram.u16(list.wrapping_add(d6)));
        if o.id(ram) != 0 {
            draw_object(ram, bank, o, a2, d5)?;
        }
        d6 = d6.wrapping_add(2);
        let n = ram.u16(list).wrapping_sub(2);
        ram.w16(list, n);
    }
    Ok(())
}

fn draw_object(
    ram: &mut Ram,
    bank: &SpriteBank,
    o: Obj,
    a2: &mut u16,
    d5: &mut u8,
) -> Result<(), crate::player::Unported> {
    let flags = o.render_flags(ram) & 0x7F;
    o.set_render_flags(ram, flags);
    if flags & 0x40 != 0 {
        draw_multi(ram, bank, o, flags, a2, d5)?;
        return Ok(());
    }
    let (d2, d3);
    if flags & 0x0C == 0 {
        d2 = o.word(ram, 0x0A) as i32;
        d3 = o.x(ram) as i32;
    } else {
        let cam_x = ram.u16(vars::CAMERA_X_POS_COPY) as i32;
        let cam_y = ram.u16(vars::CAMERA_Y_POS_COPY) as i32;
        let width = o.width_pixels(ram) as i32;
        let mut dx = o.x(ram) as i16 as i32 - cam_x;
        if dx + width < 0 || dx - width >= 0x140 {
            return Ok(());
        }
        dx += 0x80;
        d3 = dx;
        if flags & 0x10 != 0 {
            let h = o.y_radius(ram) as u8 as i32;
            let mut dy = o.y(ram) as i16 as i32 - cam_y;
            if dy + h < 0 || dy - h >= 0xE0 {
                return Ok(());
            }
            dy += 0x80;
            d2 = dy;
        } else {
            d2 = (o.y(ram) as i32 - cam_y + 0x80) & 0x7FF;
            if !(0x60..0x180).contains(&d2) {
                return Ok(());
            }
        }
    }
    let pointer = o.mappings(ram);
    let pieces: Vec<MappingPiece> = if flags & 0x20 != 0 {
        match bank.static_piece(pointer) {
            Some(p) => vec![p],
            None => return Ok(()),
        }
    } else {
        match bank
            .table(pointer)
            .and_then(|d| d.frames.get(o.mapping_frame(ram) as usize))
        {
            Some(f) => f.clone(),
            None => return Ok(()),
        }
    };
    if !pieces.is_empty() {
        write_pieces(ram, o, &pieces, d2, d3, flags, a2, d5);
    }
    o.set_render_flags(ram, o.render_flags(ram) | 0x80);
    Ok(())
}

fn draw_multi(
    ram: &mut Ram,
    bank: &SpriteBank,
    o: Obj,
    flags: u8,
    a2: &mut u16,
    d5: &mut u8,
) -> Result<(), crate::player::Unported> {
    let pointer = o.mappings(ram);
    let Some(data) = bank.table(pointer) else {
        return Ok(());
    };
    let cam_x = ram.u16(vars::CAMERA_X_POS) as i32;
    let cam_y = ram.u16(vars::CAMERA_Y_POS) as i32;
    let width = o.byte(ram, 0x0E) as i32;
    let mut d3 = o.x(ram) as i16 as i32 - cam_x;
    if d3 + width < 0 || d3 - width >= 0x140 {
        return Ok(());
    }
    d3 += 0x80;
    let d2 = if flags & 0x10 != 0 {
        let h = o.byte(ram, 0x14) as i32;
        let mut dy = o.y(ram) as i16 as i32 - cam_y;
        if dy + h < 0 || dy - h >= 0xE0 {
            return Ok(());
        }
        dy += 0x80;
        dy
    } else {
        let dy = (o.y(ram) as i32 - cam_y + 0x80) & 0x7FF;
        if !(0x60..0x180).contains(&dy) {
            return Ok(());
        }
        dy
    };
    let main = o.byte(ram, 0x0B) as usize;
    if main != 0 && *d5 < 0x50 {
        if let Some(pieces) = data.frames.get(main) {
            write_pieces(ram, o, pieces, d2, d3, flags, a2, d5);
        }
    }
    o.set_render_flags(ram, o.render_flags(ram) | 0x80);
    let count = o.byte(ram, 0x0F);
    for i in 0..count as u16 {
        let entry = o.address().wrapping_add(0x10 + i * 6);
        let x = ram.u16(entry).wrapping_sub(cam_x as u16).wrapping_add(0x80);
        let y = ram
            .u16(entry + 2)
            .wrapping_sub(cam_y as u16)
            .wrapping_add(0x80);
        let frame = ram.u8(entry + 5);
        if *d5 < 0x50 {
            if let Some(pieces) = data.frames.get(frame as usize) {
                write_pieces(ram, o, pieces, y as i32, x as i32, flags, a2, d5);
            }
        }
    }
    Ok(())
}

#[allow(clippy::too_many_arguments)]
fn write_pieces(
    ram: &mut Ram,
    o: Obj,
    pieces: &[MappingPiece],
    d2: i32,
    d3: i32,
    flags: u8,
    a2: &mut u16,
    d5: &mut u8,
) {
    let art = o.art_tile(ram);
    let fx = flags & 1 != 0;
    let fy = flags & 2 != 0;
    for p in pieces {
        if *d5 >= 0x50 {
            return;
        }
        let width = ((((p.size >> 2) & 3) + 1) * 8) as i32;
        let height = ((p.size & 3) + 1) as i32 * 8;
        let y = if fy {
            -(p.y as i32) - height
        } else {
            p.y as i32
        };
        let x = if fx {
            -(p.x as i32) - width
        } else {
            p.x as i32
        };
        let mut pattern = p.pattern.wrapping_add(art);
        if fx {
            pattern ^= 0x800;
        }
        if fy {
            pattern ^= 0x1000;
        }
        ram.w16(*a2, (y + d2) as u16);
        ram.w8(*a2 + 2, p.size);
        *d5 = d5.wrapping_add(1);
        ram.w8(*a2 + 3, *d5);
        ram.w16(*a2 + 4, pattern);
        let sx = (x + d3) as u16 & 0x1FF;
        ram.w16(*a2 + 6, if sx == 0 { 1 } else { sx });
        *a2 = a2.wrapping_add(8);
    }
}

fn write_static(
    ram: &mut Ram,
    pieces: &[MappingPiece],
    d2: i32,
    d3: i32,
    art: u16,
    a2: &mut u16,
    d5: &mut u8,
) {
    for p in pieces {
        ram.w16(*a2, (p.y as i32 + d2) as u16);
        ram.w8(*a2 + 2, p.size);
        *d5 = d5.wrapping_add(1);
        ram.w8(*a2 + 3, *d5);
        ram.w16(*a2 + 4, p.pattern.wrapping_add(art));
        let x = (p.x as i32 + d3) as u16 & 0x1FF;
        ram.w16(*a2 + 6, if x == 0 { 1 } else { x });
        *a2 = a2.wrapping_add(8);
        if *d5 >= 0x50 {
            return;
        }
    }
}
