//! Software stand-in for the Mega Drive VDP level output: a 320x224 CRAM-index framebuffer, planes in hardware
//! priority order, per-line horizontal scroll, full-screen vertical scroll and the sprite table.

use crate::graphics::{Graphics, RenderGeometry};
use crate::{vars, Ram};

pub const WIDTH: usize = 320;
pub const HEIGHT: usize = 224;

pub struct Vdp {
    pub frame: Vec<u8>,
    high_plane: Vec<bool>,
    pub cram: [u16; 64],
    pub cram_below: [u16; 64],
    pub split_line: i32,
    pub backdrop: u8,
}

impl Default for Vdp {
    fn default() -> Self {
        Self::new()
    }
}

impl Vdp {
    pub fn new() -> Self {
        Self {
            frame: vec![0; WIDTH * HEIGHT],
            high_plane: vec![false; WIDTH * HEIGHT],
            cram: [0; 64],
            cram_below: [0; 64],
            split_line: -1,
            backdrop: 0x20,
        }
    }

    pub fn render_level(
        &mut self,
        graphics: &Graphics,
        vram: &[u8],
        background: &[u16],
        ram: &Ram,
        draw_background: bool,
    ) {
        self.frame.fill(self.backdrop);
        self.high_plane.fill(false);
        let v_fg = ram.u16(vars::VSCROLL_FACTOR_FG) as i16 as i32;
        let v_bg = ram.u16(vars::VSCROLL_FACTOR_BG) as i16 as i32;
        let fg = |line: usize| -(ram.u16(vars::HORIZ_SCROLL_BUF + line as u16 * 4) as i16 as i32);
        let bg =
            |line: usize| -(ram.u16(vars::HORIZ_SCROLL_BUF + line as u16 * 4 + 2) as i16 as i32);
        if draw_background {
            self.draw_nametable(vram, background, &bg, v_bg, false);
        }
        self.draw_layout(&graphics.geometry, vram, &fg, v_fg, false);
        if draw_background {
            self.draw_nametable(vram, background, &bg, v_bg, true);
        }
        self.draw_layout(&graphics.geometry, vram, &fg, v_fg, true);
        self.draw_sprites(vram, ram);
    }

    pub fn render_black(&mut self) {
        self.frame.fill(0);
        self.cram = [0; 64];
    }

    fn draw_layout(
        &mut self,
        level: &RenderGeometry,
        tiles: &[u8],
        scroll_x: &impl Fn(usize) -> i32,
        scroll_y: i32,
        high: bool,
    ) {
        if level.foreground.len() < 128 * 16 || level.chunks.is_empty() || level.blocks.is_empty() {
            return;
        }
        let plane_w = 128 * 128;
        let plane_h = 16 * 128;
        for sy in 0..HEIGHT {
            let py = modulo(sy as i32 + scroll_y, plane_h);
            let sx0 = scroll_x(sy);
            let mut x = 0usize;
            while x < WIDTH {
                let px = modulo(sx0 + x as i32, plane_w);
                let run = (8 - (px & 7)).min((WIDTH - x) as i32) as usize;
                let pattern = pattern_at(level, &level.foreground, px, py);
                let tile = (pattern & 0x7FF) as usize;
                if ((pattern & 0x8000) != 0) == high && (tile + 1) * 32 <= tiles.len() {
                    let line = ((pattern >> 13) & 3) as u8;
                    let fx = pattern & 0x800 != 0;
                    let fy = pattern & 0x1000 != 0;
                    let row = if fy { 7 - (py & 7) } else { py & 7 };
                    for i in 0..run {
                        let mut col = (px + i as i32) & 7;
                        if fx {
                            col = 7 - col;
                        }
                        let b = tiles[tile * 32 + (row * 4 + col / 2) as usize];
                        let nib = if col & 1 == 0 { b >> 4 } else { b & 0x0F };
                        if nib != 0 {
                            let index = sy * WIDTH + x + i;
                            self.frame[index] = line * 16 + nib;
                            if high {
                                self.high_plane[index] = true;
                            }
                        }
                    }
                }
                x += run;
            }
        }
    }

    fn draw_nametable(
        &mut self,
        tiles: &[u8],
        cells: &[u16],
        scroll_x: &impl Fn(usize) -> i32,
        scroll_y: i32,
        high: bool,
    ) {
        if cells.len() < 64 * 32 {
            return;
        }
        for sy in 0..HEIGHT {
            let py = modulo(sy as i32 + scroll_y, 256);
            let sx0 = scroll_x(sy);
            for x in 0..WIDTH {
                let px = modulo(sx0 + x as i32, 512);
                let pattern = cells[((py >> 3) as usize) * 64 + (px >> 3) as usize];
                if ((pattern & 0x8000) != 0) != high {
                    continue;
                }
                let tile = (pattern & 0x7FF) as usize;
                if (tile + 1) * 32 > tiles.len() {
                    continue;
                }
                let row = if pattern & 0x1000 != 0 {
                    7 - (py & 7)
                } else {
                    py & 7
                };
                let col = if pattern & 0x800 != 0 {
                    7 - (px & 7)
                } else {
                    px & 7
                };
                let b = tiles[tile * 32 + (row * 4 + col / 2) as usize];
                let nib = if col & 1 == 0 { b >> 4 } else { b & 0x0F };
                if nib != 0 {
                    let index = sy * WIDTH + x;
                    self.frame[index] = (((pattern >> 13) & 3) as u8) * 16 + nib;
                    if high {
                        self.high_plane[index] = true;
                    }
                }
            }
        }
    }

