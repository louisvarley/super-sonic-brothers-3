//! SMB3 source-engine PCM effects (`campaign/effects/*.pcm`) and the SMB3 sound queues.
//!
//! The converter (`tools/smb3-effects`) renders the two pulse channels and noise channel of SMB3's own sound engine
//! to mono 48 kHz PCM. This player overlaps effects and mixes them with the 68k SMPS output.

use crate::{vars, Ram};

#[derive(Clone, Debug, Default)]
pub struct PcmEffect {
    pub name: String,
    /// Signed 16-bit mono samples at 48 kHz.
    pub samples: Vec<i16>,
}

#[derive(Clone, Copy)]
struct Voice {
    effect: usize,
    position: usize,
}

#[derive(Default)]
pub struct EffectPlayer {
    effects: Vec<PcmEffect>,
    voices: Vec<Voice>,
}

impl EffectPlayer {
    pub fn new(effects: Vec<PcmEffect>) -> Self {
        Self {
            effects,
            voices: Vec::new(),
        }
    }

    pub fn names(&self) -> impl Iterator<Item = &str> {
        self.effects.iter().map(|e| e.name.as_str())
    }

    pub fn is_empty(&self) -> bool {
        self.effects.is_empty()
    }

    pub fn play(&mut self, name: &str) -> bool {
        let Some(effect) = self.effects.iter().position(|e| e.name == name) else {
            return false;
        };
        if self.voices.len() >= 8 {
            self.voices.remove(0);
        }
        self.voices.push(Voice {
            effect,
            position: 0,
        });
        true
    }

    /// Mixes active effects into an already-rendered stereo buffer.
    pub fn mix(&mut self, out: &mut [i16], frames: usize) {
        if self.voices.is_empty() {
            return;
        }
        for voice in &mut self.voices {
            let samples = &self.effects[voice.effect].samples;
            let n = frames.min(samples.len().saturating_sub(voice.position));
            for i in 0..n {
                let s = samples[voice.position + i] as i32;
                let j = i * 2;
                out[j] = (out[j] as i32 + s).clamp(i16::MIN as i32, i16::MAX as i32) as i16;
                out[j + 1] = (out[j + 1] as i32 + s).clamp(i16::MIN as i32, i16::MAX as i32) as i16;
            }
            voice.position += n;
        }
        self.voices
            .retain(|v| v.position < self.effects[v.effect].samples.len());
    }

    #[cfg(test)]
    pub fn active(&self) -> usize {
        self.voices.len()
    }
}

/// The port's audio-enabled flag. Sound writes are gated on `$FFE5`, which `Session::enable_audio` sets, so existing
/// audio-disabled traces keep their exact RAM.
pub fn enabled(ram: &Ram) -> bool {
    ram.u8(vars::SOUND_DRIVER_SELECT) != 0
}
