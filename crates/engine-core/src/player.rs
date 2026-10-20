//! Sonic's player physics: `Obj01_Init`, `Obj01_Control`, the ground/roll/air modes, jumping and the terrain
//! responses, ported routine by routine from `legacy/src/SonicBros3/Logic/Player/Player.cs`.
//!
//! State lives in [`Ram`] at the object slot (`$B000` for Sonic); this module only names the operations. The port
//! keeps the original's 16.16 positions, 8.8 velocities and wrapping `u8`/`u16`/`i16` arithmetic. Side effects that
//! belong to later milestones are explicit no-ops: sprite display, dynamic PLC art, sound and ring/object touching.

use crate::animation::PlayerAnimation;
use crate::audio::sfx;
use crate::collision::{self, CollisionSets};
use crate::math;
use crate::objects::{sprites, touch};
use crate::{vars, Obj, Ram};
use std::fmt;

/// Sonic's animation scripts (`PlayerAnim` in the legacy `Ids.cs`).
pub mod anim {
    pub const WALK: u8 = 0;
    pub const RUN: u8 = 1;
    pub const ROLL: u8 = 2;
    pub const PUSH: u8 = 4;
    pub const WAIT: u8 = 5;
    pub const BALANCE: u8 = 6;
    pub const LOOK_UP: u8 = 7;
    pub const DUCK: u8 = 8;
    pub const SPINDASH: u8 = 9;
    pub const BLINK: u8 = 0x0A;
    pub const GET_UP: u8 = 0x0B;
    pub const BALANCE2: u8 = 0x0C;
    pub const STOP: u8 = 0x0D;
    pub const HANG2: u8 = 0x14;
    pub const DEATH: u8 = 0x18;
    pub const HURT2: u8 = 0x1A;
    pub const BALANCE3: u8 = 0x1D;
    pub const BALANCE4: u8 = 0x1E;
}

/// Player routines (`$24`).
pub mod routine {
    pub const INIT: u8 = 0;
    pub const CONTROL: u8 = 2;
    pub const HURT: u8 = 4;
    pub const DEAD: u8 = 6;
    pub const GONE: u8 = 8;
}

const BUTTON_ABC: u8 = 0x70;
const UP: usize = 0;
const DOWN: usize = 1;
const LEFT: usize = 2;
const RIGHT: usize = 3;

/// A path the legacy engine refuses to run (it throws `UnportedException`). Reproduced as a value so the session
/// can stop instead of silently guessing.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Unported {
    pub address: u32,
    pub what: String,
}

impl Unported {
    pub fn new(address: u32, what: impl Into<String>) -> Self {
        Self {
            address,
            what: what.into(),
        }
    }
}

impl fmt::Display for Unported {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "unported at ${:X}: {}", self.address, self.what)
    }
}

/// The SMB3 water map (`Smb3Gameplay.IsWater`): the source area's tiles, one byte per 16x16 cell.
#[derive(Clone, Debug, Default)]
pub struct WaterMap {
    pub columns: i32,
    pub rows: i32,
    pub tileset: i32,
    pub tiles: Vec<u8>,
}

impl WaterMap {
    pub fn is_water(&self, x: i32, y: i32) -> bool {
        let c = x / 16;
        let r = y / 16;
        if x < 0 || y < 0 || c >= self.columns || r >= self.rows {
            return false;
        }
        let index = (r * self.columns + c) as usize;
        let Some(&tile) = self.tiles.get(index) else {
            return false;
        };
        water_tile(self.tileset, tile)
    }
}

fn water_tile(set: i32, tile: u8) -> bool {
    match set {
        3 | 14 => tile == 0xC2 || tile == 0xC3,
        6 => matches!(tile, 0x8B | 0x8C | 0x8F | 0x90),
        1 | 4 | 5 | 9 | 11 | 12 | 13 => {
            (0xD8..=0xDE).contains(&tile) || tile == 0xE0 || tile == 0xE1
        }
        _ => false,
    }
}

/// The addresses that differ between Sonic and Tails (`PlayerProfile`).
#[derive(Clone, Copy, Debug)]
pub struct PlayerProfile {
    pub address: u16,
    pub is_tails: bool,
    pub speed: u16,
    pub ctrl: u16,
    pub look_delay: u16,
    pub camera_bias: u16,
    pub cam_min_x: u16,
    pub cam_max_x: u16,
    pub cam_max_y: u16,
    pub stand_y_radius: i8,
    pub roll_shift: i32,
    pub dust: u16,
    pub scroll_delay: u16,
}

impl PlayerProfile {
    pub const SONIC: Self = Self {
        address: vars::MAIN_CHARACTER,
        is_tails: false,
        speed: vars::SONIC_TOP_SPEED,
        ctrl: vars::CTRL1_HELD_LOGICAL,
        look_delay: vars::SONIC_LOOK_DELAY_COUNTER,
        camera_bias: vars::CAMERA_Y_POS_BIAS,
        cam_min_x: vars::CAMERA_MIN_X_POS,
        cam_max_x: vars::CAMERA_MAX_X_POS,
        cam_max_y: vars::CAMERA_MAX_Y_POS,
        stand_y_radius: 0x13,
        roll_shift: 5,
        dust: vars::SONIC_DUST,
        scroll_delay: vars::HORIZ_SCROLL_DELAY_VAL,
    };

    /// Tails on the main-character slot for 2P turns: Tails' physics and tail sprite, but the camera, rings and
    /// object collisions stay on the main player (so the unported Tails-alone camera path is not needed).
    pub const TAILS_MAIN: Self = Self {
        address: vars::MAIN_CHARACTER,
        is_tails: true,
        speed: vars::TAILS_TOP_SPEED,
        ctrl: vars::CTRL2_HELD_LOGICAL,
        look_delay: vars::TAILS_LOOK_DELAY_COUNTER,
        camera_bias: vars::CAMERA_Y_POS_BIAS,
        cam_min_x: vars::CAMERA_MIN_X_POS,
        cam_max_x: vars::CAMERA_MAX_X_POS,
        cam_max_y: vars::CAMERA_MAX_Y_POS,
        stand_y_radius: 0x0F,
        roll_shift: 1,
        dust: vars::TAILS_DUST,
        scroll_delay: vars::HORIZ_SCROLL_DELAY_VAL,
    };

    pub const TAILS: Self = Self {
        address: vars::SIDEKICK,
        is_tails: true,
        speed: vars::TAILS_TOP_SPEED,
        ctrl: vars::CTRL2_HELD_LOGICAL,
        look_delay: vars::TAILS_LOOK_DELAY_COUNTER,
        camera_bias: vars::CAMERA_Y_POS_BIAS_P2,
        cam_min_x: vars::TAILS_MIN_X_POS,
        cam_max_x: vars::TAILS_MAX_X_POS,
        cam_max_y: vars::TAILS_MAX_Y_POS,
        stand_y_radius: 0x0F,
        roll_shift: 1,
        dust: vars::TAILS_DUST,
        scroll_delay: vars::HORIZ_SCROLL_DELAY_VAL_P2,
    };
}

/// Sonic (`obj 01`, ROM `0x1DCEE`).
#[derive(Clone, Copy, Debug)]
pub struct Player {
    pub obj: Obj,
    pub profile: PlayerProfile,
}

impl Default for Player {
    fn default() -> Self {
        Self::sonic()
    }
}

impl Player {
    pub fn sonic() -> Self {
        Self {
            obj: Obj::new(vars::MAIN_CHARACTER),
            profile: PlayerProfile::SONIC,
        }
    }

    pub fn tails() -> Self {
        Self {
            obj: Obj::new(vars::SIDEKICK),
            profile: PlayerProfile::TAILS,
        }
    }

    /// Obj01/Obj02 entry: dispatch on the routine counter; `Init` falls through into `Control`.
    pub fn step(
        &mut self,
        ram: &mut Ram,
        sets: &CollisionSets,
        animation: &dyn PlayerAnimation,
        water: Option<&WaterMap>,
    ) -> Result<(), Unported> {
        if self.profile.is_tails {
            return self.tails_step(ram, sets, animation, water);
        }
        match self.obj.routine(ram) {
            routine::INIT => {
                self.init(ram);
                self.control(ram, sets, animation, water)
            }
            routine::CONTROL => self.control(ram, sets, animation, water),
            routine::HURT => self.hurt(ram, sets, animation),
            routine::DEAD => self.dead(ram, sets, animation),
            routine::GONE => {
                self.gone(ram);
                Ok(())
            }
            other => Err(Unported::new(0x1DCEE, format!("Obj01 routine {other}"))),
        }
    }

    // ---------------------------------------------------------------- helpers

    fn held(&self, ram: &Ram) -> i32 {
        ram.u8(self.profile.ctrl) as i32
    }

    fn pressed(&self, ram: &Ram) -> i32 {
        ram.u8(self.profile.ctrl.wrapping_add(1)) as i32
    }

    fn held_bit(&self, ram: &Ram, bit: usize) -> bool {
        (self.held(ram) & (1 << bit)) != 0
    }

    fn is_super(&self, ram: &Ram) -> bool {
        !self.profile.is_tails && ram.u8(vars::SUPER_SONIC_FLAG) != 0
    }

    fn restart_timer(&self, ram: &Ram) -> u16 {
        self.obj.word(ram, 0x3A)
    }

    fn set_restart_timer(&self, ram: &mut Ram, v: u16) {
        self.obj.set_word(ram, 0x3A, v);
    }

    // ---------------------------------------------------------------- routines

    /// `Obj01_Init` (0x1DD08).
    fn init(&mut self, ram: &mut Ram) {
        let o = self.obj;
        o.set_routine(ram, routine::CONTROL);
        o.set_y_radius(ram, 0x13);
        o.set_x_radius(ram, 9);
        o.set_mappings(ram, 0x76860);
        o.set_priority(ram, 2);
        o.set_width_pixels(ram, 0x18);
        o.set_render_flags(ram, 4);
        self.set_normal_speeds(ram);
        if ram.u8(vars::LAST_STAR_POLE_HIT) == 0 {
            o.set_art_tile(ram, 0x780);
            o.set_top_solid_bit(ram, 0x0C);
            o.set_lrb_solid_bit(ram, 0x0D);
            ram.w16(vars::SAVED_X_POS, o.x(ram));
            ram.w16(vars::SAVED_Y_POS, o.y(ram));
            ram.w16(vars::SAVED_ART_TILE, o.art_tile(ram));
            ram.w16(vars::SAVED_SOLID_BITS, o.solid_bits(ram));
        }
        o.set_flips_remaining(ram, 0);
        o.set_flip_speed(ram, 4);
        ram.w8(vars::SUPER_SONIC_FLAG, 0);
        o.set_air_left(ram, 0x1E);
        // Fill the position history (0x1DD90): record 64 times from 32 px left / 4 px down, clearing the entries.
        o.set_x(ram, o.x(ram).wrapping_sub(0x20));
        o.set_y(ram, o.y(ram).wrapping_add(4));
        ram.w16(vars::SONIC_POS_RECORD_INDEX, 0);
        for _ in 0..0x40 {
            let index = ram.u16(vars::SONIC_POS_RECORD_INDEX);
            self.record_pos(ram);
            ram.w32(vars::SONIC_STAT_RECORD_BUF.wrapping_add(index), 0);
        }
        o.set_x(ram, o.x(ram).wrapping_add(0x20));
        o.set_y(ram, o.y(ram).wrapping_sub(4));
    }

    /// `Obj01_Control` (0x1DDC2).
    fn control(
        &mut self,
        ram: &mut Ram,
        sets: &CollisionSets,
        animation: &dyn PlayerAnimation,
        water: Option<&WaterMap>,
    ) -> Result<(), Unported> {
        let o = self.obj;
        self.water(ram, water)?;
        if ram.u8(vars::CONTROL_LOCKED) == 0 {
            ram.w16(vars::CTRL1_HELD_LOGICAL, ram.u16(vars::CTRL1_HELD));
        }
        if (o.obj_control(ram) & 1) == 0 {
            match o.status(ram) & 6 {
                0 => self.md_normal_checks(ram, sets),
                2 => self.md_air(ram, sets, water)?,
                4 => self.md_roll(ram, sets),
                _ => self.md_air(ram, sets, water)?, // MdJump (0x1E0B0) is identical to MdAir
            }
        }
        if ram.u16(vars::CAMERA_MIN_Y_POS) == 0xFF00 && ram.u8(vars::HACK_ZONE_FLAG) == 0 {
            o.set_y(ram, o.y(ram) & 0x7FF);
        }
        self.display(ram);
        if self.is_super(ram) {
            return Err(Unported::new(0x1E976, "Sonic_Super"));
        }
        self.record_pos(ram);
        self.water(ram, water)?;
        o.set_next_tilt_copy(ram, ram.u8(vars::PRIMARY_ANGLE));
        o.set_tilt_copy(ram, ram.u8(vars::SECONDARY_ANGLE));
        if ram.bit(vars::WIND_TUNNEL_FLAG, 0) && o.anim(ram) == anim::WALK {
            o.set_anim(ram, o.prev_anim(ram));
        }
        animation.animate(ram, o);
        if (o.obj_control(ram) as i8) >= 0 {
            touch::run(ram, o);
        }
        self.load_dyn_plc(ram);
        Ok(())
    }

    /// `LoadSonicDynPLC` (0x1F65E): only the RAM bookkeeping (art loading needs PLC/VRAM, a later milestone).
    fn load_dyn_plc(&self, ram: &mut Ram) {
        let frame = self.obj.mapping_frame(ram);
        let last = if self.profile.is_tails {
            vars::TAILS_LAST_LOADED_DPLC
        } else {
            vars::SONIC_LAST_LOADED_DPLC
        };
        if frame == ram.u8(last) {
            return;
        }
        ram.w8(last, frame);
    }

