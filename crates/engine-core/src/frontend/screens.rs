#![allow(dead_code, unused_variables, unused_mut)]
#![allow(
    clippy::too_many_arguments,
    clippy::field_reassign_with_default,
    clippy::collapsible_if,
    clippy::let_unit_value,
    clippy::needless_range_loop
)]
use super::assets::FrontendAssets;
use super::nes::{NesPpu, NesPresenter, SpriteKit};
use crate::vdp::Vdp;
use crate::{vars, Ram};
use sha2::{Digest, Sha256};

pub const PLANE_A: usize = 0xC000;
pub const PLANE_B: usize = 0xE000;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Card {
    Ring,
    Shield,
    Invincibility,
}

#[derive(Clone, Debug)]
pub struct MapProgress {
    pub lives: i32,
    pub score: i32,
    pub rings: i32,
    pub world: i32,
    pub map_screen: i32,
    pub cleared: Vec<i32>,
    pub cards: Vec<Card>,
    pub position: i32,
    pub last_move: i32,
}
impl Default for MapProgress {
    fn default() -> Self {
        Self {
            lives: 4,
            score: 0,
            rings: 0,
            world: 1,
            map_screen: 0,
            cleared: Vec::new(),
            cards: Vec::new(),
            position: 2 * 16 + 2,
            last_move: 0,
        }
    }
}
impl MapProgress {
    pub fn is_cleared(&self, tile: i32) -> bool {
        self.cleared.contains(&tile)
    }
}

pub struct FrontContext {
    pub assets: FrontendAssets,
    pub ram: Ram,
    pub vram: Vec<u8>,
    pub palette_target: [u16; 64],
    pub palette_normal: [u16; 64],
}

