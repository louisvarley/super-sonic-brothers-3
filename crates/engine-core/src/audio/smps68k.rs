//! The hack's Sonic 1-style 68k SMPS driver, a routine-for-routine port of
//! `legacy/src/SonicBros3/Audio/Smps68kDriver.cs` (update routine 0x1301DC, run every VBlank when `$FFE5` is set).
//!
//! Its state lives in RAM at `$F000` exactly as in the original (validated against MAME), and its YM2612 / PSG / DAC
//! writes go to a [`SoundChips`]. The driver itself only owns the parsing cursors and its data; the caller passes the
//! one game RAM.
//!
//! RAM: `$F000` globals, tracks of `$30` bytes: music DAC `$40`, FM1-6 `$70-$160`, PSG1-3 `$190-$1F0`; SFX FM
//! `$220-$280`, PSG `$2B0-$310`; special SFX FM `$340`, PSG `$370`; the music saved for the 1-up jingle at
//! `$3A0-$5BF`. Several routines of the original return through the caller's caller (addq.w #4/#8,sp); here they
//! return a flag.

use super::{AudioAssets, SoundChips};
use crate::Ram;

pub struct Smps68kDriver {
    data: AudioAssets,
    a5: u16,
    a4: usize,
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum FlagResult {
    Continue,
    ExitTwo,
}

impl Smps68kDriver {
    pub const BASE: u16 = 0xF000;
    const A6: u16 = Self::BASE;
    const TRACK_SIZE: u16 = 0x30;
    const MUSIC_DAC: u16 = Self::A6 + 0x40;
    const MUSIC_FM1: u16 = Self::A6 + 0x70;
    const MUSIC_PSG1: u16 = Self::A6 + 0x190;
    const SFX_FM1: u16 = Self::A6 + 0x220;
    const SPECIAL_FM: u16 = Self::A6 + 0x340;
    const SPECIAL_PSG: u16 = Self::A6 + 0x370;
    const SAVED: u16 = Self::A6 + 0x3A0;

    // Pointers in the driver's data block (0x12FFEC-0x130007).
    const PRIORITY_TABLE_PTR: usize = 0x12FFEC;
    pub const MUSIC_INDEX_POINTER: usize = 0x12FFF4;
    const SFX_INDEX_PTR: usize = 0x12FFF8;
    const SFX_EXTRA_INDEX_PTR: usize = 0x12FFFC;
    const SPEED_UP_TABLE_PTR: usize = 0x130000;
    const PSG_ENVELOPES_PTR: usize = 0x130004;

    /// Channel bytes of the music tracks (0x1308BE: DAC, FM1-6; 0x1308C6: PSG1-3).
    const FM_CHANNELS: [u8; 7] = [0x06, 0x00, 0x01, 0x02, 0x04, 0x05, 0x06];
    const PSG_CHANNELS: [u8; 3] = [0x80, 0xA0, 0xC0];
    /// 0x1309CE: the music track an SFX channel overrides; 0x1309EE: the SFX track for it.
    const OVERRIDDEN_TRACK: [u16; 8] = [0xF0D0, 0, 0xF100, 0xF130, 0xF190, 0xF1C0, 0xF1F0, 0xF1F0];
    const SFX_TRACK: [u16; 8] = [0xF220, 0, 0xF250, 0xF280, 0xF2B0, 0xF2E0, 0xF310, 0xF310];

    pub fn new(data: AudioAssets) -> Self {
        Self { data, a5: 0, a4: 0 }
    }

    fn b(&self, ram: &Ram, offset: u16) -> u8 {
        ram.u8(self.a5 + offset)
    }
    fn set_b(&self, ram: &mut Ram, offset: u16, v: u8) {
        ram.w8(self.a5 + offset, v);
    }
    fn bit(&self, ram: &Ram, bit: u32) -> bool {
        (ram.u8(self.a5) & (1 << bit)) != 0
    }
    fn set(&self, ram: &mut Ram, bit: u32) {
        ram.w8(self.a5, ram.u8(self.a5) | (1 << bit));
    }
    fn clr(&self, ram: &mut Ram, bit: u32) {
        ram.w8(self.a5, ram.u8(self.a5) & !(1 << bit));
    }
    fn g(&self, ram: &Ram, offset: u16) -> u8 {
        ram.u8(Self::A6 + offset)
    }
    fn set_g(&self, ram: &mut Ram, offset: u16, v: u8) {
        ram.w8(Self::A6 + offset, v);
    }
    fn next(&mut self) -> u8 {
        let v = self.data.u8(self.a4);
        self.a4 = self.a4.wrapping_add(1);
        v
    }
    fn table(table: &[u16; 8], byte_index: i32) -> u16 {
        if byte_index >= 0 && (byte_index / 4) < table.len() as i32 && byte_index % 4 == 0 {
            table[(byte_index / 4) as usize]
        } else {
            0
        }
    }

    /// UpdateMusic (0x1301DC), once per VBlank.
    pub fn update(&mut self, ram: &mut Ram, chips: &mut dyn SoundChips) {
        self.set_g(ram, 0x0E, 0);
        if self.g(ram, 0x03) != 0 {
            self.pause_music(ram, chips);
            return;
        }
        let d = self.g(ram, 0x01).wrapping_sub(1);
        self.set_g(ram, 0x01, d);
        if d == 0 {
            self.tempo_wait(ram);
        }
        if self.g(ram, 0x04) != 0 {
            self.do_fade_out(ram, chips);
        }
        if self.g(ram, 0x24) != 0 {
            self.do_fade_in(ram, chips);
        }
        if ram.u16(Self::A6 + 0x0A) != 0 {
            self.cycle_sound_queue(ram);
        }
        if self.g(ram, 0x09) != 0x80 && self.play_sound_id(ram, chips) {
            return;
        }

        self.a5 = Self::MUSIC_DAC;
        if (self.b(ram, 0) as i8) < 0 && self.dac_update_track(ram, chips) {
            return;
        }
        self.set_g(ram, 0x08, 0);
        for _ in 0..6 {
            self.a5 += Self::TRACK_SIZE;
            if (self.b(ram, 0) as i8) < 0 {
                self.fm_update_track(ram, chips);
            }
        }
        for _ in 0..3 {
            self.a5 += Self::TRACK_SIZE;
            if (self.b(ram, 0) as i8) < 0 {
                self.psg_update_track(ram, chips);
            }
        }
        self.set_g(ram, 0x0E, 0x80);
        for _ in 0..3 {
            self.a5 += Self::TRACK_SIZE;
            if (self.b(ram, 0) as i8) < 0 {
                self.fm_update_track(ram, chips);
            }
        }
        for _ in 0..3 {
            self.a5 += Self::TRACK_SIZE;
            if (self.b(ram, 0) as i8) < 0 {
                self.psg_update_track(ram, chips);
            }
        }
        self.set_g(ram, 0x0E, 0x40);
        self.a5 += Self::TRACK_SIZE;
        if (self.b(ram, 0) as i8) < 0 {
            self.fm_update_track(ram, chips);
        }
        self.a5 += Self::TRACK_SIZE;
        if (self.b(ram, 0) as i8) < 0 {
            self.psg_update_track(ram, chips);
        }
    }

