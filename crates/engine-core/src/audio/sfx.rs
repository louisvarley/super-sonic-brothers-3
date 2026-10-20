//! Sound-effect request helpers used by the gameplay port.
//!
//! Sonic effects go to the 68k SMPS queue (`$F00B`). SMB3 PCM effects requested by the host go through
//! `Session::play_effect` instead. The queue writes are gated on `$FFE5`, so audio-disabled traces leave RAM
//! untouched.

use crate::audio::effects::enabled;
use crate::{vars, Ram};

// Sonic 2 / Sonic 1 effect ids (`Sfx` in the legacy logic).
pub mod ids {
    pub const JUMP: u16 = 0xA0;
    pub const CHECKPOINT: u16 = 0xA1;
    pub const HURT: u16 = 0xA3;
    pub const SKIDDING: u16 = 0xA4;
    pub const HURT_BY_SPIKES: u16 = 0xA6;
    pub const PUSH: u16 = 0xA7;
    pub const SPLASH: u16 = 0xAA;
    pub const SHIELD: u16 = 0xAF;
    pub const DROWN: u16 = 0xB2;
    pub const RING: u16 = 0xB5;
    pub const SPIKES_MOVE: u16 = 0xB6;
    pub const SMASH: u16 = 0xB9;
    pub const SPINDASH_RELEASE: u16 = 0xBC;
    pub const ROLL: u16 = 0xBE;
    pub const EXPLOSION: u16 = 0xC1;
    pub const SPRING: u16 = 0xCC;
    pub const BLIP: u16 = 0xCD;
    pub const SIGNPOST: u16 = 0xCF;
    pub const HELICOPTER: u16 = 0xDE;
    pub const SPINDASH_REV: u16 = 0xE0;
}

/// A Sonic/68k music request (the SMPS primary queue).
pub fn music(ram: &mut Ram, id: u16) {
    if enabled(ram) {
        ram.w8(vars::SMPS_QUEUE1, id as u8);
    }
}

/// Sonic/68k effect. Sets the SMPS secondary request queue, as `RamSound.PlaySound` does for `$FFE5 != 0`.
pub fn sonic(ram: &mut Ram, id: u16) {
    if enabled(ram) {
        ram.w8(vars::SMPS_QUEUE2, id as u8);
    }
}

/// SMB3/Mario PCM effects. These use a reserved range of the secondary queue (`$80-$8F`) that the 68k driver
/// never reads; [`crate::audio::AudioState::update`] turns them into PCM-effect requests instead.
pub mod mario {
    pub const BUMP: u8 = 0x80;
    pub const KICK: u8 = 0x81;
    pub const COIN: u8 = 0x82;
    pub const POWERUP: u8 = 0x83;
    pub const ONE_UP: u8 = 0x84;
    pub const CRUMBLE: u8 = 0x85;
    pub const FIRE: u8 = 0x86;
    pub const BOOMERANG: u8 = 0x87;
    pub const PIPE: u8 = 0x88;
    pub const MAP_MOVE: u8 = 0x89;
    pub const MAP_ENTER: u8 = 0x8A;
    pub const MAP_WORLD: u8 = 0x8B;
    pub const COURSE_CLEAR: u8 = 0x8C;
}

/// Queues a Mario PCM effect (no-op when audio is disabled, so golden traces are unchanged).
pub fn mario(ram: &mut Ram, id: u8) {
    if enabled(ram) {
        ram.w8(vars::SMPS_QUEUE2, id);
    }
}
