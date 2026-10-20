#![allow(dead_code, unused_mut)]
#![allow(clippy::too_many_arguments)]
use super::assets::FrontendAssets;
use crate::graphics::SpriteData;
use crate::{vars, Ram};
use std::collections::HashMap;

pub struct NesPpu {
    pub nametables: [u8; 0x1000],
    pub palette: [u8; 32],
    pub horizontal_mirroring: bool,
}
impl Default for NesPpu {
    fn default() -> Self {
        Self {
            nametables: [0; 0x1000],
            palette: [0; 32],
            horizontal_mirroring: true,
        }
    }
}
impl NesPpu {
    fn map(&self, a: usize) -> usize {
        let a = a & 0xFFF;
        if self.horizontal_mirroring {
            a & !0x400
        } else {
            a & !0x800
        }
    }
    pub fn write(&mut self, address: usize, value: u8) {
        let address = address & 0x3FFF;
        if address >= 0x3F00 {
            let mut i = address & 0x1F;
            if i & 3 == 0 {
                i &= 0x0F;
            }
            self.palette[i] = value & 0x3F;
        } else if address >= 0x2000 {
            let i = self.map(address - 0x2000);
            self.nametables[i] = value;
        }
    }
    pub fn read(&self, address: usize) -> u8 {
        if address & 0x3FFF >= 0x3F00 {
            self.palette[address & 0x1F]
        } else {
            self.nametables[self.map((address & 0x3FFF).wrapping_sub(0x2000))]
        }
    }
    pub fn clear(&mut self, tile: u8) {
        for nt in 0..4 {
            for i in 0..0x400 {
                let a = self.map(nt * 0x400 + i);
                self.nametables[a] = if i < 0x3C0 { tile } else { 0 };
            }
        }
    }
    pub fn tile(&self, nt: usize, col: usize, row: usize) -> u8 {
        self.nametables[self.map(nt * 0x400 + (row & 31) * 32 + (col & 31))]
    }
    pub fn cell_palette(&self, nt: usize, col: usize, row: usize) -> usize {
        let a = self.nametables[self.map(nt * 0x400 + 0x3C0 + (row >> 2) * 8 + (col >> 2))];
        ((a >> (((row >> 1) & 1) * 4 + ((col >> 1) & 1) * 2)) & 3) as usize
    }
    pub fn set_cell_palette(&mut self, nt: usize, col: usize, row: usize, pal: usize) {
        let i = self.map(nt * 0x400 + 0x3C0 + (row >> 2) * 8 + (col >> 2));
        let shift = ((row >> 1) & 1) * 4 + ((col >> 1) & 1) * 2;
        self.nametables[i] = (self.nametables[i] & !(3 << shift)) | ((pal as u8 & 3) << shift);
    }
    pub fn run_stream<F: Fn(usize) -> u8>(&mut self, read: F, mut p: usize) {
        loop {
            let hi = read(p);
            if hi == 0 {
                return;
            }
            let mut address = ((hi as usize) << 8) | read(p + 1) as usize;
            let c = read(p + 2);
            p += 3;
            let vertical = c & 0x80 != 0;
            let repeat = c & 0x40 != 0;
            let mut n = (c & 0x3F) as usize;
            if n == 0 {
                n = 64;
            }
            for i in 0..n {
                self.write(address, read(if repeat { p } else { p + i }));
                address = address.wrapping_add(if vertical { 32 } else { 1 });
            }
            p += if repeat { 1 } else { n };
        }
    }
}