    /// 0x1302E8. Returns true when a `$F2` on this track ended the whole update.
    fn dac_update_track(&mut self, ram: &mut Ram, chips: &mut dyn SoundChips) -> bool {
        self.set_b(ram, 0x0E, self.b(ram, 0x0E).wrapping_sub(1));
        if self.b(ram, 0x0E) != 0 {
            return false;
        }
        self.set_g(ram, 0x08, 0x80);
        self.a4 = ram.u32(self.a5 + 4) as usize;
        let mut d5;
        loop {
            d5 = self.next();
            if d5 < 0xE0 {
                break;
            }
            if self.coord_flag(ram, chips, d5) == FlagResult::ExitTwo {
                return true;
            }
        }
        if (d5 as i8) < 0 {
            self.set_b(ram, 0x10, d5);
            d5 = self.next();
            if (d5 as i8) < 0 {
                self.a4 = self.a4.wrapping_sub(1);
                let e = self.b(ram, 0x0F);
                self.set_b(ram, 0x0E, e);
            } else {
                self.set_duration(ram, d5);
            }
        } else {
            self.set_duration(ram, d5);
        }
        ram.w32(self.a5 + 4, self.a4 as u32);
        if self.bit(ram, 2) {
            return false;
        }
        let d0 = self.b(ram, 0x10);
        if d0 == 0x80 {
            return false;
        }
        if (d0 & 8) != 0 {
            let index = (d0.wrapping_sub(0x88) & 7) as usize;
            chips.set_dac_pitch(self.data.dac_pitches.get(index).copied().unwrap_or(0) as i32);
            chips.play_dac(0x83);
        } else {
            chips.play_dac(d0 as i32);
        }
        false
    }

    /// 0x130364.
    fn fm_update_track(&mut self, ram: &mut Ram, chips: &mut dyn SoundChips) {
        self.set_b(ram, 0x0E, self.b(ram, 0x0E).wrapping_sub(1));
        if self.b(ram, 0x0E) == 0 {
            self.clr(ram, 4);
            if !self.fm_do_next(ram, chips) {
                return;
            }
            self.fm_update_freq(ram, chips);
            self.fm_note_on(ram, chips);
            return;
        }
        if self.note_fill_update(ram, chips) {
            return;
        }
        if let Some(d6) = self.do_modulation(ram) {
            self.fm_update_freq_with(ram, chips, d6);
        }
    }

    /// 0x130386: parse up to the next note. False when a flag stopped the track.
    fn fm_do_next(&mut self, ram: &mut Ram, chips: &mut dyn SoundChips) -> bool {
        self.a4 = ram.u32(self.a5 + 4) as usize;
        self.clr(ram, 1);
        let mut d5;
        loop {
            d5 = self.next();
            if d5 < 0xE0 {
                break;
            }
            if self.coord_flag(ram, chips, d5) != FlagResult::Continue {
                return false;
            }
        }
        self.fm_note_off(ram, chips);
        if (d5 as i8) >= 0 {
            self.set_duration(ram, d5);
            self.finish_track_update(ram);
            return true;
        }
        self.fm_set_freq(ram, chips, d5);
        d5 = self.next();
        if (d5 as i8) >= 0 {
            self.set_duration(ram, d5);
        } else {
            self.a4 = self.a4.wrapping_sub(1);
        }
        self.finish_track_update(ram);
        true
    }

    /// 0x1303BC.
    fn fm_set_freq(&mut self, ram: &mut Ram, chips: &mut dyn SoundChips, d5: u8) {
        let mut d5 = d5.wrapping_sub(0x80);
        if d5 == 0 {
            self.set(ram, 1);
            ram.w16(self.a5 + 0x10, 0);
            self.finish_track_update(ram);
            return;
        }
        d5 = (d5.wrapping_add(self.b(ram, 0x08)) & 0x7F).wrapping_mul(2);
        let freq = self
            .data
            .fm_frequencies
            .get((d5 / 2) as usize)
            .copied()
            .unwrap_or(0);
        ram.w16(self.a5 + 0x10, freq);
        let _ = chips;
    }

    /// 0x1303DA: duration = d5 * the track's tempo divider (byte arithmetic).
    fn set_duration(&mut self, ram: &mut Ram, d5: u8) {
        let mut d0 = d5;
        let mut d1 = self.b(ram, 0x02);
        loop {
            d1 = d1.wrapping_sub(1);
            if d1 == 0 {
                break;
            }
            d0 = d0.wrapping_add(d5);
        }
        self.set_b(ram, 0x0F, d0);
        self.set_b(ram, 0x0E, d0);
    }

    /// 0x1303FA.
    fn finish_track_update(&mut self, ram: &mut Ram) {
        ram.w32(self.a5 + 4, self.a4 as u32);
        let f = self.b(ram, 0x0F);
        self.set_b(ram, 0x0E, f);
        if self.bit(ram, 4) {
            return;
        }
        let d = self.b(ram, 0x13);
        self.set_b(ram, 0x12, d);
        self.set_b(ram, 0x0C, 0);
        if !self.bit(ram, 3) {
            return;
        }
        let a0 = ram.u32(self.a5 + 0x14) as usize;
        let b0 = self.data.u8(a0);
        let b1 = self.data.u8(a0 + 1);
        let b2 = self.data.u8(a0 + 2);
        let b3 = self.data.u8(a0 + 3) >> 1;
        self.set_b(ram, 0x18, b0);
        self.set_b(ram, 0x19, b1);
        self.set_b(ram, 0x1A, b2);
        self.set_b(ram, 0x1B, b3);
        ram.w16(self.a5 + 0x1C, 0);
    }

    /// 0x130438. True when the note fill ran out.
    fn note_fill_update(&mut self, ram: &mut Ram, chips: &mut dyn SoundChips) -> bool {
        if self.b(ram, 0x12) == 0 {
            return false;
        }
        self.set_b(ram, 0x12, self.b(ram, 0x12).wrapping_sub(1));
        if self.b(ram, 0x12) != 0 {
            return false;
        }
        self.set(ram, 1);
        if (self.b(ram, 0x01) as i8) < 0 {
            self.psg_note_off(ram, chips);
        } else {
            self.fm_note_off(ram, chips);
        }
        true
    }

    /// 0x130460: the modulated frequency when it changes this frame.
    fn do_modulation(&mut self, ram: &mut Ram) -> Option<i32> {
        if !self.bit(ram, 3) {
            return None;
        }
        if self.b(ram, 0x18) != 0 {
            self.set_b(ram, 0x18, self.b(ram, 0x18).wrapping_sub(1));
            return None;
        }
        self.set_b(ram, 0x19, self.b(ram, 0x19).wrapping_sub(1));
        if self.b(ram, 0x19) != 0 {
            return None;
        }
        let a0 = ram.u32(self.a5 + 0x14) as usize;
        let b1 = self.data.u8(a0 + 1);
        self.set_b(ram, 0x19, b1);
        if self.b(ram, 0x1B) == 0 {
            let b3 = self.data.u8(a0 + 3);
            self.set_b(ram, 0x1B, b3);
            self.set_b(ram, 0x1A, self.b(ram, 0x1A).wrapping_neg());
            return None;
        }
        self.set_b(ram, 0x1B, self.b(ram, 0x1B).wrapping_sub(1));
        let d6 = (self.b(ram, 0x1A) as i8 as i32 + ram.u16(self.a5 + 0x1C) as i32) & 0xFFFF;
        ram.w16(self.a5 + 0x1C, d6 as u16);
        Some((d6 + ram.u16(self.a5 + 0x10) as i32) & 0xFFFF)
    }

