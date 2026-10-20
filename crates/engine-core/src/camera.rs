//! `DeformBgLayer` (ROM `0xDCD6`): `ScrollHoriz` (0xF7A6), `ScrollVerti` (0xF81C), their scroll-flag bookkeeping
//! (0xF784 / 0xF932), `DynamicLevelEvents` (0x10804) and the SMB3 zones' background scroll.
//!
//! Only the parts that move `Camera_X_Pos`/`Camera_Y_Pos` and fill the horizontal scroll buffer are ported here.
//! Zone-specific level events for the Sonic zones (Emerald Hill / Green Hill / Marble Zone) and their backgrounds
//! are later milestones; the SMB3 Easter-egg zones ($40-$43) have no events and a flat background.

use crate::player::Unported;
use crate::{vars, Obj, Ram};

/// The SMB3 World 1-1 Easter egg (built from smb3.nes by Smb3ZoneBuilder): no events, a flat background.
pub const EASTER_EGG_ZONE: u8 = 0x40;
/// SMB3 World 1-2.
pub const WORLD_1_2_ZONE: u8 = 0x41;
/// SMB3 World 1-3.
pub const WORLD_1_3_ZONE: u8 = 0x42;
/// The SMB3 campaign areas.
pub const CAMPAIGN_ZONE: u8 = 0x43;

/// Zones built from SMB3 (`Smb3Levels.ByZone` / `Smb3Campaign.Zone`).
pub fn is_easter_egg(zone: u8) -> bool {
    matches!(
        zone,
        EASTER_EGG_ZONE | WORLD_1_2_ZONE | WORLD_1_3_ZONE | CAMPAIGN_ZONE
    )
}

/// Zones whose level events are ported (the SMB3 zones; the Sonic zones need their event scripts).
pub fn is_zone_ported(zone: u8) -> bool {
    matches!(zone, 0x00 | 0x11 | 0x12 | 0x13 | 0x14) || is_easter_egg(zone)
}

#[derive(Default)]
pub struct Camera;

impl Camera {
    /// `DeformBgLayer`'s level-event half, used once by a debug start (the legacy `Camera.RunLevelEvents`).
    pub fn run_level_events(&mut self, ram: &mut Ram) -> Result<(), Unported> {
        self.zone_events(ram)
    }

    /// `DeformBgLayer` (0xDCD6) for one frame.
    pub fn deform_bg_layer(&mut self, ram: &mut Ram, wave_table: &[u8]) -> Result<(), Unported> {
        if ram.u8(vars::DEFORM_LOCK) != 0 {
            return Ok(()); // camera frozen
        }
        for offset in (0..=0x0E).step_by(2) {
            ram.w16(vars::SCROLL_FLAGS.wrapping_add(offset), 0);
        }
        for a in [
            vars::CAMERA_X_POS_DIFF,
            vars::CAMERA_Y_POS_DIFF,
            vars::CAMERA_X_POS_DIFF_P2,
            vars::CAMERA_Y_POS_DIFF_P2,
        ] {
            ram.w16(a, 0);
        }

        let skip_scroll =
            ram.u8(vars::CURRENT_ZONE_AND_ACT) == 0x10 && ram.u16(vars::DEBUG_PLACEMENT_MODE) == 0;
        if !skip_scroll && ram.u8(vars::SCROLL_LOCK) == 0 {
            if ram.u16(vars::PLAYER_MODE) == 2 {
                return Err(Unported::new(0xDD4A, "camera following Tails"));
            }
            let old_x = self.scroll_horiz(ram, vars::MAIN_CHARACTER);
            self.scroll_flags_h(ram, old_x);
            let bias = ram.u16(vars::CAMERA_Y_POS_BIAS);
            let old_y = self.scroll_verti(ram, vars::MAIN_CHARACTER, bias, old_x);
            self.scroll_flags_v(ram, old_y);
        }
        if !skip_scroll && ram.u16(vars::TWO_PLAYER_MODE) != 0 {
            return Err(Unported::new(0xDD8C, "2P camera"));
        }

        self.dynamic_level_events(ram)?;
        ram.w16(vars::VSCROLL_FACTOR_FG, ram.u16(vars::CAMERA_Y_POS));
        ram.w16(vars::VSCROLL_FACTOR_BG, ram.u16(vars::CAMERA_BG_Y_POS));
        ram.w32(vars::CAMERA_X_POS_COPY, ram.u32(vars::CAMERA_X_POS));
        ram.w32(vars::CAMERA_Y_POS_COPY, ram.u32(vars::CAMERA_Y_POS));
        let zone = ram.u8(vars::CURRENT_ZONE_AND_ACT);
        if is_easter_egg(zone) {
            self.sw_scrl_easter_egg(ram);
        } else if zone == 0x00 {
            self.sw_scrl_ehz(ram, wave_table);
        } else if zone == 0x11 || zone == 0x12 {
            self.sw_scrl_ghz(ram);
        } else if zone == 0x13 || zone == 0x14 {
            self.sw_scrl_mz(ram);
        } else {
            return Err(Unported::new(
                0xDDF2,
                format!("background deformation for zone {zone:02X}"),
            ));
        }
        Ok(())
    }

