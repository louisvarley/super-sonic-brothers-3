//! Data-driven animated level tiles (generic interpreter + Marble Zone's magma).

use crate::graphics::AnimatedScript;
use crate::oscillation::Oscillation;
use crate::{vars, Ram};

pub struct AnimatedTiles;

impl AnimatedTiles {
    /// One frame. Returns true if VRAM changed.
    pub fn step(
        ram: &mut Ram,
        scripts: &[AnimatedScript],
        magma_art: &[u8],
        magma_tile: usize,
        vram: &mut [u8],
    ) -> bool {
        let mut changed = magma(ram, magma_art, magma_tile, vram);
        for (i, script) in scripts.iter().enumerate() {
            let a = vars::ANIM_COUNTERS + (i as u16) * 2;
            let timer = ram.u8(a);
            ram.w8(a, timer.wrapping_sub(1));
            if timer != 0 {
                continue;
            }
            let mut frame = ram.u8(a + 1);
            if frame as usize >= script.frames.len() {
                frame = 0;
            }
            ram.w8(a + 1, frame + 1);
            ram.w8(a, script.frames[frame as usize].1);
            let offset = script.frames[frame as usize].0;
            let src = offset * 32;
            let dst = script.vram_tile * 32;
            let count = script.tiles_per_frame * 32;
            if src + count <= script.art.len() && dst + count <= vram.len() {
                vram[dst..dst + count].copy_from_slice(&script.art[src..src + count]);
                changed = true;
            }
        }
        changed
    }
}

fn magma(ram: &mut Ram, art: &[u8], vram_tile: usize, vram: &mut [u8]) -> bool {
    if art.is_empty() {
        return false;
    }
    let mut timer = ram.u8(vars::ANIM_COUNTERS + 4);
    timer = timer.wrapping_sub(1);
    ram.w8(vars::ANIM_COUNTERS + 4, timer);
    if (timer as i8) >= 0 {
        return false;
    }
    ram.w8(vars::ANIM_COUNTERS + 4, 1);
    let frame = (ram.u8(vars::ANIM_COUNTERS + 1).wrapping_sub(1)) & 3;
    ram.w8(
        vars::ANIM_COUNTERS + 5,
        ram.u8(vars::ANIM_COUNTERS + 5).wrapping_add(1),
    );
    let mut shift = ram.u8(Oscillation::value(2));
    let mut dest = vram_tile * 32;
    for _ in 0..4 {
        let k = ((shift as i32 * 2) & 0x1E) >> 1;
        for row in 0..32 {
            let src = frame as usize * 0x200 + row * 16;
            for i in 0..4 {
                if dest < vram.len() && src + (((k + i) & 15) as usize) < art.len() {
                    vram[dest] = art[src + ((k + i) & 15) as usize];
                }
                dest += 1;
            }
        }
        shift = shift.wrapping_add(4);
    }
    true
}