    /// 0x1304B2.
    fn fm_update_freq(&mut self, ram: &mut Ram, chips: &mut dyn SoundChips) {
        if self.bit(ram, 1) {
            return;
        }
        let d6 = ram.u16(self.a5 + 0x10) as i32;
        if d6 == 0 {
            self.set(ram, 1);
            return;
        }
        self.fm_update_freq_with(ram, chips, d6);
    }

    /// 0x1304BE.
    fn fm_update_freq_with(&mut self, ram: &mut Ram, chips: &mut dyn SoundChips, d6: i32) {
        let d6 = (d6 + (self.b(ram, 0x1E) as i8 as i32)) & 0xFFFF;
        if self.bit(ram, 2) {
            return;
        }
        self.write_fm_iorii(ram, chips, 0xA4, d6 >> 8);
        self.write_fm_iorii(ram, chips, 0xA0, d6 & 0xFF);
    }

    /// 0x130F4C.
    fn psg_update_track(&mut self, ram: &mut Ram, chips: &mut dyn SoundChips) {
        self.set_b(ram, 0x0E, self.b(ram, 0x0E).wrapping_sub(1));
        if self.b(ram, 0x0E) == 0 {
            self.clr(ram, 4);
            if !self.psg_do_next(ram, chips) {
                return;
            }
            self.psg_do_note_on(ram, chips);
            self.psg_update_volume(ram, chips);
            return;
        }
        if self.note_fill_update(ram, chips) {
            return;
        }
        if self.b(ram, 0x0B) != 0 {
            self.psg_update_volume(ram, chips);
        }
        if let Some(d6) = self.do_modulation(ram) {
            self.psg_update_freq_with(ram, chips, d6);
        }
    }

    /// 0x130F74.
    fn psg_do_next(&mut self, ram: &mut Ram, chips: &mut dyn SoundChips) -> bool {
        self.clr(ram, 1);
        self.a4 = ram.u32(self.a5 + 4) as usize;
        let mut d5;
        loop {
            d5 = self.next();
            if d5 < 0xE0 {
                break;
            }
            if self.coord_flag(ram, chips, d5) != FlagResult::Continue {
                return false;
            }
        }
        if (d5 as i8) >= 0 {
            self.set_duration(ram, d5);
            self.finish_track_update(ram);
            return true;
        }
        self.psg_set_freq(ram, chips, d5);
        d5 = self.next();
        if (d5 as i8) >= 0 {
            self.set_duration(ram, d5);
        } else {
            self.a4 = self.a4.wrapping_sub(1);
        }
        self.finish_track_update(ram);
        true
    }

    /// 0x130FA8.
    fn psg_set_freq(&mut self, ram: &mut Ram, chips: &mut dyn SoundChips, d5: u8) {
        let d5 = d5.wrapping_sub(0x81);
        if (d5 as i8) < 0 {
            self.set(ram, 1);
            ram.w16(self.a5 + 0x10, 0xFFFF);
            self.finish_track_update(ram);
            self.psg_note_off(ram, chips);
            return;
        }
        let d5 = (d5.wrapping_add(self.b(ram, 0x08)) & 0x7F).wrapping_mul(2);
        let freq = self
            .data
            .psg_frequencies
            .get((d5 / 2) as usize)
            .copied()
            .unwrap_or(0);
        ram.w16(self.a5 + 0x10, freq);
        self.finish_track_update(ram);
    }

    /// 0x130FD8.
    fn psg_do_note_on(&mut self, ram: &mut Ram, chips: &mut dyn SoundChips) {
        let d6 = ram.u16(self.a5 + 0x10) as i32;
        if (d6 as u16 as i16) < 0 {
            self.set(ram, 1);
            return;
        }
        self.psg_update_freq_with(ram, chips, d6);
    }

    /// 0x130FDE.
    fn psg_update_freq_with(&mut self, ram: &mut Ram, chips: &mut dyn SoundChips, d6: i32) {
        let d6 = (d6 + (self.b(ram, 0x1E) as i8 as i32)) & 0xFFFF;
        if self.bit(ram, 2) || self.bit(ram, 1) {
            return;
        }
        let mut d0 = self.b(ram, 0x01);
        if d0 == 0xE0 {
            d0 = 0xC0;
        }
        d0 |= (d6 & 0x0F) as u8;
        chips.write_psg(d0 as i32);
        chips.write_psg((d6 >> 4) & 0x3F);
    }

    /// 0x13102A: volume plus the PSG envelope.
    fn psg_update_volume(&mut self, ram: &mut Ram, chips: &mut dyn SoundChips) {
        let mut d6 = self.b(ram, 0x09) as i32;
        let voice = self.b(ram, 0x0B) as usize;
        if voice != 0 {
            let table = self.data.u32(Self::PSG_ENVELOPES_PTR) as usize;
            let env = self.data.u32(table + (voice - 1) * 4) as usize;
            let d0 = self.data.u8(env + self.b(ram, 0x0C) as usize);
            self.set_b(ram, 0x0C, self.b(ram, 0x0C).wrapping_add(1));
            if d0 == 0x80 {
                self.set_b(ram, 0x0C, self.b(ram, 0x0C).wrapping_sub(1));
                return;
            }
            d6 = (d6 + d0 as i32) & 0xFF;
            if d6 >= 0x10 {
                d6 = 0x0F;
            }
        }
        self.psg_set_volume(ram, chips, d6);
    }

    /// 0x131066.
    fn psg_set_volume(&mut self, ram: &mut Ram, chips: &mut dyn SoundChips, d6: i32) {
        if self.bit(ram, 1) || self.bit(ram, 2) {
            return;
        }
        if self.bit(ram, 4) && !(self.b(ram, 0x13) == 0 || self.b(ram, 0x12) != 0) {
            return;
        }
        chips.write_psg(((d6 | self.b(ram, 0x01) as i32) + 0x10) & 0xFF);
    }

    /// 0x13109C.
    fn psg_note_off(&mut self, ram: &mut Ram, chips: &mut dyn SoundChips) {
        if self.bit(ram, 2) {
            return;
        }
        chips.write_psg((self.b(ram, 0x01) | 0x1F) as i32);
    }

    // ------------------------------------------------------------------ FM writes

    /// 0x130DDE.
    fn fm_note_on(&mut self, ram: &mut Ram, chips: &mut dyn SoundChips) {
        if self.bit(ram, 1) || self.bit(ram, 2) {
            return;
        }
        chips.write_ym(0, 0x28, (self.b(ram, 0x01) | 0xF0) as i32);
    }

    /// 0x130DFA.
    fn fm_note_off(&mut self, ram: &mut Ram, chips: &mut dyn SoundChips) {
        if self.bit(ram, 4) || self.bit(ram, 2) {
            return;
        }
        chips.write_ym(0, 0x28, self.b(ram, 0x01) as i32);
    }

    /// 0x130E12.
    fn write_fm_iorii_main(
        &mut self,
        ram: &mut Ram,
        chips: &mut dyn SoundChips,
        reg: i32,
        value: i32,
    ) {
        if self.bit(ram, 2) {
            return;
        }
        self.write_fm_iorii(ram, chips, reg, value);
    }