impl FrontContext {
    pub fn new(assets: FrontendAssets) -> Self {
        Self {
            assets,
            ram: Ram::new(),
            vram: vec![0; 0x10000],
            palette_target: [0; 64],
            palette_normal: [0; 64],
        }
    }
    pub fn load_palette_03(&mut self) {
        for (i, w) in self.assets.palette_03.iter().take(64).enumerate() {
            self.palette_target[i] = *w;
        }
    }
    pub fn write_palette_ram(&mut self) {
        for i in 0..64 {
            self.ram
                .w16(vars::NORMAL_PALETTE + i as u16 * 2, self.palette_normal[i]);
        }
    }
    pub fn begin_fade_in(&mut self) {
        self.palette_normal = [0; 64];
        for i in 0..64 {
            self.ram.w16(vars::NORMAL_PALETTE + i as u16 * 2, 0);
        }
        self.ram.w8(vars::PAL_FADE_START, 0x3F);
    }
    pub fn fade_in_step(&mut self) {
        for i in 0..64 {
            let t = self.palette_target[i];
            let c = self.palette_normal[i];
            if c == t {
                continue;
            }
            let n = if c + 0x200 <= t {
                c + 0x200
            } else if c + 0x20 <= t {
                c + 0x20
            } else {
                c + 2
            };
            self.palette_normal[i] = n;
            self.ram.w16(vars::NORMAL_PALETTE + i as u16 * 2, n);
        }
    }
    pub fn fade_out_step(&mut self) {
        for i in 0..64 {
            let mut c = self.palette_normal[i];
            if c == 0 {
                continue;
            }
            c = if (c & 0xE) != 0 {
                c - 2
            } else if (c & 0xE0) != 0 {
                c - 0x20
            } else if (c & 0xE00) != 0 {
                c - 0x200
            } else {
                0
            };
            self.palette_normal[i] = c;
            self.ram.w16(vars::NORMAL_PALETTE + i as u16 * 2, c);
        }
    }
    pub fn clear_screen(&mut self) {
        self.vram[PLANE_A..PLANE_A + 0x4000].fill(0);
        for i in 0..0x280 {
            self.ram.w8(vars::SPRITE_TABLE + i, 0);
        }
        for i in 0..0x400 {
            self.ram.w8(vars::HORIZ_SCROLL_BUF + i, 0);
        }
        self.ram.w32(vars::VSCROLL_FACTOR_FG, 0);
    }
    pub fn load_art(&mut self, screen: &str) {
        for e in &self.assets.art {
            if e.screen == screen {
                self.vram[e.vram_tile * 32..e.vram_tile * 32 + e.tiles.len()]
                    .copy_from_slice(&e.tiles);
            }
        }
    }
    pub fn palette_word(&mut self, _id: usize) {
        let start = 0usize;
        for (i, w) in self
            .assets
            .palette_03
            .iter()
            .skip(start)
            .take(64)
            .enumerate()
        {
            self.palette_normal[i] = *w;
        }
    }
    pub fn render_hash(
        &mut self,
        vdp: &mut Vdp,
        line_a: &dyn Fn(usize) -> Option<i32>,
        line_b: &dyn Fn(usize) -> Option<i32>,
    ) -> u64 {
        for i in 0..64 {
            vdp.cram[i] = self.ram.u16(vars::NORMAL_PALETTE + i as u16 * 2);
        }
        vdp.backdrop = 0x20;
        vdp.split_line = -1;
        vdp.render_screen(&self.vram, &self.ram, PLANE_A, PLANE_B, line_a, line_b);
        let rgba = vdp.to_rgba();
        let d = Sha256::digest(&rgba);
        u64::from_le_bytes(d[..8].try_into().unwrap())
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Start {
    Title,
    Map,
    Match,
}

pub struct GameFlow {
    pub assets: FrontendAssets,
    pub progress: MapProgress,
    pub screen: ScreenState,
    pub vdp: Vdp,
}
pub enum ScreenState {
    Title(TitleScreen),
    Map(MapScreen),
    Match(MatchScreen),
}

impl GameFlow {
    pub fn new(assets: FrontendAssets, start: Start) -> Self {
        let mut f = Self {
            assets: assets.clone(),
            progress: MapProgress::default(),
            screen: ScreenState::Map(MapScreen::new(&assets, MapProgress::default(), false)),
            vdp: Vdp::new(),
        };
        f.screen = match start {
            Start::Title => ScreenState::Title(TitleScreen::new(&assets)),
            Start::Map => ScreenState::Map(MapScreen::new(&assets, MapProgress::default(), true)),
            Start::Match => {
                let mut p = MapProgress::default();
                p.position = 4 * 16 + 8;
                ScreenState::Match(MatchScreen::new(&assets, p))
            }
        };
        f
    }
    pub fn step(&mut self, buttons: u8) {
        match &mut self.screen {
            ScreenState::Title(s) => s.step(&self.assets, buttons),
            ScreenState::Map(s) => s.step(&self.assets, buttons),
            ScreenState::Match(s) => s.step(&self.assets, buttons),
        }
    }
    pub fn render_hash(&mut self) -> u64 {
        match &mut self.screen {
            ScreenState::Title(s) => s.render_hash(&mut self.vdp, &self.assets),
            ScreenState::Map(s) => s.render_hash(&mut self.vdp, &self.assets),
            ScreenState::Match(s) => s.render_hash(&mut self.vdp, &self.assets),
        }
    }
}

// Placeholder screen definitions filled below.
pub struct TitleScreen {
    pub ctx: FrontContext,
    phase: u8,
    frames: u32,
}
impl TitleScreen {
    pub fn new(a: &FrontendAssets) -> Self {
        Self {
            ctx: FrontContext::new(a.clone()),
            phase: 0,
            frames: 0,
        }
    }
    pub fn step(&mut self, _a: &FrontendAssets, _b: u8) {
        self.frames += 1;
    }
    pub fn render_hash(&mut self, v: &mut Vdp, _a: &FrontendAssets) -> u64 {
        self.ctx.render_hash(v, &|_| Some(0), &|_| Some(0))
    }
}
pub struct MatchScreen {
    pub ctx: FrontContext,
    progress: MapProgress,
    ppu: NesPpu,
    frames: i32,
    phase: u8,
    item: usize,
    text_pos: usize,
    text_v: usize,
    pause: i32,
    fade_left: i32,
    room: bool,
    table: std::collections::HashSet<(usize, usize)>,
}
impl MatchScreen {
    pub fn new(a: &FrontendAssets, progress: MapProgress) -> Self {
        let mut s = Self {
            ctx: FrontContext::new(a.clone()),
            progress,
            ppu: NesPpu {
                horizontal_mirroring: false,
                ..Default::default()
            },
            frames: 0,
            phase: 0,
            item: 8,
            text_pos: 0,
            text_v: 0x28C5,
            pause: 4,
            fade_left: 0,
            room: true,
            table: std::collections::HashSet::new(),
        };
        s.init_room();
        s
    }
    fn init_room(&mut self) {
        self.ctx.clear_screen();
        self.ctx.load_palette_03();
        let mon = self.ctx.assets.monitor_icon_art.clone();
        let n = mon.len().min(60 * 32);
        self.ctx.vram[0x580 * 32..0x580 * 32 + n].copy_from_slice(&mon[..n]);
        let life = self.ctx.assets.life_icon_art.clone();
        let n = life.len().min(4 * 32);
        self.ctx.vram[0x5C0 * 32..0x5C0 * 32 + n].copy_from_slice(&life[..n]);
        for i in 0..32 {
            self.ppu.palette[i] = self
                .ctx
                .assets
                .prg(27, 0xB712 + if i < 16 { i } else { 0x80 + i - 16 });
        }
        self.ppu.clear(0xFC);
        let room = self.ctx.assets.bonus_room.clone();
        for r in 0..12 {
            for c in 0..16 {
                let mut tt = room[(15 + r) * 16 + c];
                if tt == 0x4A || tt == 0x86 {
                    tt = 0x40
                }
                if (0xCE..=0xD3).contains(&tt) {
                    for q in 0..4 {
                        self.table.insert((c * 2 + (q & 1), r * 2 + (q >> 1)));
                    }
                }
                let nt = 0x2800 + r * 64 + c * 2;
                let vals = [
                    self.ctx.assets.prg(22, 0xC000 + tt as usize),
                    self.ctx.assets.prg(22, 0xC000 + 256 + tt as usize),
                    self.ctx.assets.prg(22, 0xC000 + 512 + tt as usize),
                    self.ctx.assets.prg(22, 0xC000 + 768 + tt as usize),
                ];
                self.ppu.write(nt, vals[0]);
                self.ppu.write(nt + 32, vals[1]);
                self.ppu.write(nt + 1, vals[2]);
                self.ppu.write(nt + 33, vals[3]);
                self.ppu
                    .set_cell_palette(2, c * 2, r * 2, (tt >> 6) as usize);
            }
        }
        self.status_draw();
        self.ctx.begin_fade_in();
        self.fade_left = 22;
    }
    fn status_draw(&mut self) {
        self.ppu.run_stream(|a| self.ctx.assets.prg(30, a), 0x811B);
        let b = 0x2B00;
        self.ppu.write(b + 0x26, 0xF0 + self.progress.world as u8);
        self.ppu.write(b + 0x42, 0x74);
        self.ppu.write(b + 0x43, 0x75);
        self.status_values(b);
    }
    fn status_values(&mut self, b: usize) {
        let lives = self.progress.lives.clamp(0, 99);
        self.ppu.write(
            b + 0x45,
            if lives >= 10 {
                0xF0 + (lives / 10) as u8
            } else {
                0xFE
            },
        );
        self.ppu.write(b + 0x46, 0xF0 + (lives % 10) as u8);
        let score = (self.progress.score / 10).min(999999);
        for i in 0..6 {
            let d = (score / [100000, 10000, 1000, 100, 10, 1][i]) % 10;
            self.ppu.write(b + 0x48 + i, 0xF0 + d as u8);
        }
        let rings = self.progress.rings.clamp(0, 99);
        self.ppu.write(
            b + 0x32,
            if rings >= 10 {
                0xF0 + (rings / 10) as u8
            } else {
                0xFE
            },
        );
        self.ppu.write(b + 0x33, 0xF0 + (rings % 10) as u8);
        for i in 0..3 {
            self.ppu.write(b + 0x51 + i, 0xFE);
        }
    }
    fn video_update(&mut self, item: usize) {
        let addr = self.ctx.assets.prg_word(30, 0x803E + item * 2) as usize;
        let bank = if addr >= 0xC000 { 22 } else { 30 };
        self.ppu.run_stream(|a| self.ctx.assets.prg(bank, a), addr);
    }
    fn present(&mut self) {
        let p = &mut self.ctx;
        let mut nes = NesPresenter::new(&mut p.vram, &mut p.ram, &p.assets);
        nes.bg_banks = [0x24, 0x25, 0x5E, 0x5F];
        let high = |c: usize| self.table.contains(&(c, 0));
        for row in 0..24 {
            let h = |c: usize| self.table.contains(&(c, row));
            nes.row(
                &self.ppu,
                2,
                row,
                PLANE_A,
                row,
                None::<&fn(i32) -> Option<usize>>,
                Some(&h),
            );
        }
        nes.bg_banks = [0x5C, 0x5D, 0x5E, 0x5F];
        for row in 24..30 {
            nes.row(
                &self.ppu,
                2,
                row,
                PLANE_A,
                row,
                None::<&fn(i32) -> Option<usize>>,
                None::<&fn(usize) -> bool>,
            );
        }
        nes.palettes(
            &self.ppu,
            if self.fade_left > 0 {
                vars::TARGET_PALETTE
            } else {
                vars::NORMAL_PALETTE
            },
        );
        let mut sk = SpriteKit::new(&mut p.ram, &mut p.vram);
        sk.begin();
        sk.load_dplc(&p.assets.tails, 1, 0x540);
        sk.frame(&p.assets.tails, 1, 136 + 32, 114 - 8, 0x540, false, false);
        sk.load_dplc(&p.assets.sonic, 1, 0x500);
        sk.frame(&p.assets.sonic, 1, 40 + 32, 124 - 8, 0x500, false, false);
        sk.end();
    }
    pub fn step(&mut self, _a: &FrontendAssets, buttons: u8) {
        if self.fade_left > 0 {
            self.fade_left -= 1;
            if self.fade_left > 0 {
                self.ctx.fade_in_step();
            }
            self.present();
            return;
        }
        self.frames += 1;
        if self.phase == 0 {
            if self.item <= 0x0C {
                self.video_update(self.item);
                self.item += 1;
                if self.item > 0x0C {
                    self.phase = 1;
                    self.text_v = 0x28C5;
                }
                self.present();
                return;
            }
        }
        if self.phase == 1 {
            self.pause -= 1;
            if self.pause == 0 {
                self.pause = 4;
                loop {
                    let ch = self.ctx.assets.prg(22, 0xCA9B + self.text_pos);
                    if ch != 0 {
                        if ch == 0xFF {
                            self.phase = 2;
                            break;
                        }
                        self.text_pos += 1;
                        let _ = self.ppu.write(self.text_v, ch);
                        self.text_v = (self.text_v + 1) & 0xFFFF;
                        break;
                    }
                    self.text_v = (self.text_v + 32) & 0xFFE0 | 5;
                    self.text_pos += 1;
                }
            }
            self.present();
            return;
        }
        if self.phase == 2 {
            if buttons & 0xF0 != 0 || self.frames > 10000 {
                self.phase = 3;
            }
            self.present();
        }
    }
    pub fn render_hash(&mut self, v: &mut Vdp, _a: &FrontendAssets) -> u64 {
        let line = |line: usize| Some(if line + 8 < 193 { 8 } else { 7 });
        self.ctx.render_hash(v, &line, &line)
    }
}
pub const LEVEL11_TILE: i32 = 4;
pub const LEVEL12_TILE: i32 = 8;
pub const LEVEL13_TILE: i32 = 10;
pub const SPADE_TILE: i32 = 4 * 16 + 8;
const TILE_HORZ: u8 = 0x45;
const TILE_VERT: u8 = 0x46;
const TILE_BRIDGE: u8 = 0xB3;
const TILE_START: u8 = 0xE5;
const HORZ_PATHS: [u8; 9] = [0x45, 0xB2, 0xB3, 0xAC, 0xB7, 0xB8, 0xDA, 0xB9, 0xE6];
const VERT_PATHS: [u8; 7] = [0x46, 0xB1, 0xAA, 0xAB, 0xB0, 0xDB, 0xBA];
const COMPLETABLE: [u8; 5] = [0x50, 0xE8, 0xE6, 0xBD, 0xE0];

pub struct MapScreen {
    pub ctx: FrontContext,
    pub progress: MapProgress,
    ppu: NesPpu,
    layout: [u8; 144],
    intro: bool,
    frames: i32,
    x: i32,
    y: i32,
    move_left: i32,
    move_dir: i32,
    anim_frame: usize,
    anim_ticks: i32,
    box_shown: bool,
    fade_left: i32,
    fade_mode: u8,
    started: bool,
    result: Option<()>,
    overrides: std::collections::HashMap<(u8, usize), [u8; 64]>,
}
impl MapScreen {
    pub fn new(a: &FrontendAssets, progress: MapProgress, intro: bool) -> Self {
        let mut s = Self {
            ctx: FrontContext::new(a.clone()),
            progress: progress.clone(),
            ppu: NesPpu {
                horizontal_mirroring: false,
                ..Default::default()
            },
            layout: [0; 144],
            intro,
            frames: 0,
            x: 0,
            y: 0,
            move_left: 0,
            move_dir: 0,
            anim_frame: 0,
            anim_ticks: 18,
            box_shown: false,
            fade_left: 0,
            fade_mode: 0,
            started: false,
            result: None,
            overrides: std::collections::HashMap::new(),
        };
        s.ctx.clear_screen();
        s.ctx.load_art("map");
        let mon = s.ctx.assets.monitor_icon_art.clone();
        let n = mon.len().min(60 * 32);
        s.ctx.vram[0x580 * 32..0x580 * 32 + n].copy_from_slice(&mon[..n]);
        let mini = mini_sonic_tiles();
        s.ctx.vram[0x5C0 * 32..0x5C0 * 32 + mini.len()].copy_from_slice(&mini);
        for q in 0..4 {
            s.overrides
                .insert((0x16, 0x08 + q), glyph(&S_PANEL, (q >> 1) * 8, (q & 1) * 8));
        }
        s.overrides.insert((0x5D, 0x34), glyph(&S_BADGE, 0, 0));
        s.overrides.insert((0x5D, 0x35), glyph(&S_BADGE, 8, 0));
        s.overrides.insert((0x17, 0x3D), glyph(&S_LETTER, 0, 0));
        for i in 0..16 {
            s.ppu.palette[i] = s
                .ctx
                .assets
                .prg(27, 0xABD2 + (progress.world as usize - 1) * 16 + i);
            s.ppu.palette[16 + i] = s.ctx.assets.prg(27, 0xABD2 + 8 * 16 + i);
        }
        s.ctx.load_palette_03();
        let mut p = s.progress.clone();
        if intro {
            p.position = 2 * 16 + 2;
        }
        s.progress = p;
        for i in 0..144 {
            s.layout[i] = s.ctx.assets.prg(12, 0xA5AA + i);
        }
        s.x = (s.progress.position & 15) * 16;
        s.y = ((s.progress.position >> 4) + 2) * 16;
        s.draw_map();
        s.draw_status();
        if intro {
            s.draw_world_box();
            s.box_shown = true;
        }
        s.ctx.begin_fade_in();
        s.fade_mode = 1;
        s.fade_left = 22;
        s
    }
    fn layout_at(&self, x: i32, y: i32) -> u8 {
        let c = x >> 4;
        let r = (y >> 4) - 2;
        if !(0..16).contains(&c) || !(0..9).contains(&r) {
            0x02
        } else {
            self.layout[(r * 16 + c) as usize]
        }
    }
    fn current_tile(&self) -> i32 {
        ((self.y >> 4) - 2) * 16 + (self.x >> 4)
    }
    fn draw_metatile(&mut self, c: usize, r: usize, t: u8) {
        let nt = 0x2800 + r * 64 + c * 2;
        let a = self.ctx.assets.prg(12, 0xA000 + t as usize);
        let b = self.ctx.assets.prg(12, 0xA000 + 256 + t as usize);
        let cc = self.ctx.assets.prg(12, 0xA000 + 512 + t as usize);
        let d = self.ctx.assets.prg(12, 0xA000 + 768 + t as usize);
        self.ppu.write(nt, a);
        self.ppu.write(nt + 32, b);
        self.ppu.write(nt + 1, cc);
        self.ppu.write(nt + 33, d);
        self.ppu
            .set_cell_palette(2, c * 2, r * 2, (t >> 6) as usize);
    }
    fn draw_map(&mut self) {
        for r in 0..12 {
            for c in 0..16 {
                let t = match r {
                    0 => 0x02,
                    1 => 0x4E,
                    11 => 0x4F,
                    _ => self.layout[(r - 2) * 16 + c],
                };
                self.draw_metatile(c, r, t);
            }
        }
    }
    fn draw_status(&mut self) {
        self.ppu.run_stream(|a| self.ctx.assets.prg(30, a), 0x811B);
        let b = 0x2B00;
        self.ppu.write(b + 0x26, 0xF0 + self.progress.world as u8);
        self.ppu.write(b + 0x42, 0x74);
        self.ppu.write(b + 0x43, 0x75);
        self.status_values(b);
    }
    fn status_values(&mut self, b: usize) {
        let lives = self.progress.lives.clamp(0, 99);
        self.ppu.write(
            b + 0x45,
            if lives >= 10 {
                0xF0 + (lives / 10) as u8
            } else {
                0xFE
            },
        );
        self.ppu.write(b + 0x46, 0xF0 + (lives % 10) as u8);
        let score = (self.progress.score / 10).min(999999);
        for i in 0..6 {
            let d = (score / [100000, 10000, 1000, 100, 10, 1][i]) % 10;
            self.ppu.write(b + 0x48 + i, 0xF0 + d as u8);
        }
        let rings = self.progress.rings.clamp(0, 99);
        self.ppu.write(
            b + 0x32,
            if rings >= 10 {
                0xF0 + (rings / 10) as u8
            } else {
                0xFE
            },
        );
        self.ppu.write(b + 0x33, 0xF0 + (rings % 10) as u8);
        for i in 0..3 {
            self.ppu.write(b + 0x51 + i, 0xFE);
        }
    }
    fn draw_world_box(&mut self) {
        self.ppu.run_stream(|a| self.ctx.assets.prg(10, a), 0xC000);
        let sonic = [0xFD, 0xF0, 0xDB, 0xFC, 0xED];
        for i in 0..5 {
            self.ppu.write(0x29AA + i, sonic[i]);
        }
        self.ppu.write(0x2952, 0xF0 + self.progress.world as u8);
        self.ppu
            .write(0x29B4, 0xF0 + self.progress.lives.min(9) as u8);
    }
    fn present(&mut self) {
        self.anim_frame = ((self.frames / 18) & 3) as usize;
        // banks and nametable rows
        let frame = self.anim_frame;
        let banks = [0x14, 0x70, 0x72, 0x74];
        let p = &mut self.ctx;
        let mut nes = NesPresenter::new(&mut p.vram, &mut p.ram, &p.assets);
        nes.overrides = self.overrides.clone();
        nes.bg_banks = [banks[frame], banks[frame] + 1, 0x16, 0x17];
        for row in 0..24 {
            nes.row(
                &self.ppu,
                2,
                row,
                PLANE_A,
                row,
                None::<&fn(i32) -> Option<usize>>,
                None::<&fn(usize) -> bool>,
            );
        }
        nes.bg_banks = [0x5C, 0x5D, 0x5E, 0x5F];
        for row in 24..30 {
            nes.row(
                &self.ppu,
                2,
                row,
                PLANE_A,
                row,
                None::<&fn(i32) -> Option<usize>>,
                None::<&fn(usize) -> bool>,
            );
        }
        nes.palettes(
            &self.ppu,
            if self.fade_mode == 1 || self.fade_mode == 2 {
                vars::TARGET_PALETTE
            } else {
                vars::NORMAL_PALETTE
            },
        );
        let mut sk = SpriteKit::new(&mut p.ram, &mut p.vram);
        sk.begin();
        let fr = ((self.frames >> (if self.move_left > 0 { 3 } else { 4 })) & 1) as usize;
        sk.piece(
            self.x + 32,
            self.y - 8 - 1,
            5,
            (0x8000 | (0x5C0 + fr * 4)) as u16,
        );
        sk.end();
    }
    pub fn step(&mut self, _a: &FrontendAssets, buttons: u8) {
        if self.fade_left > 0 {
            self.fade_left -= 1;
            if self.fade_left == 0 {
                self.fade_mode = 0;
            } else if self.fade_mode == 1 {
                self.ctx.fade_in_step();
            }
            self.present();
            return;
        }
        self.frames += 1;
        if self.box_shown {
            if self.frames < 120 && buttons & 0xF0 == 0 {
                self.present();
                return;
            }
            self.box_shown = false;
            self.draw_map();
        }
        self.step_move(buttons);
        self.present();
    }
    fn step_move(&mut self, _buttons: u8) {
        if self.move_left > 0 {
            let dx = if self.move_dir == 8 {
                2
            } else if self.move_dir == 4 {
                -2
            } else {
                0
            };
            let dy = if self.move_dir == 2 {
                2
            } else if self.move_dir == 1 {
                -2
            } else {
                0
            };
            self.x += dx;
            self.y += dy;
            self.move_left -= 2;
            if self.move_left == 0 {
                self.progress.position = self.current_tile();
            }
        }
    }
    pub fn render_hash(&mut self, v: &mut Vdp, _a: &FrontendAssets) -> u64 {
        let line = |line: usize| Some(if line + 8 < 193 { 8 } else { 7 });
        self.ctx.render_hash(v, &line, &line)
    }
}

fn mini_sonic_tiles() -> Vec<u8> {
    const F0: [&str; 16] = [
        "......kkk.......",
        "....kkBBBkk.....",
        "..kkBBBBBBBk....",
        ".kBBBBBBBBBBk...",
        "kBBBBkwwkwwBk...",
        ".kkBBwwkwwkwBk..",
        "..kBBwwkwwkwk...",
        ".kBBBpwwwwpk....",
        "kBBBBkppkppk....",
        ".kkBBkPppppk....",
        "..kkpkkPPPk.....",
        "...kpkBBkkpk....",
        "....kkBBBBkk....",
        "...krrkkkrrk....",
        "..krrrwk.krrrk..",
        "..kkkkk...kkkk..",
    ];
    const F1: [&str; 16] = [
        "......kkk.......",
        "....kkBBBkk.....",
        "..kkBBBBBBBk....",
        ".kBBBBBBBBBBk...",
        "kBBBBkwwkwwBk...",
        ".kkBBwwkwwkwBk..",
        "..kBBwwkwwkwk...",
        ".kBBBpwwwwpk....",
        "kBBBBkppkppk....",
        ".kkBBkPppppk....",
        "..kkkpPPPPkpk...",
        "...kpkBBBkkk....",
        "....kkBBBBk.....",
        "....krrkkrrk....",
        "...krrrwkrrrk...",
        "...kkkkk.kkkk...",
    ];
    let mut d = vec![0u8; 2 * 4 * 32];
    let frames = [F0, F1];
    for f in 0..2 {
        for y in 0..16 {
            for x in 0..16 {
                let c = match frames[f][y].as_bytes()[x] {
                    b'k' => 1,
                    b'b' => 2,
                    b'B' => 3,
                    b'w' => 6,
                    b'p' => 10,
                    b'P' => 11,
                    b'r' => 12,
                    b'R' => 13,
                    _ => 0,
                };
                let tile = f * 4 + (x >> 3) * 2 + (y >> 3);
                let i = tile * 32 + (y & 7) * 4 + (x & 7) / 2;
                d[i] |= if x & 1 == 0 { c << 4 } else { c };
            }
        }
    }
    d
}

fn glyph(rows: &[&str], x0: usize, y0: usize) -> [u8; 64] {
    let mut p = [0u8; 64];
    for y in 0..8 {
        for x in 0..8 {
            p[y * 8 + x] = match rows[y0 + y].as_bytes()[x0 + x] {
                b'w' => 1,
                b'b' => 2,
                b'r' => 3,
                _ => 0,
            };
        }
    }
    p
}
const S_BADGE: [&str; 8] = [
    "rrrrrrrrrrrrrrrr",
    "rwwwwwwwwwwwwwwr",
    "wwrrwwbbbbbwrrww",
    "wrrrwbbwwwwwrrrw",
    "wrrrwwbbbbwwrrrw",
    "wrrrwwwwwbbwrrrw",
    "wwrrwbbbbbwwrrww",
    "rwwwwwwwwwwwwwwr",
];
const S_LETTER: [&str; 8] = [
    "rwwwwwwr", "wwbbbbww", "wbbwwwww", "wwbbbbwr", "rwwwwbbw", "wwwwwbbw", "wbbbbbww", "wwwwwwwr",
];
const S_PANEL: [&str; 16] = [
    "................",
    ".bbbbbbbbbbbbbb.",
    ".bbbbbbbbbbbbbb.",
    ".bb.........bbb.",
    ".b.rrrrrrrrr.bb.",
    ".b.rr.......bbb.",
    ".b.rr.bbbbbbbbb.",
    ".b..rrrrrrr.bbb.",
    ".bb.......rr.bb.",
    ".bbbbbbbb.rr.bb.",
    ".b........rr.bb.",
    ".b.rrrrrrrr.bbb.",
    ".b.........bbbb.",
    ".bbbbbbbbbbbbbb.",
    ".bbbbbbbbbbbbbb.",
    "................",
];