    /// `ScrollHoriz` (0xF7A6). Returns the previous camera X (d4).
    fn scroll_horiz(&mut self, ram: &mut Ram, obj: u16) -> i32 {
        let cam_x = ram.u16(vars::CAMERA_X_POS) as i32;
        let d4 = cam_x;
        if ram.u8(vars::TELEPORT_FLAG) != 0 {
            return d4;
        }
        let mut delay = ram.u16(vars::HORIZ_SCROLL_DELAY_VAL);
        let target = if delay != 0 {
            // Spindash scroll delay: follow Sonic's recorded position from a few frames back.
            delay = delay.wrapping_sub(0x100);
            ram.w16(vars::HORIZ_SCROLL_DELAY_VAL, delay);
            let d1 = (((delay >> 8) << 2) + 4) & 0xFF;
            let index = ram.u16(vars::HORIZ_SCROLL_DELAY_VAL.wrapping_add(2));
            let index = (index & 0xFF00) | (index.wrapping_sub(d1) & 0xFF);
            (ram.u16(vars::SONIC_POS_RECORD_BUF.wrapping_add(index)) & 0x3FFF) as i32
        } else {
            ram.u16(obj.wrapping_add(0x08)) as i32
        };
        let mut d0 = (target - cam_x - 0x90) as i16 as i32;
        let mut new_x;
        if d0 < 0 {
            if d0 <= -0x10 {
                d0 = -0x10;
            }
            new_x = (d0 + cam_x) as i16 as i32;
            let min = ram.u16(vars::CAMERA_MIN_X_POS) as i16 as i32;
            if new_x <= min {
                new_x = min;
            }
        } else {
            d0 -= 0x10;
            if d0 < 0 {
                ram.w16(vars::CAMERA_X_POS_DIFF, 0);
                return d4;
            }
            if d0 >= 0x10 {
                d0 = 0x10;
            }
            new_x = (d0 + cam_x) as i16 as i32;
            let max = ram.u16(vars::CAMERA_MAX_X_POS) as i16 as i32;
            if new_x >= max {
                new_x = max;
            }
        }
        ram.w16(vars::CAMERA_X_POS_DIFF, ((new_x - cam_x) << 8) as u16);
        ram.w16(vars::CAMERA_X_POS, new_x as u16);
        d4
    }

    /// 0xF784: mark a column redraw when the camera crosses a 16 px boundary.
    fn scroll_flags_h(&mut self, ram: &mut Ram, old_x: i32) {
        let cam = ram.u16(vars::CAMERA_X_POS) as i32;
        if ((cam & 0x10) ^ ram.u8(vars::HORIZ_BLOCK_CROSSED_FLAG) as i32) != 0 {
            return;
        }
        let flag = ram.u8(vars::HORIZ_BLOCK_CROSSED_FLAG) ^ 0x10;
        ram.w8(vars::HORIZ_BLOCK_CROSSED_FLAG, flag);
        ram.set_bit(
            vars::SCROLL_FLAGS,
            if (cam - old_x) as i16 >= 0 { 3 } else { 2 },
        );
    }

    /// 0xF932: as above, for rows.
    fn scroll_flags_v(&mut self, ram: &mut Ram, old_y: i32) {
        let cam = ram.u16(vars::CAMERA_Y_POS) as i32;
        if ((cam & 0x10) ^ ram.u8(vars::VERTI_BLOCK_CROSSED_FLAG) as i32) != 0 {
            return;
        }
        let flag = ram.u8(vars::VERTI_BLOCK_CROSSED_FLAG) ^ 0x10;
        ram.w8(vars::VERTI_BLOCK_CROSSED_FLAG, flag);
        ram.set_bit(
            vars::SCROLL_FLAGS,
            if (cam - old_y) as i16 >= 0 { 1 } else { 0 },
        );
    }

    /// `ScrollVerti` (0xF81C). `bias` is the camera bias; returns the previous camera Y when the camera moved,
    /// otherwise `d4` (ScrollHoriz's previous X).
    fn scroll_verti(&mut self, ram: &mut Ram, obj: u16, bias: u16, d4: i32) -> i32 {
        let o = Obj::new(obj);
        let cam_y = ram.u16(vars::CAMERA_Y_POS) as i32;
        let mut d0 = (o.y(ram) as i32 - cam_y) & 0xFFFF;
        if ram.u16(vars::CAMERA_MIN_Y_POS) == 0xFF00 {
            d0 &= 0x7FF;
        }
        if o.rolling(ram) {
            d0 = (d0 - 5) & 0xFFFF;
        }
        let bias = bias as i32;

        if o.in_air(ram) {
            d0 = (d0 + 0x20) & 0xFFFF;
            let u = d0 - bias;
            if u < 0 {
                return self.verti_fast(ram, (u & 0xFFFF) as i16 as i32);
            }
            let u = u - 0x40;
            if u >= 0 {
                return self.verti_fast(ram, u);
            }
            if ram.u8(vars::CAMERA_MAX_Y_POS_CHANGING) != 0 {
                return self.verti_settle(ram);
            }
            ram.w16(vars::CAMERA_Y_POS_DIFF, 0);
            return d4;
        }

        d0 = (d0 - bias) & 0xFFFF;
        if d0 == 0 {
            if ram.u8(vars::CAMERA_MAX_Y_POS_CHANGING) != 0 {
                return self.verti_settle(ram);
            }
            ram.w16(vars::CAMERA_Y_POS_DIFF, 0);
            return d4;
        }
        let sd0 = d0 as i16 as i32;
        if bias == 0x60 {
            let inertia = (o.inertia(ram) as i32).unsigned_abs() & 0xFFFF;
            if inertia >= 0x800 {
                return self.verti_fast(ram, d0 as i16 as i32);
            }
            if sd0 > 6 {
                return self.verti_move_down(ram, 0x600);
            }
            if sd0 < -6 {
                return self.verti_move_up(ram, 0x600);
            }
            return self.verti_snap(ram, sd0);
        }
        if sd0 > 2 {
            return self.verti_move_down(ram, 0x200);
        }
        if sd0 < -2 {
            return self.verti_move_up(ram, 0x200);
        }
        self.verti_snap(ram, sd0)
    }

    fn verti_fast(&mut self, ram: &mut Ram, v: i32) -> i32 {
        let s = v as i16 as i32;
        if s > 0x10 {
            return self.verti_move_down(ram, 0x1000);
        }
        if s < -0x10 {
            return self.verti_move_up(ram, 0x1000);
        }
        self.verti_snap(ram, s)
    }

    fn verti_settle(&mut self, ram: &mut Ram) -> i32 {
        ram.w8(vars::CAMERA_MAX_Y_POS_CHANGING, 0);
        self.verti_snap(ram, 0)
    }