    /// `Obj01_Dead` (0x1EFB4).
    fn dead(
        &mut self,
        ram: &mut Ram,
        sets: &CollisionSets,
        animation: &dyn PlayerAnimation,
    ) -> Result<(), Unported> {
        let _ = sets;
        if ram.u16(vars::DEBUG_MODE_FLAG) != 0 && (ram.u8(vars::CTRL1_PRESS) & 0x10) != 0 {
            return Err(Unported::new(0x1EFC2, "debug mode"));
        }
        self.game_over_check(ram)?;
        self.object_move_and_fall(ram);
        self.record_pos(ram);
        animation.animate(ram, self.obj);
        self.load_dyn_plc(ram);
        sprites::display_sprite(ram, self.obj);
        Ok(())
    }

    /// `CheckGameOver` (0x1EFEA). The game-over objects and music (no lives left) are M4+ side effects; the RAM
    /// they would write is written here, the slot spawn is not.
    fn game_over_check(&mut self, ram: &mut Ram) -> Result<(), Unported> {
        let o = self.obj;
        ram.w8(vars::SCROLL_LOCK, 1);
        o.set_spindash_flag(ram, 0);
        let limit = (ram.u16(vars::CAMERA_MAX_Y_POS).wrapping_add(0x100)) as i16;
        if limit >= o.y(ram) as i16 {
            return Ok(());
        }
        o.set_routine(ram, routine::GONE);
        self.set_restart_timer(ram, 0x3C);
        if ram.u8(vars::TIMED_MODE) != 0 {
            return Err(Unported::new(0x1F062, "death in the hack's timed mode"));
        }
        ram.w8(
            vars::UPDATE_HUD_LIVES,
            ram.u8(vars::UPDATE_HUD_LIVES).wrapping_add(1),
        );
        ram.w8(vars::LIFE_COUNT, ram.u8(vars::LIFE_COUNT).wrapping_sub(1));
        if ram.u8(vars::LIFE_COUNT) == 0 {
            // 0x1F022: no restart; the game-over objects take over.
            self.set_restart_timer(ram, 0);
            let game = Obj::new(vars::TITLE_CARD_ZONE_NAME);
            let over = Obj::new(vars::TITLE_CARD_ZONE);
            game.set_id(ram, 0x39);
            over.set_id(ram, 0x39);
            over.set_mapping_frame(ram, 1);
            game.set_word(ram, 0x3E, o.address());
            ram.w8(vars::TIME_OVER_FLAG, 0);
            ram.w8(vars::UPDATE_HUD_TIMER, 0);
            ram.w8(vars::UPDATE_HUD_TIMER_P2, 0);
            o.set_routine(ram, routine::GONE);
            return Ok(());
        }
        if ram.u8(vars::TIME_OVER_FLAG) != 0 {
            return Err(Unported::new(0x1F068, "time over"));
        }
        if ram.u16(vars::TWO_PLAYER_MODE) != 0 {
            return Err(Unported::new(0x1F0A6, "death in 2-player mode"));
        }
        Ok(())
    }

    /// `Obj01_Gone` (0x1F104).
    fn gone(&mut self, ram: &mut Ram) {
        let t = self.restart_timer(ram);
        if t == 0 {
            return;
        }
        self.set_restart_timer(ram, t - 1);
        if t - 1 == 0 {
            ram.w16(vars::LEVEL_INACTIVE_FLAG, 1);
        }
    }

    /// `Obj01_Hurt` (0x1EEEE).
    fn hurt(
        &mut self,
        ram: &mut Ram,
        sets: &CollisionSets,
        animation: &dyn PlayerAnimation,
    ) -> Result<(), Unported> {
        let o = self.obj;
        if ram.u16(vars::DEBUG_MODE_FLAG) != 0 && (ram.u8(vars::CTRL1_PRESS) & 0x10) != 0 {
            return Err(Unported::new(0x1EEFC, "debug mode"));
        }
        if (o.routine_secondary(ram) as i8) < 0 {
            // 0x1EF98: back to normal control.
            o.set_routine(ram, routine::CONTROL);
            o.set_routine_secondary(ram, 0);
            self.record_pos(ram);
            animation.animate(ram, o);
            self.load_dyn_plc(ram);
            sprites::display_sprite(ram, o);
            return Ok(());
        }
        self.object_move(ram);
        o.set_y_vel(ram, o.y_vel(ram).wrapping_add(0x30));
        if o.underwater(ram) {
            o.set_y_vel(ram, o.y_vel(ram).wrapping_sub(0x20));
        }
        if ram.u16(vars::CAMERA_MIN_Y_POS) == 0xFF00 {
            o.set_y(ram, o.y(ram) & 0x7FF);
        }
        self.hurt_stop(ram, sets);
        self.level_bound(ram)?;
        self.record_pos(ram);
        animation.animate(ram, o);
        self.load_dyn_plc(ram);
        sprites::display_sprite(ram, o);
        Ok(())
    }

    /// `Sonic_HurtStop` (0x1EF52).
    fn hurt_stop(&mut self, ram: &mut Ram, sets: &CollisionSets) {
        let o = self.obj;
        let bottom = ram.u16(self.profile.cam_max_y).wrapping_add(0xE0) as i16;
        if bottom < o.y(ram) as i16 {
            self.fell_out(ram);
            return;
        }
        self.do_level_collision(ram, sets);
        if o.in_air(ram) {
            return;
        }
        o.set_y_vel(ram, 0);
        o.set_x_vel(ram, 0);
        o.set_inertia(ram, 0);
        o.set_obj_control(ram, 0);
        o.set_anim(ram, anim::WALK);
        o.set_routine(ram, routine::CONTROL);
        o.set_invulnerable_time(ram, 0x78);
        o.set_spindash_flag(ram, 0);
    }

    /// `Sonic_Display` (0x1DE46): the timers (drawing is a later milestone).
    fn display(&mut self, ram: &mut Ram) {
        let o = self.obj;
        let timer = o.invulnerable_time(ram);
        if timer != 0 {
            o.set_invulnerable_time(ram, timer.wrapping_sub(1));
        }
        // `(timer == 0 || ((timer >> 2) & 1) != 0)` displays the sprite (it blinks while invulnerable).
        if timer == 0 || ((timer >> 2) & 1) != 0 {
            sprites::display_sprite(ram, o);
        }
        if o.invincible(ram) && o.invincibility_time(ram) != 0 {
            let next = o.invincibility_time(ram).wrapping_sub(1);
            o.set_invincibility_time(ram, next);
            if next == 0 {
                // The music restart (0x1F00A) is a sound side effect.
                o.set_invincible(ram, false);
            }
        }
        if o.has_speed_shoes(ram) && o.speed_shoes_time(ram) != 0 {
            let next = o.speed_shoes_time(ram).wrapping_sub(1);
            o.set_speed_shoes_time(ram, next);
            if next == 0 {
                self.set_normal_speeds(ram);
                o.set_has_speed_shoes(ram, false);
            }
        }
    }

    fn set_normal_speeds(&self, ram: &mut Ram) {
        let super_sonic = self.is_super(ram);
        let shoes = self.obj.has_speed_shoes(ram);
        ram.w16(
            self.profile.speed,
            if super_sonic {
                0xA00
            } else if shoes {
                0xC00
            } else {
                0x600
            },
        );
        ram.w16(
            self.profile.speed + 2,
            if super_sonic {
                0x30
            } else if shoes {
                0x18
            } else {
                0x0C
            },
        );
        ram.w16(
            self.profile.speed + 4,
            if super_sonic { 0x100 } else { 0x80 },
        );
    }

    /// `Sonic_Water` (0x1DF06).
    fn water(&mut self, ram: &mut Ram, water: Option<&WaterMap>) -> Result<(), Unported> {
        let o = self.obj;
        if let Some(map) = water {
            let wet = map.is_water(o.x(ram) as i32, o.y(ram) as i32);
            self.set_water(ram, wet);
            return Ok(());
        }
        if ram.u8(vars::WATER_FLAG) == 0 {
            return Ok(());
        }
        if ram.s16(vars::WATER_LEVEL1) >= o.y(ram) as i16 {
            if !ram.test_clear(o.address().wrapping_add(0x22), 6) {
                return Ok(());
            }
            return Err(Unported::new(0x1DF86, "leaving water"));
        }
        Err(Unported::new(0x1DF18, "entering/under water"))
    }

    fn set_water(&mut self, ram: &mut Ram, wet: bool) {
        let o = self.obj;
        if wet == o.underwater(ram) {
            return;
        }
        o.set_underwater(ram, wet);
        if wet {
            ram.w16(self.profile.speed, 0x300);
            ram.w16(self.profile.speed + 2, 6);
            ram.w16(self.profile.speed + 4, 0x40);
            o.set_x_vel(ram, o.x_vel(ram) / 2);
            o.set_y_vel(ram, o.y_vel(ram) / 2);
        } else {
            self.set_normal_speeds(ram);
            let yv = o.y_vel(ram);
            if yv < 0 {
                o.set_y_vel(ram, (-0x600).max(yv as i32 * 2) as i16);
            }
        }
    }

    /// `Sonic_RecordPos` (0x1DEDC): position/input history (used by Tails).
    fn record_pos(&self, ram: &mut Ram) {
        let index = ram.u16(vars::SONIC_POS_RECORD_INDEX);
        ram.w16(
            vars::SONIC_POS_RECORD_BUF.wrapping_add(index),
            self.obj.x(ram),
        );
        ram.w16(
            vars::SONIC_POS_RECORD_BUF
                .wrapping_add(index)
                .wrapping_add(2),
            self.obj.y(ram),
        );
        ram.w8(
            vars::SONIC_POS_RECORD_INDEX.wrapping_add(1),
            ram.u8(vars::SONIC_POS_RECORD_INDEX.wrapping_add(1))
                .wrapping_add(4),
        );
        ram.w16(
            vars::SONIC_STAT_RECORD_BUF.wrapping_add(index),
            ram.u16(vars::CTRL1_HELD_LOGICAL),
        );
        ram.w16(
            vars::SONIC_STAT_RECORD_BUF
                .wrapping_add(index)
                .wrapping_add(2),
            ram.u16(self.obj.address().wrapping_add(0x22)),
        );
    }

    // ---------------------------------------------------------------- ground

    /// `Obj01_MdNormal_Checks` (0x1DFEE): idle animations suppress physics until input.
    fn md_normal_checks(&mut self, ram: &mut Ram, sets: &CollisionSets) {
        let o = self.obj;
        if self.profile.is_tails {
            self.md_normal(ram, sets); // Tails has no idle-animation check (Obj02_MdNormal_Checks)
            return;
        }
        if (self.pressed(ram) & BUTTON_ABC as i32) == 0 {
            let a = o.anim(ram);
            if a == anim::BLINK || a == anim::GET_UP {
                return;
            }
            if a == anim::WAIT && o.anim_frame(ram) >= 0x1E {
                if (self.held(ram) & 0x7F) == 0 {
                    return;
                }
                o.set_anim(ram, anim::BLINK);
                if o.anim_frame(ram) >= 0xAC {
                    o.set_anim(ram, anim::GET_UP);
                }
                return;
            }
        }
        self.md_normal(ram, sets);
    }

    /// `Obj01_MdNormal` (0x1E038).
    fn md_normal(&mut self, ram: &mut Ram, sets: &CollisionSets) {
        if self.check_spindash(ram, sets) {
            return;
        }
        if self.jump(ram, sets) {
            return;
        }
        self.slope_resist(ram);
        self.move_ground(ram, sets);
        self.roll(ram);
        let _ = self.level_bound(ram);
        self.object_move(ram);
        self.angle_pos(ram, sets);
        self.slope_repel(ram);
    }

    /// `Obj01_MdRoll` (0x1E08A).
    fn md_roll(&mut self, ram: &mut Ram, sets: &CollisionSets) {
        if self.obj.spindash_flag(ram) == 0 && self.jump(ram, sets) {
            return;
        }
        self.roll_repel(ram);
        self.roll_speed(ram, sets);
        let _ = self.level_bound(ram);
        self.object_move(ram);
        self.angle_pos(ram, sets);
        self.slope_repel(ram);
    }

    /// `Obj01_MdAir` / `MdJump` (0x1E060 / 0x1E0B0).
    fn md_air(
        &mut self,
        ram: &mut Ram,
        sets: &CollisionSets,
        water: Option<&WaterMap>,
    ) -> Result<(), Unported> {
        let o = self.obj;
        if water.is_some() && o.underwater(ram) {
            if (self.pressed(ram) & BUTTON_ABC as i32) != 0 {
                o.set_y_vel(ram, -0x280);
                o.set_jumping(ram, 0);
                o.set_roll_jumping(ram, false);
                sfx::sonic(ram, sfx::ids::JUMP);
            }
            o.set_y_vel(ram, o.y_vel(ram).min(0x180));
        } else {
            self.jump_height(ram)?;
        }
        self.chg_jump_dir(ram);
        let _ = self.level_bound(ram);
        self.object_move_and_fall(ram);
        if o.underwater(ram) {
            o.set_y_vel(ram, o.y_vel(ram).wrapping_sub(0x28));
        }
        if water.is_some() && o.underwater(ram) {
            o.set_y_vel(ram, o.y_vel(ram).min(0x180));
        }
        self.jump_angle(ram);
        self.do_level_collision(ram, sets);
        Ok(())
    }

