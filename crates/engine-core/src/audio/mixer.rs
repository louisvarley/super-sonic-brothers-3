//! Sound mixer, a direct port of `legacy/src/SonicBros3/Audio/SoundMixer.cs`.
//!
//! Chip writes are applied as they come; [`SoundMixer::render`] produces one frame's audio at the output rate
//! (stereo 16-bit). It is I/O free.

use super::sn76489::Sn76489;
use super::ym2612::{sample_rate, Ym2612};

pub const DEFAULT_OUTPUT_RATE: u32 = 48_000;

/// Playback rate from the Z80 loop (0x0077-0x00B3): 275 + 26*pitch cycles per byte (two nibbles) at 3.579545 MHz.
pub fn dac_sample_rate(pitch: i32) -> f64 {
    3_579_545.0 / ((275.0 + 26.0 * pitch as f64) / 2.0)
}

/// Where the sound driver's chip writes go.
pub trait SoundChips {
    /// A YM2612 register write; part 0 = $A04000/1, part 1 = $A04002/3.
    fn write_ym(&mut self, part: i32, register: i32, value: i32);
    /// A byte written to the PSG port ($C00011).
    fn write_psg(&mut self, value: i32);
    /// The Z80 DAC driver's request byte ($A01FFF): a sample ID $81-$83.
    fn play_dac(&mut self, sample: i32);
    /// Sample $83's pitch byte in the Z80 driver ($A000EA).
    fn set_dac_pitch(&mut self, pitch: i32);
    /// The SEGA voice (command $FA): the original streams it to the DAC in a 68k busy loop.
    fn play_sega_pcm(&mut self) {}
    /// Sonic 2's Z80 driver: unsigned 8-bit samples written to the DAC at `rate` Hz.
    fn play_dac_pcm(&mut self, _samples: &[u8], _rate: f64) {}
}

pub struct SoundMixer {
    ym: Ym2612,
    psg: Sn76489,
    samples: Vec<Vec<u8>>,
    pitches: Vec<i32>,
    playing: Option<Vec<u8>>,
    position: f64,
    step: f64,
    output_rate: u32,
    resample: f64,
    prev: (i32, i32),
    cur: (i32, i32),
    /// Debugging: render only these parts ("ym", "psg", "dac"); `None` = all.
    pub solo: Option<String>,
    /// Master volume, 0-1.
    pub volume: f32,
}

impl SoundMixer {
    pub fn new(samples: Vec<Vec<u8>>, pitches: Vec<i32>, output_rate: u32) -> Self {
        let native = sample_rate();
        Self {
            ym: Ym2612::new(),
            psg: Sn76489::new(native),
            samples,
            pitches,
            playing: None,
            position: 0.0,
            step: 0.0,
            output_rate,
            resample: 0.0,
            prev: (0, 0),
            cur: (0, 0),
            solo: None,
            volume: 0.8,
        }
    }

    pub fn output_rate(&self) -> u32 {
        self.output_rate
    }

    fn native_sample(&mut self) -> (i32, i32) {
        if let Some(s) = &self.playing {
            let p = self.position as usize;
            if p < s.len() {
                self.ym.set_dac(s[p] as i32);
                self.position += self.step;
            } else {
                self.playing = None;
            }
        }
        let (mut left, mut right) = self.ym.clock();
        let mut psg = self.psg.clock();
        if let Some(solo) = &self.solo {
            if solo == "psg" {
                left = 0;
                right = 0;
            }
            if solo != "psg" {
                psg = 0;
            }
        }
        left += psg;
        right += psg;
        (left, right)
    }

    /// Renders `frames` stereo output frames into `buffer` (interleaved L/R).
    pub fn render(&mut self, buffer: &mut [i16], frames: usize) {
        let ratio = sample_rate() / self.output_rate as f64;
        let scale = (self.volume * 0.48f32) as f64;
        for i in 0..frames {
            self.resample += ratio;
            while self.resample >= 1.0 {
                self.resample -= 1.0;
                self.prev = self.cur;
                self.cur = self.native_sample();
            }
            let t = self.resample;
            let l = (self.prev.0 as f64 + (self.cur.0 - self.prev.0) as f64 * t) * scale;
            let r = (self.prev.1 as f64 + (self.cur.1 - self.prev.1) as f64 * t) * scale;
            buffer[i * 2] = l.clamp(i16::MIN as f64, i16::MAX as f64) as i16;
            buffer[i * 2 + 1] = r.clamp(i16::MIN as f64, i16::MAX as f64) as i16;
        }
    }
}

impl SoundChips for SoundMixer {
    fn write_ym(&mut self, part: i32, register: i32, value: i32) {
        self.ym.write(part, register, value);
    }

    fn write_psg(&mut self, value: i32) {
        self.psg.write(value);
    }

    fn play_dac(&mut self, sample: i32) {
        let i = sample - 0x81;
        if i < 0 || i as usize >= self.samples.len() {
            return;
        }
        self.playing = Some(self.samples[i as usize].clone());
        self.position = 0.0;
        self.step = dac_sample_rate(self.pitches[i as usize]) / sample_rate();
    }

    fn set_dac_pitch(&mut self, pitch: i32) {
        if self.pitches.len() > 2 {
            self.pitches[2] = pitch;
        }
    }

    fn play_dac_pcm(&mut self, samples: &[u8], rate: f64) {
        self.playing = if samples.is_empty() {
            None
        } else {
            Some(samples.to_vec())
        };
        self.position = 0.0;
        self.step = rate / sample_rate();
    }
}
