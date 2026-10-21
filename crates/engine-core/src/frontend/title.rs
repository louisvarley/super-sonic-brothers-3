use super::assets::FrontendAssets;
use super::nes::{NesPpu, NesPresenter, SpriteKit};
use super::screens::{FrontContext, PLANE_A, PLANE_B};
use crate::graphics::SpriteData;
use crate::vars;
use crate::vdp::Vdp;

/// `Sb3TitleScreen` port: the SMB3 curtain/logo/menu with Sonic and Tails actors.

#[derive(Clone, Copy, Debug, Default)]
pub struct TitleActor {
    pub x: f64,
    pub y: f64,
    pub frame: usize,
    pub flip_x: bool,
    pub flip_y: bool,
    pub visible: bool,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum TitleAction {
    StartGame,
    BackToTitle,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum TitleMode {
    Wait(u32),
    Curtain,
    Logo(u32),
    WaitDrop,
    Drop,
    Shake(u32, u8),
    Init(u32),
    Fade(u32, u32),
    Intro,
    Menu,
    Cursor(u32),
    FadeOut(u32),
}

#[derive(Clone, Debug, Default)]
struct TitleFlags {
    goomba_landed: bool,
    goomba_hit: bool,
    shell_landed: bool,
    shell_kicked: bool,
    tails_home: bool,
    intro_done: bool,
}

struct SonicScript {
    phase: u8,
    wait: u32,
    t: u32,
    target: f64,
    speed: f64,
    rev: u32,
}

impl SonicScript {
    fn step(
        &mut self,
        a: &mut TitleActor,
        flags: &mut TitleFlags,
        scroll: i32,
        since: i32,
        goomba_x: f64,
    ) {
        match self.phase {
            0 => {
                a.x = 300.0;
                a.y = 172.0;
                a.flip_x = true;
                a.visible = true;
                a.frame = SONIC_STAND;
                self.wait = 0x3F;
                self.phase = 1;
            }
            1 => {
                if self.wait > 0 {
                    self.wait -= 1;
                } else {
                    self.phase = 2;
                    self.t = 0;
                    self.target = 168.0;
                    self.speed = 5.0;
                    self.run(a, &SONIC_WALK, &SONIC_RUN);
                }
            }
            2 => {
                if !self.run(a, &SONIC_WALK, &SONIC_RUN) {
                    a.frame = SONIC_STAND;
                    self.wait = 9;
                    self.phase = 3;
                }
            }
            3 => {
                if self.wait > 0 {
                    self.wait -= 1;
                } else {
                    a.frame = SONIC_DUCK;
                    self.wait = 39;
                    self.phase = 4;
                }
            }
            4 => {
                if self.wait > 0 {
                    self.wait -= 1;
                } else {
                    a.frame = SONIC_STAND;
                    a.flip_x = false;
                    if scroll == 0 && since >= LOGO_DROP_AT {
                        a.frame = SONIC_LOOK_UP;
                        self.wait = 69;
                        self.phase = 6;
                    }
                }
            }
            6 => {
                if self.wait > 0 {
                    self.wait -= 1;
                } else {
                    a.frame = SONIC_STAND;
                    a.flip_x = true;
                    self.phase = 7;
                }
            }
            7 => {
                if flags.goomba_landed {
                    self.wait = 19;
                    self.phase = 8;
                }
            }
            8 => {
                if self.wait > 0 {
                    self.wait -= 1;
                } else {
                    a.frame = SONIC_DUCK;
                    self.wait = 11;
                    self.phase = 9;
                }
            }
            9 => {
                if self.wait > 0 {
                    self.wait -= 1;
                } else {
                    self.rev = 0;
                    self.t = 0;
                    self.phase = 10;
                    a.frame = SONIC_DASH[0];
                    self.t = 1;
                }
            }
            10 => {
                if self.t < 16 {
                    a.frame = SONIC_DASH[(self.t as usize) % SONIC_DASH.len()];
                    self.t += 1;
                } else {
                    self.rev += 1;
                    if self.rev < 3 {
                        self.t = 0;
                        a.frame = SONIC_DASH[0];
                        self.t = 1;
                    } else {
                        self.phase = 11;
                        self.t = 0;
                        if a.x > -48.0 {
                            a.x -= 9.0;
                            a.frame = SONIC_ROLL[0];
                            if !flags.goomba_hit && a.visible && (a.x - goomba_x).abs() < 16.0 {
                                flags.goomba_hit = true;
                            }
                            self.t = 1;
                        } else {
                            a.visible = false;
                            self.phase = 12;
                        }
                    }
                }
            }
            11 => {
                if a.x > -48.0 {
                    a.x -= 9.0;
                    a.frame = SONIC_ROLL[(self.t as usize) % SONIC_ROLL.len()];
                    if !flags.goomba_hit && a.visible && (a.x - goomba_x).abs() < 16.0 {
                        flags.goomba_hit = true;
                    }
                    self.t += 1;
                } else {
                    a.visible = false;
                    self.phase = 12;
                }
            }
            12 => {
                if flags.shell_landed {
                    self.wait = 29;
                    self.phase = 13;
                }
            }
            13 => {
                if self.wait > 0 {
                    self.wait -= 1;
                } else {
                    a.visible = true;
                    a.x = -40.0;
                    a.flip_x = false;
                    self.t = 0;
                    self.phase = 14;
                    if a.x < 120.0 - 14.0 {
                        a.x += 6.0;
                        a.frame = SONIC_ROLL[0];
                        self.t = 1;
                    }
                }
            }
            14 => {
                if a.x < 120.0 - 14.0 {
                    a.x += 6.0;
                    a.frame = SONIC_ROLL[(self.t as usize) % SONIC_ROLL.len()];
                    self.t += 1;
                } else {
                    flags.shell_kicked = true;
                    self.t = 0;
                    self.phase = 15;
                    a.x += 3.0;
                    a.frame = SONIC_ROLL[0];
                    self.t = 1;
                }
            }
            15 => {
                if self.t < 12 {
                    a.x += 3.0 - self.t as f64 * 0.25;
                    a.frame = SONIC_ROLL[(self.t as usize) % SONIC_ROLL.len()];
                    self.t += 1;
                } else {
                    self.phase = 16;
                    self.target = 48.0;
                    self.speed = 4.0;
                    self.t = 0;
                    self.run(a, &SONIC_WALK, &SONIC_RUN);
                }
            }
            16 => {
                if !self.run(a, &SONIC_WALK, &SONIC_RUN) {
                    a.flip_x = false;
                    a.frame = SONIC_STAND;
                    self.phase = 17;
                }
            }
            17 => {
                if flags.tails_home {
                    self.wait = 29;
                    self.phase = 18;
                }
            }
            18 => {
                if self.wait > 0 {
                    self.wait -= 1;
                } else {
                    flags.intro_done = true;
                    self.phase = 19;
                }
            }
            _ => a.frame = SONIC_STAND,
        }
    }

    /// One `RunTo` iteration; returns false when the target was reached.
    fn run(&mut self, a: &mut TitleActor, walk: &[usize], run: &[usize]) -> bool {
        if (a.x - self.target).abs() <= self.speed {
            a.x = self.target;
            return false;
        }
        let v = self.speed.min(1.0 + self.t as f64 * 0.125);
        a.x += if a.flip_x { -v } else { v };
        a.frame = if v >= 4.0 {
            anim(run, self.t, 3)
        } else {
            anim(walk, self.t, 2.max(6 - v as u32))
        };
        self.t += 1;
        true
    }
}

struct TailsScript {
    phase: u8,
    wait: u32,
    t: u32,
    target: f64,
    speed: f64,
    vy: f64,
}

impl TailsScript {
    fn step(&mut self, a: &mut TitleActor, flags: &mut TitleFlags) {
        match self.phase {
            0 => {
                a.x = -44.0;
                a.y = 176.0;
                a.flip_x = false;
                a.visible = true;
                a.frame = TAILS_STAND;
                self.wait = 0x40 + 19;
                self.phase = 1;
            }
            1 => {
                if self.wait > 0 {
                    self.wait -= 1;
                } else {
                    self.phase = 2;
                    self.t = 0;
                    self.target = 96.0;
                    self.speed = 5.0;
                    self.run(a, &TAILS_WALK, &TAILS_RUN);
                }
            }
            2 => {
                if !self.run(a, &TAILS_WALK, &TAILS_RUN) {
                    self.vy = -6.5;
                    self.t = 0;
                    self.phase = 3;
                    self.jump(a);
                }
            }
            3 => {
                if a.y <= 176.0 || self.vy < 0.0 {
                    self.jump(a);
                } else {
                    a.y = 176.0;
                    a.frame = TAILS_STAND;
                    a.flip_x = true;
                    self.phase = 4;
                }
            }
            4 => {
                if flags.goomba_hit {
                    self.wait = 29;
                    self.phase = 5;
                }
            }
            5 => {
                if self.wait > 0 {
                    self.wait -= 1;
                } else {
                    self.t = 0;
                    self.phase = 6;
                    self.step(a, flags);
                }
            }
            6 => {
                if a.y > -40.0 {
                    a.y -= (0.5 + self.t as f64 * 0.06).min(2.5);
                    a.x += (self.t as f64 / 12.0).sin() * 1.2;
                    a.frame = anim(&TAILS_FLY, self.t, 2);
                    self.t += 1;
                } else {
                    a.visible = false;
                    self.phase = 7;
                }
            }
            7 => {
                if flags.shell_kicked {
                    self.wait = 39;
                    self.phase = 8;
                }
            }
            8 => {
                if self.wait > 0 {
                    self.wait -= 1;
                } else {
                    a.visible = true;
                    a.x = 250.0;
                    a.y = -40.0;
                    a.flip_x = true;
                    self.t = 0;
                    self.phase = 9;
                    self.step(a, flags);
                }
            }
            9 => {
                if a.y < 176.0 {
                    a.y += 1.5;
                    a.x += (212.0 - a.x) * 0.03;
                    a.frame = anim(&TAILS_FLY, self.t, 2);
                    self.t += 1;
                } else {
                    a.y = 176.0;
                    a.x = a.x.round();
                    a.frame = TAILS_STAND;
                    flags.tails_home = true;
                    self.phase = 10;
                }
            }
            _ => {}
        }
    }

    fn jump(&mut self, a: &mut TitleActor) {
        a.x += 2.6;
        a.y += self.vy;
        self.vy += 0.21875;
        a.frame = TAILS_ROLL[(self.t as usize / 2) % TAILS_ROLL.len()];
        self.t += 1;
    }

    fn run(&mut self, a: &mut TitleActor, walk: &[usize], run: &[usize]) -> bool {
        if (a.x - self.target).abs() <= self.speed {
            a.x = self.target;
            false
        } else {
            let v = self.speed.min(1.0 + self.t as f64 * 0.125);
            a.x += if a.flip_x { -v } else { v };
            a.frame = if v >= 4.0 {
                anim(run, self.t, 3)
            } else {
                anim(walk, self.t, 2.max(6 - v as u32))
            };
            self.t += 1;
            true
        }
    }
}

struct PropScript {
    phase: u8,
    wait: u32,
    t: u32,
    vy: f64,
}

impl PropScript {
    fn step(
        &mut self,
        goomba: &mut TitleActor,
        shell: &mut TitleActor,
        flags: &mut TitleFlags,
        scroll: i32,
        since: i32,
    ) {
        match self.phase {
            0 => {
                if scroll == 0 && since >= LOGO_DROP_AT + 60 {
                    goomba.visible = true;
                    goomba.x = 56.0;
                    goomba.y = -16.0;
                    goomba.frame = 0;
                    self.vy = 0.0;
                    self.phase = 1;
                    self.step(goomba, shell, flags, scroll, since);
                }
            }
            1 => {
                if goomba.y < 184.0 {
                    self.vy = (self.vy + 0.25).min(4.0);
                    goomba.y += self.vy;
                } else {
                    goomba.y = 184.0;
                    flags.goomba_landed = true;
                    self.t = 0;
                    self.phase = 2;
                    self.step(goomba, shell, flags, scroll, since);
                }
            }
            2 => {
                if !flags.goomba_hit {
                    if self.t & 1 == 0 {
                        goomba.x += 0.5;
                    }
                    goomba.frame = ((self.t >> 3) & 1) as usize;
                    goomba.flip_x = (self.t >> 3) & 1 != 0;
                    self.t += 1;
                } else {
                    goomba.flip_y = true;
                    self.vy = -4.0;
                    self.phase = 3;
                    self.step(goomba, shell, flags, scroll, since);
                }
            }
            3 => {
                if goomba.y < 260.0 {
                    goomba.x -= 1.5;
                    goomba.y += self.vy;
                    self.vy += 0.25;
                } else {
                    goomba.visible = false;
                    self.wait = 59;
                    self.phase = 4;
                }
            }
            4 => {
                if self.wait > 0 {
                    self.wait -= 1;
                } else {
                    shell.visible = true;
                    shell.x = 120.0;
                    shell.y = -16.0;
                    shell.frame = 2;
                    self.vy = 0.0;
                    self.phase = 5;
                    self.step(goomba, shell, flags, scroll, since);
                }
            }
            5 => {
                if shell.y < 184.0 {
                    self.vy = (self.vy + 0.25).min(4.0);
                    shell.y += self.vy;
                } else {
                    shell.y = 184.0;
                    flags.shell_landed = true;
                    self.phase = 6;
                }
            }
            6 => {
                if flags.shell_kicked {
                    self.phase = 7;
                    shell.x += 5.0;
                }
            }
            7 => {
                if shell.x < 300.0 {
                    shell.x += 5.0;
                } else {
                    shell.visible = false;
                    self.phase = 8;
                }
            }
            _ => {}
        }
    }
}

fn anim(frames: &[usize], clock: u32, speed: u32) -> usize {
    frames[(clock / speed.max(1)) as usize % frames.len()]
}

const SONIC_WALK: [usize; 8] = [13, 14, 15, 16, 17, 18, 19, 20];
const SONIC_RUN: [usize; 4] = [45, 46, 47, 48];
const SONIC_ROLL: [usize; 8] = [61, 65, 62, 65, 63, 65, 64, 65];
const SONIC_DASH: [usize; 10] = [66, 67, 66, 68, 66, 69, 66, 70, 66, 71];
const TAILS_WALK: [usize; 8] = [16, 17, 18, 19, 20, 21, 14, 15];
const TAILS_RUN: [usize; 4] = [46, 47, 48, 49];
const TAILS_ROLL: [usize; 3] = [72, 71, 70];
const TAILS_FLY: [usize; 2] = [94, 95];
const SONIC_STAND: usize = 1;
const SONIC_DUCK: usize = 77;
const SONIC_LOOK_UP: usize = 76;
const TAILS_STAND: usize = 1;
const LOGO_DROP_AT: i32 = 0x4C + 0x14 + 0x20 + 0x03;

pub struct TitleScreen {
    pub ctx: FrontContext,
    pub action: Option<TitleAction>,
    sounds: Vec<u16>,
    ppu: NesPpu,
    mode: TitleMode,
    vert_scroll: i32,
    since_curtain: i32,
    glow: bool,
    glow_ticker: i32,
    glow_index: u8,
    menu: bool,
    player: usize,
    single_player: bool,
    cursor_shown: bool,
    reset_count: i32,
    reset_fine: i32,
    prev_buttons: u8,
    actors: [TitleActor; 4],
    flags: TitleFlags,
    sonic: SonicScript,
    tails: TailsScript,
    prop: PropScript,
    scripts: bool,
}

const TITLE_CURSOR_Y: [i32; 2] = [151, 167];
const TITLE_CURSOR_X: i32 = 72;
const TITLE_CURSOR_TILE: u8 = 0xDF;
const TITLE_SPLIT: i32 = 193;
const TITLE_SONIC_TILE: usize = 0x500;
const TITLE_TAILS_TILE: usize = 0x540;
const TITLE_OBJECTS_TILE: usize = 0x380;

impl TitleScreen {
    pub fn new(a: &FrontendAssets) -> Self {
        let mut s = Self {
            ctx: FrontContext::new(a.clone()),
            action: None,
            sounds: Vec::new(),
            ppu: NesPpu {
                horizontal_mirroring: true,
                ..Default::default()
            },
            mode: TitleMode::Wait(0),
            vert_scroll: 0,
            since_curtain: -1,
            glow: false,
            glow_ticker: 0,
            glow_index: 0,
            menu: false,
            player: 0,
            single_player: false,
            cursor_shown: true,
            reset_count: 20,
            reset_fine: 60,
            prev_buttons: 0,
            actors: [TitleActor::default(); 4],
            flags: TitleFlags::default(),
            sonic: SonicScript {
                phase: 0,
                wait: 0,
                t: 0,
                target: 0.0,
                speed: 0.0,
                rev: 0,
            },
            tails: TailsScript {
                phase: 0,
                wait: 0,
                t: 0,
                target: 0.0,
                speed: 0.0,
                vy: 0.0,
            },
            prop: PropScript {
                phase: 0,
                wait: 0,
                t: 0,
                vy: 0.0,
            },
            scripts: true,
        };
        s.ctx.clear_screen();
        let obj = s.ctx.assets.smb3_objects_art.clone();
        let n = (obj.len()).min(60 * 32);
        s.ctx.vram[TITLE_OBJECTS_TILE * 32..TITLE_OBJECTS_TILE * 32 + n].copy_from_slice(&obj[..n]);
        s.ppu.clear(0xFC);
        for i in 0..0x300 {
            s.ppu.write(0x2000 + i, 0x08 | (i as u8 & 1));
        }
        s.run_item(1);
        s.load_palette_now();
        s.present();
        s
    }

    fn load_palette_now(&mut self) {
        for (i, word) in self.ctx.assets.palette_03.iter().take(32).enumerate() {
            self.ctx.palette_normal[i] = *word;
            self.ctx.ram.w16(vars::NORMAL_PALETTE + i as u16 * 2, *word);
        }
    }

    fn run_item(&mut self, item: usize) {
        let address = self.ctx.assets.prg_word_le(25, 0xC5DA + item * 2) as usize;
        self.ppu.run_stream(|a| self.ctx.assets.prg(25, a), address);
    }

    pub fn step(&mut self, _a: &FrontendAssets, buttons: u8) {
        let press = buttons & !self.prev_buttons;
        self.prev_buttons = buttons;
        if press & 0x80 != 0 {
            match self.mode {
                TitleMode::Intro => {
                    self.begin_menu(false);
                    self.menu_frame(0);
                    return;
                }
                TitleMode::Menu | TitleMode::Cursor(_) | TitleMode::FadeOut(_) => {}
                _ => {
                    self.begin_menu(true);
                    self.menu_frame(0);
                    return;
                }
            }
        }
        match self.mode {
            TitleMode::Wait(n) => {
                if n < 54 {
                    self.mode = TitleMode::Wait(n + 1);
                    self.present();
                } else {
                    self.mode = TitleMode::Curtain;
                    self.curtain_step();
                }
            }
            TitleMode::Curtain => self.curtain_step(),
            TitleMode::Logo(item) => {
                if item <= 0x22 {
                    self.run_item(item as usize);
                    self.frame();
                    self.mode = TitleMode::Logo(item + 1);
                } else {
                    self.mode = TitleMode::WaitDrop;
                    self.frame();
                }
            }
            TitleMode::WaitDrop => {
                if self.since_curtain < LOGO_DROP_AT {
                    self.frame();
                } else {
                    self.mode = TitleMode::Drop;
                    self.drop_frame();
                }
            }
            TitleMode::Drop => {
                if self.vert_scroll > 0 {
                    self.drop_frame();
                } else {
                    self.mode = TitleMode::Shake(0, 0);
                    self.shake_frame();
                }
            }
            TitleMode::Shake(i, _half) => {
                if i <= 0x10 {
                    self.shake_frame();
                } else {
                    self.mode = TitleMode::Init(1);
                    self.frame();
                }
            }
            TitleMode::Init(n) => {
                if n < 3 + 0x17 {
                    self.frame();
                    self.mode = TitleMode::Init(n + 1);
                } else {
                    self.run_item(3);
                    self.frame();
                    self.mode = TitleMode::Fade(3, 1);
                }
            }
            TitleMode::Fade(item, n) => {
                if item > 6 {
                    self.glow = true;
                    self.mode = TitleMode::Intro;
                    self.frame();
                } else if n < 5 {
                    self.frame();
                    self.mode = TitleMode::Fade(item, n + 1);
                } else if item < 6 {
                    self.run_item((item + 1) as usize);
                    self.frame();
                    self.mode = TitleMode::Fade(item + 1, 1);
                } else {
                    self.glow = true;
                    self.mode = TitleMode::Intro;
                    self.frame();
                }
            }
            TitleMode::Intro => {
                if self.flags.intro_done {
                    self.begin_menu(false);
                    self.menu_frame(0);
                } else {
                    self.frame();
                }
            }
            TitleMode::Menu => {
                if self.menu_tick(press) {
                    self.begin_cursor();
                }
            }
            TitleMode::Cursor(n) => {
                if n < 60 {
                    self.cursor_shown = (n & 0x18) != 0;
                    self.frame();
                    self.mode = TitleMode::Cursor(n + 1);
                } else {
                    self.cursor_shown = true;
                    self.ctx.ram.w8(vars::PAL_FADE_START, 0x3F);
                    self.mode = TitleMode::FadeOut(1);
                }
            }
            TitleMode::FadeOut(n) => {
                if n == 0 {
                    self.mode = TitleMode::FadeOut(1);
                } else {
                    for i in 0..64u16 {
                        let a = vars::NORMAL_PALETTE + i * 2;
                        let mut c = self.ctx.ram.u16(a);
                        if c == 0 {
                            continue;
                        }
                        if (c & 0x00E) != 0 {
                            c -= 2;
                        } else if (c & 0x0E0) != 0 {
                            c -= 0x20;
                        } else if (c & 0xE00) != 0 {
                            c -= 0x200;
                        }
                        self.ctx.ram.w16(a, c);
                        self.ctx.palette_normal[i as usize] = c;
                    }
                    if n >= 22 {
                        self.action = Some(TitleAction::StartGame);
                    } else {
                        self.mode = TitleMode::FadeOut(n + 1);
                    }
                }
            }
        }
    }

    fn curtain_step(&mut self) {
        self.vert_scroll = (self.vert_scroll + 2).min(208);
        if self.vert_scroll >= 78 {
            self.step_actors();
        }
        self.present();
        if self.vert_scroll >= 208 {
            self.since_curtain = 0;
            self.mode = TitleMode::Logo(8);
        }
    }

    fn drop_frame(&mut self) {
        self.vert_scroll = (self.vert_scroll - 5).max(0);
        if self.vert_scroll == 0 {
            self.sounds.push(crate::audio::sfx::ids::SMASH);
        }
        self.frame();
    }

    fn shake_frame(&mut self) {
        if let TitleMode::Shake(i, half) = self.mode {
            if half == 0 {
                self.vert_scroll = (self.vert_scroll - 2 + 480) % 480;
                self.frame();
                self.mode = TitleMode::Shake(i, 1);
            } else {
                self.vert_scroll = (self.vert_scroll + 2) % 480;
                self.frame();
                self.mode = TitleMode::Shake(i + 1, 0);
            }
        }
    }

    fn frame(&mut self) {
        if self.since_curtain >= 0 {
            self.since_curtain += 1;
        }
        self.step_actors();
        if self.glow {
            self.glow_ticker -= 1;
            if self.glow_ticker < 0 {
                self.glow_ticker = 4;
                let c = self.ctx.assets.prg(24, 0xAAAA + self.glow_index as usize);
                self.ppu.write(0x3F0E, c);
                self.glow_index = (self.glow_index + 1) & 7;
            }
        }
        self.present();
    }

    fn step_actors(&mut self) {
        if !self.scripts {
            return;
        }
        let scroll = self.vert_scroll;
        let since = self.since_curtain;
        let before = self.flags.clone();
        let sonic_phase = self.sonic.phase;
        let sonic_rev = self.sonic.rev;
        let tails_phase = self.tails.phase;
        let mut a = self.actors;
        let goomba_x = a[2].x;
        self.sonic
            .step(&mut a[0], &mut self.flags, scroll, since, goomba_x);
        self.tails.step(&mut a[1], &mut self.flags);
        {
            let (goomba, shell) = a[2..].split_at_mut(1);
            self.prop.step(
                &mut goomba[0],
                &mut shell[0],
                &mut self.flags,
                scroll,
                since,
            );
        }
        self.actors = a;
        if !before.goomba_hit && self.flags.goomba_hit {
            self.sounds.push(crate::audio::sfx::ids::EXPLOSION);
        }
        if !before.shell_kicked && self.flags.shell_kicked {
            self.sounds.push(crate::audio::sfx::ids::SMASH);
        }
        if !before.tails_home && self.flags.tails_home {
            self.sounds.push(crate::audio::sfx::ids::HELICOPTER);
        }
        if self.sonic.phase == 9 && sonic_phase != 9 {
            self.sounds.push(crate::audio::sfx::ids::SPINDASH_REV);
        }
        if self.sonic.phase == 10 && self.sonic.rev > sonic_rev {
            self.sounds.push(crate::audio::sfx::ids::SPINDASH_REV);
        }
        if self.sonic.phase == 11 && sonic_phase != 11 {
            self.sounds.push(crate::audio::sfx::ids::SPINDASH_RELEASE);
        }
        if matches!(self.sonic.phase, 13 | 14) && !matches!(sonic_phase, 13 | 14) {
            self.sounds.push(crate::audio::sfx::ids::ROLL);
        }
        if self.tails.phase == 3 && tails_phase != 3 {
            self.sounds.push(crate::audio::sfx::ids::JUMP);
        }
        if matches!(self.tails.phase, 6 | 9) && !matches!(tails_phase, 6 | 9) {
            self.sounds.push(crate::audio::sfx::ids::HELICOPTER);
        }
    }

    fn begin_menu(&mut self, skip: bool) {
        if skip {
            self.vert_scroll = 0;
            for item in (6..=0x22).rev() {
                self.run_item(item);
            }
            self.glow = true;
            self.final_poses();
        }
        self.run_item(7);
        self.menu = true;
        self.ctx.ram.w8(vars::CTRL1_PRESS, 0);
        self.reset_count = 20;
        self.reset_fine = 60;
        self.mode = TitleMode::Menu;
    }

    fn menu_frame(&mut self, press: u8) {
        if self.menu_tick(press) {
            self.begin_cursor();
        }
    }

    fn menu_tick(&mut self, press: u8) -> bool {
        self.reset_fine -= 1;
        if self.reset_fine == 0 {
            self.reset_fine = 96;
            self.reset_count -= 1;
            if self.reset_count == 0 {
                self.action = Some(TitleAction::BackToTitle);
                return false;
            }
        }
        if !self.single_player
            && (((press & 0x70) != 0 && (press & 0x80) == 0) || (press & 0x03) != 0)
        {
            self.player ^= 1;
        }
        if press & 0x80 != 0 {
            self.sounds.push(crate::audio::sfx::ids::RING);
            return true;
        }
        self.frame();
        false
    }

    fn begin_cursor(&mut self) {
        self.cursor_shown = false;
        self.frame();
        self.mode = TitleMode::Cursor(1);
    }

    fn final_poses(&mut self) {
        self.scripts = false;
        self.actors[0] = TitleActor {
            x: 48.0,
            y: 172.0,
            frame: SONIC_STAND,
            flip_x: false,
            flip_y: false,
            visible: true,
        };
        self.actors[1] = TitleActor {
            x: 212.0,
            y: 176.0,
            frame: TAILS_STAND,
            flip_x: true,
            flip_y: false,
            visible: true,
        };
        self.actors[2].visible = false;
        self.actors[3].visible = false;
    }

    pub fn take_action(&mut self) -> Option<TitleAction> {
        self.action.take()
    }

    /// Sonic SFX ids queued by the curtain/logo/menu, for the title's 68k audio state.
    pub fn take_sounds(&mut self) -> Vec<u16> {
        std::mem::take(&mut self.sounds)
    }

    /// The native build no longer offers 2 PLAYER: blank the menu line while keeping the golden title art intact.
    pub fn set_single_player(&mut self) {
        self.single_player = true;
        self.player = 0;
    }

    fn present(&mut self) {
        let first = (((8 + self.vert_scroll) % 480) >> 3) as usize;
        let margin = |c: i32| Some(c.rem_euclid(32) as usize);
        let goomba_art = self.ctx.assets.goomba.clone();
        let koopa_art = self.ctx.assets.koopa.clone();
        let tails_art = self.ctx.assets.tails.clone();
        let sonic_art = self.ctx.assets.sonic.clone();
        let line4: Vec<(usize, u16)> = (1..16)
            .filter(|i| i & 3 != 0)
            .map(|i| {
                (
                    i,
                    super::nes::to_md(self.ctx.assets.prg(27, 0xAC92 + 0x80 + i)),
                )
            })
            .collect();
        let actors = self.actors;
        let menu = self.menu;
        let cursor_shown = self.cursor_shown;
        let player = self.player;
        let p = &mut self.ctx;
        let mut nes = NesPresenter::new(&mut p.vram, &mut p.ram, &p.assets);
        nes.bg_banks = [0x78, 0x79, 0x7A, 0x7B];
        nes.sprite_banks = [0x20, 0x21, 0x04, 0x7F];
        for i in 0..25 {
            let q = (first + i) % 60;
            let nt = if q < 30 { 0 } else { 2 };
            nes.row(
                &self.ppu,
                nt,
                q % 30,
                PLANE_A,
                q & 31,
                Some(&margin),
                None::<&fn(usize) -> bool>,
            );
        }
        for r in 24..30 {
            nes.row(
                &self.ppu,
                2,
                r,
                PLANE_B,
                r - 24,
                Some(&margin),
                None::<&fn(usize) -> bool>,
            );
        }
        nes.palettes(&self.ppu, vars::NORMAL_PALETTE);
        let cursor_tile = if menu && cursor_shown {
            Some(nes.sprite_tile(TITLE_CURSOR_TILE, 0))
        } else {
            None
        };
        drop(nes);
        for (i, c) in line4 {
            p.ram.w16(vars::NORMAL_PALETTE_LINE4 + i as u16 * 2, c);
        }

        let mut sk = SpriteKit::new(&mut p.ram, &mut p.vram);
        sk.begin();
        if let Some(t) = cursor_tile {
            sk.piece(
                TITLE_CURSOR_X + 32,
                TITLE_CURSOR_Y[player] + 1 - 8,
                1,
                0xE000 | t as u16,
            );
        }
        Self::draw_actor(&mut sk, actors[2], &goomba_art, 0x380 | 0x6000, None);
        Self::draw_actor(&mut sk, actors[3], &koopa_art, 0x380 | 0x6000, None);
        Self::draw_actor(
            &mut sk,
            actors[1],
            &tails_art,
            TITLE_TAILS_TILE,
            Some(TITLE_TAILS_TILE),
        );
        Self::draw_actor(
            &mut sk,
            actors[0],
            &sonic_art,
            TITLE_SONIC_TILE,
            Some(TITLE_SONIC_TILE),
        );
        sk.end();
    }

    fn draw_actor(
        sk: &mut SpriteKit<'_>,
        a: TitleActor,
        art: &SpriteData,
        art_tile: usize,
        dplc_tile: Option<usize>,
    ) {
        if !a.visible {
            return;
        }
        if let Some(t) = dplc_tile {
            sk.load_dplc(art, a.frame, t);
        }
        sk.frame(
            art,
            a.frame,
            a.x as i32 + 32,
            a.y as i32 - 8,
            art_tile as u16,
            a.flip_x,
            a.flip_y,
        );
    }

    fn plane_a(line: usize, vert_scroll: i32) -> Option<i32> {
        let n = line as i32 + 8;
        if n >= TITLE_SPLIT {
            return None;
        }
        let y = (n + vert_scroll) % 480;
        let q = y >> 3;
        Some((q & 31) * 8 + (y & 7) - line as i32)
    }

    fn plane_b(line: usize) -> Option<i32> {
        let n = line as i32 + 8;
        if n < TITLE_SPLIT {
            None
        } else {
            Some(n - TITLE_SPLIT - line as i32)
        }
    }

    pub fn render_hash(&mut self, v: &mut Vdp, _a: &FrontendAssets) -> u64 {
        let vs = self.vert_scroll;
        let a = move |line: usize| Self::plane_a(line, vs);
        self.ctx.render_hash(v, &a, &Self::plane_b)
    }

    pub fn render_rgba(&mut self, v: &mut Vdp) -> Vec<u8> {
        let vs = self.vert_scroll;
        let a = move |line: usize| Self::plane_a(line, vs);
        let mut rgba = self.ctx.render_rgba(v, &a, &Self::plane_b);
        if self.menu && self.single_player {
            // The title art still contains SMB3's "2 PLAYER GAME" line; cover it with the nearby backdrop.
            let source = (170 * 320 + 100) * 4;
            let background = [
                rgba[source],
                rgba[source + 1],
                rgba[source + 2],
                rgba[source + 3],
            ];
            for y in 161..178 {
                for x in 88..232 {
                    let i = (y * 320 + x) * 4;
                    rgba[i..i + 4].copy_from_slice(&background);
                }
            }
        }
        rgba
    }
}