    /// `Sonic_Move` (0x1E0DA).
    fn move_ground(&mut self, ram: &mut Ram, sets: &CollisionSets) {
        let o = self.obj;
        let top = ram.s16(self.profile.speed) as i32;
        let acc = ram.s16(self.profile.speed + 2) as i32;
        let dec = ram.s16(self.profile.speed + 4) as i32;
        if (o.status_secondary(ram) as i8) < 0 {
            self.apply_ground_velocity(ram, sets);
            return;
        }
        if o.move_lock(ram) != 0 {
            self.look_timer_reset(ram);
            self.friction_and_velocity(ram, sets, acc);
            return;
        }
        if self.held_bit(ram, LEFT) {
            self.move_left(ram, top, acc, dec);
        }
        if self.held_bit(ram, RIGHT) {
            self.move_right(ram, top, acc, dec);
        }
        if ((o.angle(ram) as i32 + 0x20) & 0xC0) != 0 || o.inertia(ram) != 0 {
            self.look_timer_reset(ram);
            self.friction_and_velocity(ram, sets, acc);
            return;
        }

        // Standing still: balancing and looking (0x1E126).
        o.set_pushing(ram, false);
        o.set_anim(ram, anim::WAIT);
        let balancing = if self.profile.is_tails {
            self.tails_balance(ram, sets)
        } else if o.on_object(ram) {
            self.balance_on_object(ram)
        } else {
            self.balance_on_ledge(ram, sets)
        };
        if !balancing {
            self.look_up_down(ram, sets, acc);
            return;
        }
        self.look_timer_reset(ram);
        self.friction_and_velocity(ram, sets, acc);
    }

    /// 0x1E13C: balancing on the edge of an object.
    fn balance_on_object(&mut self, ram: &mut Ram) -> bool {
        let o = self.obj;
        let obj = o.interact_object(ram);
        if (obj.status(ram) as i8) < 0 {
            return false;
        }
        let mut d1 = obj.width_pixels(ram) as i32;
        let d2 = d1 * 2 - 2;
        d1 = (d1 + o.x(ram) as i32 - obj.x(ram) as i32) as i16 as i32;
        if self.is_super(ram) {
            if d1 < 2 {
                o.set_facing_left(ram, true);
                o.set_anim(ram, anim::BALANCE);
                return true;
            }
            if d1 >= d2 {
                o.set_facing_left(ram, false);
                o.set_anim(ram, anim::BALANCE);
                return true;
            }
            return false;
        }
        if d1 < 2 {
            if o.facing_left(ram) {
                o.set_anim(ram, anim::BALANCE);
                if d1 < -4 {
                    o.set_anim(ram, anim::BALANCE2);
                }
            } else {
                o.set_anim(ram, anim::BALANCE3);
                if d1 < -4 {
                    o.set_anim(ram, anim::BALANCE4);
                    o.set_facing_left(ram, true);
                }
            }
            return true;
        }
        if d1 >= d2 {
            if !o.facing_left(ram) {
                o.set_anim(ram, anim::BALANCE);
                if d1 >= d2 + 6 {
                    o.set_anim(ram, anim::BALANCE2);
                }
            } else {
                o.set_anim(ram, anim::BALANCE3);
                if d1 >= d2 + 6 {
                    o.set_anim(ram, anim::BALANCE4);
                    o.set_facing_left(ram, false);
                }
            }
            return true;
        }
        false
    }

    /// 0x1E20C: balancing on a ledge.
    fn balance_on_ledge(&mut self, ram: &mut Ram, sets: &CollisionSets) -> bool {
        let o = self.obj;
        if collision::floor_edge(ram, sets, o, o.x(ram) as i32).0 < 0x0C {
            return false;
        }
        if self.is_super(ram) {
            if o.next_tilt_copy(ram) == 3 {
                o.set_facing_left(ram, false);
                o.set_anim(ram, anim::BALANCE);
                return true;
            }
            if o.tilt_copy(ram) == 3 {
                o.set_facing_left(ram, true);
                o.set_anim(ram, anim::BALANCE);
                return true;
            }
            return false;
        }
        if o.next_tilt_copy(ram) == 3 {
            if !o.facing_left(ram) {
                o.set_anim(ram, anim::BALANCE);
                if collision::floor_edge(ram, sets, o, o.x(ram) as i32 - 6).0 >= 0x0C {
                    o.set_anim(ram, anim::BALANCE2);
                }
            } else {
                o.set_anim(ram, anim::BALANCE3);
                if collision::floor_edge(ram, sets, o, o.x(ram) as i32 - 6).0 >= 0x0C {
                    o.set_anim(ram, anim::BALANCE4);
                    o.set_facing_left(ram, false);
                }
            }
            return true;
        }
        if o.tilt_copy(ram) == 3 {
            if o.facing_left(ram) {
                o.set_anim(ram, anim::BALANCE);
                if collision::floor_edge(ram, sets, o, o.x(ram) as i32 + 6).0 >= 0x0C {
                    o.set_anim(ram, anim::BALANCE2);
                }
            } else {
                o.set_anim(ram, anim::BALANCE3);
                if collision::floor_edge(ram, sets, o, o.x(ram) as i32 + 6).0 >= 0x0C {
                    o.set_anim(ram, anim::BALANCE4);
                    o.set_facing_left(ram, true);
                }
            }
            return true;
        }
        false
    }

    /// 0x1E304: looking up / ducking, which moves the camera bias after a delay.
    fn look_up_down(&mut self, ram: &mut Ram, sets: &CollisionSets, acc: i32) {
        let o = self.obj;
        let delay = self.profile.look_delay;
        let bias = self.profile.camera_bias;
        if self.held_bit(ram, UP) {
            o.set_anim(ram, anim::LOOK_UP);
            ram.w16(delay, ram.u16(delay).wrapping_add(1));
            if ram.u16(delay) < 0x78 {
                self.bias_toward_default(ram);
                self.friction_and_velocity(ram, sets, acc);
                return;
            }
            ram.w16(delay, 0x78);
            if ram.u16(bias) != 0xC8 {
                ram.w16(bias, ram.u16(bias).wrapping_add(2));
            }
            self.friction_and_velocity(ram, sets, acc);
            return;
        }
        if self.held_bit(ram, DOWN) {
            o.set_anim(ram, anim::DUCK);
            ram.w16(delay, ram.u16(delay).wrapping_add(1));
            if ram.u16(delay) < 0x78 {
                self.bias_toward_default(ram);
                self.friction_and_velocity(ram, sets, acc);
                return;
            }
            ram.w16(delay, 0x78);
            if ram.u16(bias) != 8 {
                ram.w16(bias, ram.u16(bias).wrapping_sub(2));
            }
            self.friction_and_velocity(ram, sets, acc);
            return;
        }
        self.look_timer_reset(ram);
        self.friction_and_velocity(ram, sets, acc);
    }

    /// 0x1E360 / 0x1E366.
    fn look_timer_reset(&mut self, ram: &mut Ram) {
        ram.w16(self.profile.look_delay, 0);
        self.bias_toward_default(ram);
    }

    fn bias_toward_default(&mut self, ram: &mut Ram) {
        let bias = self.profile.camera_bias;
        let mut v = ram.u16(bias);
        if v == 0x60 {
            return;
        }
        if v < 0x60 {
            v += 4;
        }
        ram.w16(bias, v.wrapping_sub(2));
    }

    /// 0x1E378: friction when no direction is held, then 0x1E3B0.
    fn friction_and_velocity(&mut self, ram: &mut Ram, sets: &CollisionSets, acc: i32) {
        let d5 = if self.is_super(ram) { 0x0C } else { acc };
        if (self.held(ram) & 0x0C) == 0 {
            let mut d0 = self.obj.inertia(ram) as u16 as i32;
            if d0 != 0 {
                if (d0 as i16) > 0 {
                    d0 = if d0 < d5 { 0 } else { d0 - d5 };
                } else {
                    let sum = d0 + d5;
                    d0 = if sum > 0xFFFF { 0 } else { sum };
                }
                self.obj.set_inertia(ram, d0 as i16);
            }
        }
        self.apply_ground_velocity(ram, sets);
    }

    /// 0x1E3B0: velocity from inertia and angle, then the wall check in the direction of travel.
    fn apply_ground_velocity(&mut self, ram: &mut Ram, sets: &CollisionSets) {
        let o = self.obj;
        let (sin, cos) = math::calc_sine(o.angle(ram));
        let inertia = o.inertia(ram) as i32;
        o.set_x_vel(ram, ((cos as i32 * inertia) >> 8) as i16);
        o.set_y_vel(ram, ((sin as i32 * inertia) >> 8) as i16);
        self.wall_check_ahead(ram, sets);
    }

    /// 0x1E3CE: stop against walls in the direction of travel.
    fn wall_check_ahead(&mut self, ram: &mut Ram, sets: &CollisionSets) {
        let o = self.obj;
        if (o.angle(ram).wrapping_add(0x40) as i8) < 0 {
            return;
        }
        if o.inertia(ram) == 0 {
            return;
        }
        let d1 = if o.inertia(ram) > 0 { -0x40 } else { 0x40 };
        let d0 = (o.angle(ram) as i32 + d1) & 0xFF;
        let (dist, _) = collision::calc_room_in_front(ram, sets, o, d0);
        if dist >= 0 {
            return;
        }
        let push = ((dist << 8) as i16) as i32;
        match (d0 + 0x20) & 0xC0 {
            0x00 => o.set_y_vel(ram, o.y_vel(ram).wrapping_add(push as i16)),
            0x40 => {
                o.set_x_vel(ram, o.x_vel(ram).wrapping_sub(push as i16));
                o.set_pushing(ram, true);
                o.set_inertia(ram, 0);
            }
            0x80 => o.set_y_vel(ram, o.y_vel(ram).wrapping_sub(push as i16)),
            _ => {
                o.set_x_vel(ram, o.x_vel(ram).wrapping_add(push as i16));
                o.set_pushing(ram, true);
                o.set_inertia(ram, 0);
            }
        }
    }

    /// `Sonic_MoveLeft` (0x1E440).
    fn move_left(&mut self, ram: &mut Ram, top: i32, acc: i32, dec: i32) {
        let o = self.obj;
        let mut d0 = o.inertia(ram) as i32;
        if d0 <= 0 {
            if !ram.test_set(o.address().wrapping_add(0x22), 0) {
                o.set_pushing(ram, false);
                o.set_prev_anim(ram, 1);
            }
            d0 -= acc;
            let d1 = -top;
            if d0 <= d1 {
                d0 += acc;
                if d0 > d1 {
                    d0 = d1;
                }
            }
            o.set_inertia(ram, d0 as i16);
            o.set_anim(ram, anim::WALK);
            return;
        }
        let u = (d0 as u16 as i32) - (dec as u16 as i32);
        d0 = if u < 0 { -0x80 } else { u };
        o.set_inertia(ram, d0 as i16);
        if ((o.angle(ram) as i32 + 0x20) & 0xC0) != 0 {
            return;
        }
        if ((d0 & 0xFF00) as i16) < 0x400 {
            return;
        }
        self.skid(ram, false);
    }

    /// `Sonic_MoveRight` (0x1E4C6).
    fn move_right(&mut self, ram: &mut Ram, top: i32, acc: i32, dec: i32) {
        let o = self.obj;
        let mut d0 = o.inertia(ram) as i32;
        if d0 >= 0 {
            if ram.test_clear(o.address().wrapping_add(0x22), 0) {
                o.set_pushing(ram, false);
                o.set_prev_anim(ram, 1);
            }
            d0 += acc;
            if d0 >= top {
                d0 -= acc;
                if d0 < top {
                    d0 = top;
                }
            }
            o.set_inertia(ram, d0 as i16);
            o.set_anim(ram, anim::WALK);
            return;
        }
        let u = (d0 as u16 as i32) + (dec as u16 as i32);
        d0 = if u > 0xFFFF { 0x80 } else { u };
        o.set_inertia(ram, d0 as i16);
        if ((o.angle(ram) as i32 + 0x20) & 0xC0) != 0 {
            return;
        }
        if ((d0 & 0xFF00) as i16) > (-0x400) {
            return;
        }
        self.skid(ram, true);
    }

    fn skid(&mut self, ram: &mut Ram, face_left: bool) {
        let o = self.obj;
        o.set_anim(ram, anim::STOP);
        o.set_facing_left(ram, face_left);
        sfx::sonic(ram, sfx::ids::SKIDDING);
        if o.air_left(ram) >= 0x0C {
            ram.w8(self.profile.dust.wrapping_add(0x24), 6);
            ram.w8(self.profile.dust.wrapping_add(0x1A), 0x15);
        }
    }

    /// `Sonic_RollSpeed` (0x1E546).
    fn roll_speed(&mut self, ram: &mut Ram, sets: &CollisionSets) {
        let o = self.obj;
        let d5 = (ram.s16(self.profile.speed + 2) >> 1) as i32;
        let d4 = if self.profile.is_tails {
            (ram.s16(self.profile.speed + 4) >> 2) as i32
        } else {
            0x20
        };
        if (o.status_secondary(ram) as i8) >= 0 {
            if o.move_lock(ram) == 0 {
                if self.held_bit(ram, LEFT) {
                    self.roll_left(ram, d4);
                }
                if self.held_bit(ram, RIGHT) {
                    self.roll_right(ram, d4);
                }
            }
            let mut d0 = o.inertia(ram) as u16 as i32;
            if d0 != 0 {
                if (d0 as i16) > 0 {
                    d0 = if d0 < d5 { 0 } else { d0 - d5 };
                } else {
                    let sum = d0 + d5;
                    d0 = if sum > 0xFFFF { 0 } else { sum };
                }
                o.set_inertia(ram, d0 as i16);
            }
            if o.inertia(ram) == 0 {
                if o.spindash_flag(ram) == 0 {
                    o.set_rolling(ram, false);
                    o.set_y_radius(ram, self.profile.stand_y_radius);
                    o.set_x_radius(ram, 9);
                    o.set_anim(ram, anim::WAIT);
                    o.set_y(ram, o.y(ram).wrapping_sub(self.profile.roll_shift as u16));
                } else {
                    let mut inertia = 0x400i16;
                    if o.facing_left(ram) {
                        inertia = inertia.wrapping_neg();
                    }
                    o.set_inertia(ram, inertia);
                }
            }
        }
        self.bias_toward_default(ram);
        let (sin, cos) = math::calc_sine(o.angle(ram));
        let inertia = o.inertia(ram) as i32;
        o.set_y_vel(ram, ((sin as i32 * inertia) >> 8) as i16);
        let d1 = ((cos as i32 * inertia) >> 8).clamp(-0x1000, 0x1000);
        o.set_x_vel(ram, d1 as i16);
        self.wall_check_ahead(ram, sets);
    }