    /// 0x130E1E.
    fn write_fm_iorii(&mut self, ram: &mut Ram, chips: &mut dyn SoundChips, reg: i32, value: i32) {
        let ch = self.b(ram, 0x01) as i32;
        if (ch & 4) != 0 {
            chips.write_ym(1, (reg + (ch & !4)) & 0xFF, value & 0xFF);
        } else {
            chips.write_ym(0, (reg + ch) & 0xFF, value & 0xFF);
        }
    }

    /// 0x13134A: load FM voice `index` from the voice table at `table`.
    fn set_voice(&mut self, ram: &mut Ram, chips: &mut dyn SoundChips, table: usize, index: u8) {
        let mut a1 = table + 25 * (index as usize);
        let d1 = self.data.u8(a1);
        a1 += 1;
        self.set_b(ram, 0x1F, d1);
        let algorithm = d1;
        self.write_fm_iorii(ram, chips, 0xB0, algorithm as i32);
        for i in 0..20 {
            let reg = self.data.voice_registers[i] as i32;
            let v = self.data.u8(a1);
            a1 += 1;
            self.write_fm_iorii(ram, chips, reg, v as i32);
        }
        let mut carriers = self
            .data
            .carrier_masks
            .get((algorithm & 7) as usize)
            .copied()
            .unwrap_or(0);
        let volume = self.b(ram, 0x09) as i32;
        for i in 0..4 {
            let reg = self.data.voice_registers[20 + i] as i32;
            let mut v = self.data.u8(a1 + i) as i32;
            if (carriers & 1) != 0 {
                v = (v + volume) & 0xFF;
            }
            carriers >>= 1;
            self.write_fm_iorii(ram, chips, reg, v);
        }
        let pan = self.b(ram, 0x0A);
        self.write_fm_iorii(ram, chips, 0xB4, pan as i32);
    }

    /// 0x1313B0.
    fn set_total_levels(&mut self, ram: &mut Ram, chips: &mut dyn SoundChips) {
        if self.bit(ram, 2) {
            return;
        }
        let index = self.b(ram, 0x0B) as usize;
        let ptr = if self.g(ram, 0x0E) != 0 { 0x20 } else { 0x18 };
        let a1 = ram.u32(Self::A6 + ptr) as usize + 25 * index + 0x15;
        let mut carriers = self
            .data
            .carrier_masks
            .get((self.b(ram, 0x1F) & 7) as usize)
            .copied()
            .unwrap_or(0);
        let volume = self.b(ram, 0x09) as i32;
        if (volume as i8) < 0 {
            return;
        }
        for i in 0..4 {
            let reg = self.data.voice_registers[20 + i] as i32;
            let mut v = self.data.u8(a1 + i) as i32;
            let carrier = (carriers & 1) != 0;
            carriers >>= 1;
            if !carrier {
                continue;
            }
            v += volume;
            if v > 0xFF {
                continue;
            }
            self.write_fm_iorii(ram, chips, reg, v);
        }
    }

    // ------------------------------------------------------------------ coordination flags

    /// 0x131156: flags $E0-$F9 (handler table 0x131160).
    fn coord_flag(&mut self, ram: &mut Ram, chips: &mut dyn SoundChips, flag: u8) -> FlagResult {
        match flag {
            0xE0 => {
                let mut d1 = self.next() as i32;
                if (self.b(ram, 0x01) as i8) < 0 {
                    return FlagResult::Continue;
                }
                d1 |= (self.b(ram, 0x0A) & 0x37) as i32;
                self.set_b(ram, 0x0A, d1 as u8);
                self.write_fm_iorii_main(ram, chips, 0xB4, d1);
            }
            0xE1 => {
                let v = self.next();
                self.set_b(ram, 0x1E, v);
            }
            0xE2 => {
                let v = self.next();
                self.set_g(ram, 0x07, v);
            }
            0xE3 => {
                let d0 = self.b(ram, 0x0D) as u16;
                self.a4 = ram.u32(self.a5 + d0) as usize;
                ram.w32(self.a5 + d0, 0);
                self.a4 += 2;
                self.set_b(ram, 0x0D, (d0 + 4) as u8);
            }
            0xE4 => {
                self.restore_after_one_up(ram, chips);
                return FlagResult::ExitTwo;
            }
            0xE5 => {
                let v = self.next();
                self.set_b(ram, 0x02, v);
            }
            0xE6 => {
                let v = self.next();
                self.set_b(ram, 0x09, self.b(ram, 0x09).wrapping_add(v));
                self.set_total_levels(ram, chips);
            }
            0xE7 => self.set(ram, 4),
            0xE8 => {
                let v = self.data.u8(self.a4);
                self.set_b(ram, 0x12, v);
                let v2 = self.next();
                self.set_b(ram, 0x13, v2);
            }
            0xE9 => {
                let v = self.next();
                self.set_b(ram, 0x08, self.b(ram, 0x08).wrapping_add(v));
            }
            0xEA => {
                let v = self.data.u8(self.a4);
                self.set_g(ram, 0x02, v);
                let v2 = self.next();
                self.set_g(ram, 0x01, v2);
            }
            0xEB => {
                let d0 = self.next();
                for i in 0..10 {
                    ram.w8(Self::MUSIC_DAC + i as u16 * Self::TRACK_SIZE + 2, d0);
                }
            }
            0xEC => {
                let v = self.next();
                self.set_b(ram, 0x09, self.b(ram, 0x09).wrapping_add(v));
            }
            0xED => self.set_g(ram, 0x2C, 0),
            0xEE => {
                self.stop_special_fm4(ram, chips);
                return FlagResult::ExitTwo;
            }
            0xEF => {
                let d0 = self.next();
                self.set_b(ram, 0x0B, d0);
                if self.bit(ram, 2) {
                    return FlagResult::Continue;
                }
                let mut a1 = ram.u32(Self::A6 + 0x18) as usize;
                if self.g(ram, 0x0E) != 0 {
                    a1 = ram.u32(self.a5 + 0x20) as usize;
                    if (self.g(ram, 0x0E) as i8) >= 0 {
                        a1 = ram.u32(Self::A6 + 0x20) as usize;
                    }
                }
                self.set_voice(ram, chips, a1, d0);
            }
            0xF0 => {
                self.set(ram, 3);
                ram.w32(self.a5 + 0x14, self.a4 as u32);
                let v = self.next();
                self.set_b(ram, 0x18, v);
                let v = self.next();
                self.set_b(ram, 0x19, v);
                let v = self.next();
                self.set_b(ram, 0x1A, v);
                let v = self.next() >> 1;
                self.set_b(ram, 0x1B, v);
                ram.w16(self.a5 + 0x1C, 0);
            }
            0xF1 => self.set(ram, 3),
            0xF2 => {
                self.stop_track(ram, chips);
                return FlagResult::ExitTwo;
            }
            0xF3 => {
                self.set_b(ram, 0x01, 0xE0);
                let v = self.next();
                self.set_b(ram, 0x1F, v);
                if !self.bit(ram, 2) {
                    chips.write_psg(self.data.u8(self.a4 - 1) as i32);
                }
            }
            0xF4 => self.clr(ram, 3),
            0xF5 => {
                let v = self.next();
                self.set_b(ram, 0x0B, v);
            }
            0xF6 => self.jump(),
            0xF7 => {
                let index = self.next() as u16;
                let count = self.next();
                if self.b(ram, 0x24 + index) == 0 {
                    self.set_b(ram, 0x24 + index, count);
                }
                self.set_b(ram, 0x24 + index, self.b(ram, 0x24 + index).wrapping_sub(1));
                if self.b(ram, 0x24 + index) != 0 {
                    self.jump();
                } else {
                    self.a4 += 2;
                }
            }
            0xF8 => {
                let d0 = self.b(ram, 0x0D).wrapping_sub(4);
                ram.w32(self.a5 + d0 as u16, self.a4 as u32);
                self.set_b(ram, 0x0D, d0);
                self.jump();
            }
            0xF9 => {
                chips.write_ym(0, 0x88, 0x0F);
                chips.write_ym(0, 0x8C, 0x0F);
            }
            _ => {}
        }
        FlagResult::Continue
    }