pub struct NesPresenter<'a> {
    pub vram: &'a mut Vec<u8>,
    pub ram: &'a mut Ram,
    pub assets: &'a FrontendAssets,
    pub bg_banks: [u8; 4],
    pub sprite_banks: [u8; 4],
    pub overrides: HashMap<(u8, usize), [u8; 64]>,
    cache: HashMap<(i16, usize, usize), usize>,
    next: usize,
}
impl<'a> NesPresenter<'a> {
    pub fn new(vram: &'a mut Vec<u8>, ram: &'a mut Ram, assets: &'a FrontendAssets) -> Self {
        Self {
            vram,
            ram,
            assets,
            bg_banks: [0; 4],
            sprite_banks: [0; 4],
            overrides: HashMap::new(),
            cache: HashMap::new(),
            next: 1,
        }
    }
    pub fn bg_tile(&mut self, tile: u8, pal: usize) -> usize {
        let bank = self.bg_banks[(tile >> 6) as usize];
        self.tile(bank, (tile & 63) as usize, pal)
    }
    pub fn tile(&mut self, bank: u8, tile: usize, pal: usize) -> usize {
        let key = (bank as i16, tile, pal);
        if let Some(t) = self.cache.get(&key) {
            return *t;
        }
        if self.next > 0x2FF {
            self.cache.clear();
            self.next = 1;
        }
        let t = self.next;
        self.next += 1;
        let md = convert(
            self.assets,
            bank,
            tile,
            pal,
            self.overrides.get(&(bank, tile)),
        );
        if t * 32 + 32 <= self.vram.len() {
            self.vram[t * 32..t * 32 + 32].copy_from_slice(&md);
        }
        self.cache.insert(key, t);
        t
    }
    pub fn sprite_tile(&mut self, tile: u8, pal: usize) -> usize {
        let top = (tile & 0xFE) as usize;
        let bank = self.sprite_banks[(top >> 6) & 3];
        let local = top & 63;
        let key = (-1 - bank as i16, local, pal);
        if let Some(t) = self.cache.get(&key) {
            return *t;
        }
        if self.next + 1 > 0x2FF {
            self.cache.clear();
            self.next = 1;
        }
        let t = self.next;
        self.next += 2;
        for k in 0..2 {
            let md = convert(
                self.assets,
                bank,
                local + k,
                pal,
                self.overrides.get(&(bank, local + k)),
            );
            self.vram[(t + k) * 32..(t + k) * 32 + 32].copy_from_slice(&md);
        }
        self.cache.insert(key, t);
        t
    }
    pub fn cell(&mut self, plane: usize, col: usize, row: usize, tile: u8, pal: usize, high: bool) {
        let t = self.bg_tile(tile, pal);
        self.word(
            plane,
            col,
            row,
            (t as u16) | (2 << 13) | if high { 0x8000 } else { 0 },
        );
    }
    pub fn word(&mut self, plane: usize, col: usize, row: usize, w: u16) {
        let a = plane + (row & 31) * 128 + (col & 63) * 2;
        self.vram[a] = (w >> 8) as u8;
        self.vram[a + 1] = w as u8;
    }
    pub fn row<F: Fn(i32) -> Option<usize>, H: Fn(usize) -> bool>(
        &mut self,
        ppu: &NesPpu,
        nt: usize,
        nt_row: usize,
        plane: usize,
        plane_row: usize,
        margin: Option<&F>,
        high: Option<&H>,
    ) {
        for c in -4i32..36 {
            let src = if (0..32).contains(&c) {
                Some(c as usize)
            } else {
                margin.and_then(|m| m(c))
            };
            if let Some(s) = src {
                let tile = ppu.tile(nt, s, nt_row);
                let pal = ppu.cell_palette(nt, s, nt_row);
                self.cell(
                    plane,
                    (c + 4) as usize,
                    plane_row,
                    tile,
                    pal,
                    high.map(|h| h(s)).unwrap_or(false),
                );
            } else {
                self.word(plane, (c + 4) as usize, plane_row, 0);
            }
        }
    }
    pub fn palettes(&mut self, ppu: &NesPpu, palette: u16) {
        for i in 0..16 {
            self.ram
                .w16(palette + 0x40 + i as u16 * 2, to_md(ppu.palette[i]));
            self.ram.w16(
                palette + 0x60 + i as u16 * 2,
                to_md(ppu.palette[if i & 3 == 0 { 0 } else { 16 + i }]),
            );
        }
    }
}

fn convert(
    assets: &FrontendAssets,
    bank: u8,
    tile: usize,
    pal: usize,
    over: Option<&[u8; 64]>,
) -> [u8; 32] {
    let mut md = [0u8; 32];
    if let Some(px) = over {
        for i in 0..64 {
            let c = if px[i] == 0 { 0 } else { pal as u8 * 4 + px[i] };
            md[i / 2] |= if i & 1 == 0 { c << 4 } else { c };
        }
        return md;
    }
    let nes = assets.chr(bank, tile);
    for y in 0..8 {
        for x in 0..8 {
            let v = ((nes[y] >> (7 - x)) & 1) | (((nes[y + 8] >> (7 - x)) & 1) << 1);
            let c = if v == 0 { 0 } else { pal as u8 * 4 + v };
            md[y * 4 + x / 2] |= if x & 1 == 0 { c << 4 } else { c };
        }
    }
    md
}