    /// `Sonic_RollLeft` (0x1E622).
    fn roll_left(&mut self, ram: &mut Ram, d4: i32) {
        let o = self.obj;
        let d0 = o.inertia(ram) as i32;
        if d0 <= 0 {
            o.set_facing_left(ram, true);
            o.set_anim(ram, anim::ROLL);
            return;
        }
        let u = (d0 as u16 as i32) - d4;
        o.set_inertia(ram, if u < 0 { -0x80 } else { u } as i16);
    }

    /// `Sonic_RollRight` (0x1E646).
    fn roll_right(&mut self, ram: &mut Ram, d4: i32) {
        let o = self.obj;
        let d0 = o.inertia(ram) as i32;
        if d0 >= 0 {
            o.set_facing_left(ram, false);
            o.set_anim(ram, anim::ROLL);
            return;
        }
        let u = (d0 as u16 as i32) + d4;
        o.set_inertia(ram, if u > 0xFFFF { 0x80 } else { u } as i16);
    }

    /// `Sonic_Roll` (0x1E774).
    fn roll(&mut self, ram: &mut Ram) {
        let o = self.obj;
        if (o.status_secondary(ram) as i8) < 0 {
            return;
        }
        let speed = (o.inertia(ram) as i32).unsigned_abs() & 0xFFFF;
        if speed < 0x80 {
            return;
        }
        if (self.held(ram) & 0x0C) != 0 {
            return;
        }
        if !self.held_bit(ram, DOWN) {
            return;
        }
        if o.rolling(ram) {
            return;
        }
        o.set_rolling(ram, true);
        o.set_y_radius(ram, 0x0E);
        o.set_x_radius(ram, 7);
        o.set_anim(ram, anim::ROLL);
        o.set_y(ram, o.y(ram).wrapping_add(self.profile.roll_shift as u16));
        sfx::sonic(ram, sfx::ids::ROLL);
        if o.inertia(ram) == 0 {
            o.set_inertia(ram, 0x200);
        }
    }

    /// `Sonic_Jump` (0x1E7DA). Returns true if Sonic jumped.
    fn jump(&mut self, ram: &mut Ram, sets: &CollisionSets) -> bool {
        let o = self.obj;
        if (self.pressed(ram) & BUTTON_ABC as i32) == 0 {
            return false;
        }
        if collision::calc_room_over_head(ram, sets, o, (o.angle(ram).wrapping_add(0x80)) as i32)
            < 6
        {
            return false;
        }
        let d2 = if self.is_super(ram) {
            0x800
        } else if o.underwater(ram) {
            0x380
        } else {
            0x680
        };
        let (sin, cos) = math::calc_sine(o.angle(ram).wrapping_sub(0x40));
        o.set_x_vel(
            ram,
            o.x_vel(ram).wrapping_add(((cos as i32 * d2) >> 8) as i16),
        );
        o.set_y_vel(
            ram,
            o.y_vel(ram).wrapping_add(((sin as i32 * d2) >> 8) as i16),
        );
        o.set_in_air(ram, true);
        o.set_pushing(ram, false);
        o.set_jumping(ram, 1);
        o.set_stick_to_convex(ram, 0);
        sfx::sonic(ram, sfx::ids::JUMP);
        o.set_y_radius(ram, self.profile.stand_y_radius);
        o.set_x_radius(ram, 9);
        if o.rolling(ram) {
            o.set_roll_jumping(ram, true);
            return true;
        }
        o.set_y_radius(ram, 0x0E);
        o.set_x_radius(ram, 7);
        o.set_anim(ram, anim::ROLL);
        o.set_rolling(ram, true);
        o.set_y(ram, o.y(ram).wrapping_add(self.profile.roll_shift as u16));
        true
    }

    /// `Sonic_CheckSpindash` / `Sonic_UpdateSpindash` (0x1EA0E).
    fn check_spindash(&mut self, ram: &mut Ram, sets: &CollisionSets) -> bool {
        let o = self.obj;
        if o.spindash_flag(ram) == 0 {
            if o.anim(ram) != anim::DUCK {
                return false;
            }
            if (self.pressed(ram) & BUTTON_ABC as i32) == 0 {
                return false;
            }
            o.set_anim(ram, anim::SPINDASH);
            o.set_spindash_flag(ram, 1);
            o.set_spindash_counter(ram, 0);
            sfx::sonic(ram, sfx::ids::SPINDASH_REV);
            if o.air_left(ram) >= 0x0C {
                ram.w8(self.profile.dust.wrapping_add(0x1C), 2);
            }
            let _ = self.level_bound(ram);
            self.angle_pos(ram, sets);
            return true;
        }
        if !self.held_bit(ram, DOWN) {
            // Release (0x1EA6A).
            o.set_y_radius(ram, 0x0E);
            o.set_x_radius(ram, 7);
            o.set_anim(ram, anim::ROLL);
            o.set_y(ram, o.y(ram).wrapping_add(self.profile.roll_shift as u16));
            o.set_spindash_flag(ram, 0);
            let index = (o.spindash_counter(ram) >> 8) as usize;
            let base = if self.is_super(ram) { 9 } else { 0 };
            o.set_inertia(ram, SPINDASH_SPEEDS[base + index] as i16);
            let mut d0 = o.inertia(ram) as i32 - 0x800;
            d0 = (d0 * 2) & 0x1F00;
            d0 = -d0 + 0x2000;
            ram.w16(self.profile.scroll_delay, d0 as u16);
            if o.facing_left(ram) {
                o.set_inertia(ram, o.inertia(ram).wrapping_neg());
            }
            o.set_rolling(ram, true);
            ram.w8(self.profile.dust.wrapping_add(0x1C), 0);
            sfx::sonic(ram, sfx::ids::SPINDASH_RELEASE);
        } else {
            // Charging (0x1EB00).
            if o.spindash_counter(ram) != 0 {
                let c = o.spindash_counter(ram) as i32;
                let u = c - (c >> 5);
                o.set_spindash_counter(ram, if u < 0 { 0 } else { u as u16 });
            }
            if (self.pressed(ram) & BUTTON_ABC as i32) != 0 {
                o.set_anim(ram, anim::SPINDASH);
                sfx::sonic(ram, sfx::ids::SPINDASH_REV);
                o.set_prev_anim(ram, 0);
                let c = o.spindash_counter(ram) as i32 + 0x200;
                o.set_spindash_counter(ram, if c >= 0x800 { 0x800 } else { c as u16 });
            }
        }
        // 0x1EB48: common tail.
        self.bias_toward_default(ram);
        let _ = self.level_bound(ram);
        self.angle_pos(ram, sets);
        true
    }

    /// `Sonic_SlopeResist` (0x1EB66).
    fn slope_resist(&mut self, ram: &mut Ram) {
        let o = self.obj;
        if o.angle(ram).wrapping_add(0x60) >= 0xC0 {
            return;
        }
        let d0 = (math::calc_sine(o.angle(ram)).0 as i32 * 0x20) >> 8;
        if o.inertia(ram) == 0 {
            return;
        }
        if o.inertia(ram) > 0 && d0 == 0 {
            return;
        }
        o.set_inertia(ram, o.inertia(ram).wrapping_add(d0 as i16));
    }

    /// `Sonic_RollRepel` (0x1EB9C).
    fn roll_repel(&mut self, ram: &mut Ram) {
        let o = self.obj;
        if o.angle(ram).wrapping_add(0x60) >= 0xC0 {
            return;
        }
        let mut d0 = (math::calc_sine(o.angle(ram)).0 as i32 * 0x50) >> 8;
        if o.inertia(ram) >= 0 {
            if d0 < 0 {
                d0 >>= 2;
            }
        } else if d0 >= 0 {
            d0 >>= 2;
        }
        o.set_inertia(ram, o.inertia(ram).wrapping_add(d0 as i16));
    }

    /// `Sonic_SlopeRepel` (0x1EBD8): fall off steep slopes when too slow.
    fn slope_repel(&mut self, ram: &mut Ram) {
        let o = self.obj;
        if o.stick_to_convex(ram) != 0 {
            return;
        }
        if o.move_lock(ram) != 0 {
            o.set_move_lock(ram, o.move_lock(ram) - 1);
            return;
        }
        if ((o.angle(ram) as i32 + 0x20) & 0xC0) == 0 {
            return;
        }
        if ((o.inertia(ram) as i32).unsigned_abs() & 0xFFFF) >= 0x280 {
            return;
        }
        o.set_inertia(ram, 0);
        o.set_in_air(ram, true);
        o.set_move_lock(ram, 0x1E);
    }

    /// `Sonic_LevelBound` (0x1E6F4): keep Sonic inside the camera's horizontal limits.
    fn level_bound(&mut self, ram: &mut Ram) -> Result<(), Unported> {
        let o = self.obj;
        let next_x = ((o.x_long(ram) as i64 + ((o.x_vel(ram) as i64) << 8)) >> 16) as u16;
        let left = ram.u16(self.profile.cam_min_x).wrapping_add(0x10);
        let right = ram
            .u16(self.profile.cam_max_x)
            .wrapping_add(0x128)
            .wrapping_add(if ram.u8(vars::CURRENT_BOSS_ID) == 0 {
                0x40
            } else {
                0
            });
        if left > next_x {
            self.stop_at(ram, left);
        } else if right <= next_x {
            self.stop_at(ram, right);
        }

        let bottom = ram.u16(self.profile.cam_max_y).wrapping_add(0xE0) as i16;
        if bottom < o.y(ram) as i16 {
            if ram.u16(vars::CURRENT_ZONE_AND_ACT) == 0x1B01 && o.x(ram) >= 0x2000 {
                return Err(Unported::new(
                    0x1E74A,
                    "act transition below the bottom boundary",
                ));
            }
            self.fell_out(ram);
        }
        Ok(())
    }

    fn stop_at(&mut self, ram: &mut Ram, x: u16) {
        let o = self.obj;
        o.set_x(ram, x);
        o.set_x_sub(ram, 0);
        o.set_x_vel(ram, 0);
        o.set_inertia(ram, 0);
    }

    /// `ObjectMove` (0x1A0F2).
    fn object_move(&mut self, ram: &mut Ram) {
        let o = self.obj;
        o.set_x_long(
            ram,
            o.x_long(ram)
                .wrapping_add(((o.x_vel(ram) as i32) << 8) as u32),
        );
        o.set_y_long(
            ram,
            o.y_long(ram)
                .wrapping_add(((o.y_vel(ram) as i32) << 8) as u32),
        );
    }

    /// `ObjectMoveAndFall` (0x1A0C6): gravity is added after the old y velocity is used.
    fn object_move_and_fall(&mut self, ram: &mut Ram) {
        let o = self.obj;
        let yv = o.y_vel(ram);
        o.set_y_vel(ram, yv.wrapping_add(0x38));
        o.set_x_long(
            ram,
            o.x_long(ram)
                .wrapping_add(((o.x_vel(ram) as i32) << 8) as u32),
        );
        o.set_y_long(ram, o.y_long(ram).wrapping_add(((yv as i32) << 8) as u32));
    }

    // ---------------------------------------------------------------- air

    /// `Sonic_JumpHeight` (0x1E892).
    fn jump_height(&mut self, ram: &mut Ram) -> Result<(), Unported> {
        let o = self.obj;
        if o.jumping(ram) != 0 {
            let cap = if o.underwater(ram) { -0x200 } else { -0x400 };
            if cap > o.y_vel(ram) as i32 && (self.held(ram) & BUTTON_ABC as i32) == 0 {
                o.set_y_vel(ram, cap as i16);
            }
            if !self.profile.is_tails && o.y_vel(ram) >> 8 == 0 {
                self.check_super_transform(ram)?;
            }
            return Ok(());
        }
        if o.spindash_flag(ram) == 0 && o.y_vel(ram) < -0xFC0 {
            o.set_y_vel(ram, -0xFC0);
        }
        Ok(())
    }

    /// 0x1E8DA: the transform conditions; the transformation itself is not ported.
    fn check_super_transform(&mut self, ram: &mut Ram) -> Result<(), Unported> {
        if self.is_super(ram) {
            return Ok(());
        }
        if ram.u8(vars::EMERALD_COUNT) != 0x0D {
            return Ok(());
        }
        if ram.u16(vars::RING_COUNT) < 0x32 {
            return Ok(());
        }
        if ram.u8(vars::TIMED_MODE) != 0 {
            return Ok(());
        }
        if ram.u8(vars::UPDATE_HUD_TIMER) == 0 {
            return Ok(());
        }
        Err(Unported::new(0x1E904, "Super Sonic transformation"))
    }

    /// `Sonic_ChgJumpDir` (0x1E668): air control and air drag.
    fn chg_jump_dir(&mut self, ram: &mut Ram) {
        let o = self.obj;
        let top = ram.s16(self.profile.speed) as i32;
        let acc = ram.s16(self.profile.speed + 2) as i32 * 2;
        if !o.roll_jumping(ram) {
            let mut d0 = o.x_vel(ram) as i32;
            if self.held_bit(ram, LEFT) {
                o.set_facing_left(ram, true);
                d0 -= acc;
                if d0 <= -top {
                    d0 = -top;
                }
            }
            if self.held_bit(ram, RIGHT) {
                o.set_facing_left(ram, false);
                d0 += acc;
                if d0 >= top {
                    d0 = top;
                }
            }
            o.set_x_vel(ram, d0 as i16);
        }
        self.bias_toward_default(ram);
        if (o.y_vel(ram) as u16) < 0xFC00 {
            return; // only while rising slower than $400
        }
        let x = o.x_vel(ram) as u16 as i32;
        let drag = ((x as i16) >> 5) as i32;
        if drag == 0 {
            return;
        }
        let u = x - (drag as u16 as i32);
        if drag > 0 {
            o.set_x_vel(ram, if u < 0 { 0 } else { u } as i16);
        } else {
            o.set_x_vel(ram, if u < 0 { u } else { 0 } as i16);
        }
    }

