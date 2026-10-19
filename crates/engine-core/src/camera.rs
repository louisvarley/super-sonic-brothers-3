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
    /// `DeformBgLayer` (0xDCD6) for one frame.
    pub fn deform_bg_layer(&mut self, ram: &mut Ram) -> Result<(), Unported> {
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

    /// `DynamicLevelEvents` (0x10804): per-zone events, then ease `Camera_Max_Y` toward its target.
    fn dynamic_level_events(&mut self, ram: &mut Ram) -> Result<(), Unported> {
        let zone = ram.u8(vars::CURRENT_ZONE_AND_ACT);
        if !is_zone_ported(zone) {
            return Err(Unported::new(
                0x10804,
                format!("level events for zone {zone:02X}"),
            ));
        }
        // The SMB3 zones have no event script; the Sonic zones' scripts are a later milestone.
        if !is_easter_egg(zone) {
            return Err(Unported::new(
                0x10804,
                format!("level events for zone {zone:02X}"),
            ));
        }

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
