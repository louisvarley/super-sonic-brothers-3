//! The hack's lava line (0x164470/0x16461A): per-act line height and palette split. The renderer consumes the
//! split in M5; this module keeps the RAM side exact.

use crate::{vars, Ram};

pub fn init(ram: &mut Ram, zone: u8, colours: &[u8]) {
    ram.w8(vars::LAVA_LINE_ENABLED, 0);
    if ram.u8(vars::HACK_ZONE_FLAG) == 0 || ram.u8(vars::WATER_FLAG) != 0 {
        return;
    }
    ram.w8(vars::LAVA_LINE_ENABLED, 1);
    ram.w8(vars::LAVA_LINE_PALETTE_MODE, 0);
    ram.w16(vars::LAVA_LINE_HEIGHT, 0xFFFF);
    ram.w16(vars::LAVA_LINE_EXTRA, 0);
    let set = if zone < 0x13 {
        0
    } else if zone < 0x19 {
        1
    } else if zone < 0x1B {
        2
    } else {
        1
    };
    for i in 0..6u16 {
        ram.w8(
            vars::LAVA_LINE_BELOW + i,
            colours
                .get((set * 6) as usize + i as usize)
                .copied()
                .unwrap_or(0),
        );
    }
    ram.w8(vars::LAVA_LINE_PALETTE_STATE, 0);
}

pub fn step(ram: &mut Ram) {
    if ram.u8(vars::LAVA_LINE_ENABLED) == 0 {
        return;
    }
    let (height, art) = zone(ram);
    ram.w16(vars::LAVA_LINE_HEIGHT, height as u16);
    if ram.u8(vars::CURRENT_ZONE_AND_ACT) == 0x16 {
        ram.w8(vars::LAVA_LINE_PALETTE_STATE, art as u8);
    } else if matches!(ram.u8(vars::CURRENT_ZONE_AND_ACT), 0x11 | 0x12) {
        // The art swap is a VRAM effect (M5); the palette state is RAM.
        ram.w8(vars::LAVA_LINE_PALETTE_STATE, art as u8);
    }
    if ram.u8(vars::LAVA_LINE_PALETTE_MODE) != 0 {
        return;
    }
    ram.w8(vars::LAVA_LINE_MODE, 1);
    let height = ram.u16(vars::LAVA_LINE_HEIGHT);
    let cam = ram.u16(vars::CAMERA_Y_POS);
    if height < cam {
        ram.w8(vars::LAVA_LINE_SCANLINE, 0xDF);
        ram.w8(vars::LAVA_LINE_MODE, 2);
        return;
    }
    let mut d0 = height.wrapping_sub(cam);
    if d0 >= 0xDF {
        d0 = 0xDF;
        ram.w8(vars::LAVA_LINE_MODE, 0);
    }
    ram.w8(vars::LAVA_LINE_SCANLINE, d0 as u8);
}

fn zone(ram: &Ram) -> (i32, i32) {
    let cam_x = ram.u16(vars::CAMERA_X_POS) as i32;
    let cam_y = ram.u16(vars::CAMERA_Y_POS) as i32;
    match ram.u16(vars::CURRENT_ZONE_AND_ACT) {
        0x1100 => stretch(cam_x, 0x1CC0, 0x1E80, 0x310),
        0x1101 => stretch(cam_x, 0x15C0, 0x1780, 0x220),
        0x1200 => stretch(cam_x, 0x6C0, 0x840, 0x320),
        0x1201 => (0xFFFF, 0),
        0x1300 => {
            if cam_x < 0x440 {
                (0x244, 0)
            } else if cam_x < 0x900 {
                (0x184, 0)
            } else if cam_x < 0x1340 {
                (0xFFFF, 0)
            } else if cam_x < 0x1700 {
                (0x260, 0)
            } else {
                (0, 0)
            }
        }
        0x1301 => mz2(cam_x, cam_y),
        0x1400 => mz3(cam_x, cam_y),
        0x1401 => (0xFFFF, 0),
        z if crate::camera::is_easter_egg((z >> 8) as u8) => (0xFFFF, 0),
        _ => (0xFFFF, 0),
    }
}

fn stretch(cam_x: i32, from: i32, to: i32, height: i32) -> (i32, i32) {
    if cam_x < from {
        (0xFFFF, 0)
    } else if cam_x < to {
        (height, 1)
    } else {
        (0xFFFF, 0)
    }
}

fn mz2(cam_x: i32, cam_y: i32) -> (i32, i32) {
    if cam_x >= 0x1200 {
        return if cam_y >= 0x4A0 { (0x540, 0) } else { (0, 0) };
    }
    if cam_x >= 0xE00 {
        return (0x18C, 0);
    }
    if cam_x < 0x340 {
        return (0x274, 0);
    }
    if cam_x < 0x500 && cam_y < 0x3E0 {
        return (0x3C0, 0);
    }
    if cam_x >= 0xA80 {
        return (0x18C, 0);
    }
    if cam_y >= 0x3E0 {
        return (0xFFFF, 0);
    }
    if cam_x >= 0x700 {
        return (0x18C, 0);
    }
    (0xFFFF, 0)
}

fn mz3(cam_x: i32, cam_y: i32) -> (i32, i32) {
    if cam_x >= 0x1600 {
        return (0xFFFF, 0);
    }
    if cam_x >= 0x1200 {
        if cam_y < 0x3A0 {
            return (0xFFFF, 0);
        }
        return if cam_y < 0x560 {
            (0x4E0, 0)
        } else {
            (0xFFFF, 0)
        };
    }
    if cam_x >= 0xC00 {
        return (0x600, 0);
    }
    if cam_y < 0x360 {
        (0x174, 0)
    } else {
        (0x520, 0)
    }
}

/// The renderer's split for a frame: the scanline (exclusive) and the CRAM below it.
pub fn split(ram: &Ram, cram: &[u16; 64]) -> Option<(usize, [u16; 64])> {
    if ram.u8(vars::LAVA_LINE_ENABLED) == 0 || ram.u8(vars::LAVA_LINE_MODE) == 0 {
        return None;
    }
    let mut below = *cram;
    for i in 0..3 {
        below[18 + i] = ram.u16(vars::LAVA_LINE_BELOW + i as u16 * 2);
    }
    if ram.u16(vars::LAVA_LINE_EXTRA) != 0 {
        below[30] = ram.u16(vars::LAVA_LINE_EXTRA);
    }
    Some((ram.u8(vars::LAVA_LINE_SCANLINE) as usize + 1, below))
}