    /// `Sonic_JumpAngle` (0x1EC1A): return the angle to 0 and advance flips.
    fn jump_angle(&mut self, ram: &mut Ram) {
        let o = self.obj;
        let mut a = o.angle(ram) as i32;
        if a != 0 {
            if (a as i8) < 0 {
                a += 2;
                if a > 0xFF {
                    a = 0;
                }
            } else {
                a -= 2;
                if a < 0 {
                    a = 0;
                }
            }
            o.set_angle(ram, a as u8);
        }
        let mut d0 = o.flip_angle(ram) as i32;
        if d0 == 0 {
            return;
        }
        let forward = o.inertia(ram) >= 0 || o.flip_turned(ram) != 0;
        let speed = o.flip_speed(ram) as i32;
        if forward {
            d0 += speed;
            if d0 > 0xFF {
                d0 &= 0xFF;
                if o.flips_remaining(ram) == 0 {
                    o.set_flips_remaining(ram, 0);
                    d0 = 0;
                } else {
                    o.set_flips_remaining(ram, o.flips_remaining(ram).wrapping_sub(1));
                }
            }
        } else {
            d0 -= speed;
            if d0 < 0 {
                d0 &= 0xFF;
                if o.flips_remaining(ram) == 0 {
                    o.set_flips_remaining(ram, 0);
                    d0 = 0;
                } else {
                    o.set_flips_remaining(ram, o.flips_remaining(ram).wrapping_sub(1));
                }
            }
        }
        o.set_flip_angle(ram, d0 as u8);
    }

    /// `Sonic_DoLevelCollision` (0x1EC7A).
    fn do_level_collision(&mut self, ram: &mut Ram, sets: &CollisionSets) {
        let o = self.obj;
        collision::select_layer(ram, o);
        let solid = o.lrb_solid_bit(ram);
        let dir = (math::calc_angle(o.x_vel(ram), o.y_vel(ram)) as i32 - 0x20) & 0xC0;
        match dir {
            0x40 => {
                self.air_moving_left(ram, sets, solid);
                return;
            }
            0x80 => {
                self.air_moving_up(ram, sets, solid);
                return;
            }
            0xC0 => {
                self.air_moving_right(ram, sets, solid);
                return;
            }
            _ => {}
        }

        // Moving down (0x1ECC4).
        let w = collision::left_wall_dist(ram, sets, o.y(ram) as i32, o.x(ram) as i32, solid).0;
        if w < 0 {
            o.set_x(ram, (o.x(ram) as i32 - w) as u16);
            o.set_x_vel(ram, 0);
        }
        let w = collision::right_wall_dist(ram, sets, o.y(ram) as i32, o.x(ram) as i32, solid).0;
        if w < 0 {
            o.set_x(ram, (o.x(ram) as i32 + w) as u16);
            o.set_x_vel(ram, 0);
        }
        let f = collision::check_floor(ram, sets, o);
        if f.distance >= 0 {
            return;
        }
        let d2 = (-(((o.y_vel(ram) >> 8) + 8) as i8 as i32)) as i8 as i32;
        if (f.distance as i8 as i32) < d2 && (f.other as i8 as i32) < d2 {
            return;
        }
        o.set_y(ram, (o.y(ram) as i32 + f.distance) as u16);
        o.set_angle(ram, f.angle);
        self.reset_on_floor(ram);
        if ((f.angle as i32 + 0x20) & 0x40) != 0 {
            o.set_x_vel(ram, 0);
            if o.y_vel(ram) > 0xFC0 {
                o.set_y_vel(ram, 0xFC0);
            }
            self.inertia_from_y_vel(ram, f.angle);
            return;
        }
        if ((f.angle as i32 + 0x10) & 0x20) != 0 {
            o.set_y_vel(ram, o.y_vel(ram) >> 1);
            self.inertia_from_y_vel(ram, f.angle);
            return;
        }
        o.set_y_vel(ram, 0);
        o.set_inertia(ram, o.x_vel(ram));
    }

    fn inertia_from_y_vel(&mut self, ram: &mut Ram, angle: u8) {
        let o = self.obj;
        let mut inertia = o.y_vel(ram);
        if (angle as i8) < 0 {
            inertia = inertia.wrapping_neg();
        }
        o.set_inertia(ram, inertia);
    }

    /// 0x1ED5C.
    fn air_moving_left(&mut self, ram: &mut Ram, sets: &CollisionSets, solid: u8) {
        let o = self.obj;
        let w = collision::left_wall_dist(ram, sets, o.y(ram) as i32, o.x(ram) as i32, solid).0;
        if w < 0 {
            o.set_x(ram, (o.x(ram) as i32 - w) as u16);
            o.set_x_vel(ram, 0);
            o.set_inertia(ram, o.y_vel(ram));
            return;
        }
        let c = collision::check_ceiling(ram, sets, o, solid);
        if c.distance < 0 {
            o.set_y(ram, (o.y(ram) as i32 - c.distance) as u16);
            if o.y_vel(ram) < 0 {
                o.set_y_vel(ram, 0);
            }
            return;
        }
        if o.y_vel(ram) < 0 {
            return;
        }
        self.land_if_on_floor(ram, sets);
    }

    /// 0x1EE14.
    fn air_moving_right(&mut self, ram: &mut Ram, sets: &CollisionSets, solid: u8) {
        let o = self.obj;
        let w = collision::right_wall_dist(ram, sets, o.y(ram) as i32, o.x(ram) as i32, solid).0;
        if w < 0 {
            o.set_x(ram, (o.x(ram) as i32 + w) as u16);
            o.set_x_vel(ram, 0);
            o.set_inertia(ram, o.y_vel(ram));
            return;
        }
        let c = collision::check_ceiling(ram, sets, o, solid);
        if c.distance < 0 {
            o.set_y(ram, (o.y(ram) as i32 - c.distance) as u16);
            if o.y_vel(ram) < 0 {
                o.set_y_vel(ram, 0);
            }
            return;
        }
        if o.y_vel(ram) < 0 {
            return;
        }
        self.land_if_on_floor(ram, sets);
    }

    fn land_if_on_floor(&mut self, ram: &mut Ram, sets: &CollisionSets) {
        let o = self.obj;
        let f = collision::check_floor(ram, sets, o);
        if f.distance >= 0 {
            return;
        }
        o.set_y(ram, (o.y(ram) as i32 + f.distance) as u16);
        o.set_angle(ram, f.angle);
        self.reset_on_floor(ram);
        o.set_y_vel(ram, 0);
        o.set_inertia(ram, o.x_vel(ram));
    }

    /// 0x1EDB8.
    fn air_moving_up(&mut self, ram: &mut Ram, sets: &CollisionSets, solid: u8) {
        let o = self.obj;
        let w = collision::left_wall_dist(ram, sets, o.y(ram) as i32, o.x(ram) as i32, solid).0;
        if w < 0 {
            o.set_x(ram, (o.x(ram) as i32 - w) as u16);
            o.set_x_vel(ram, 0);
        }
        let w = collision::right_wall_dist(ram, sets, o.y(ram) as i32, o.x(ram) as i32, solid).0;
        if w < 0 {
            o.set_x(ram, (o.x(ram) as i32 + w) as u16);
            o.set_x_vel(ram, 0);
        }
        let c = collision::check_ceiling(ram, sets, o, solid);
        if c.distance >= 0 {
            return;
        }
        o.set_y(ram, (o.y(ram) as i32 - c.distance) as u16);
        if ((c.angle as i32 + 0x20) & 0x40) == 0 {
            o.set_y_vel(ram, 0);
            return;
        }
        o.set_angle(ram, c.angle);
        self.reset_on_floor(ram);
        self.inertia_from_y_vel(ram, c.angle);
    }

    /// `Sonic_ResetOnFloor` (0x1EE70).
    fn reset_on_floor(&mut self, ram: &mut Ram) {
        if self.obj.spindash_flag(ram) == 0 {
            self.obj.set_anim(ram, anim::WALK);
            Self::reset_on_floor_part2(ram, self.obj);
            return;
        }
        Self::clear_landing_state(ram, self.obj);
    }

    /// `Sonic_ResetOnFloor_Part2` (0x1EE7C), also used by HurtCharacter/KillCharacter.
    pub fn reset_on_floor_part2(ram: &mut Ram, o: Obj) {
        let tails = o.id(ram) != 1;
        if o.rolling(ram) {
            o.set_rolling(ram, false);
            o.set_y_radius(ram, if tails { 0x0F } else { 0x13 });
            o.set_x_radius(ram, 9);
            o.set_anim(ram, anim::WALK);
            o.set_y(ram, o.y(ram).wrapping_sub(if tails { 1 } else { 5 }));
        }
        Self::clear_landing_state(ram, o);
    }

    /// 0x1EEA8: clear air/push/roll-jump state on landing.
    fn clear_landing_state(ram: &mut Ram, o: Obj) {
        o.set_in_air(ram, false);
        o.set_pushing(ram, false);
        o.set_roll_jumping(ram, false);
        o.set_jumping(ram, 0);
        ram.w16(vars::CHAIN_BONUS_COUNTER, 0);
        o.set_flip_angle(ram, 0);
        o.set_flip_turned(ram, 0);
        o.set_flips_remaining(ram, 0);
        ram.w16(
            if o.id(ram) != 1 {
                vars::TAILS_LOOK_DELAY_COUNTER
            } else {
                vars::SONIC_LOOK_DELAY_COUNTER
            },
            0,
        );
        if o.anim(ram) == anim::HANG2 {
            o.set_anim(ram, anim::WALK);
        }
    }

    /// The falling-out-of-the-level half of `KillCharacter` (0x43B44).
    fn fell_out(&mut self, ram: &mut Ram) {
        touch::kill(ram, self.obj, self.obj);
    }

    // ---------------------------------------------------------------- ground collision

    /// `AnglePos` (0x220B4): keep Sonic on the surface for his current quadrant.
    fn angle_pos(&mut self, ram: &mut Ram, sets: &CollisionSets) {
        let o = self.obj;
        collision::select_layer(ram, o);
        let solid = o.top_solid_bit(ram);
        if o.on_object(ram) {
            ram.w8(vars::PRIMARY_ANGLE, 0);
            ram.w8(vars::SECONDARY_ANGLE, 0);
            return;
        }
        ram.w8(vars::PRIMARY_ANGLE, 3);
        ram.w8(vars::SECONDARY_ANGLE, 3);
        match collision::rounded_quadrant(o.angle(ram) as i32) {
            0x00 => {
                let y = o.y(ram) as i32 + o.y_radius(ram) as i32;
                let d0 = collision::find_floor(
                    ram,
                    sets,
                    y,
                    o.x(ram) as i32 + o.x_radius(ram) as i32,
                    0x10,
                    solid,
                    0,
                    vars::PRIMARY_ANGLE,
                );
                let d1 = collision::find_floor(
                    ram,
                    sets,
                    y,
                    o.x(ram) as i32 - o.x_radius(ram) as i32,
                    0x10,
                    solid,
                    0,
                    vars::SECONDARY_ANGLE,
                );
                let d1 = self.pick_angle(ram, d0, d1);
                self.stick_or_fall(ram, d1, o.x_vel(ram), AxisMove::YAdd);
            }
            0x80 => {
                let y = (o.y(ram) as i32 - o.y_radius(ram) as i32) ^ 0xF;
                let d0 = collision::find_floor(
                    ram,
                    sets,
                    y,
                    o.x(ram) as i32 + o.x_radius(ram) as i32,
                    -0x10,
                    solid,
                    0x800,
                    vars::PRIMARY_ANGLE,
                );
                let d1 = collision::find_floor(
                    ram,
                    sets,
                    y,
                    o.x(ram) as i32 - o.x_radius(ram) as i32,
                    -0x10,
                    solid,
                    0x800,
                    vars::SECONDARY_ANGLE,
                );
                let d1 = self.pick_angle(ram, d0, d1);
                self.stick_or_fall(ram, d1, o.x_vel(ram), AxisMove::YSub);
            }
            0xC0 => {
                let x = o.x(ram) as i32 + o.y_radius(ram) as i32;
                let d0 = collision::find_wall(
                    ram,
                    sets,
                    o.y(ram) as i32 - o.x_radius(ram) as i32,
                    x,
                    0x10,
                    solid,
                    0,
                    vars::PRIMARY_ANGLE,
                );
                let d1 = collision::find_wall(
                    ram,
                    sets,
                    o.y(ram) as i32 + o.x_radius(ram) as i32,
                    x,
                    0x10,
                    solid,
                    0,
                    vars::SECONDARY_ANGLE,
                );
                let d1 = self.pick_angle(ram, d0, d1);
                self.stick_or_fall(ram, d1, o.y_vel(ram), AxisMove::XAdd);
            }
            _ => {
                let x = (o.x(ram) as i32 - o.y_radius(ram) as i32) ^ 0xF;
                let d0 = collision::find_wall(
                    ram,
                    sets,
                    o.y(ram) as i32 - o.x_radius(ram) as i32,
                    x,
                    -0x10,
                    solid,
                    0x400,
                    vars::PRIMARY_ANGLE,
                );
                let d1 = collision::find_wall(
                    ram,
                    sets,
                    o.y(ram) as i32 + o.x_radius(ram) as i32,
                    x,
                    -0x10,
                    solid,
                    0x400,
                    vars::SECONDARY_ANGLE,
                );
                let d1 = self.pick_angle(ram, d0, d1);
                self.stick_or_fall(ram, d1, o.y_vel(ram), AxisMove::XSub);
            }
        }
    }