    /// 0xF8B6: move straight to the target; sub-pixel cleared.
    fn verti_snap(&mut self, ram: &mut Ram, delta: i32) -> i32 {
        let cam_y = ram.u16(vars::CAMERA_Y_POS) as i32;
        let pos = (delta + cam_y) & 0xFFFF;
        let full = (pos as i64) << 16;
        if delta >= 0 {
            self.verti_clamp_down(ram, full)
        } else {
            self.verti_clamp_up(ram, full)
        }
    }

    fn verti_move_up(&mut self, ram: &mut Ram, sp: i32) -> i32 {
        let full = ((ram.u32(vars::CAMERA_Y_POS) as i64) - ((sp as i64) << 8)) & 0xFFFF_FFFF;
        self.verti_clamp_up(ram, full)
    }

    fn verti_move_down(&mut self, ram: &mut Ram, sp: i32) -> i32 {
        let full = ((ram.u32(vars::CAMERA_Y_POS) as i64) + ((sp as i64) << 8)) & 0xFFFF_FFFF;
        self.verti_clamp_down(ram, full)
    }

    /// 0xF8D0.
    fn verti_clamp_up(&mut self, ram: &mut Ram, full: i64) -> i32 {
        let pos = ((full >> 16) & 0xFFFF) as i32;
        let min = ram.u16(vars::CAMERA_MIN_Y_POS) as i16 as i32;
        if (pos as i16 as i32) > min {
            return self.verti_commit(ram, full);
        }
        if (pos as i16 as i32) > -0x100 {
            return self.verti_commit(ram, ((min as u16 as i64) << 16) | (full & 0xFFFF));
        }
        let pos = pos & 0x7FF;
        ram.w16(vars::CAMERA_Y_POS, ram.u16(vars::CAMERA_Y_POS) & 0x7FF);
        let sonic = Obj::new(vars::MAIN_CHARACTER);
        sonic.set_y(ram, sonic.y(ram) & 0x7FF); // the level wraps vertically
        let tails = Obj::new(vars::SIDEKICK);
        tails.set_y(ram, tails.y(ram) & 0x7FF);
        self.verti_commit(ram, ((pos as i64) << 16) | (full & 0xFFFF))
    }

    /// 0xF900.
    fn verti_clamp_down(&mut self, ram: &mut Ram, full: i64) -> i32 {
        let pos = ((full >> 16) & 0xFFFF) as i32;
        let max = ram.u16(vars::CAMERA_MAX_Y_POS) as i16 as i32;
        if (pos as i16 as i32) < max {
            return self.verti_commit(ram, full);
        }
        let wrapped = pos - 0x800;
        if wrapped < 0 {
            return self.verti_commit(ram, ((max as u16 as i64) << 16) | (full & 0xFFFF));
        }
        ram.w16(
            vars::CAMERA_Y_POS,
            ram.u16(vars::CAMERA_Y_POS).wrapping_sub(0x800),
        );
        let sonic = Obj::new(vars::MAIN_CHARACTER);
        sonic.set_y(ram, sonic.y(ram) & 0x7FF);
        let tails = Obj::new(vars::SIDEKICK);
        tails.set_y(ram, tails.y(ram) & 0x7FF);
        self.verti_commit(ram, ((wrapped as i64) << 16) | (full & 0xFFFF))
    }

    /// 0xF922: store, and the scroll difference (`ror.l #8` of the long delta).
    fn verti_commit(&mut self, ram: &mut Ram, full: i64) -> i32 {
        let old = ram.u16(vars::CAMERA_Y_POS) as i32;
        let delta = (full - ram.u32(vars::CAMERA_Y_POS) as i64) as u32;
        ram.w16(vars::CAMERA_Y_POS_DIFF, delta.rotate_right(8) as u16);
        ram.w32(vars::CAMERA_Y_POS, full as u32);
        old
    }

    /// The per-zone part of `DynamicLevelEvents`.
    fn zone_events(&mut self, ram: &mut Ram) -> Result<(), Unported> {
        let zone = ram.u8(vars::CURRENT_ZONE_AND_ACT);
        let act = ram.u8(vars::CURRENT_ZONE_AND_ACT.wrapping_add(1));
        if !is_zone_ported(zone) {
            return Err(Unported::new(
                0x10804,
                format!("level events for zone {zone:02X}"),
            ));
        }
        if zone == 0x00 {
            self.ehz_events(ram, act);
        } else if zone == 0x11 || zone == 0x12 {
            self.ghz_events(ram, act);
        } else if zone == 0x13 {
            if act == 0 {
                self.mz1_events(ram);
            } else {
                self.mz2_events(ram);
            }
        } else if zone == 0x14 {
            self.mz3_events(ram);
        } else if !is_easter_egg(zone) {
            return Err(Unported::new(
                0x10804,
                format!("level events for zone {zone:02X}"),
            ));
        }
        Ok(())
    }

    /// `DynamicLevelEvents` (0x10804): per-zone events, then ease `Camera_Max_Y` toward its target.
    fn dynamic_level_events(&mut self, ram: &mut Ram) -> Result<(), Unported> {
        self.zone_events(ram)?;

        let target = ram.u16(vars::CAMERA_MAX_Y_POS_TARGET);
        let max = ram.u16(vars::CAMERA_MAX_Y_POS);
        if target == max {
            return Ok(());
        }
        let mut step = 2i32;
        if target < max {
            step = -2;
            let cam_y = ram.u16(vars::CAMERA_Y_POS) as i32;
            if cam_y > target as i32 {
                ram.w16(vars::CAMERA_MAX_Y_POS, (cam_y & 0xFFFE) as u16);
            }
            ram.w16(
                vars::CAMERA_MAX_Y_POS,
                ram.u16(vars::CAMERA_MAX_Y_POS).wrapping_add(step as u16),
            );
            ram.w8(vars::CAMERA_MAX_Y_POS_CHANGING, 1);
            return Ok(());
        }
        if ram.u16(vars::CAMERA_Y_POS).wrapping_add(8) >= max {
            let sonic = Obj::new(vars::MAIN_CHARACTER);
            if sonic.in_air(ram) {
                step *= 4;
            }
        }
        ram.w16(vars::CAMERA_MAX_Y_POS, max.wrapping_add(step as u16));
        ram.w8(vars::CAMERA_MAX_Y_POS_CHANGING, 1);
        Ok(())
    }