    /// 0x131526: branch to operand address + 1 + offset.
    fn jump(&mut self) {
        let offset =
            (((self.data.u8(self.a4) as u16) << 8) | self.data.u8(self.a4 + 1) as u16) as i16;
        self.a4 += 2;
        self.a4 = (self.a4 as isize + offset as isize - 1) as usize;
    }

    /// 0x131454 ($F2).
    fn stop_track(&mut self, ram: &mut Ram, chips: &mut dyn SoundChips) {
        self.clr(ram, 7);
        self.clr(ram, 4);
        if (self.b(ram, 0x01) as i8) < 0 {
            self.psg_note_off(ram, chips);
        } else {
            if (self.g(ram, 0x08) as i8) < 0 {
                return;
            }
            self.fm_note_off(ram, chips);
        }
        if (self.g(ram, 0x0E) as i8) >= 0 {
            return;
        }
        self.set_g(ram, 0x00, 0);
        let d0 = self.b(ram, 0x01) as i32;
        let a3 = self.a5;
        if (d0 as i8) >= 0 {
            let a1 = if d0 == 4 && (ram.u8(Self::SPECIAL_FM) as i8) < 0 {
                self.a5 = Self::SPECIAL_FM;
                ram.u32(Self::A6 + 0x20) as usize
            } else {
                self.a5 = Self::table(&Self::OVERRIDDEN_TRACK, ((d0 - 2) << 2) & 0xFF);
                if self.a5 == 0 || (self.b(ram, 0) as i8) >= 0 {
                    self.a5 = a3;
                    return;
                }
                ram.u32(Self::A6 + 0x18) as usize
            };
            self.clr(ram, 2);
            self.set(ram, 1);
            let index = self.b(ram, 0x0B);
            self.set_voice(ram, chips, a1, index);
            self.a5 = a3;
            return;
        }
        let mut a0 = Self::SPECIAL_PSG;
        if !((ram.u8(a0) as i8) < 0 && (d0 == 0xE0 || d0 == 0xC0)) {
            a0 = Self::table(&Self::OVERRIDDEN_TRACK, (d0 >> 3) & 0x1F);
        }
        if a0 == 0 {
            return;
        }
        ram.w8(a0, ram.u8(a0) & !4);
        ram.w8(a0, ram.u8(a0) | 2);
        if ram.u8(a0 + 1) == 0xE0 {
            chips.write_psg(ram.u8(a0 + 0x1F) as i32);
        }
    }

    /// 0x1312F0 ($EE).
    fn stop_special_fm4(&mut self, ram: &mut Ram, chips: &mut dyn SoundChips) {
        self.clr(ram, 7);
        self.clr(ram, 4);
        self.fm_note_off(ram, chips);
        if (ram.u8(Self::A6 + 0x250) as i8) < 0 {
            return;
        }
        let a3 = self.a5;
        self.a5 = Self::A6 + 0x100;
        let a1 = ram.u32(Self::A6 + 0x18) as usize;
        self.clr(ram, 2);
        self.set(ram, 1);
        let index = self.b(ram, 0x0B);
        self.set_voice(ram, chips, a1, index);
        self.a5 = a3;
    }

    /// 0x131210 ($E4).
    fn restore_after_one_up(&mut self, ram: &mut Ram, chips: &mut dyn SoundChips) {
        for i in 0..0x220 {
            let v = ram.u8(Self::SAVED + i);
            ram.w8(Self::A6 + i, v);
        }
        ram.w8(Self::MUSIC_DAC, ram.u8(Self::MUSIC_DAC) | 4);
        let a3 = self.a5;
        let d6 = 0x28u8.wrapping_sub(self.g(ram, 0x26));
        self.a5 = Self::MUSIC_FM1;
        for _ in 0..6 {
            if self.bit(ram, 7) {
                self.set(ram, 1);
                let v = self.b(ram, 0x09).wrapping_add(d6);
                self.set_b(ram, 0x09, v);
                if !self.bit(ram, 2) {
                    let a1 = ram.u32(Self::A6 + 0x18) as usize;
                    let index = self.b(ram, 0x0B);
                    self.set_voice(ram, chips, a1, index);
                }
            }
            self.a5 += Self::TRACK_SIZE;
        }
        for _ in 0..3 {
            if self.bit(ram, 7) {
                self.set(ram, 1);
                self.psg_note_off(ram, chips);
                let v = self.b(ram, 0x09).wrapping_add(d6);
                self.set_b(ram, 0x09, v);
            }
            self.a5 += Self::TRACK_SIZE;
        }
        self.a5 = a3;
        self.set_g(ram, 0x24, 0x80);
        self.set_g(ram, 0x26, 0x28);
        self.set_g(ram, 0x27, 0);
    }

    // ------------------------------------------------------------------ requests

    /// 0x13059C.
    fn cycle_sound_queue(&mut self, ram: &mut Ram) {
        let table = self.data.u32(Self::PRIORITY_TABLE_PTR) as usize;
        let mut d3 = self.g(ram, 0x00) as i32;
        for i in 0..3 {
            let d1 = ram.u8(Self::A6 + 0x0A + i);
            ram.w8(Self::A6 + 0x0A + i, 0);
            if d1 < 0x81 {
                continue;
            }
            if self.g(ram, 0x09) != 0x80 {
                self.set_g(ram, 0x0A, d1);
                continue;
            }
            let d2 = self.data.u8(table + ((d1 - 0x81) & 0x7F) as usize) as i32;
            if d2 < d3 {
                continue;
            }
            d3 = d2;
            self.set_g(ram, 0x09, d1);
        }
        if (d3 as i8) >= 0 {
            self.set_g(ram, 0x00, d3 as u8);
        }
    }

    /// 0x1305E2. True when the rest of this frame's update is skipped.
    fn play_sound_id(&mut self, ram: &mut Ram, chips: &mut dyn SoundChips) -> bool {
        let d7 = self.g(ram, 0x09);
        if d7 == 0 {
            self.stop_all_sound(ram, chips);
            return false;
        }
        if d7 < 0x80 {
            return false;
        }
        self.set_g(ram, 0x09, 0x80);
        if (0xE0..0xF8).contains(&d7) {
            if self.g(ram, 0x27) != 0 || self.g(ram, 0x04) != 0 || self.g(ram, 0x24) != 0 {
                self.set_g(ram, 0x00, 0);
                return false;
            }
            let table = self.data.u32(Self::SFX_EXTRA_INDEX_PTR) as usize;
            self.play_sfx_common(ram, chips, table, (d7 - 0xE0) as i32);
            return false;
        }
        if d7 == 0xFF {
            return false;
        }
        if d7 <= 0x9F {
            self.play_music(ram, chips, d7);
            return true;
        }
        if d7 <= 0xF0 {
            self.play_sfx(ram, chips, d7);
            return false;
        }
        match d7 {
            0xF9 => self.fade_out_music(ram, chips),
            0xFA | 0xFE => {
                chips.play_sega_pcm();
                return true;
            }
            0xFB => self.speed_up(ram),
            0xFC => self.normal_speed(ram),
            0xFD => self.stop_all_sound(ram, chips),
            _ => {}
        }
        false
    }