    /// Shared tail of the four AnglePos quadrants (0x2218C).
    fn stick_or_fall(&mut self, ram: &mut Ram, d1: i32, velocity: i16, axis: AxisMove) {
        let o = self.obj;
        if d1 == 0 {
            return;
        }
        if d1 < 0 {
            if d1 >= -0x0E {
                self.move_axis(ram, axis, d1);
            }
            return;
        }
        let mut d0 = ((velocity >> 8) as i8 as i32).unsigned_abs() as i32;
        d0 = (d0 + 4) & 0xFF;
        if d0 >= 0x0E {
            d0 = 0x0E;
        }
        if (d1 as i8 as i32) > (d0 as i8 as i32) && o.stick_to_convex(ram) == 0 {
            o.set_in_air(ram, true);
            o.set_pushing(ram, false);
            o.set_prev_anim(ram, 1);
            return;
        }
        self.move_axis(ram, axis, d1);
    }

    fn move_axis(&mut self, ram: &mut Ram, axis: AxisMove, v: i32) {
        let o = self.obj;
        match axis {
            AxisMove::YAdd => o.set_y(ram, (o.y(ram) as i32 + v) as u16),
            AxisMove::YSub => o.set_y(ram, (o.y(ram) as i32 - v) as u16),
            AxisMove::XAdd => o.set_x(ram, (o.x(ram) as i32 + v) as u16),
            AxisMove::XSub => o.set_x(ram, (o.x(ram) as i32 - v) as u16),
        }
    }

    /// `Sonic_Angle` (0x221D6): take the nearer probe and its angle, unless it differs too much.
    fn pick_angle(&mut self, ram: &mut Ram, d0: i32, d1: i32) -> i32 {
        let o = self.obj;
        let mut d2 = ram.u8(vars::SECONDARY_ANGLE);
        let mut d1 = d1;
        if d1 > d0 {
            d2 = ram.u8(vars::PRIMARY_ANGLE);
            d1 = d0;
        }
        if (d2 & 1) == 0 {
            let diff = (d2.wrapping_sub(o.angle(ram)) as i8 as i32).unsigned_abs();
            if (diff & 0xFF) < 0x20 {
                o.set_angle(ram, d2);
                return d1;
            }
        }
        // SMB3's 45-degree slopes meet flat blocks directly; keep floor/ceiling ties consistent with AnglePos.
        let zone = ram.u8(vars::CURRENT_ZONE_AND_ACT);
        let angle = if crate::camera::is_easter_egg(zone) {
            collision::rounded_quadrant(o.angle(ram) as i32) as u8
        } else {
            o.angle(ram).wrapping_add(0x20) & 0xC0
        };
        o.set_angle(ram, angle);
        d1
    }

    // =============================================================== Tails (Obj02, 0x1F6B8)

    fn tails_step(
        &mut self,
        ram: &mut Ram,
        sets: &CollisionSets,
        animation: &dyn PlayerAnimation,
        water: Option<&WaterMap>,
    ) -> Result<(), Unported> {
        match self.obj.routine(ram) {
            routine::INIT => {
                self.tails_init(ram)?;
                self.tails_control(ram, sets, animation, water)
            }
            routine::CONTROL => self.tails_control(ram, sets, animation, water),
            routine::HURT => self.tails_hurt(ram, sets, animation),
            routine::DEAD => self.tails_dead(ram, sets, animation),
            routine::GONE => {
                self.tails_gone(ram);
                Ok(())
            }
            0x0A => {
                self.tails_respawning(ram, animation);
                Ok(())
            }
            other => Err(Unported::new(0x1F6D2, format!("Obj02 routine {other}"))),
        }
    }

    /// `Obj02_Init` (0x1F6EC), Sonic-and-Tails mode.
    fn tails_init(&mut self, ram: &mut Ram) -> Result<(), Unported> {
        let o = self.obj;
        if ram.u16(vars::PLAYER_MODE) == 2 {
            return Err(Unported::new(0x1F730, "Tails-alone mode"));
        }
        o.set_routine(ram, o.routine(ram).wrapping_add(2));
        o.set_y_radius(ram, 0x0F);
        o.set_x_radius(ram, 9);
        o.set_mappings(ram, 0x7A6F6);
        o.set_priority(ram, 2);
        o.set_width_pixels(ram, 0x18);
        o.set_render_flags(ram, 0x84);
        self.set_normal_speeds(ram);
        o.set_art_tile(ram, 0x7A0);
        let sonic = Obj::new(vars::MAIN_CHARACTER);
        o.set_solid_bits(ram, sonic.solid_bits(ram));
        if (sonic.art_tile(ram) as i16) < 0 {
            o.set_art_tile(ram, o.art_tile(ram) | 0x8000);
        }
        ram.w16(vars::SAVED_X_POS_P2, o.x(ram));
        ram.w16(vars::SAVED_Y_POS_P2, o.y(ram));
        ram.w16(vars::SAVED_ART_TILE_P2, o.art_tile(ram));
        ram.w16(vars::SAVED_SOLID_BITS_P2, o.solid_bits(ram));
        o.set_flips_remaining(ram, 0);
        o.set_flip_speed(ram, 4);
        o.set_air_left(ram, 0x1E);
        ram.w16(vars::TAILS_CPU_ROUTINE, 0);
        ram.w16(vars::TAILS_CONTROL_COUNTER, 0);
        ram.w16(vars::TAILS_RESPAWN_COUNTER, 0);
        ram.w8(vars::TAILS_TAILS, 5);
        ram.w16(vars::TAILS_TAILS.wrapping_add(0x3E), o.address());
        Ok(())
    }

    /// `Obj02_Control` (0x1F7C8).
    fn tails_control(
        &mut self,
        ram: &mut Ram,
        sets: &CollisionSets,
        animation: &dyn PlayerAnimation,
        water: Option<&WaterMap>,
    ) -> Result<(), Unported> {
        let o = self.obj;
        self.water(ram, water)?;
        if ram.u8(vars::CONTROL_LOCKED_P2) == 0 {
            ram.w16(vars::CTRL2_HELD_LOGICAL, ram.u16(vars::CTRL2_HELD));
        }
        if ram.u16(vars::TWO_PLAYER_MODE) == 0 && ram.u16(vars::PLAYER_MODE) != 3 {
            self.tails_cpu(ram)?;
        }
        if (o.obj_control(ram) & 1) == 0 {
            match o.status(ram) & 6 {
                0 => self.md_normal(ram, sets),
                2 => self.md_air(ram, sets, water)?,
                4 => self.md_roll(ram, sets),
                _ => self.md_air(ram, sets, water)?,
            }
        }
        if ram.u16(vars::CAMERA_MIN_Y_POS) == 0xFF00 {
            o.set_y(ram, o.y(ram) & 0x7FF);
        }
        self.display(ram);
        self.tails_record_pos(ram);
        self.water(ram, water)?;
        o.set_next_tilt_copy(ram, ram.u8(vars::PRIMARY_ANGLE));
        o.set_tilt_copy(ram, ram.u8(vars::SECONDARY_ANGLE));
        if ram.bit(vars::WIND_TUNNEL_FLAG, 1) && o.anim(ram) == anim::WALK {
            o.set_anim(ram, o.prev_anim(ram));
        }
        animation.animate(ram, o);
        if (o.obj_control(ram) as i8) >= 0 {
            touch::run(ram, o);
        }
        self.load_dyn_plc(ram);
        Ok(())
    }

    /// 0x1FF2E/0x1FF78: Tails' simpler balancing (no Super or fall-off variants).
    fn tails_balance(&mut self, ram: &mut Ram, sets: &CollisionSets) -> bool {
        let o = self.obj;
        if o.on_object(ram) {
            let obj = o.interact_object(ram);
            if (obj.status(ram) as i8) < 0 {
                return false;
            }
            let mut d1 = obj.width_pixels(ram) as i32;
            let d2 = d1 * 2 - 4;
            d1 = (d1 + o.x(ram) as i32 - obj.x(ram) as i32) as i16 as i32;
            if d1 < 4 {
                o.set_facing_left(ram, true);
                o.set_anim(ram, anim::BALANCE);
                return true;
            }
            if d1 >= d2 {
                o.set_facing_left(ram, false);
                o.set_anim(ram, anim::BALANCE);
                return true;
            }
            return false;
        }
        if collision::floor_edge(ram, sets, o, o.x(ram) as i32).0 < 0x0C {
            return false;
        }
        if o.next_tilt_copy(ram) == 3 {
            o.set_facing_left(ram, false);
            o.set_anim(ram, anim::BALANCE);
            return true;
        }
        if o.tilt_copy(ram) == 3 {
            o.set_facing_left(ram, true);
            o.set_anim(ram, anim::BALANCE);
            return true;
        }
        false
    }

    /// `Tails_RecordPos` (0x1FD6E): his own position history at `$E600`.
    fn tails_record_pos(&self, ram: &mut Ram) {
        let index = ram.u16(vars::TAILS_POS_RECORD_INDEX);
        ram.w16(
            vars::TAILS_POS_RECORD_BUF.wrapping_add(index),
            self.obj.x(ram),
        );
        ram.w16(
            vars::TAILS_POS_RECORD_BUF
                .wrapping_add(index)
                .wrapping_add(2),
            self.obj.y(ram),
        );
        ram.w8(
            vars::TAILS_POS_RECORD_INDEX.wrapping_add(1),
            ram.u8(vars::TAILS_POS_RECORD_INDEX.wrapping_add(1))
                .wrapping_add(4),
        );
    }

    /// `Obj02_Hurt` (0x20A26).
    fn tails_hurt(
        &mut self,
        ram: &mut Ram,
        sets: &CollisionSets,
        animation: &dyn PlayerAnimation,
    ) -> Result<(), Unported> {
        let o = self.obj;
        self.object_move(ram);
        o.set_y_vel(ram, o.y_vel(ram).wrapping_add(0x30));
        if o.underwater(ram) {
            o.set_y_vel(ram, o.y_vel(ram).wrapping_sub(0x20));
        }
        if ram.u16(vars::CAMERA_MIN_Y_POS) == 0xFF00 {
            o.set_y(ram, o.y(ram) & 0x7FF);
        }
        if self.tails_hurt_stop(ram, sets) {
            return Ok(());
        }
        self.level_bound(ram)?;
        self.tails_record_pos(ram);
        animation.animate(ram, o);
        self.load_dyn_plc(ram);
        sprites::display_sprite(ram, o);
        Ok(())
    }

    /// `Tails_HurtStop` (0x20A68). True when he was killed (the original jumps away).
    fn tails_hurt_stop(&mut self, ram: &mut Ram, sets: &CollisionSets) -> bool {
        let o = self.obj;
        let bottom = ram.u16(self.profile.cam_max_y).wrapping_add(0xE0) as i16;
        if bottom < o.y(ram) as i16 {
            self.fell_out(ram);
            return true;
        }
        self.do_level_collision(ram, sets);
        if o.in_air(ram) {
            return false;
        }
        o.set_y_vel(ram, 0);
        o.set_x_vel(ram, 0);
        o.set_inertia(ram, 0);
        o.set_obj_control(ram, 0);
        o.set_anim(ram, anim::WALK);
        o.set_routine(ram, routine::CONTROL);
        o.set_invulnerable_time(ram, 0x78);
        o.set_spindash_flag(ram, 0);
        false
    }

    /// `Obj02_Dead` (0x20AB0).
    fn tails_dead(
        &mut self,
        ram: &mut Ram,
        sets: &CollisionSets,
        animation: &dyn PlayerAnimation,
    ) -> Result<(), Unported> {
        let _ = sets;
        self.tails_game_over_check(ram)?;
        self.object_move_and_fall(ram);
        self.tails_record_pos(ram);
        animation.animate(ram, self.obj);
        self.load_dyn_plc(ram);
        sprites::display_sprite(ram, self.obj);
        Ok(())
    }

    /// 0x20ACC.
    fn tails_game_over_check(&mut self, ram: &mut Ram) -> Result<(), Unported> {
        if ram.u16(vars::PLAYER_MODE) == 2 {
            return Err(Unported::new(0x1EFEA, "Tails-alone game over"));
        }
        ram.w8(vars::SCROLL_LOCK_P2, 1);
        self.obj.set_spindash_flag(ram, 0);
        let limit = ram.u16(self.profile.cam_max_y).wrapping_add(0x100) as i16;
        if limit >= self.obj.y(ram) as i16 {
            return Ok(());
        }
        self.obj.set_routine(ram, routine::CONTROL);
        if ram.u16(vars::TWO_PLAYER_MODE) != 0 {
            return Err(Unported::new(0x20B02, "Tails' death in 2-player mode"));
        }
        self.cpu_respawn(ram);
        Ok(())
    }

    /// Routine 8 (0x20BF0).
    fn tails_gone(&mut self, ram: &mut Ram) {
        self.gone(ram);
    }

    /// Routine `$A` (0x20C04).
    fn tails_respawning(&mut self, ram: &mut Ram, animation: &dyn PlayerAnimation) {
        if ram.u16(vars::CAMERA_X_POS_DIFF_P2) == 0 && ram.u16(vars::CAMERA_Y_POS_DIFF_P2) == 0 {
            self.obj.set_routine(ram, routine::CONTROL);
        }
        animation.animate(ram, self.obj);
        self.load_dyn_plc(ram);
        sprites::display_sprite(ram, self.obj);
    }

    // =============================================================== TailsCPU_Control (0x1F8EA)