    /// Green Hill events (0x11872 / 0x118F8). Act 3 places Eggman at x $2A60 and locks the arena at $2960.
    fn ghz_events(&mut self, ram: &mut Ram, act: u8) {
        let cam_x = ram.u16(vars::CAMERA_X_POS);
        if ram.u8(vars::CURRENT_ZONE_AND_ACT) == 0x12 {
            match ram.u8(vars::DYNAMIC_RESIZE_ROUTINE) {
                0 => {
                    set_bottom_target(ram, 0x300);
                    if cam_x < 0x380 {
                        return;
                    }
                    set_bottom_target(ram, 0x310);
                    if cam_x < 0x960 {
                        return;
                    }
                    if ram.u16(vars::CAMERA_Y_POS) >= 0x280 {
                        set_bottom_target(ram, 0x400);
                        if cam_x < 0x1380 {
                            ram.w16(vars::CAMERA_MAX_Y_POS, 0x4C0);
                        }
                        if cam_x < 0x1380 {
                            set_bottom_target(ram, 0x4C0);
                        }
                        if cam_x < 0x1700 {
                            return;
                        }
                    }
                    set_bottom_target(ram, 0x300);
                    next_event(ram);
                }
                2 => {
                    if cam_x < 0x960 {
                        let r = ram.u8(vars::DYNAMIC_RESIZE_ROUTINE).wrapping_sub(2);
                        ram.w8(vars::DYNAMIC_RESIZE_ROUTINE, r);
                    }
                    if cam_x < 0x2960 {
                        return;
                    }
                    if let Some(boss) = crate::objects::find_free_slot(ram) {
                        boss.set_id(ram, 0x3D);
                        boss.set_x(ram, 0x2A60);
                        boss.set_y(ram, 0x280);
                    }
                    set_left(ram, 0x2960);
                    set_right(ram, 0x2960);
                    next_event(ram);
                    ram.w8(vars::CURRENT_BOSS_ID, 1);
                }
                _ => {
                    set_left(ram, cam_x);
                }
            }
            return;
        }
        match act {
            0 => {
                set_bottom_target(ram, 0x300);
                if cam_x >= 0x1780 {
                    set_bottom_target(ram, 0x400);
                    ram.w16(vars::CAMERA_MAX_Y_POS, 0x400);
                }
            }
            1 => {
                set_bottom_target(ram, 0x300);
                if cam_x < 0xED0 {
                    return;
                }
                set_bottom_target(ram, 0x200);
                if cam_x < 0x1600 {
                    return;
                }
                set_bottom_target(ram, 0x400);
                if cam_x < 0x1D60 {
                    return;
                }
                set_bottom_target(ram, 0x300);
            }
            _ => {}
        }
    }

    /// Marble Zone act 1/2/3 events (0x11A02 / 0x11B5E / 0x11B80).
    fn mz1_events(&mut self, ram: &mut Ram) {
        let cam_x = ram.u16(vars::CAMERA_X_POS);
        let cam_y = ram.u16(vars::CAMERA_Y_POS);
        match ram.u8(vars::DYNAMIC_RESIZE_ROUTINE) {
            0 => {
                set_bottom_target(ram, 0x1D0);
                if cam_x < 0x700 {
                    return;
                }
                set_bottom_target(ram, 0x220);
                if cam_x < 0xD00 {
                    return;
                }
                set_bottom_target(ram, 0x340);
                if cam_y < 0x340 {
                    return;
                }
                next_event(ram);
            }
            2 => {
                ram.w16(
                    vars::TAILS_MAX_Y_POS,
                    ram.u16(vars::CAMERA_MAX_Y_POS_TARGET),
                );
                if cam_y < 0x340 {
                    let r = ram.u8(vars::DYNAMIC_RESIZE_ROUTINE).wrapping_sub(2);
                    ram.w8(vars::DYNAMIC_RESIZE_ROUTINE, r);
                    return;
                }
                set_top(ram, 0);
                if cam_x >= 0xE00 {
                    return;
                }
                set_top(ram, 0x340);
                set_bottom_target(ram, 0x340);
                if cam_x >= 0xA90 {
                    return;
                }
                set_bottom_target(ram, 0x500);
                if cam_y >= 0x370 {
                    next_event(ram);
                }
            }
            4 => {
                ram.w16(
                    vars::TAILS_MAX_Y_POS,
                    ram.u16(vars::CAMERA_MAX_Y_POS_TARGET),
                );
                if cam_y < 0x370 {
                    let r = ram.u8(vars::DYNAMIC_RESIZE_ROUTINE).wrapping_sub(2);
                    ram.w8(vars::DYNAMIC_RESIZE_ROUTINE, r);
                    return;
                }
                if cam_y >= 0x500 && cam_x >= 0xB80 {
                    set_top(ram, 0x500);
                    next_event(ram);
                }
            }
            6 => {
                if cam_x < 0xB80 {
                    if ram.u16(vars::CAMERA_MIN_Y_POS) == 0x340 {
                        return;
                    }
                    ram.w16(
                        vars::CAMERA_MIN_Y_POS,
                        ram.u16(vars::CAMERA_MIN_Y_POS).wrapping_sub(2),
                    );
                    ram.w16(
                        vars::TAILS_MIN_Y_POS,
                        ram.u16(vars::TAILS_MIN_Y_POS).wrapping_sub(2),
                    );
                    return;
                }
                if ram.u16(vars::CAMERA_MIN_Y_POS) != 0x500 {
                    if cam_y < 0x500 {
                        return;
                    }
                    set_top(ram, 0x500);
                }
                if cam_x < 0xE70 {
                    return;
                }
                set_top(ram, 0);
                set_bottom_target(ram, 0x500);
                if cam_x < 0x1430 {
                    return;
                }
                set_bottom_target(ram, 0x210);
            }
            _ => {}
        }
    }

