//! Plane B's 64x32 nametable (`BackgroundPlane`): the initial draw and `LoadTilesAsYouMove`'s background rows and
//! columns, including Marble Zone's shared layer flags.

use crate::graphics::RenderGeometry;
use crate::vdp::pattern_at;
use crate::{vars, Ram};

pub const COLUMNS: usize = 64;
pub const ROWS: usize = 32;

pub struct BackgroundPlane {
    pub cells: Vec<u16>,
}

impl Default for BackgroundPlane {
    fn default() -> Self {
        Self::new()
    }
}

impl BackgroundPlane {
    pub fn new() -> Self {
        Self {
            cells: vec![0; COLUMNS * ROWS],
        }
    }

    fn x(ram: &Ram, layer: u16) -> i32 {
        ram.u16(layer) as i16 as i32
    }

    fn y(ram: &Ram, layer: u16) -> i32 {
        ram.u16(layer + 4) as i16 as i32
    }

    pub fn draw_initial(&mut self, ram: &Ram, geometry: &RenderGeometry) {
        let layer = vars::CAMERA_BG_Y_POS - 4;
        for row in 0..16 {
            self.draw_row(
                ram,
                geometry,
                Self::x(ram, layer),
                Self::y(ram, layer) - 16 + row * 16,
                32,
            );
        }
    }

    pub fn update(&mut self, ram: &mut Ram, geometry: &RenderGeometry) {
        let zone = ram.u8(vars::CURRENT_ZONE_AND_ACT);
        self.update_layer1(ram, geometry);
        if zone == 0x1B {
            return;
        }
        self.band_columns(
            ram,
            geometry,
            vars::CAMERA_BG2_X_POS,
            vars::SCROLL_FLAGS_BG2,
            0x70,
        );
        if zone == 0x13 || zone == 0x14 {
            self.marble_layers(ram, geometry);
            return;
        }
        self.band_columns(
            ram,
            geometry,
            vars::CAMERA_BG3_X_POS,
            vars::SCROLL_FLAGS_BG3,
            0x40,
        );
    }

    fn marble_rows() -> [u8; 0x80] {
        let mut t = [0u8; 0x80];
        for (i, v) in t.iter_mut().enumerate() {
            *v = if i < 6 {
                0
            } else if i < 8 {
                6
            } else if i < 17 {
                4
            } else if i < 97 {
                2
            } else {
                0
            };
        }
        t
    }

    fn layer_for(bit: u8) -> u16 {
        match bit {
            4 => vars::CAMERA_BG2_X_POS,
            6 => vars::CAMERA_BG3_X_POS,
            _ => vars::CAMERA_BG_Y_POS - 4,
        }
    }

    fn marble_layers(&mut self, ram: &mut Ram, geometry: &RenderGeometry) {
        let rows = Self::marble_rows();
        let mut f = ram.u8(vars::SCROLL_FLAGS_BG3);
        if f == 0 {
            return;
        }
        let layer = vars::CAMERA_BG_Y_POS - 4;
        let bg_y = Self::y(ram, layer);
        let mut d4 = -16i32;
        let mut row = false;
        if f & 1 != 0 {
            f &= !1;
            row = true;
        } else if f & 2 != 0 {
            f &= !2;
            d4 = 0xE0;
            row = true;
        }
        if row {
            let idx = ((((bg_y - 0x200 + d4) & 0x7F0) >> 4) + 1) & 0x7F;
            let bit = rows[idx as usize];
            let l = Self::layer_for(bit);
            self.draw_row(
                ram,
                geometry,
                Self::x(ram, l) - 16,
                Self::y(ram, l) + d4,
                22,
            );
        }
        ram.w8(vars::SCROLL_FLAGS_BG3, f);
        if f == 0 {
            return;
        }
        let mut d5 = -16i32;
        let right = f & 0xA8;
        if right != 0 {
            f = right >> 1;
            d5 = 0x140;
        }
        let start = ((bg_y - 0x200) & 0x7F0) >> 4;
        for r in 0..16 {
            let bit = rows[((start + r) & 0x7F) as usize];
            if (f >> bit) & 1 != 0 {
                let l = Self::layer_for(bit);
                self.draw_block(
                    ram,
                    geometry,
                    Self::x(ram, l) + d5,
                    Self::y(ram, l) - 16 + r * 16,
                );
            }
        }
        ram.w8(vars::SCROLL_FLAGS_BG3, 0);
    }