    fn tails_cpu(&mut self, ram: &mut Ram) -> Result<(), Unported> {
        if (ram.u8(vars::CTRL2_HELD) & 0x7F) != 0 {
            ram.w16(vars::TAILS_CONTROL_COUNTER, 0x258); // player 2 took over: 10 s
        }
        match ram.u16(vars::TAILS_CPU_ROUTINE) {
            0 => self.cpu_init(ram),
            2 => self.cpu_wait_to_spawn(ram),
            4 => self.cpu_fly_in(ram),
            6 => self.cpu_follow(ram),
            8 => self.cpu_panic(ram),
            other => return Err(Unported::new(0x1F902, format!("TailsCPU routine {other}"))),
        }
        Ok(())
    }

    /// 0x1F914.
    fn cpu_init(&mut self, ram: &mut Ram) {
        let o = self.obj;
        ram.w16(vars::TAILS_CPU_ROUTINE, 6);
        o.set_obj_control(ram, 0);
        o.set_anim(ram, anim::WALK);
        o.set_x_vel(ram, 0);
        o.set_y_vel(ram, 0);
        o.set_inertia(ram, 0);
        o.set_status(ram, 0);
        ram.w16(vars::TAILS_RESPAWN_COUNTER, 0);
    }

    /// 0x1F946: wait (every 64 frames, Sonic on the ground and free) before flying in.
    fn cpu_wait_to_spawn(&mut self, ram: &mut Ram) {
        let o = self.obj;
        let sonic = Obj::new(vars::MAIN_CHARACTER);
        if (ram.u8(self.profile.ctrl) & 0xF0) == 0 {
            if (ram.u16(vars::LEVEL_FRAME_COUNTER) & 0x3F) != 0 {
                return;
            }
            if sonic.obj_control(ram) != 0 {
                return;
            }
            if (sonic.status(ram) & 0xD2) != 0 {
                return;
            }
        }
        ram.w16(vars::TAILS_CPU_ROUTINE, 4);
        o.set_x(ram, sonic.x(ram));
        ram.w16(vars::TAILS_CPU_TARGET_X, sonic.x(ram));
        ram.w16(vars::TAILS_CPU_TARGET_Y, sonic.y(ram));
        o.set_y(ram, sonic.y(ram).wrapping_sub(0xC0));
        o.set_art_tile(ram, o.art_tile(ram) | 0x8000);
        o.set_spindash_flag(ram, 0);
        o.set_spindash_counter(ram, 0);
    }

    /// 0x1F9A0: fly toward where Sonic was 16 frames ago; land when there.
    fn cpu_fly_in(&mut self, ram: &mut Ram) {
        let o = self.obj;
        if (o.render_flags(ram) as i8) < 0 {
            ram.w16(vars::TAILS_RESPAWN_COUNTER, 0);
        } else {
            let r = ram.u16(vars::TAILS_RESPAWN_COUNTER).wrapping_add(1);
            ram.w16(vars::TAILS_RESPAWN_COUNTER, r);
            if r >= 0x12C {
                ram.w16(vars::TAILS_RESPAWN_COUNTER, 0);
                ram.w16(vars::TAILS_CPU_ROUTINE, 2);
                o.set_obj_control(ram, 0x81);
                o.set_status(ram, 2);
                o.set_x(ram, 0);
                o.set_y(ram, 0);
                o.set_anim(ram, 0x20); // TailsFly
                return;
            }
        }
        let d3 = history_index(ram);
        ram.w16(
            vars::TAILS_CPU_TARGET_X,
            ram.u16(vars::SONIC_POS_RECORD_BUF.wrapping_add(d3)),
        );
        ram.w16(
            vars::TAILS_CPU_TARGET_Y,
            ram.u16(vars::SONIC_POS_RECORD_BUF.wrapping_add(d3).wrapping_add(2)),
        );
        if ram.u8(vars::WATER_FLAG) != 0 {
            let limit = ram.s16(vars::WATER_LEVEL1).wrapping_sub(0x10);
            let target_y = ram.u16(vars::TAILS_CPU_TARGET_Y);
            if limit < target_y as i16 {
                ram.w16(vars::TAILS_CPU_TARGET_Y, limit as u16);
            }
        }

        let target_x = ram.u16(vars::TAILS_CPU_TARGET_X);
        let mut d0 = o.x(ram).wrapping_sub(target_x) as i16 as i32;
        if d0 != 0 {
            let mut d2 = (d0.unsigned_abs() >> 4) as i32;
            if d2 >= 0x0C {
                d2 = 0x0C;
            }
            let sonic = Obj::new(vars::MAIN_CHARACTER);
            let mut d1 = (sonic.x_vel(ram) >> 8) as i32;
            if d1 < 0 {
                d1 = -d1;
            }
            d2 = ((d2 & 0xFF00) | ((d2 + d1) & 0xFF)) + 1;
            if d0 >= 0 {
                o.set_facing_left(ram, true);
                if d2 >= d0 {
                    d2 = d0;
                    d0 = 0;
                }
                d2 = -d2;
            } else {
                o.set_facing_left(ram, false);
                d0 = -d0;
                if d2 >= d0 {
                    d2 = (d2 & 0xFF00) | (d0 & 0xFF);
                    d0 = 0;
                }
            }
            o.set_x(ram, (o.x(ram) as i32 + d2) as u16);
        }
        let target_y = ram.u16(vars::TAILS_CPU_TARGET_Y);
        let dy = o.y(ram).wrapping_sub(target_y) as i16 as i32;
        if dy != 0 {
            o.set_y(ram, (o.y(ram) as i32 + if dy > 0 { -1 } else { 1 }) as u16);
        }

        if ram.u8(vars::SPECIAL_STAGE_REQUESTED) == 0 {
            let recorded_status =
                ram.u8(vars::SONIC_STAT_RECORD_BUF.wrapping_add(d3).wrapping_add(2));
            if (recorded_status & 0xD2) != 0 {
                return;
            }
            if (d0 | dy) != 0 {
                return;
            }
        }
        // Arrived (0x1FA96).
        ram.w16(vars::TAILS_CPU_ROUTINE, 6);
        o.set_obj_control(ram, 0);
        o.set_anim(
            ram,
            if ram.u8(vars::SPECIAL_STAGE_REQUESTED) != 0 {
                2
            } else {
                0
            },
        );
        o.set_x_vel(ram, 0);
        o.set_y_vel(ram, 0);
        o.set_inertia(ram, 0);
        o.set_status(ram, 2);
        o.set_move_lock(ram, 0);
        o.set_art_tile(ram, o.art_tile(ram) & 0x7FFF);
        let sonic = Obj::new(vars::MAIN_CHARACTER);
        if (sonic.art_tile(ram) as i16) < 0 {
            o.set_art_tile(ram, o.art_tile(ram) | 0x8000);
        }
        o.set_top_solid_bit(ram, sonic.top_solid_bit(ram));
        o.set_lrb_solid_bit(ram, sonic.lrb_solid_bit(ram));
        if sonic.anim(ram) == anim::SPINDASH {
            return;
        }
        let dash = o.spindash_flag(ram);
        if dash == 0 {
            return;
        }
        sonic.set_spindash_flag(ram, dash);
        // 0x25318: put Sonic into his rolling state.
        if sonic.rolling(ram) {
            return;
        }
        sonic.set_rolling(ram, true);
        sonic.set_y_radius(ram, 0x0E);
        sonic.set_x_radius(ram, 7);
        sonic.set_anim(ram, anim::ROLL);
        sonic.set_y(ram, sonic.y(ram).wrapping_add(5));
    }

    /// 0x1FB08: follow Sonic by replaying his recorded input with corrections; jump when he is above.
    fn cpu_follow(&mut self, ram: &mut Ram) {
        let o = self.obj;
        let sonic = Obj::new(vars::MAIN_CHARACTER);
        if sonic.routine(ram) >= 6 {
            ram.w16(vars::TAILS_CPU_ROUTINE, 4);
            o.set_spindash_flag(ram, 0);
            o.set_spindash_counter(ram, 0);
            o.set_obj_control(ram, 0x81);
            o.set_status(ram, 2);
            o.set_anim(ram, 0x20);
            return;
        }
        self.cpu_stuck_check(ram);
        if ram.u16(vars::TAILS_CONTROL_COUNTER) != 0 || (o.obj_control(ram) as i8) < 0 {
            self.cpu_manual_countdown(ram);
            return;
        }
        if ram.u8(vars::SPECIAL_STAGE_REQUESTED) != 0 {
            ram.w16(self.profile.ctrl, 0x0808);
            return;
        }
        if o.move_lock(ram) != 0 && o.inertia(ram) == 0 {
            ram.w16(vars::TAILS_CPU_ROUTINE, 8);
        }

        let d0 = history_index(ram);
        let mut d2 = ram
            .u16(vars::SONIC_POS_RECORD_BUF.wrapping_add(d0))
            .wrapping_sub(o.x(ram)) as i16 as i32;
        let d3 = ram.u16(vars::SONIC_POS_RECORD_BUF.wrapping_add(d0).wrapping_add(2));
        let mut d1 = ram.u16(vars::SONIC_STAT_RECORD_BUF.wrapping_add(d0));
        let d4 = ram.u8(vars::SONIC_STAT_RECORD_BUF.wrapping_add(d0).wrapping_add(2));

        let skip_x = o.pushing(ram) && (d4 & 0x20) == 0;
        if skip_x {
            Self::cpu_maybe_jump(ram, &mut d1);
            ram.w16(self.profile.ctrl, d1);
            return;
        }
        if d2 == 0 {
            o.set_facing_left(ram, false);
            if (d4 & 1) != 0 {
                o.set_facing_left(ram, true);
            }
        } else if d2 < 0 {
            d2 = -d2;
            if d2 >= 0x10 {
                d1 = (d1 & 0xF3F3) | 0x0404;
            }
            if o.inertia(ram) != 0 && o.facing_left(ram) {
                o.set_x(ram, o.x(ram).wrapping_sub(1));
            }
        } else {
            if d2 >= 0x10 {
                d1 = (d1 & 0xF3F3) | 0x0808;
            }
            if o.inertia(ram) != 0 && !o.facing_left(ram) {
                o.set_x(ram, o.x(ram).wrapping_add(1));
            }
        }
        if ram.u8(vars::TAILS_CPU_JUMPING) != 0 {
            d1 |= 0x7000;
            if o.in_air(ram) {
                ram.w16(self.profile.ctrl, d1);
                return;
            }
            ram.w8(vars::TAILS_CPU_JUMPING, 0);
        }
        if (ram.u16(vars::LEVEL_FRAME_COUNTER) & 0xFF) != 0 && (d2 as u32 & 0xFFFF) >= 0x40 {
            ram.w16(self.profile.ctrl, d1);
            return;
        }
        let dy = d3.wrapping_sub(o.y(ram)) as i16 as i32;
        if dy >= 0 {
            ram.w16(self.profile.ctrl, d1);
            return;
        }
        if -dy < 0x20 {
            ram.w16(self.profile.ctrl, d1);
            return;
        }
        Self::cpu_maybe_jump(ram, &mut d1);
        ram.w16(self.profile.ctrl, d1);
    }

    /// 0x1FC3C: every 64 frames, unless ducking, press jump.
    fn cpu_maybe_jump(ram: &mut Ram, d1: &mut u16) {
        if (ram.u8(vars::LEVEL_FRAME_COUNTER.wrapping_add(1)) & 0x3F) != 0 {
            return;
        }
        let o = Obj::new(vars::SIDEKICK);
        if o.anim(ram) == anim::DUCK {
            return;
        }
        *d1 |= 0x7070;
        ram.w8(vars::TAILS_CPU_JUMPING, 1);
    }

    fn cpu_manual_countdown(&mut self, ram: &mut Ram) {
        let counter = ram.u16(vars::TAILS_CONTROL_COUNTER);
        if counter != 0 {
            ram.w16(vars::TAILS_CONTROL_COUNTER, counter - 1);
        }
    }

    /// 0x1FC9C: off screen for 300 frames (or the object he stood on changed) -> respawn.
    fn cpu_stuck_check(&mut self, ram: &mut Ram) {
        let o = self.obj;
        if (o.render_flags(ram) as i8) < 0 {
            ram.w16(vars::TAILS_RESPAWN_COUNTER, 0);
        } else {
            if o.on_object(ram) {
                let obj = o.interact_object(ram);
                if ram.u8(vars::TAILS_INTERACT_ID) != obj.id(ram) {
                    self.cpu_respawn(ram);
                    return;
                }
            }
            let r = ram.u16(vars::TAILS_RESPAWN_COUNTER).wrapping_add(1);
            ram.w16(vars::TAILS_RESPAWN_COUNTER, r);
            if r >= 0x12C {
                self.cpu_respawn(ram);
                return;
            }
        }
        let id = o.interact_object(ram).id(ram);
        ram.w8(vars::TAILS_INTERACT_ID, id);
    }

    /// 0x1FC6A: take Tails off the field; the CPU flies him back in.
    fn cpu_respawn(&mut self, ram: &mut Ram) {
        let o = self.obj;
        ram.w16(vars::TAILS_CONTROL_COUNTER, 0);
        ram.w16(vars::TAILS_RESPAWN_COUNTER, 0);
        ram.w16(vars::TAILS_CPU_ROUTINE, 2);
        o.set_obj_control(ram, 0x81);
        o.set_status(ram, 2);
        o.set_x(ram, 0x4000);
        o.set_y(ram, 0);
        o.set_anim(ram, 0x20);
    }

