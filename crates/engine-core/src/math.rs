//! `CalcSine` (ROM 0x40B6) and `CalcAngle` (0x434E) with the oracle's exact tables.
//!
//! The tables are embedded (`math_tables.rs`) rather than recomputed so a different libm cannot change physics.
//! The legacy code regenerates them with `Math.Sin`/`Math.Atan`; `--dump-math` exported the result.

use crate::math_tables::{ARCTAN, SINE};

/// Returns `(sin, cos)` for an angle byte, as the ROM returns them in d0/d1.
pub fn calc_sine(angle: u8) -> (i16, i16) {
    let i = angle as usize;
    (SINE[i], SINE[i + 0x40])
}

/// Angle byte of the vector `(x = d1, y = d2)`; `0x40` for `(0, 0)`.
pub fn calc_angle(x: i16, y: i16) -> u8 {
    if x == 0 && y == 0 {
        return 0x40;
    }
    // C#: Math.Abs((int)x) & 0xFFFF; i16::MIN stays 32768.
    let ax = (x as i32).unsigned_abs() & 0xFFFF;
    let ay = (y as i32).unsigned_abs() & 0xFFFF;
    let mut d0: u32 = if ay < ax {
        ARCTAN[((ay << 8) / ax) as usize] as u32 // divu: the quotient fits (ay < ax)
    } else {
        0x40u32.wrapping_sub(ARCTAN[((ax << 8) / ay) as usize] as u32) & 0xFF
    };
    if x < 0 {
        d0 = 0u32.wrapping_sub(d0).wrapping_add(0x80) & 0xFFFF;
    }
    if y < 0 {
        d0 = 0u32.wrapping_sub(d0).wrapping_add(0x100) & 0xFFFF;
    }
    (d0 & 0xFF) as u8
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn sine_cardinals_and_quarter() {
        assert_eq!(calc_sine(0), (0, 256));
        assert_eq!(calc_sine(0x40), (256, 0));
        assert_eq!(calc_sine(0x80), (0, -256));
        assert_eq!(calc_sine(0xC0), (-256, 0));
        // The ROM's two hand-corrected entries.
        assert_eq!(SINE[147], -117);
        assert_eq!(SINE[237], -117);
    }

    #[test]
    fn angle_cardinals() {
        assert_eq!(calc_angle(0, 0), 0x40);
        assert_eq!(calc_angle(1, 0), 0x00);
        assert_eq!(calc_angle(0, 1), 0x40);
        assert_eq!(calc_angle(-1, 0), 0x80);
        assert_eq!(calc_angle(0, -1), 0xC0);
        assert_eq!(calc_angle(1, 1), 0x20);
        // The ROM's two negations are applied in sequence: (-1,-1) is $A0, not $E0.
        assert_eq!(calc_angle(-1, -1), 0xA0);
    }

    #[test]
    fn angle_matches_embedded_table() {
        // For every ratio the table is indexed directly; check the 45-degree boundary too.
        assert_eq!(calc_angle(256, 256), 0x20);
        assert_eq!(calc_angle(i16::MIN, 0), 0x80);
        assert_eq!(calc_angle(0, i16::MIN), 0xC0);
    }
}