    /// 0x1306DA.
    fn play_music(&mut self, ram: &mut Ram, chips: &mut dyn SoundChips, d7: u8) {
        if d7 == 0x98 {
            if self.g(ram, 0x27) != 0 {
                return;
            }
            for i in 0..10 {
                let a = Self::MUSIC_DAC + i as u16 * Self::TRACK_SIZE;
                ram.w8(a, ram.u8(a) & !4);
            }
            for i in 0..6 {
                let a = Self::SFX_FM1 + i as u16 * Self::TRACK_SIZE;
                ram.w8(a, ram.u8(a) & 0x7F);
            }
            self.set_g(ram, 0x00, 0);
            for i in 0..0x220 {
                let v = ram.u8(Self::A6 + i);
                ram.w8(Self::SAVED + i, v);
            }
            self.set_g(ram, 0x27, 0x80);
            self.set_g(ram, 0x00, 0);
        } else {
            self.set_g(ram, 0x27, 0);
            self.set_g(ram, 0x26, 0);
        }
        self.init_music_playback(ram, chips);
        let index = d7 as usize - 0x81;
        let speed = self
            .data
            .u8(self.data.u32(Self::SPEED_UP_TABLE_PTR) as usize + index);
        self.set_g(ram, 0x29, speed);
        let music_table = self.data.u32(Self::MUSIC_INDEX_POINTER) as usize;
        let mut a4 = self.data.u32(music_table + index * 4) as usize;
        let header = self.data.u16(a4) as usize + a4;
        ram.w32(Self::A6 + 0x18, header as u32);
        let mut tempo = self.data.u8(a4 + 5);
        self.set_g(ram, 0x28, tempo);
        if self.g(ram, 0x2A) != 0 {
            tempo = self.g(ram, 0x29);
        }
        self.set_g(ram, 0x02, tempo);
        self.set_g(ram, 0x01, tempo);
        let a3 = a4;
        a4 += 6;
        let divider = self.data.u8(a3 + 4);
        let fm_count = self.data.u8(a3 + 2) as usize;
        if fm_count != 0 {
            let mut a1 = Self::MUSIC_DAC;
            for i in 0..fm_count {
                ram.w8(a1, ram.u8(a1) | 0x80);
                ram.w8(a1 + 1, Self::FM_CHANNELS.get(i).copied().unwrap_or(0));
                ram.w8(a1 + 2, divider);
                ram.w8(a1 + 0x0D, Self::TRACK_SIZE as u8);
                ram.w8(a1 + 0x0A, 0xC0);
                ram.w8(a1 + 0x0E, 1);
                let off = self.data.u16(a4) as usize + a3;
                ram.w32(a1 + 4, off as u32);
                let freq = self.data.u16(a4 + 2);
                ram.w16(a1 + 8, freq);
                a4 += 4;
                a1 += Self::TRACK_SIZE;
            }
            if fm_count == 7 {
                chips.write_ym(0, 0x2B, 0x00);
            } else {
                chips.write_ym(0, 0x28, 0x06);
                chips.write_ym(1, 0x42, 0x7F);
                chips.write_ym(1, 0x4A, 0x7F);
                chips.write_ym(1, 0x46, 0x7F);
                chips.write_ym(1, 0x4E, 0x7F);
                chips.write_ym(1, 0xB6, 0xC0);
            }
        }
        let psg_count = self.data.u8(a3 + 3) as usize;
        if psg_count != 0 {
            let mut a1 = Self::MUSIC_PSG1;
            for i in 0..psg_count {
                ram.w8(a1, ram.u8(a1) | 0x80);
                ram.w8(a1 + 1, Self::PSG_CHANNELS.get(i).copied().unwrap_or(0));
                ram.w8(a1 + 2, divider);
                ram.w8(a1 + 0x0D, Self::TRACK_SIZE as u8);
                ram.w8(a1 + 0x0E, 1);
                let off = self.data.u16(a4) as usize + a3;
                ram.w32(a1 + 4, off as u32);
                let freq = self.data.u16(a4 + 2);
                ram.w16(a1 + 8, freq);
                ram.w8(a1 + 0x0B, self.data.u8(a4 + 5));
                a4 += 6;
                a1 += Self::TRACK_SIZE;
            }
        }
        for i in 0..6 {
            let a1 = Self::SFX_FM1 + i as u16 * Self::TRACK_SIZE;
            if (ram.u8(a1) as i8) >= 0 {
                continue;
            }
            let mut d0 = ram.u8(a1 + 1) as i32;
            d0 = if (d0 as i8) < 0 {
                d0 >> 3
            } else {
                ((d0 - 2) << 2) & 0xFF
            };
            let a0 = Self::table(&Self::OVERRIDDEN_TRACK, d0);
            if a0 != 0 {
                ram.w8(a0, ram.u8(a0) | 4);
            }
        }
        if (ram.u16(Self::SPECIAL_FM) as i16) < 0 {
            ram.w8(Self::A6 + 0x100, ram.u8(Self::A6 + 0x100) | 4);
        }
        if (ram.u16(Self::SPECIAL_PSG) as i16) < 0 {
            ram.w8(Self::A6 + 0x1F0, ram.u8(Self::A6 + 0x1F0) | 4);
        }
        self.a5 = Self::MUSIC_FM1;
        for _ in 0..6 {
            self.fm_note_off(ram, chips);
            self.a5 += Self::TRACK_SIZE;
        }
        for _ in 0..3 {
            self.psg_note_off(ram, chips);
            self.a5 += Self::TRACK_SIZE;
        }
    }

    /// 0x1308CA.
    fn play_sfx(&mut self, ram: &mut Ram, chips: &mut dyn SoundChips, mut d7: u8) {
        if self.g(ram, 0x27) != 0 || self.g(ram, 0x04) != 0 || self.g(ram, 0x24) != 0 {
            self.set_g(ram, 0x00, 0);
            return;
        }
        if d7 == 0xB5 {
            if self.g(ram, 0x2B) == 0 {
                d7 = 0xCE;
            }
            let v = self.g(ram, 0x2B) ^ 1;
            self.set_g(ram, 0x2B, v);
        }
        if d7 == 0xA7 {
            if self.g(ram, 0x2C) != 0 {
                return;
            }
            self.set_g(ram, 0x2C, 0x80);
        }
        let table = self.data.u32(Self::SFX_INDEX_PTR) as usize;
        self.play_sfx_common(ram, chips, table, d7 as i32 - 0xA0);
    }