    /// 0x1FCEE: stuck against something: duck and spindash toward Sonic.
    fn cpu_panic(&mut self, ram: &mut Ram) {
        let o = self.obj;
        self.cpu_stuck_check(ram);
        if ram.u16(vars::TAILS_CONTROL_COUNTER) != 0 {
            return;
        }
        if o.move_lock(ram) != 0 {
            return;
        }
        let sonic = Obj::new(vars::MAIN_CHARACTER);
        if o.spindash_flag(ram) != 0 {
            ram.w16(self.profile.ctrl, 0x0202);
            let t = ram.u8(vars::LEVEL_FRAME_COUNTER.wrapping_add(1)) & 0x7F;
            if t == 0 {
                ram.w16(self.profile.ctrl, 0);
                ram.w16(vars::TAILS_CPU_ROUTINE, 6);
                return;
            }
            if (t & 0x1F) == 0 {
                let ctrl = ram.u16(self.profile.ctrl);
                ram.w16(self.profile.ctrl, ctrl | 0x7070);
            }
            return;
        }
        if o.inertia(ram) != 0 {
            return;
        }
        o.set_facing_left(ram, false);
        if o.x(ram) >= sonic.x(ram) {
            o.set_facing_left(ram, true);
        }
        ram.w16(self.profile.ctrl, 0x0202);
        if (ram.u8(vars::LEVEL_FRAME_COUNTER.wrapping_add(1)) & 0x7F) == 0 {
            ram.w16(self.profile.ctrl, 0);
            ram.w16(vars::TAILS_CPU_ROUTINE, 6);
            return;
        }
        if o.anim(ram) != anim::DUCK {
            return;
        }
        ram.w16(self.profile.ctrl, 0x7272);
    }
}

/// Index of the position-history entry 16 frames back (byte arithmetic, as `HistoryIndex`).
fn history_index(ram: &Ram) -> u16 {
    let d2 = ((0x10u16 << 2) + 4) & 0xFF;
    let index = ram.u16(vars::SONIC_POS_RECORD_INDEX);
    (index & 0xFF00) | (index.wrapping_sub(d2) & 0xFF)
}

#[derive(Clone, Copy)]
enum AxisMove {
    YAdd,
    YSub,
    XAdd,
    XSub,
}

/// Spindash release speeds (0x1EADC normal, 0x1EAEE super), indexed by charge level 0-8.
const SPINDASH_SPEEDS: [u16; 18] = [
    0x800, 0x880, 0x900, 0x980, 0xA00, 0xA80, 0xB00, 0xB80, 0xC00, 0xB00, 0xB80, 0xC00, 0xC80,
    0xD00, 0xD80, 0xE00, 0xE80, 0xF00,
];

#[cfg(test)]
mod tests {
    use super::*;

    fn flat_sets() -> CollisionSets {
        // One fully flat collision id (angle 0, height/width 0 everywhere).
        CollisionSets {
            normal: collision::CollisionSet {
                name: "normal".into(),
                angles: vec![0; 256],
                heights: vec![0; 256 * 16],
                widths: vec![0; 256 * 16],
            },
            alternate: collision::CollisionSet {
                name: "alternate".into(),
                angles: vec![0; 256],
                heights: vec![0; 256 * 16],
                widths: vec![0; 256 * 16],
            },
        }
    }

    #[test]
    fn bias_eases_toward_the_default_sixty() {
        let mut ram = Ram::new();
        let mut player = Player::sonic();
        ram.w16(vars::CAMERA_Y_POS_BIAS, 0x50);
        player.bias_toward_default(&mut ram);
        assert_eq!(ram.u16(vars::CAMERA_Y_POS_BIAS), 0x52);
        ram.w16(vars::CAMERA_Y_POS_BIAS, 0x70);
        player.bias_toward_default(&mut ram);
        assert_eq!(ram.u16(vars::CAMERA_Y_POS_BIAS), 0x6E);
        ram.w16(vars::CAMERA_Y_POS_BIAS, 0x60);
        player.bias_toward_default(&mut ram);
        assert_eq!(ram.u16(vars::CAMERA_Y_POS_BIAS), 0x60);
    }

    #[test]
    fn friction_uses_the_unsigned_inertia_word() {
        // -$100 inertia decelerates by acceleration and wraps through zero as $FF00 + $0C.
        let sets = flat_sets();
        let mut ram = Ram::new();
        let mut player = Player::sonic();
        let o = player.obj;
        o.set_inertia(&mut ram, -0x100);
        ram.w16(vars::SONIC_TOP_SPEED, 0x600);
        ram.w16(vars::SONIC_ACCELERATION, 0x0C);
        ram.w16(vars::SONIC_DECELERATION, 0x80);
        player.friction_and_velocity(&mut ram, &sets, 0x0C);
        assert_eq!(o.inertia(&ram), -0xF4);
    }

    #[test]
    fn object_move_and_fall_uses_the_old_y_velocity() {
        // y_vel $100 moves a whole pixel, then gravity is added afterwards.
        let mut ram = Ram::new();
        let mut player = Player::sonic();
        let o = player.obj;
        o.set_y_vel(&mut ram, 0x100);
        o.set_y_long(&mut ram, 0x0010_0000);
        player.object_move_and_fall(&mut ram);
        assert_eq!(o.y_long(&ram), 0x0011_0000);
        assert_eq!(o.y_vel(&ram), 0x138);
    }

    #[test]
    fn move_right_skid_uses_the_high_byte_test() {
        let sets = flat_sets();
        let mut ram = Ram::new();
        let mut player = Player::sonic();
        let o = player.obj;
        ram.w16(vars::SONIC_TOP_SPEED, 0x600);
        ram.w16(vars::SONIC_ACCELERATION, 0x0C);
        ram.w16(vars::SONIC_DECELERATION, 0x80);
        o.set_inertia(&mut ram, -0x200);
        player.move_right(&mut ram, 0x600, 0x0C, 0x80);
        assert_eq!(o.inertia(&ram), -0x180);
        assert_eq!(o.anim(&ram), anim::WALK);
        // -$200 with deceleration $80 reaches -$180 (high byte $FE80 -> skid threshold is only near -$400).
        o.set_inertia(&mut ram, -0x400);
        player.move_right(&mut ram, 0x600, 0x0C, 0x80);
        assert_eq!(o.anim(&ram), anim::STOP);
        let _ = sets;
    }

    #[test]
    fn move_left_wraps_a_small_positive_inertia_to_minus_0x80() {
        // `sub.w d4,d0 / bcc`: a positive inertia smaller than the deceleration becomes -$80, not zero.
        let mut ram = Ram::new();
        let mut player = Player::sonic();
        let o = player.obj;
        o.set_inertia(&mut ram, 0x40);
        player.move_left(&mut ram, 0x600, 0x0C, 0x80);
        assert_eq!(o.inertia(&ram), -0x80);
    }

    #[test]
    fn spindash_release_uses_the_charge_table_and_scroll_delay() {
        let sets = flat_sets();
        let mut ram = Ram::new();
        let mut player = Player::sonic();
        let o = player.obj;
        ram.w16(vars::CURRENT_ZONE_AND_ACT, 0x4000);
        ram.w8(vars::HACK_ZONE_FLAG, 1);
        ram.w16(vars::CAMERA_MAX_Y_POS, 0x100);
        o.set_x(&mut ram, 0x40);
        o.set_y(&mut ram, 0x18C);
        o.set_spindash_flag(&mut ram, 1);
        o.set_spindash_counter(&mut ram, 0x400); // charge level 4
                                                 // Down is not held: release.
        assert!(player.check_spindash(&mut ram, &sets));
        assert_eq!(o.spindash_flag(&ram), 0);
        assert_eq!(o.inertia(&ram), 0xA00);
        assert_eq!(ram.u16(vars::HORIZ_SCROLL_DELAY_VAL), 0x1C00);
        assert_eq!(o.anim(&ram), anim::ROLL);
        assert!(o.rolling(&ram));
        assert_eq!(o.y_radius(&ram), 0x0E);
        assert_eq!(o.x_radius(&ram), 7);
        assert_eq!(o.y(&ram), 0x18C + 5);
    }

    #[test]
    fn jump_angle_finishes_a_flip_without_underflowing() {
        let mut ram = Ram::new();
        let mut player = Player::sonic();
        let o = player.obj;
        o.set_flip_angle(&mut ram, 0xFE);
        o.set_flip_speed(&mut ram, 4);
        o.set_flips_remaining(&mut ram, 0);
        o.set_inertia(&mut ram, 0x100);
        player.jump_angle(&mut ram);
        // $FE + 4 wraps to 2; with no flips left the angle is cleared instead of decremented below zero.
        assert_eq!(o.flip_angle(&ram), 0);
        assert_eq!(o.flips_remaining(&ram), 0);

        o.set_flip_angle(&mut ram, 0xFE);
        o.set_flips_remaining(&mut ram, 1);
        player.jump_angle(&mut ram);
        assert_eq!(o.flip_angle(&ram), 2);
        assert_eq!(o.flips_remaining(&ram), 0);
    }

    #[test]
    fn chg_jump_dir_drags_x_only_while_rising_slower_than_400() {
        let mut ram = Ram::new();
        let mut player = Player::sonic();
        let o = player.obj;
        ram.w16(vars::SONIC_TOP_SPEED, 0x600);
        ram.w16(vars::SONIC_ACCELERATION, 0x0C);
        o.set_x_vel(&mut ram, 0x200);
        o.set_y_vel(&mut ram, -0x100); // (y_vel & $FFFF) >= $FC00: drag applies
        player.chg_jump_dir(&mut ram);
        assert_eq!(o.x_vel(&ram), 0x1F0); // $200 - ($200 >> 5)

        o.set_x_vel(&mut ram, 0x200);
        o.set_y_vel(&mut ram, -0x500); // rising faster than $400: no drag
        player.chg_jump_dir(&mut ram);
        assert_eq!(o.x_vel(&ram), 0x200);
    }

    #[test]
    fn stick_or_fall_leaves_the_ground_when_it_drops_faster_than_the_speed_allows() {
        let mut ram = Ram::new();
        let mut player = Player::sonic();
        let o = player.obj;
        o.set_y(&mut ram, 0x100);
        // threshold = |x_vel >> 8| + 4 = 4: a 5 px drop leaves the ground, a 4 px drop sticks.
        player.stick_or_fall(&mut ram, 5, 0, AxisMove::YAdd);
        assert!(o.in_air(&ram));
        assert_eq!(o.y(&ram), 0x100);
        assert_eq!(o.prev_anim(&ram), 1);

        o.set_in_air(&mut ram, false);
        player.stick_or_fall(&mut ram, 4, 0, AxisMove::YAdd);
        assert!(!o.in_air(&ram));
        assert_eq!(o.y(&ram), 0x104);

        // Negative distances only move within 14 px.
        o.set_y(&mut ram, 0x100);
        player.stick_or_fall(&mut ram, -0x0F, 0, AxisMove::YAdd);
        assert_eq!(o.y(&ram), 0x100);
        player.stick_or_fall(&mut ram, -0x0E, 0, AxisMove::YAdd);
        assert_eq!(o.y(&ram), 0xF2);
    }

    #[test]
    fn level_bound_stops_at_both_camera_x_limits() {
        let mut ram = Ram::new();
        let mut player = Player::sonic();
        let o = player.obj;
        ram.w16(vars::CAMERA_MIN_X_POS, 0x200);
        ram.w16(vars::CAMERA_MAX_X_POS, 0x300);
        ram.w16(vars::CAMERA_MAX_Y_POS, 0x100);
        o.set_x(&mut ram, 0x100);
        player.level_bound(&mut ram).expect("in bounds");
        assert_eq!(o.x(&ram), 0x210);
        assert_eq!(o.x_vel(&ram), 0);

        o.set_x(&mut ram, 0x500);
        o.set_x_vel(&mut ram, 0x10);
        player.level_bound(&mut ram).expect("in bounds");
        assert_eq!(o.x(&ram), 0x468); // $300 + $128 + $40
        assert_eq!(o.x_vel(&ram), 0);
        assert_eq!(o.inertia(&ram), 0);
    }

    #[test]
    fn water_halves_velocities_and_clamps_rising_out() {
        let mut ram = Ram::new();
        let mut player = Player::sonic();
        let o = player.obj;
        o.set_x_vel(&mut ram, 0x200);
        o.set_y_vel(&mut ram, -0x200);
        player.set_water(&mut ram, true);
        assert!(o.underwater(&ram));
        assert_eq!(o.x_vel(&ram), 0x100);
        assert_eq!(o.y_vel(&ram), -0x100);
        assert_eq!(ram.u16(vars::SONIC_TOP_SPEED), 0x300);
        assert_eq!(ram.u16(vars::SONIC_ACCELERATION), 6);
        assert_eq!(ram.u16(vars::SONIC_DECELERATION), 0x40);

        o.set_y_vel(&mut ram, -0x500);
        player.set_water(&mut ram, false);
        assert!(!o.underwater(&ram));
        assert_eq!(o.y_vel(&ram), -0x600);
        assert_eq!(ram.u16(vars::SONIC_TOP_SPEED), 0x600);
    }

    #[test]
    fn tails_history_index_is_byte_arithmetic() {
        let mut ram = Ram::new();
        ram.w16(vars::SONIC_POS_RECORD_INDEX, 0x0044);
        assert_eq!(history_index(&ram), 0x0000);
        ram.w16(vars::SONIC_POS_RECORD_INDEX, 0x0050);
        assert_eq!(history_index(&ram), 0x000C);
        ram.w16(vars::SONIC_POS_RECORD_INDEX, 0x0144);
        assert_eq!(history_index(&ram), 0x0100);
    }

    #[test]
    fn tails_cpu_presses_jump_every_64_frames() {
        let mut ram = Ram::new();
        ram.w16(vars::LEVEL_FRAME_COUNTER, 0x40); // low byte $40: the $3F mask is zero
        let mut d1 = 0u16;
        Player::cpu_maybe_jump(&mut ram, &mut d1);
        assert_eq!(d1, 0x7070);
        assert_eq!(ram.u8(vars::TAILS_CPU_JUMPING), 1);

        ram.w8(vars::TAILS_CPU_JUMPING, 0);
        ram.w16(vars::LEVEL_FRAME_COUNTER, 0x41);
        let mut d2 = 0u16;
        Player::cpu_maybe_jump(&mut ram, &mut d2);
        assert_eq!(d2, 0);
        assert_eq!(ram.u8(vars::TAILS_CPU_JUMPING), 0);
    }
}