    fn mz2_events(&mut self, ram: &mut Ram) {
        set_bottom_target(ram, 0x520);
        if ram.u16(vars::CAMERA_X_POS) >= 0x1700 {
            set_bottom_target(ram, 0x200);
        }
    }

    fn mz3_events(&mut self, ram: &mut Ram) {
        let cam_x = ram.u16(vars::CAMERA_X_POS);
        if ram.u8(vars::DYNAMIC_RESIZE_ROUTINE) != 0 {
            set_left(ram, cam_x);
            return;
        }
        set_bottom_target(ram, 0x720);
        if cam_x < 0x1560 {
            return;
        }
        set_bottom_target(ram, 0x210);
        if cam_x < 0x17F0 {
            return;
        }
        if let Some(boss) = crate::objects::find_free_slot(ram) {
            boss.set_id(ram, 0x73);
            boss.set_x(ram, 0x19F0);
            boss.set_y(ram, 0x22C);
        }
        set_left(ram, 0x1800);
        set_right(ram, 0x1800);
        ram.w8(vars::DYNAMIC_RESIZE_ROUTINE, 2);
        ram.w8(vars::CURRENT_BOSS_ID, 1);
    }

    /// `SwScrlGhz` (0xF006): Green Hill's parallax clouds and water.
    fn sw_scrl_ghz(&mut self, ram: &mut Ram) {
        let diff = ram.u16(vars::CAMERA_X_POS_DIFF) as i16 as i32;
        bg_scroll(
            ram,
            vars::CAMERA_BG3_X_POS,
            vars::HORIZ_BLOCK_CROSSED_FLAG_BG3,
            vars::SCROLL_FLAGS_BG3,
            diff * 96,
            0,
        );
        bg_scroll(
            ram,
            vars::CAMERA_BG2_X_POS,
            vars::HORIZ_BLOCK_CROSSED_FLAG_BG2,
            vars::SCROLL_FLAGS_BG2,
            diff << 7,
            0,
        );

        let mut a1 = vars::HORIZ_SCROLL_BUF;
        let mut d0 = ((ram.u16(vars::CAMERA_Y_POS) & 0x7FF) >> 5) as i32;
        d0 = -d0 + 0x20;
        if d0 < 0 {
            d0 = 0;
        }
        let d4 = d0;
        ram.w16(vars::VSCROLL_FACTOR_BG, d0 as u16);
        let fg = (-(ram.u16(vars::CAMERA_X_POS) as i32)) as u16;

        ram.w32(
            vars::CLOUD_COUNTERS,
            ram.u32(vars::CLOUD_COUNTERS).wrapping_add(0x10000),
        );
        ram.w32(
            vars::CLOUD_COUNTERS + 4,
            ram.u32(vars::CLOUD_COUNTERS + 4).wrapping_add(0xC000),
        );
        ram.w32(
            vars::CLOUD_COUNTERS + 8,
            ram.u32(vars::CLOUD_COUNTERS + 8).wrapping_add(0x8000),
        );
        let bg3 = ram.u16(vars::CAMERA_BG3_X_POS);

        let lines = |ram: &mut Ram, a1: &mut u16, count: i32, bg: u16| {
            for _ in 0..count {
                line(ram, a1, fg, bg);
            }
        };
        let n = 0x1F - d4;
        if n >= 0 {
            let bg = (-(ram.u16(vars::CLOUD_COUNTERS).wrapping_add(bg3) as i32)) as u16;
            lines(ram, &mut a1, n + 1, bg);
        }
        let bg = (-(ram.u16(vars::CLOUD_COUNTERS + 4).wrapping_add(bg3) as i32)) as u16;
        lines(ram, &mut a1, 16, bg);
        let bg = (-(ram.u16(vars::CLOUD_COUNTERS + 8).wrapping_add(bg3) as i32)) as u16;
        lines(ram, &mut a1, 16, bg);
        lines(ram, &mut a1, 0x30, (-(bg3 as i32)) as u16);
        let bg2 = ram.u16(vars::CAMERA_BG2_X_POS);
        lines(ram, &mut a1, 0x28, (-(bg2 as i32)) as u16);

        let mut d2 = (ram.u16(vars::CAMERA_X_POS).wrapping_sub(bg2)) as i16 as i32;
        d2 = ((d2 << 8) / 0x68) << 8;
        let mut d3 = (bg2 as i64) << 16;
        for _ in 0..=0x47 + d4 {
            ram.w16(a1, fg);
            ram.w16(a1.wrapping_add(2), (-(d3 >> 16) as i32) as u16);
            a1 = a1.wrapping_add(4);
            d3 = (d3 + d2 as i64) & 0xFFFF_FFFF;
        }
    }

