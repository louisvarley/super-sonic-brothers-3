//! 68k SMPS audio path (M6a).
//!
//! `rom-assets` loads the archive's player data into [`AudioAssets`]; [`Smps68kDriver`] is a routine-for-routine
//! port of the hack's Sonic 1-style 68k driver, and [`SoundMixer`] renders its YM2612/PSG/DAC writes at 48 kHz.

pub mod mixer;
pub mod smps68k;
pub mod sn76489;
pub mod ym2612;

pub use mixer::{dac_sample_rate, SoundChips, SoundMixer, DEFAULT_OUTPUT_RATE};
pub use smps68k::Smps68kDriver;
pub use sn76489::Sn76489;
pub use ym2612::Ym2612;

/// The 68k driver's data, read at the original ROM addresses (`audio/smps68k/player.json`).
#[derive(Clone, Debug, Default)]
pub struct AudioAssets {
    /// (base address, bytes) for `player.json`'s segments.
    pub segments: Vec<(u32, Vec<u8>)>,
    pub fm_frequencies: Vec<u16>,
    pub psg_frequencies: Vec<u16>,
    pub dac_pitches: Vec<u8>,
    pub carrier_masks: Vec<u8>,
    pub voice_registers: Vec<u8>,
    pub level_music: Vec<u8>,
    pub level_music2p: Vec<u8>,
    /// Decoded WAV payloads for `driver.json`'s DAC samples.
    pub dac_samples: Vec<Vec<u8>>,
    pub dac_sample_pitches: Vec<i32>,
    /// When the act has an extra SMB3 song, the music id to play instead of the zone list's.
    pub extra_music: Option<u16>,
}

impl AudioAssets {
    /// A byte at a ROM address in the driver's data block or the sound data (0 elsewhere).
    pub fn u8(&self, address: usize) -> u8 {
        let address = (address & 0xFFFFFF) as u32;
        for (base, data) in &self.segments {
            if address >= *base && (address as usize) < *base as usize + data.len() {
                return data[(address - *base) as usize];
            }
        }
        0
    }

    pub fn u16(&self, address: usize) -> u16 {
        ((self.u8(address) as u16) << 8) | self.u8(address + 1) as u16
    }

    pub fn u32(&self, address: usize) -> u32 {
        ((self.u16(address) as u32) << 16) | self.u16(address + 2) as u32
    }

    fn patch(&mut self, address: usize, value: u8) {
        let address = address as u32;
        for (base, data) in &mut self.segments {
            if address >= *base && (address as usize) < *base as usize + data.len() {
                data[(address - *base) as usize] = value;
                return;
            }
        }
    }

    /// Maps an extra song at `address` and points the music table entry for `id` at it.
    pub fn add_extra_song(&mut self, id: u8, address: u32, data: Vec<u8>) {
        self.segments.push((address, data));
        let entry =
            self.u32(Smps68kDriver::MUSIC_INDEX_POINTER) as usize + (id as usize - 0x81) * 4;
        for i in 0..4 {
            self.patch(entry + i, (address >> (24 - i * 8)) as u8);
        }
    }

    pub fn music_for_zone(&self, zone: usize, two_player: bool) -> u16 {
        let list = if two_player {
            &self.level_music2p
        } else {
            &self.level_music
        };
        list.get(zone).copied().unwrap_or(0) as u16
    }
}

/// The port's optional audio path: the driver's per-frame update and the chip mixer it writes to.
///
/// The driver state lives in the caller's [`crate::Ram`] at `$F000`, exactly as in the original.
pub struct AudioState {
    pub driver: Smps68kDriver,
    pub mixer: SoundMixer,
    pub music: u16,
}

impl AudioState {
    pub fn new(assets: AudioAssets) -> Self {
        let mixer = SoundMixer::new(
            assets.dac_samples.clone(),
            assets.dac_sample_pitches.clone(),
            DEFAULT_OUTPUT_RATE,
        );
        Self {
            driver: Smps68kDriver::new(assets),
            mixer,
            music: 0,
        }
    }

    /// `World`'s sound setup: the driver as left by its boot init, then the zone music queued in `$F00A`.
    pub fn start_music(&mut self, ram: &mut crate::Ram, music: u16) {
        self.music = music;
        ram.w8(Smps68kDriver::BASE + 0x09, 0x80);
        if music != 0 {
            ram.w16(crate::vars::LEVEL_MUSIC, music);
            self.play_music(ram, music);
        }
    }

    /// `RamSound.PlayMusic`: queue a music id for the next VBlank.
    pub fn play_music(&mut self, ram: &mut crate::Ram, music: u16) {
        ram.w8(crate::vars::SMPS_QUEUE1, music as u8);
    }

    /// `RamSound.PlaySound`: queue an SFX id for the next VBlank.
    pub fn play_sound(&mut self, ram: &mut crate::Ram, sound: u16) {
        ram.w8(crate::vars::SMPS_QUEUE2, sound as u8);
    }

    /// `World.UpdateSound`: the 68k driver's update in VBlank.
    pub fn update(&mut self, ram: &mut crate::Ram) {
        self.driver.update(ram, &mut self.mixer);
    }

    pub fn render(&mut self, out: &mut [i16], frames: usize) {
        self.mixer.render(out, frames);
    }
}
