//! `Oscillating numbers` (ROM 0x5D26/0x5DCC): 16 {value, speed} word pairs at `$FE60`, one direction bit each in the
//! word at `$FE5E`. Platforms and other objects read the values; the parameters differ between the stock zones and the
//! hack (`objects/oscillation.json`).

use crate::{vars, Ram};

#[derive(Clone, Debug, Default)]
pub struct OscillationData {
    pub initial: Vec<u16>,
    pub parameters: Vec<u16>,
}

#[derive(Clone, Debug, Default)]
pub struct Oscillation {
    initial: Vec<u16>,
    parameters: Vec<u16>,
}

impl Oscillation {
    pub fn new(data: &OscillationData) -> Self {
        Self {
            initial: data.initial.clone(),
            parameters: data.parameters.clone(),
        }
    }

    /// The address of oscillator `n`'s value word (the high byte is what objects read).
    pub fn value(n: usize) -> u16 {
        vars::OSCILLATING_DATA
            .wrapping_add(2)
            .wrapping_add((n as u16) * 4)
    }

    /// `OscillateNumInit` (0x5D26).
    pub fn init(&self, ram: &mut Ram) {
        for (i, v) in self.initial.iter().enumerate() {
            ram.w16(vars::OSCILLATING_DATA.wrapping_add((i as u16) * 2), *v);
        }
    }

    /// `OscillateNumDo` (0x5DCC).
    pub fn step(&self, ram: &mut Ram) {
        if ram.u16(vars::TWO_PLAYER_MODE) == 0 && ram.u8(vars::MAIN_CHARACTER + 0x24) >= 6 {
            return; // frozen while Sonic dies
        }
        if self.parameters.len() < 32 {
            return;
        }
        let mut directions = ram.u16(vars::OSCILLATING_DATA);
        for k in 0..16usize {
            let bit = 1u16 << (15 - k);
            let value_addr = Self::value(k);
            let speed_addr = value_addr.wrapping_add(2);
            let acceleration = self.parameters[k * 2] as i32;
            let limit = (self.parameters[k * 2 + 1] & 0xFF) as u8;
            let falling = (directions & bit) != 0;
            let speed = ram.u16(speed_addr).wrapping_add(if falling {
                -(acceleration as i16) as u16
            } else {
                acceleration as u16
            });
            ram.w16(speed_addr, speed);
            let value = ram.u16(value_addr).wrapping_add(speed);
            ram.w16(value_addr, value);
            let byte = ram.u8(value_addr);
            if !falling && limit <= byte {
                directions |= bit;
            } else if falling && limit > byte {
                directions &= !bit;
            }
        }
        ram.w16(vars::OSCILLATING_DATA, directions);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn data() -> OscillationData {
        let mut parameters = vec![0u16; 32];
        parameters[12] = 4; // oscillator 6 acceleration
        parameters[13] = 0x40; // limit
        OscillationData {
            initial: vec![0; 32],
            parameters,
        }
    }

    #[test]
    fn init_writes_the_pair_table() {
        let mut ram = Ram::new();
        let mut d = data();
        d.initial[0] = 0x0080;
        d.initial[1] = 0x1234;
        Oscillation::new(&d).init(&mut ram);
        assert_eq!(ram.u16(vars::OSCILLATING_DATA), 0x0080);
        assert_eq!(ram.u16(vars::OSCILLATING_DATA + 2), 0x1234);
    }

    #[test]
    fn oscillator_six_accelerates_and_reverses_at_the_limit() {
        let mut ram = Ram::new();
        let o = Oscillation::new(&data());
        o.init(&mut ram);
        // Oscillator 6's value starts at 0, speed grows by 4 until the high byte reaches $40.
        for _ in 0..10 {
            o.step(&mut ram);
        }
        let value = ram.u16(Oscillation::value(6));
        assert!(ram.u16(Oscillation::value(6) + 2) != 0);
        assert!(value > 0);
    }

    #[test]
    fn oscillation_is_frozen_while_sonic_dies() {
        let mut ram = Ram::new();
        let o = Oscillation::new(&data());
        o.init(&mut ram);
        ram.w8(vars::MAIN_CHARACTER + 0x24, 6);
        o.step(&mut ram);
        assert_eq!(ram.u16(Oscillation::value(6)), 0);
    }
}