    /// `SwScrlMz` (0xF0F6): Marble Zone's parallax layers and band table.
    fn sw_scrl_mz(&mut self, ram: &mut Ram) {
        let diff = ram.u16(vars::CAMERA_X_POS_DIFF) as i16 as i32;
        layer_scroll(
            ram,
            vars::CAMERA_BG_X_POS,
            vars::HORIZ_BLOCK_CROSSED_FLAG_BG,
            vars::SCROLL_FLAGS_BG,
            (diff << 6) * 3,
            2,
        );
        layer_scroll(
            ram,
            vars::CAMERA_BG3_X_POS,
            vars::HORIZ_BLOCK_CROSSED_FLAG_BG3,
            vars::SCROLL_FLAGS_BG3,
            diff << 6,
            6,
        );
        layer_scroll(
            ram,
            vars::CAMERA_BG2_X_POS,
            vars::HORIZ_BLOCK_CROSSED_FLAG_BG2,
            vars::SCROLL_FLAGS_BG2,
            diff << 7,
            4,
        );
        let mut d0 = 0x200i32;
        let d1 = ram.u16(vars::CAMERA_Y_POS).wrapping_sub(0x1C8) as i16 as i32;
        if ram.u16(vars::CAMERA_Y_POS) >= 0x1C8 {
            d0 += (d1 * 3) >> 2;
        }
        let d0 = d0 as u16;
        ram.w16(vars::CAMERA_BG2_Y_POS, d0);
        ram.w16(vars::CAMERA_BG3_Y_POS, d0);
        set_bg_y(ram, d0);
        ram.w16(vars::VSCROLL_FACTOR_BG, ram.u16(vars::CAMERA_BG_Y_POS));
        let combined = ram.u8(vars::SCROLL_FLAGS_BG3)
            | ram.u8(vars::SCROLL_FLAGS_BG)
            | ram.u8(vars::SCROLL_FLAGS_BG2);
        ram.w8(vars::SCROLL_FLAGS_BG3, combined);
        ram.w8(vars::SCROLL_FLAGS_BG, 0);
        ram.w8(vars::SCROLL_FLAGS_BG2, 0);

        let mut a1 = vars::CLOUD_COUNTERS;
        let d2 = (-(ram.u16(vars::CAMERA_X_POS) as i32)) as u16 as i16 as i32;
        let step = ((((d2 >> 2) - d2) * 8) / 5) as i16;
        let mut acc = ((d2 >> 1) & 0xFFFF) as u32;
        acc <<= 16;
        for _ in 0..5 {
            ram.w16(a1, (acc >> 16) as u16);
            a1 = a1.wrapping_add(2);
            acc = acc.wrapping_add((step as u32) << 12);
        }
        let bg3 = (-(ram.u16(vars::CAMERA_BG3_X_POS) as i32)) as u16;
        for _ in 0..2 {
            ram.w16(a1, bg3);
            a1 = a1.wrapping_add(2);
        }
        let bg2 = (-(ram.u16(vars::CAMERA_BG2_X_POS) as i32)) as u16;
        for _ in 0..9 {
            ram.w16(a1, bg2);
            a1 = a1.wrapping_add(2);
        }
        let bg1 = (-(ram.u16(vars::CAMERA_BG_X_POS) as i32)) as u16;
        for _ in 0..16 {
            ram.w16(a1, bg1);
            a1 = a1.wrapping_add(2);
        }

        let y = ram.u16(vars::CAMERA_BG_Y_POS).wrapping_sub(0x200);
        let mut a2 = vars::CLOUD_COUNTERS + (((if y >= 0x100 { 0x100 } else { y }) & 0x1F0) >> 3);
        let fg = (-(ram.u16(vars::CAMERA_X_POS) as i32)) as u16;
        let mut line = 0i32;
        let first = 16 - (y & 0xF) as i32;
        let mut bg = ram.u16(a2);
        a2 = a2.wrapping_add(2);
        for _ in 0..first {
            write_bg_line(ram, &mut line, fg, bg);
        }
        for _ in 0..15 {
            bg = ram.u16(a2);
            a2 = a2.wrapping_add(2);
            for _ in 0..16 {
                write_bg_line(ram, &mut line, fg, bg);
            }
        }
    }

    /// Emerald Hill (zone $00, Sonic 2's events, 0x108A4).
    /// Act 1 has none. Act 2: past x $2780 the left limit
    /// follows the camera and the bottom limit becomes $390; at $28F0 the arena locks ($28F0-$2940), the music
    /// fades, the boss flag becomes 2 and the boss art loads (PLC $29); once the camera is down at $388, 90
    /// frames later the boss (object $56, subtype $81) appears at $29D0,$426 with the boss music; once it is
    /// beaten the left limit follows.
    fn ehz_events(&mut self, ram: &mut Ram, act: u8) {
        if act == 0 {
            return;
        }
        let cam_x = ram.u16(vars::CAMERA_X_POS);
        match ram.u8(vars::DYNAMIC_RESIZE_ROUTINE) {
            0 => {
                if ram.u16(vars::TWO_PLAYER_MODE) != 0 {
                    set_right(ram, 0x2920);
                    return;
                }
                if cam_x < 0x2780 {
                    return;
                }
                set_left(ram, cam_x);
                ram.w16(vars::CAMERA_MAX_Y_POS_TARGET, 0x390);
                ram.w16(vars::TAILS_MAX_Y_POS, 0x390);
                next_event(ram);
            }
            2 => {
                if cam_x < 0x28F0 {
                    return;
                }
                set_left(ram, 0x28F0);
                set_right(ram, 0x2940);
                next_event(ram);
                ram.w8(vars::BOSS_SPAWN_DELAY, 0);
                ram.w8(vars::CURRENT_BOSS_ID, 2);
            }
            4 => {
                if ram.u16(vars::CAMERA_Y_POS) >= 0x388 {
                    ram.w16(vars::CAMERA_MIN_Y_POS, 0x388);
                    ram.w16(vars::TAILS_MIN_Y_POS, 0x388);
                }
                let delay = ram.u8(vars::BOSS_SPAWN_DELAY).wrapping_add(1);
                ram.w8(vars::BOSS_SPAWN_DELAY, delay);
                if delay < 0x5A {
                    return;
                }
                if let Some(boss) = crate::objects::find_free_slot(ram) {
                    boss.set_id(ram, 0x56);
                    boss.set_subtype(ram, 0x81);
                    boss.set_x(ram, 0x29D0);
                    boss.set_y(ram, 0x426);
                }
                next_event(ram);
            }
            6 => {
                if ram.u8(vars::BOSS_DEFEATED_FLAG) == 0 {
                    return;
                }
                ram.w16(vars::CAMERA_MIN_X_POS, cam_x);
                ram.w16(vars::TAILS_MAX_X_POS, ram.u16(vars::CAMERA_MAX_X_POS));
                ram.w16(vars::TAILS_MIN_X_POS, cam_x);
            }
            _ => {}
        }
    }