    /// 0x130916.
    fn play_sfx_common(
        &mut self,
        ram: &mut Ram,
        chips: &mut dyn SoundChips,
        table: usize,
        index: i32,
    ) {
        let a3 = self.data.u32(table + ((index & 0x3FFF) as usize) * 4) as usize;
        let mut a1 = a3;
        let voices = self.data.u16(a1) as usize + a3;
        a1 += 2;
        let divider = self.data.u8(a1);
        a1 += 1;
        let count = self.data.u8(a1);
        a1 += 1;
        for _ in 0..count {
            let d4 = self.data.u8(a1 + 1);
            let mut d3 = d4 as i32;
            if (d3 as i8) >= 0 {
                d3 = ((d3 - 2) << 2) & 0xFFFF;
            } else {
                d3 >>= 3;
            }
            let over = Self::table(&Self::OVERRIDDEN_TRACK, d3);
            if over != 0 {
                ram.w8(over, ram.u8(over) | 4);
            }
            if d4 == 0xC0 {
                chips.write_psg(0xDF);
                chips.write_psg(0xFF);
            }
            let a5 = Self::table(&Self::SFX_TRACK, d3);
            if a5 == 0 {
                a1 += 6;
                continue;
            }
            for k in 0..Self::TRACK_SIZE {
                ram.w8(a5 + k, 0);
            }
            ram.w16(a5, self.data.u16(a1));
            ram.w8(a5 + 2, divider);
            let off = self.data.u16(a1 + 2) as usize + a3;
            ram.w32(a5 + 4, off as u32);
            ram.w16(a5 + 8, self.data.u16(a1 + 4));
            a1 += 6;
            ram.w8(a5 + 0x0E, 1);
            ram.w8(a5 + 0x0D, Self::TRACK_SIZE as u8);
            if (d4 as i8) >= 0 {
                ram.w8(a5 + 0x0A, 0xC0);
                ram.w32(a5 + 0x20, voices as u32);
            }
        }
        if (ram.u8(Self::A6 + 0x250) as i8) < 0 {
            ram.w8(Self::SPECIAL_FM, ram.u8(Self::SPECIAL_FM) | 4);
        }
        if (ram.u8(Self::A6 + 0x310) as i8) < 0 {
            ram.w8(Self::SPECIAL_PSG, ram.u8(Self::SPECIAL_PSG) | 4);
        }
    }

    /// 0x130AE2: stop all SFX and give their channels back to the music.
    fn stop_sfx(&mut self, ram: &mut Ram, chips: &mut dyn SoundChips) {
        self.set_g(ram, 0x00, 0);
        let mut a3 = 0u16;
        self.a5 = Self::SFX_FM1;
        while self.a5 < Self::SFX_FM1 + 6 * Self::TRACK_SIZE {
            if (self.b(ram, 0) as i8) >= 0 {
                self.a5 += Self::TRACK_SIZE;
                continue;
            }
            self.clr(ram, 7);
            let d3 = self.b(ram, 0x01) as i32;
            if (d3 as i8) >= 0 {
                self.fm_note_off(ram, chips);
                let self_track = self.a5;
                let a1;
                if d3 == 4 && (ram.u8(Self::SPECIAL_FM) as i8) < 0 {
                    self.a5 = Self::SPECIAL_FM;
                    a1 = ram.u32(Self::A6 + 0x20) as usize;
                } else {
                    a3 = self_track;
                    self.a5 = Self::table(&Self::OVERRIDDEN_TRACK, ((d3 - 2) << 2) & 0xFF);
                    a1 = ram.u32(Self::A6 + 0x18) as usize;
                    if self.a5 == 0 {
                        self.a5 = a3;
                        self.a5 += Self::TRACK_SIZE;
                        continue;
                    }
                }
                self.clr(ram, 2);
                self.set(ram, 1);
                let index = self.b(ram, 0x0B);
                self.set_voice(ram, chips, a1, index);
                self.a5 = if a3 != 0 { a3 } else { self_track };
                self.a5 += Self::TRACK_SIZE;
                continue;
            }
            self.psg_note_off(ram, chips);
            let mut a0 = Self::SPECIAL_PSG;
            if d3 != 0xE0 && d3 != 0xC0 {
                a0 = Self::table(&Self::OVERRIDDEN_TRACK, (d3 >> 3) & 0x1F);
            }
            if a0 != 0 {
                ram.w8(a0, ram.u8(a0) & !4);
                ram.w8(a0, ram.u8(a0) | 2);
                if ram.u8(a0 + 1) == 0xE0 {
                    chips.write_psg(ram.u8(a0 + 0x1F) as i32);
                }
            }
            self.a5 += Self::TRACK_SIZE;
        }
    }

    /// 0x130B7C.
    fn stop_special_sfx(&mut self, ram: &mut Ram, chips: &mut dyn SoundChips) {
        self.a5 = Self::SPECIAL_FM;
        if (self.b(ram, 0) as i8) < 0 {
            self.clr(ram, 7);
            if !self.bit(ram, 2) {
                chips.write_ym(0, 0x28, self.b(ram, 0x01) as i32);
                self.a5 = Self::A6 + 0x100;
                self.clr(ram, 2);
                self.set(ram, 1);
                if (self.b(ram, 0) as i8) < 0 {
                    let a1 = ram.u32(Self::A6 + 0x18) as usize;
                    let index = self.b(ram, 0x0B);
                    self.set_voice(ram, chips, a1, index);
                }
            }
        }
        self.a5 = Self::SPECIAL_PSG;
        if (self.b(ram, 0) as i8) < 0 {
            self.clr(ram, 7);
            if !self.bit(ram, 2) {
                chips.write_psg((self.b(ram, 0x01) | 0x1F) as i32);
                self.a5 = Self::A6 + 0x1F0;
                self.clr(ram, 2);
                self.set(ram, 1);
                if (self.b(ram, 0) as i8) < 0 && self.b(ram, 0x01) == 0xE0 {
                    chips.write_psg(self.b(ram, 0x1F) as i32);
                }
            }
        }
    }

    /// 0x130BE6 ($F9).
    fn fade_out_music(&mut self, ram: &mut Ram, chips: &mut dyn SoundChips) {
        self.stop_sfx(ram, chips);
        self.stop_special_sfx(ram, chips);
        self.set_g(ram, 0x06, 3);
        self.set_g(ram, 0x04, 0x28);
        ram.w8(Self::MUSIC_DAC, 0);
        self.set_g(ram, 0x2A, 0);
    }

    /// 0x130C04.
    fn do_fade_out(&mut self, ram: &mut Ram, chips: &mut dyn SoundChips) {
        if self.g(ram, 0x06) != 0 {
            let v = self.g(ram, 0x06).wrapping_sub(1);
            self.set_g(ram, 0x06, v);
            return;
        }
        let v = self.g(ram, 0x04).wrapping_sub(1);
        self.set_g(ram, 0x04, v);
        if v == 0 {
            self.stop_all_sound(ram, chips);
            return;
        }
        self.set_g(ram, 0x06, 3);
        self.a5 = Self::MUSIC_FM1;
        for _ in 0..6 {
            if (self.b(ram, 0) as i8) >= 0 {
                self.a5 += Self::TRACK_SIZE;
                continue;
            }
            let v = self.b(ram, 0x09).wrapping_add(1);
            self.set_b(ram, 0x09, v);
            if (self.b(ram, 0x09) as i8) < 0 {
                self.clr(ram, 7);
            } else {
                self.set_total_levels(ram, chips);
            }
            self.a5 += Self::TRACK_SIZE;
        }
        for _ in 0..3 {
            if (self.b(ram, 0) as i8) >= 0 {
                self.a5 += Self::TRACK_SIZE;
                continue;
            }
            let v = self.b(ram, 0x09).wrapping_add(1);
            self.set_b(ram, 0x09, v);
            if self.b(ram, 0x09) >= 0x10 {
                self.clr(ram, 7);
            } else {
                let v = self.b(ram, 0x09);
                self.psg_set_volume(ram, chips, v as i32);
            }
            self.a5 += Self::TRACK_SIZE;
        }
    }