pub fn to_md(nes: u8) -> u16 {
    const RGB: [u32; 64] = [
        0x545454, 0x001E74, 0x081090, 0x300088, 0x440064, 0x5C0030, 0x540400, 0x3C1800, 0x202A00,
        0x083A00, 0x004000, 0x003C00, 0x00323C, 0, 0, 0, 0x989698, 0x084CC4, 0x3032EC, 0x5C1EE4,
        0x8814B0, 0xA01464, 0x982220, 0x783C00, 0x545A00, 0x287200, 0x087C00, 0x007628, 0x006678,
        0, 0, 0, 0xECEEEC, 0x4C9AEC, 0x787CEC, 0xB062EC, 0xE454EC, 0xEC58B4, 0xEC6A64, 0xD48820,
        0xA0AA00, 0x74C400, 0x4CD020, 0x38CC6C, 0x38B4CC, 0x3C3C3C, 0, 0, 0xECEEEC, 0xA8CCEC,
        0xBCBCEC, 0xD4B2EC, 0xECAEEC, 0xECAED4, 0xECB4B0, 0xE4C490, 0xCCD278, 0xB4DE78, 0xA8E290,
        0x98E2B4, 0xA0D6E4, 0xA0A2A0, 0, 0,
    ];
    let c = RGB[(nes & 0x3F) as usize];
    let q = |v: u32| ((v * 7 + 127) / 255) as u16;
    (q((c >> 16) & 255) << 1) | (q((c >> 8) & 255) << 5) | (q(c & 255) << 9)
}

pub struct SpriteKit<'a> {
    pub ram: &'a mut Ram,
    pub vram: &'a mut Vec<u8>,
    count: usize,
}
impl<'a> SpriteKit<'a> {
    pub fn new(ram: &'a mut Ram, vram: &'a mut Vec<u8>) -> Self {
        Self {
            ram,
            vram,
            count: 0,
        }
    }
    pub fn begin(&mut self) {
        self.count = 0;
    }
    pub fn end(&mut self) {
        if self.count == 0 {
            self.piece(-64, -64, 0, 0);
        }
        let e = vars::SPRITE_TABLE + ((self.count - 1) as u16) * 8;
        self.ram.w8(e + 3, 0);
    }
    pub fn piece(&mut self, x: i32, y: i32, size: u8, pattern: u16) {
        if self.count >= 80 {
            return;
        }
        let e = vars::SPRITE_TABLE + (self.count as u16) * 8;
        self.ram.w16(e, ((y + 128) & 0x3FF) as u16);
        self.ram.w8(e + 2, size);
        self.ram.w8(e + 3, (self.count + 1) as u8);
        self.ram.w16(e + 4, pattern);
        let sx = ((x + 128) & 0x1FF) as u16;
        self.ram.w16(e + 6, if sx == 0 { 1 } else { sx });
        self.count += 1;
    }
    pub fn frame(
        &mut self,
        data: &SpriteData,
        frame: usize,
        x: i32,
        y: i32,
        art: u16,
        fx: bool,
        fy: bool,
    ) {
        let Some(ps) = data.frames.get(frame) else {
            return;
        };
        for p in ps {
            let w = (((p.size >> 2) & 3) + 1) as i32 * 8;
            let h = ((p.size & 3) + 1) as i32 * 8;
            let px = if fx { -p.x as i32 - w } else { p.x as i32 };
            let py = if fy { -p.y as i32 - h } else { p.y as i32 };
            let mut pat = p.pattern.wrapping_add(art)
                ^ if fx { 0x800 } else { 0 }
                ^ if fy { 0x1000 } else { 0 };
            self.piece(x + px, y + py, p.size, pat);
        }
    }
    pub fn load_dplc(&mut self, data: &SpriteData, frame: usize, vram_tile: usize) {
        let Some(dplc) = data.dplc.as_ref().and_then(|d| d.get(frame)) else {
            return;
        };
        let mut t = vram_tile;
        for (first, count) in dplc {
            let n = (count * 32).min(data.art.len().saturating_sub(first * 32));
            if t * 32 + n <= self.vram.len() {
                self.vram[t * 32..t * 32 + n]
                    .copy_from_slice(&data.art[first * 32..first * 32 + n]);
            }
            t += count;
        }
    }
}