    /// `SwScrlEhz` (0xDE9C): Emerald Hill's six bands of parallax plus the near-hills interpolation.
    fn sw_scrl_ehz(&mut self, ram: &mut Ram, wave_table: &[u8]) {
        ram.w16(vars::VSCROLL_FACTOR_BG, ram.u16(vars::CAMERA_BG_Y_POS));
        let mut a1 = vars::HORIZ_SCROLL_BUF;
        let d2 = (-(ram.u16(vars::CAMERA_X_POS) as i16)) as i32;
        let fg = d2 as u16;
        for _ in 0..=0x15 {
            line(ram, &mut a1, fg, 0);
        }
        let d0 = d2 >> 6;
        for _ in 0..=0x39 {
            line(ram, &mut a1, fg, d0 as u16);
        }
        let d3 = d0;
        if ram.u8(vars::VINT_RUNCOUNT_LOW) & 7 == 0 {
            ram.w16(
                vars::TEMP_ARRAY_LAYER_DEF,
                ram.u16(vars::TEMP_ARRAY_LAYER_DEF).wrapping_sub(1),
            );
        }
        let wave = (ram.u16(vars::TEMP_ARRAY_LAYER_DEF) & 0x1F) as usize;
        let wave_byte =
            |i: usize| -> i32 { wave_table.get(wave + i).copied().unwrap_or(0) as i8 as i32 };
        for i in 0..=0x14 {
            line(ram, &mut a1, fg, (wave_byte(i) + d3) as u16);
        }
        for _ in 0..=0x0A {
            line(ram, &mut a1, fg, 0);
        }
        let d0 = d2 >> 4;
        for _ in 0..=0x0F {
            line(ram, &mut a1, fg, d0 as u16);
        }
        let d0 = (d2 >> 4) + ((d2 >> 4) >> 1);
        for _ in 0..=0x0F {
            line(ram, &mut a1, fg, d0 as u16);
        }
        // The near hills: d3 is 16.16 fixed point kept swapped.
        let mut step = ((d2 >> 1) - (d2 >> 3)) << 8;
        step = ((step / 0x30) as i16 as i32) << 8;
        let mut acc = ((d2 >> 3) as u32 & 0xFFFF) << 16;
        for _ in 0..=0x0E {
            line(ram, &mut a1, fg, (acc >> 16) as u16);
            acc = acc.wrapping_add(step as u32);
        }
        for _ in 0..=0x08 {
            line(ram, &mut a1, fg, (acc >> 16) as u16);
            line(ram, &mut a1, fg, (acc >> 16) as u16);
            acc = acc.wrapping_add((step as u32).wrapping_mul(2));
        }
        for _ in 0..=0x0E {
            for _ in 0..5 {
                line(ram, &mut a1, fg, (acc >> 16) as u16);
            }
            acc = acc.wrapping_add((step as u32).wrapping_mul(3));
        }
    }

    /// The SMB3 Easter egg: SMB3's sky is the backdrop colour, so plane B stays still and empty.
    fn sw_scrl_easter_egg(&mut self, ram: &mut Ram) {
        ram.w16(vars::VSCROLL_FACTOR_BG, 0);
        let fg = (-(ram.u16(vars::CAMERA_X_POS) as i32)) & 0xFFFF;
        for line in 0..0x100u16 {
            ram.w16(
                vars::HORIZ_SCROLL_BUF.wrapping_add(line.wrapping_mul(4)),
                fg as u16,
            );
            ram.w16(
                vars::HORIZ_SCROLL_BUF
                    .wrapping_add(line.wrapping_mul(4))
                    .wrapping_add(2),
                0,
            );
        }
    }
}

fn line(ram: &mut Ram, a1: &mut u16, fg: u16, bg: u16) {
    ram.w16(*a1, fg);
    ram.w16(a1.wrapping_add(2), bg);
    *a1 = a1.wrapping_add(4);
}

fn next_event(ram: &mut Ram) {
    let r = ram.u8(vars::DYNAMIC_RESIZE_ROUTINE).wrapping_add(2);
    ram.w8(vars::DYNAMIC_RESIZE_ROUTINE, r);
}

fn set_left(ram: &mut Ram, x: u16) {
    ram.w16(vars::CAMERA_MIN_X_POS, x);
    ram.w16(vars::TAILS_MIN_X_POS, x);
}

fn set_right(ram: &mut Ram, x: u16) {
    ram.w16(vars::CAMERA_MAX_X_POS, x);
    ram.w16(vars::TAILS_MAX_X_POS, x);
}

fn set_top(ram: &mut Ram, y: u16) {
    ram.w16(vars::CAMERA_MIN_Y_POS, y);
    ram.w16(vars::TAILS_MIN_Y_POS, y);
}

fn set_bottom_target(ram: &mut Ram, y: u16) {
    ram.w16(vars::CAMERA_MAX_Y_POS_TARGET, y);
    ram.w16(vars::TAILS_MAX_Y_POS, y);
}

fn bg_scroll(ram: &mut Ram, pos: u16, toggle: u16, flags: u16, delta: i32, bit: u8) {
    let old = ram.u32(pos);
    let now = old.wrapping_add(delta as u32);
    ram.w32(pos, now);
    let d1 = ((now >> 16) as i32) & 0x10;
    if d1 ^ ram.u8(toggle) as i32 != 0 {
        return;
    }
    ram.w8(toggle, ram.u8(toggle) ^ 0x10);
    if (now.wrapping_sub(old) as i32) < 0 {
        ram.set_bit(flags, bit);
    } else {
        ram.set_bit(flags, bit + 1);
    }
}