    fn update_layer1(&mut self, ram: &mut Ram, geometry: &RenderGeometry) {
        let mut f = ram.u8(vars::SCROLL_FLAGS_BG);
        if f == 0 {
            return;
        }
        let layer = vars::CAMERA_BG_Y_POS - 4;
        let x = Self::x(ram, layer);
        let y = Self::y(ram, layer);
        if f & 0x01 != 0 {
            self.draw_row(ram, geometry, x - 16, y - 16, 22);
        }
        if f & 0x02 != 0 {
            self.draw_row(ram, geometry, x - 16, y + 0xE0, 22);
        }
        if f & 0x04 != 0 {
            self.draw_column(ram, geometry, x - 16, y - 16, 16);
        }
        if f & 0x08 != 0 {
            self.draw_column(ram, geometry, x + 0x140, y - 16, 16);
        }
        if f & 0x10 != 0 {
            self.draw_row(ram, geometry, 0, y - 16, 32);
        }
        if f & 0x20 != 0 {
            self.draw_row(ram, geometry, 0, y + 0xE0, 32);
        }
        if f & 0x40 != 0 {
            self.draw_row(ram, geometry, x - 16, y - 16, 32);
        }
        if f & 0x80 != 0 {
            self.draw_row(ram, geometry, x - 16, y + 0xE0, 32);
        }
        f = 0;
        ram.w8(vars::SCROLL_FLAGS_BG, f);
    }

    fn band_columns(
        &mut self,
        ram: &mut Ram,
        geometry: &RenderGeometry,
        layer: u16,
        flags: u16,
        band_y: i32,
    ) {
        let mut f = ram.u8(flags);
        if f == 0 {
            return;
        }
        let x = Self::x(ram, layer);
        let y = Self::y(ram, layer) + band_y;
        if f & 1 != 0 {
            self.draw_column(ram, geometry, x - 16, y, 3);
        }
        if f & 2 != 0 {
            self.draw_column(ram, geometry, x + 0x140, y, 3);
        }
        f &= !3;
        ram.w8(flags, f);
    }

    fn draw_row(&mut self, ram: &Ram, geometry: &RenderGeometry, x: i32, y: i32, blocks: i32) {
        for i in 0..blocks {
            self.draw_block(ram, geometry, x + i * 16, y);
        }
    }

    fn draw_column(&mut self, ram: &Ram, geometry: &RenderGeometry, x: i32, y: i32, blocks: i32) {
        for i in 0..blocks {
            self.draw_block(ram, geometry, x, y + i * 16);
        }
    }

    fn draw_block(&mut self, _ram: &Ram, geometry: &RenderGeometry, x: i32, y: i32) {
        let wx = ((x & 0xFFFF) % (128 * 128)) & !15;
        let wy = (((y * 2) & 0xF00) >> 1) | (y & 0x70);
        let col = ((x & 0x1F0) >> 3) as usize;
        let row = ((y & 0xF0) >> 3) as usize;
        for dy in 0..2usize {
            for dx in 0..2usize {
                if row + dy < ROWS && col + dx < COLUMNS {
                    self.cells[(row + dy) * COLUMNS + col + dx] = pattern_at(
                        geometry,
                        &geometry.background,
                        wx + dx as i32 * 8,
                        wy + dy as i32 * 8,
                    );
                }
            }
        }
    }
}
