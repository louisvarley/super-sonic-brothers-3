//! The 64 KiB big-endian model of 68k work RAM (`$FF0000-$FFFFFF`, addressed by the low 16 bits).
//!
//! Game logic keeps all state here, exactly as the legacy engine does: byte/word/long aliasing is load-bearing
//! (`move.w #$900,anim` sets two fields), and an emulator RAM dump can be loaded directly for validation.

pub const RAM_SIZE: usize = 0x10000;

#[derive(Clone)]
pub struct Ram {
    bytes: Box<[u8; RAM_SIZE]>,
}

impl Default for Ram {
    fn default() -> Self {
        Self::new()
    }
}

impl Ram {
    pub fn new() -> Self {
        Self {
            bytes: Box::new([0; RAM_SIZE]),
        }
    }

    pub fn bytes(&self) -> &[u8; RAM_SIZE] {
        &self.bytes
    }

    pub fn bytes_mut(&mut self) -> &mut [u8; RAM_SIZE] {
        &mut self.bytes
    }

    /// A slice of `len` bytes starting at `start` (wrapping like the 16-bit address bus).
    pub fn slice(&self, start: u16, len: usize) -> &[u8] {
        let start = start as usize;
        if start + len <= RAM_SIZE {
            &self.bytes[start..start + len]
        } else {
            // The engine never asks for a wrapped slice; keep the call total.
            &self.bytes[start..]
        }
    }

    pub fn u8(&self, a: u16) -> u8 {
        self.bytes[a as usize]
    }

    pub fn s8(&self, a: u16) -> i8 {
        self.bytes[a as usize] as i8
    }

    pub fn u16(&self, a: u16) -> u16 {
        u16::from_be_bytes([
            self.bytes[a as usize],
            self.bytes[a.wrapping_add(1) as usize],
        ])
    }

    pub fn s16(&self, a: u16) -> i16 {
        self.u16(a) as i16
    }

    pub fn u32(&self, a: u16) -> u32 {
        u32::from_be_bytes([
            self.bytes[a as usize],
            self.bytes[a.wrapping_add(1) as usize],
            self.bytes[a.wrapping_add(2) as usize],
            self.bytes[a.wrapping_add(3) as usize],
        ])
    }

    pub fn s32(&self, a: u16) -> i32 {
        self.u32(a) as i32
    }

    pub fn w8(&mut self, a: u16, v: u8) {
        self.bytes[a as usize] = v;
    }

    pub fn w16(&mut self, a: u16, v: u16) {
        let [hi, lo] = v.to_be_bytes();
        self.bytes[a as usize] = hi;
        self.bytes[a.wrapping_add(1) as usize] = lo;
    }

    pub fn w32(&mut self, a: u16, v: u32) {
        let [b0, b1, b2, b3] = v.to_be_bytes();
        self.bytes[a as usize] = b0;
        self.bytes[a.wrapping_add(1) as usize] = b1;
        self.bytes[a.wrapping_add(2) as usize] = b2;
        self.bytes[a.wrapping_add(3) as usize] = b3;
    }

    pub fn bit(&self, a: u16, bit: u8) -> bool {
        self.bytes[a as usize] & (1 << bit) != 0
    }

    pub fn set_bit(&mut self, a: u16, bit: u8) {
        self.bytes[a as usize] |= 1 << bit;
    }

    pub fn clear_bit(&mut self, a: u16, bit: u8) {
        self.bytes[a as usize] &= !(1 << bit);
    }

    /// `bset`: sets the bit and returns its previous state.
    pub fn test_set(&mut self, a: u16, bit: u8) -> bool {
        let was = self.bit(a, bit);
        self.set_bit(a, bit);
        was
    }

    /// `bclr`: clears the bit and returns its previous state.
    pub fn test_clear(&mut self, a: u16, bit: u8) -> bool {
        let was = self.bit(a, bit);
        self.clear_bit(a, bit);
        was
    }

    pub fn load(&mut self, dump: &[u8]) -> Result<(), String> {
        if dump.len() != RAM_SIZE {
            return Err(format!(
                "RAM dump must be {RAM_SIZE} bytes, got {}",
                dump.len()
            ));
        }
        self.bytes.copy_from_slice(dump);
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn big_endian_and_aliasing() {
        let mut ram = Ram::new();
        ram.w32(0x0100, 0x1234_5678);
        assert_eq!(ram.u16(0x0100), 0x1234);
        assert_eq!(ram.u16(0x0102), 0x5678);
        assert_eq!(ram.u8(0x0101), 0x34);
        assert_eq!(ram.u32(0x0100), 0x1234_5678);
        ram.w16(0x0102, 0xABCD);
        assert_eq!(ram.u32(0x0100), 0x1234_ABCD);
    }

    #[test]
    fn signed_reads() {
        let mut ram = Ram::new();
        ram.w16(0x10, 0xFFFF);
        assert_eq!(ram.s16(0x10), -1);
        assert_eq!(ram.s8(0x10), -1);
        assert_eq!(ram.s8(0x11), 0xFFu8 as i8);
        ram.w8(0x12, 0x80);
        assert_eq!(ram.s8(0x12), -128);
    }

    #[test]
    fn addresses_wrap_on_the_16_bit_bus() {
        let mut ram = Ram::new();
        ram.w16(0xFFFF, 0x1234);
        assert_eq!(ram.u8(0xFFFF), 0x12);
        assert_eq!(ram.u8(0x0000), 0x34);
        assert_eq!(ram.u16(0xFFFF), 0x1234);
    }

    #[test]
    fn bit_operations_return_previous_state() {
        let mut ram = Ram::new();
        assert!(!ram.test_set(0x20, 3));
        assert!(ram.test_set(0x20, 3));
        assert!(ram.bit(0x20, 3));
        assert!(ram.test_clear(0x20, 3));
        assert!(!ram.test_clear(0x20, 3));
        assert!(!ram.bit(0x20, 3));
    }

    #[test]
    fn load_requires_exactly_64_kib() {
        let mut ram = Ram::new();
        assert!(ram.load(&[0; RAM_SIZE]).is_ok());
        assert!(ram.load(&[0; 10]).is_err());
    }
}