fn layer_scroll(ram: &mut Ram, pos: u16, toggle: u16, flags: u16, delta: i32, bit: u8) {
    bg_scroll(ram, pos, toggle, flags, delta, bit);
}

fn set_bg_y(ram: &mut Ram, y: u16) {
    let old = ram.u16(vars::CAMERA_BG_Y_POS);
    ram.w16(vars::CAMERA_BG_Y_POS, y);
    let crossed = ((y & 0x10) as u8 ^ ram.u8(vars::VERTI_BLOCK_CROSSED_FLAG_BG)) != 0;
    if crossed {
        return;
    }
    ram.w8(
        vars::VERTI_BLOCK_CROSSED_FLAG_BG,
        ram.u8(vars::VERTI_BLOCK_CROSSED_FLAG_BG) ^ 0x10,
    );
    if (y.wrapping_sub(old) as i16) < 0 {
        ram.set_bit(vars::SCROLL_FLAGS_BG, 0);
    } else {
        ram.set_bit(vars::SCROLL_FLAGS_BG, 1);
    }
}

fn write_bg_line(ram: &mut Ram, line: &mut i32, fg: u16, bg: u16) {
    if *line >= 0x100 {
        return;
    }
    ram.w16(
        vars::HORIZ_SCROLL_BUF.wrapping_add((*line as u16).wrapping_mul(4)),
        fg,
    );
    ram.w16(
        vars::HORIZ_SCROLL_BUF
            .wrapping_add((*line as u16).wrapping_mul(4))
            .wrapping_add(2),
        bg,
    );
    *line += 1;
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn easter_egg_zones_are_the_four_smb3_zones() {
        for zone in 0x40..=0x43 {
            assert!(is_easter_egg(zone), "zone ${zone:02X}");
            assert!(is_zone_ported(zone), "zone ${zone:02X}");
        }
        assert!(!is_easter_egg(0x11));
        assert!(!is_easter_egg(0x3F));
    }

    #[test]
    fn horizontal_scroll_has_a_sixteen_pixel_dead_zone() {
        let mut ram = Ram::new();
        let mut camera = Camera;
        ram.w32(vars::CAMERA_X_POS, 0);
        ram.w16(vars::CAMERA_MIN_X_POS, 0);
        ram.w16(vars::CAMERA_MAX_X_POS, 0x1000);
        let sonic = Obj::new(vars::MAIN_CHARACTER);
        // 0x90 + 0x10 from the camera: the dead zone is not crossed yet.
        sonic.set_x(&mut ram, 0xA0);
        let old = camera.scroll_horiz(&mut ram, vars::MAIN_CHARACTER);
        assert_eq!(old, 0);
        assert_eq!(ram.u16(vars::CAMERA_X_POS), 0);
        // One pixel further: the camera moves by (target - camera - $90) - $10 = 1.
        sonic.set_x(&mut ram, 0xA1);
        camera.scroll_horiz(&mut ram, vars::MAIN_CHARACTER);
        assert_eq!(ram.u16(vars::CAMERA_X_POS), 1);
        assert_eq!(ram.u16(vars::CAMERA_X_POS_DIFF), 0x0100);
    }

    #[test]
    fn easter_egg_background_fills_every_scroll_line() {
        let mut ram = Ram::new();
        let mut camera = Camera;
        ram.w16(vars::CAMERA_X_POS, 0x20);
        camera.sw_scrl_easter_egg(&mut ram);
        assert_eq!(ram.u16(vars::HORIZ_SCROLL_BUF), 0xFFE0);
        assert_eq!(ram.u16(vars::HORIZ_SCROLL_BUF + 2), 0);
        assert_eq!(ram.u16(vars::HORIZ_SCROLL_BUF + 0xFF * 4), 0xFFE0);
        assert_eq!(ram.u16(vars::VSCROLL_FACTOR_BG), 0);
    }

    #[test]
    fn horizontal_scroll_flags_mark_the_direction_bit() {
        let mut ram = Ram::new();
        let mut camera = Camera;
        ram.w8(vars::HORIZ_BLOCK_CROSSED_FLAG, 0x10);
        ram.w16(vars::CAMERA_X_POS, 0x30); // (cam & $10) matches the toggle: a boundary was crossed
        camera.scroll_flags_h(&mut ram, 0x20);
        assert_eq!(ram.u8(vars::SCROLL_FLAGS), 0x08); // bit 3 = moved right
        assert_eq!(ram.u8(vars::HORIZ_BLOCK_CROSSED_FLAG), 0x00);

        ram.w16(vars::CAMERA_X_POS, 0x200);
        camera.scroll_flags_h(&mut ram, 0x210); // moved left
        assert_eq!(ram.u8(vars::SCROLL_FLAGS), 0x08 | 0x04); // bits 3 and 2
        assert_eq!(ram.u8(vars::HORIZ_BLOCK_CROSSED_FLAG), 0x10);
    }

    #[test]
    fn vertical_scroll_does_not_pass_camera_max_y() {
        let mut ram = Ram::new();
        let mut camera = Camera;
        let sonic = Obj::new(vars::MAIN_CHARACTER);
        ram.w16(vars::CAMERA_MIN_Y_POS, 0);
        ram.w16(vars::CAMERA_MAX_Y_POS, 0xD0);
        ram.w32(vars::CAMERA_Y_POS, 0x00D0_0000);
        ram.w16(vars::CAMERA_Y_POS_BIAS, 0x60);
        sonic.set_y(&mut ram, 0x18C);
        // d0 = $18C - $D0 - $60 = $5C > 6: move down, but the max clamp keeps the camera at $D0.
        let old = camera.scroll_verti(&mut ram, vars::MAIN_CHARACTER, 0x60, 0x1234);
        assert_eq!(old, 0xD0);
        assert_eq!(ram.u16(vars::CAMERA_Y_POS), 0xD0);
        assert_eq!(ram.u16(vars::CAMERA_Y_POS_DIFF), 0);
    }
}