    fn draw_sprites(&mut self, tiles: &[u8], ram: &Ram) {
        let mut drawn = vec![false; WIDTH * HEIGHT];
        let mut line_sprites = [0u16; HEIGHT];
        let mut line_pixels = [0u16; HEIGHT];
        let mut index = 0usize;
        for _ in 0..80 {
            let e = vars::SPRITE_TABLE + (index as u16) * 8;
            let sy = (ram.u16(e) & 0x1FF) as i32 - 128;
            let size = ram.u8(e + 2);
            let link = (ram.u8(e + 3) & 0x7F) as usize;
            let pattern = ram.u16(e + 4);
            let sx = (ram.u16(e + 6) & 0x1FF) as i32 - 128;
            let w_cells = ((size >> 2) & 3) as i32 + 1;
            let h_cells = (size & 3) as i32 + 1;
            let fx = pattern & 0x800 != 0;
            let fy = pattern & 0x1000 != 0;
            let high = pattern & 0x8000 != 0;
            let line0 = ((pattern >> 13) & 3) as u8;
            for y in 0..h_cells * 8 {
                let py = sy + y;
                if !(0..HEIGHT as i32).contains(&py) {
                    continue;
                }
                let pyi = py as usize;
                if line_sprites[pyi] >= 20 || line_pixels[pyi] >= 320 {
                    continue;
                }
                let cy = if fy { h_cells * 8 - 1 - y } else { y };
                for x in 0..w_cells * 8 {
                    let px = sx + x;
                    if line_pixels[pyi] + x as u16 >= 320 {
                        break;
                    }
                    if !(0..WIDTH as i32).contains(&px) {
                        continue;
                    }
                    let cx = if fx { w_cells * 8 - 1 - x } else { x };
                    let tile = (pattern & 0x7FF) as i32 + (cx >> 3) * h_cells + (cy >> 3);
                    if tile < 0 || (tile as usize + 1) * 32 > tiles.len() {
                        continue;
                    }
                    let b =
                        tiles[tile as usize * 32 + (cy & 7) as usize * 4 + ((cx & 7) / 2) as usize];
                    let nib = if cx & 1 == 0 { b >> 4 } else { b & 0x0F };
                    let i = pyi * WIDTH + px as usize;
                    if nib == 0 || drawn[i] {
                        continue;
                    }
                    drawn[i] = true;
                    if !high && self.high_plane[i] {
                        continue;
                    }
                    self.frame[i] = line0 * 16 + nib;
                }
            }
            for y in 0..h_cells * 8 {
                let py = sy + y;
                if (0..HEIGHT as i32).contains(&py) {
                    line_sprites[py as usize] += 1;
                    line_pixels[py as usize] += (w_cells * 8) as u16;
                }
            }
            if link == 0 {
                break;
            }
            index = link;
        }
    }

    /// The frame as RGBA bytes in the C# `ToRgba` memory order (R, G, B, A per pixel).
    pub fn to_rgba(&self) -> Vec<u8> {
        let mut rgba = vec![0u8; WIDTH * HEIGHT * 4];
        let mut above = [0u32; 64];
        let mut below = [0u32; 64];
        for i in 0..64 {
            above[i] = rgba_word(self.cram[i]);
            below[i] = rgba_word(self.cram_below[i]);
        }
        for y in 0..HEIGHT {
            let pal = if self.split_line >= 0 && y as i32 >= self.split_line {
                &below
            } else {
                &above
            };
            for x in 0..WIDTH {
                let c = pal[self.frame[y * WIDTH + x] as usize];
                let i = (y * WIDTH + x) * 4;
                rgba[i] = c as u8;
                rgba[i + 1] = (c >> 8) as u8;
                rgba[i + 2] = (c >> 16) as u8;
                rgba[i + 3] = (c >> 24) as u8;
            }
        }
        rgba
    }
}

fn rgba_word(c: u16) -> u32 {
    let r = ((c >> 1) & 7) as u32;
    let g = ((c >> 5) & 7) as u32;
    let b = ((c >> 9) & 7) as u32;
    let r = (r * 255 + 3) / 7;
    let g = (g * 255 + 3) / 7;
    let b = (b * 255 + 3) / 7;
    0xFF00_0000 | (b << 16) | (g << 8) | r
}

pub fn pattern_at(level: &RenderGeometry, plane: &[u8], px: i32, py: i32) -> u16 {
    if plane.len() < 128 * 16 {
        return 0;
    }
    let chunk = plane[((py >> 7) * 128 + (px >> 7)) as usize] as usize;
    if chunk * 64 >= level.chunks.len() {
        return 0;
    }
    let cw = level.chunks[chunk * 64 + (((py >> 4) & 7) * 8 + ((px >> 4) & 7)) as usize];
    let block = (cw & 0x3FF) as usize;
    if block * 4 >= level.blocks.len() {
        return 0;
    }
    let bfx = cw & 0x400 != 0;
    let bfy = cw & 0x800 != 0;
    let mut qx = (px >> 3) & 1;
    let mut qy = (py >> 3) & 1;
    if bfx {
        qx ^= 1;
    }
    if bfy {
        qy ^= 1;
    }
    let mut pw = level.blocks[block * 4 + (qy * 2 + qx) as usize];
    if bfx {
        pw ^= 0x800;
    }
    if bfy {
        pw ^= 0x1000;
    }
    pw
}

fn modulo(a: i32, m: i32) -> i32 {
    ((a % m) + m) % m
}