    /// 0x130D78.
    fn do_fade_in(&mut self, ram: &mut Ram, chips: &mut dyn SoundChips) {
        if self.g(ram, 0x25) != 0 {
            let v = self.g(ram, 0x25).wrapping_sub(1);
            self.set_g(ram, 0x25, v);
            return;
        }
        if self.g(ram, 0x26) == 0 {
            ram.w8(Self::MUSIC_DAC, ram.u8(Self::MUSIC_DAC) & !4);
            self.set_g(ram, 0x24, 0);
            return;
        }
        let v = self.g(ram, 0x26).wrapping_sub(1);
        self.set_g(ram, 0x26, v);
        self.set_g(ram, 0x25, 2);
        self.a5 = Self::MUSIC_FM1;
        for _ in 0..6 {
            if (self.b(ram, 0) as i8) >= 0 {
                self.a5 += Self::TRACK_SIZE;
                continue;
            }
            let v = self.b(ram, 0x09).wrapping_sub(1);
            self.set_b(ram, 0x09, v);
            self.set_total_levels(ram, chips);
            self.a5 += Self::TRACK_SIZE;
        }
        for _ in 0..3 {
            if (self.b(ram, 0) as i8) >= 0 {
                self.a5 += Self::TRACK_SIZE;
                continue;
            }
            let v = self.b(ram, 0x09).wrapping_sub(1);
            self.set_b(ram, 0x09, v);
            let mut d6 = self.b(ram, 0x09);
            if d6 >= 0x10 {
                d6 = 0x0F;
            }
            self.psg_set_volume(ram, chips, d6 as i32);
            self.a5 += Self::TRACK_SIZE;
        }
    }

    /// 0x130D08: every tempo period, every music track waits one more frame.
    fn tempo_wait(&mut self, ram: &mut Ram) {
        let v = self.g(ram, 0x02);
        self.set_g(ram, 0x01, v);
        for i in 0..10 {
            let a = Self::MUSIC_DAC + i as u16 * Self::TRACK_SIZE + 0x0E;
            ram.w8(a, ram.u8(a).wrapping_add(1));
        }
    }

    /// 0x130D20 ($FB).
    fn speed_up(&mut self, ram: &mut Ram) {
        if self.g(ram, 0x27) != 0 {
            let v = ram.u8(Self::SAVED + 0x29);
            ram.w8(Self::SAVED + 0x02, v);
            ram.w8(Self::SAVED + 0x01, v);
            ram.w8(Self::SAVED + 0x2A, 0x80);
            return;
        }
        let v = self.g(ram, 0x29);
        self.set_g(ram, 0x02, v);
        self.set_g(ram, 0x01, v);
        self.set_g(ram, 0x2A, 0x80);
    }

    /// 0x130D4E ($FC).
    fn normal_speed(&mut self, ram: &mut Ram) {
        if self.g(ram, 0x27) != 0 {
            let v = ram.u8(Self::SAVED + 0x28);
            ram.w8(Self::SAVED + 0x02, v);
            ram.w8(Self::SAVED + 0x01, v);
            ram.w8(Self::SAVED + 0x2A, 0);
            return;
        }
        let v = self.g(ram, 0x28);
        self.set_g(ram, 0x02, v);
        self.set_g(ram, 0x01, v);
        self.set_g(ram, 0x2A, 0);
    }

    /// 0x130C9E.
    fn stop_all_sound(&mut self, ram: &mut Ram, chips: &mut dyn SoundChips) {
        chips.write_ym(0, 0x2B, 0x80);
        chips.write_ym(0, 0x27, 0x00);
        for i in 0..0x390 {
            ram.w8(Self::A6 + i, 0);
        }
        self.set_g(ram, 0x09, 0x80);
        self.fm_silence_all(chips);
        self.psg_silence_all(chips);
    }

    /// 0x130CCA: clear the music state, keeping priority, 1-up / speed / fade flags and the queue.
    fn init_music_playback(&mut self, ram: &mut Ram, chips: &mut dyn SoundChips) {
        let d1 = self.g(ram, 0x00);
        let d2 = self.g(ram, 0x27);
        let d3 = self.g(ram, 0x2A);
        let d4 = self.g(ram, 0x26);
        let d5 = ram.u16(Self::A6 + 0x0A);
        for i in 0..0x220 {
            ram.w8(Self::A6 + i, 0);
        }
        self.set_g(ram, 0x00, d1);
        self.set_g(ram, 0x27, d2);
        self.set_g(ram, 0x2A, d3);
        self.set_g(ram, 0x26, d4);
        ram.w16(Self::A6 + 0x0A, d5);
        self.set_g(ram, 0x09, 0x80);
        self.fm_silence_all(chips);
        self.psg_silence_all(chips);
    }

    /// 0x130C6A.
    fn fm_silence_all(&mut self, chips: &mut dyn SoundChips) {
        for d3 in (0..=2).rev() {
            chips.write_ym(0, 0x28, d3);
            chips.write_ym(0, 0x28, d3 + 4);
        }
        let mut d0 = 0x40;
        for _ in 0..3 {
            for _ in 0..4 {
                chips.write_ym(0, d0, 0x7F);
                chips.write_ym(1, d0, 0x7F);
                d0 += 4;
            }
            d0 -= 0x0F;
        }
    }

    /// 0x1310B2.
    fn psg_silence_all(&mut self, chips: &mut dyn SoundChips) {
        chips.write_psg(0x9F);
        chips.write_psg(0xBF);
        chips.write_psg(0xDF);
        chips.write_psg(0xFF);
    }

    /// 0x1304EA: $F003 set = pause ($80 = resume).
    fn pause_music(&mut self, ram: &mut Ram, chips: &mut dyn SoundChips) {
        if (self.g(ram, 0x03) as i8) < 0 {
            self.set_g(ram, 0x03, 0);
            for i in 0..7 {
                let track = Self::MUSIC_DAC + i as u16 * Self::TRACK_SIZE;
                self.a5 = track;
                if self.bit(ram, 7) && !self.bit(ram, 2) {
                    let pan = self.b(ram, 0x0A);
                    self.write_fm_iorii(ram, chips, 0xB4, pan as i32);
                }
            }
            for i in 0..3 {
                let track = Self::SFX_FM1 + i as u16 * Self::TRACK_SIZE;
                self.a5 = track;
                if self.bit(ram, 7) && !self.bit(ram, 2) {
                    let pan = self.b(ram, 0x0A);
                    self.write_fm_iorii(ram, chips, 0xB4, pan as i32);
                }
            }
            self.a5 = Self::SPECIAL_FM;
            if self.bit(ram, 7) && !self.bit(ram, 2) {
                let pan = self.b(ram, 0x0A);
                self.write_fm_iorii(ram, chips, 0xB4, pan as i32);
            }
            return;
        }
        if self.g(ram, 0x03) == 2 {
            return;
        }
        self.set_g(ram, 0x03, 2);
        for i in 0..3 {
            chips.write_ym(0, 0xB4 + i, 0);
            chips.write_ym(1, 0xB4 + i, 0);
        }
        for d3 in (0..=2).rev() {
            chips.write_ym(0, 0x28, d3);
            chips.write_ym(0, 0x28, d3 + 4);
        }
        self.psg_silence_all(chips);
    }
}
