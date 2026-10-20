//! `Sonic_Animate` (ROM `0x1F138`) and the parts of it that feed physics.
//!
//! Animation is not just cosmetic here: a script change clears the player's pushing bit and the walk/run, roll
//! and push animations read `inertia` (and write `anim_frame_duration`, which the next frame decrements), so the
//! animation state is part of the deterministic tick. Scripts are windows into the raw animation block, exactly as
//! the legacy `SonicAnimation.Script` is, so an index that runs past a script reads the following bytes.

use crate::{vars, Obj, Ram};

/// A player's animation (Sonic's or Tails' own interpreter); both live in RAM like the physics.
pub trait PlayerAnimation {
    fn animate(&self, ram: &mut Ram, o: Obj);
}

/// Sonic's extracted animation block and the two script tables (`objects/sonic/animations.json`).
#[derive(Clone, Debug, Default)]
pub struct SonicAnimation {
    block: Vec<u8>,
    normal: Vec<usize>,
    super_: Vec<usize>,
}

const WALK: u8 = 0;
const RUN: u8 = 1;
const ROLL: u8 = 2;
const ROLL2: u8 = 3;
const PUSH: u8 = 4;

impl SonicAnimation {
    /// Build from the raw block and the two table offsets the extractor records.
    pub fn from_block(block: Vec<u8>, normal_table: usize, super_table: usize) -> Self {
        let normal = table(&block, normal_table);
        let super_ = table(&block, super_table);
        Self {
            block,
            normal,
            super_,
        }
    }

    /// A script as a window into the block (the legacy `Script` struct): `None` past the block's end.
    fn byte(&self, script: usize, index: usize) -> u8 {
        self.block.get(script + index).copied().unwrap_or(0)
    }

    /// `Sonic_Animate` (0x1F138) for one frame.
    pub fn animate(&self, ram: &mut Ram, o: Obj) {
        let is_super = ram.u8(vars::SUPER_SONIC_FLAG) != 0;
        let table = if is_super { &self.super_ } else { &self.normal };
        let anim = o.anim(ram);
        if anim != o.prev_anim(ram) {
            o.set_prev_anim(ram, anim);
            o.set_anim_frame(ram, 0);
            o.set_anim_frame_duration(ram, 0);
            o.set_pushing(ram, false);
        }
        let Some(&script) = table.get(anim as usize) else {
            return;
        };
        let duration = self.byte(script, 0);
        if (duration as i8) >= 0 {
            set_flip_from_status(ram, o);
            let next = o.anim_frame_duration(ram).wrapping_sub(1);
            o.set_anim_frame_duration(ram, next);
            if (next as i8) >= 0 {
                return;
            }
            o.set_anim_frame_duration(ram, duration);
            self.step(ram, o, script);
            return;
        }
        if duration == 0xFF {
            self.walk_run(ram, o, table, is_super);
            return;
        }

        // $FE (roll) and $FD (push) share a first timer decrement (0x1F36E).
        let next = o.anim_frame_duration(ram).wrapping_sub(1);
        o.set_anim_frame_duration(ram, next);
        if (next as i8) >= 0 {
            return;
        }
        if duration == 0xFE {
            self.roll_anim(ram, o, table);
        } else {
            self.push_anim(ram, o, table);
        }
    }

    /// `Anim_Do` (0x1F192): show the next frame or execute a command byte.
    fn step(&self, ram: &mut Ram, o: Obj, script: usize) {
        let d1 = o.anim_frame(ram);
        let d0 = self.byte(script, 1 + d1 as usize);
        if d0 < 0xF0 {
            show(ram, o, d0);
            return;
        }
        match d0 {
            0xFF => {
                o.set_anim_frame(ram, 0);
                show(ram, o, self.byte(script, 1));
            }
            0xFE => {
                let back = self.byte(script, 2 + d1 as usize);
                let frame = d1.wrapping_sub(back);
                o.set_anim_frame(ram, frame);
                show(ram, o, self.byte(script, 1 + frame as usize));
            }
            0xFD => o.set_anim(ram, self.byte(script, 2 + d1 as usize)),
            _ => {}
        }
    }

    /// Walk/run (0x1F1E2): frame set chosen by ground angle, speed by inertia.
    fn walk_run(&self, ram: &mut Ram, o: Obj, table: &[usize], is_super: bool) {
        if o.flip_angle(ram) != 0 {
            flip_anim(ram, o);
            return;
        }
        let mut d1 = 0;
        let mut d0 = o.angle(ram);
        if (d0 as i8) > 0 {
            d0 = d0.wrapping_sub(1);
        }
        let d2 = o.status(ram) & 1;
        if d2 == 0 {
            d0 = !d0;
        }
        d0 = d0.wrapping_add(0x10);
        if (d0 as i8) < 0 {
            d1 = 3;
        }
        o.set_render_flags(ram, (o.render_flags(ram) & !3) | (d2 ^ d1));
        if o.pushing(ram) {
            self.push_anim_from_walk(ram, o, table);
            return;
        }
        d0 = (d0 >> 4) & 6;
        let mut speed = ((o.inertia(ram) as i32).unsigned_abs() & 0xFFFF) as i32;
        if (o.status_secondary(ram) as i8) < 0 {
            speed = (speed * 2) & 0xFFFF;
        }
        if is_super {
            // 0x1F296: Super Sonic has his own scripts; not part of this milestone.
            return;
        }

        let mut script = *table.get(RUN as usize).unwrap_or(&0);
        if speed < 0x600 {
            script = *table.get(WALK as usize).unwrap_or(&0);
            d0 *= 2;
        }
        d0 *= 2;
        let d3 = d0;
        let index = o.anim_frame(ram);
        let mut frame = self.byte(script, 1 + index as usize);
        if frame == 0xFF {
            o.set_anim_frame(ram, 0);
            frame = self.byte(script, 1);
        }
        o.set_mapping_frame(ram, frame.wrapping_add(d3));
        let next = o.anim_frame_duration(ram).wrapping_sub(1);
        o.set_anim_frame_duration(ram, next);
        if (next as i8) >= 0 {
            return;
        }
        let mut dur = -speed + 0x800;
        if (dur as i16) < 0 {
            dur = 0;
        }
        o.set_anim_frame_duration(ram, ((dur & 0xFFFF) >> 8) as u8);
        o.set_anim_frame(ram, o.anim_frame(ram).wrapping_add(1));
    }

    /// Roll (0x1F37A): faster script above `$600`, frame time from inertia.
    fn roll_anim(&self, ram: &mut Ram, o: Obj, table: &[usize]) {
        let speed = ((o.inertia(ram) as i32).unsigned_abs() & 0xFFFF) as i32;
        let script = if speed >= 0x600 {
            *table.get(ROLL2 as usize).unwrap_or(&0)
        } else {
            *table.get(ROLL as usize).unwrap_or(&0)
        };
        // The legacy throws for Tails' roll (0x1F39A); this milestone animates Sonic only.
        if o.id(ram) != 1 {
            return;
        }
        let mut dur = -speed + 0x400;
        if (dur as i16) < 0 {
            dur = 0;
        }
        o.set_anim_frame_duration(ram, ((dur & 0xFFFF) >> 8) as u8);
        set_flip_from_status(ram, o);
        self.step(ram, o, script);
    }

    /// Push (0x1F3D4), reached directly from `Animate` after one timer decrement.
    fn push_anim(&self, ram: &mut Ram, o: Obj, table: &[usize]) {
        let next = o.anim_frame_duration(ram).wrapping_sub(1);
        o.set_anim_frame_duration(ram, next);
        if (next as i8) >= 0 {
            return;
        }
        self.push_body(ram, o, *table.get(PUSH as usize).unwrap_or(&0));
    }

    /// Push reached from the walk code (status bit 5): its single timer decrement.
    fn push_anim_from_walk(&self, ram: &mut Ram, o: Obj, table: &[usize]) {
        let next = o.anim_frame_duration(ram).wrapping_sub(1);
        o.set_anim_frame_duration(ram, next);
        if (next as i8) >= 0 {
            return;
        }
        self.push_body(ram, o, *table.get(PUSH as usize).unwrap_or(&0));
    }

    fn push_body(&self, ram: &mut Ram, o: Obj, script: usize) {
        let mut d2 = o.inertia(ram) as i32;
        if d2 >= 0 {
            d2 = -d2;
        }
        d2 += 0x800;
        if (d2 as i16) < 0 {
            d2 = 0;
        }
        o.set_anim_frame_duration(ram, ((d2 & 0xFFFF) >> 6) as u8);
        set_flip_from_status(ram, o);
        self.step(ram, o, script);
    }
}

impl PlayerAnimation for SonicAnimation {
    fn animate(&self, ram: &mut Ram, o: Obj) {
        SonicAnimation::animate(self, ram, o);
    }
}

/// Tails' animation (TailsAnimation.cs, objects/tails/animations.json): the same script format with his own
/// three speed sets for walk/run/fastest, roll and push.
#[derive(Clone, Debug, Default)]
pub struct TailsAnimation {
    block: Vec<u8>,
    table: usize,
    walk: usize,
    run: usize,
    fastest: usize,
    roll: usize,
    roll2: usize,
    push: usize,
    tails_block: Vec<u8>,
    tails_table: usize,
    tails_directional: usize,
}

impl TailsAnimation {
    #[allow(clippy::too_many_arguments)] // one JSON field per script-table offset
    pub fn from_block(
        block: Vec<u8>,
        table: usize,
        walk: usize,
        run: usize,
        fastest: usize,
        roll: usize,
        roll2: usize,
        push: usize,
    ) -> Self {
        Self {
            block,
            table,
            walk,
            run,
            fastest,
            roll,
            roll2,
            push,
            tails_block: Vec::new(),
            tails_table: 0,
            tails_directional: 0,
        }
    }

    /// The tails object's own animation block (objects/tails-tails/animations.json).
    pub fn set_tails_animation(&mut self, block: Vec<u8>, table: usize, directional: usize) {
        self.tails_block = block;
        self.tails_table = table;
        self.tails_directional = directional;
    }

    pub fn tails_animation_map(&self, parent_anim: u8) -> u8 {
        self.tails_block
            .get(parent_anim as usize)
            .copied()
            .unwrap_or(0)
    }

    pub fn run_tails(&self, ram: &mut Ram, o: Obj, parent: Obj) {
        let block = &self.tails_block;
        if block.is_empty() {
            return;
        }
        let anim = o.anim(ram);
        if anim != o.prev_anim(ram) {
            o.set_prev_anim(ram, anim);
            o.set_anim_frame(ram, 0);
            o.set_anim_frame_duration(ram, 0);
            o.set_pushing(ram, false);
        }
        let raw = ((block[self.tails_table + anim as usize * 2] as u16) << 8)
            | block[self.tails_table + anim as usize * 2 + 1] as u16;
        let script = (self.tails_table as isize + (raw as i16) as isize).max(0) as usize;
        let d0 = block[script];
        if (d0 as i8) >= 0 {
            o.set_render_flags(ram, (o.render_flags(ram) & !3) | (o.status(ram) & 1));
            let d = o.anim_frame_duration(ram).wrapping_sub(1);
            o.set_anim_frame_duration(ram, d);
            if (d as i8) >= 0 {
                return;
            }
            o.set_anim_frame_duration(ram, d0);
            self.step_block(ram, o, block, script);
            return;
        }
        let d = o.anim_frame_duration(ram).wrapping_sub(1);
        o.set_anim_frame_duration(ram, d);
        if (d as i8) >= 0 {
            return;
        }
        match d0 {
            0xFF => self.walk_run_block(ram, o),
            0xFE => self.roll_block(ram, o),
            _ => self.directional(ram, o, parent),
        }
    }

    fn step_block(&self, ram: &mut Ram, o: Obj, block: &[u8], script: usize) {
        let d1 = o.anim_frame(ram);
        let d0 = block[script + 1 + d1 as usize];
        if d0 < 0xF0 {
            show(ram, o, d0);
            return;
        }
        match d0 {
            0xFF => {
                o.set_anim_frame(ram, 0);
                show(ram, o, block[script + 1]);
            }
            0xFE => {
                let back = block[script + 2 + d1 as usize];
                let frame = d1.wrapping_sub(back);
                o.set_anim_frame(ram, frame);
                show(ram, o, block[script + 1 + frame as usize]);
            }
            0xFD => o.set_anim(ram, block[script + 2 + d1 as usize]),
            _ => {}
        }
    }

    fn walk_run_block(&self, ram: &mut Ram, o: Obj) {
        if o.flip_angle(ram) != 0 {
            flip(ram, o, 0x75);
            return;
        }
        let mut d1 = 0;
        let mut d0 = o.angle(ram);
        if (d0 as i8) > 0 {
            d0 = d0.wrapping_sub(1);
        }
        let d2 = o.status(ram) & 1;
        if d2 == 0 {
            d0 = !d0;
        }
        d0 = d0.wrapping_add(0x10);
        if (d0 as i8) < 0 {
            d1 = 3;
        }
        o.set_render_flags(ram, (o.render_flags(ram) & !3) | (d2 ^ d1));
        if o.pushing(ram) {
            let mut d2 = o.inertia(ram) as i32;
            if d2 >= 0 {
                d2 = -d2;
            }
            d2 += 0x800;
            if (d2 as i16) < 0 {
                d2 = 0;
            }
            o.set_anim_frame_duration(ram, ((d2 & 0xFFFF) >> 6) as u8);
            o.set_render_flags(ram, (o.render_flags(ram) & !3) | (o.status(ram) & 1));
            self.step_block(ram, o, &self.tails_block, self.push);
            return;
        }
        d0 = (d0 >> 4) & 6;
        let mut speed = ((o.inertia(ram) as i32).unsigned_abs() & 0xFFFF) as i32;
        if (o.status_secondary(ram) as i8) < 0 {
            speed = (speed * 2) & 0xFFFF;
        }
        let mut d3 = (d0 as u32 * 4) as u8;
        let mut script = self.walk;
        if speed >= 0x600 {
            script = self.run;
            let half = d0 >> 1;
            d0 = d0.wrapping_add(half).wrapping_mul(2);
            d3 = d0;
            if speed >= 0x700 {
                script = self.fastest;
            }
        }
        let mut dur = -speed + 0x800;
        if (dur as i16) < 0 {
            dur = 0;
        }
        o.set_anim_frame_duration(ram, ((dur & 0xFFFF) >> 8) as u8);
        self.step_block(ram, o, &self.tails_block, script);
        o.set_mapping_frame(ram, o.mapping_frame(ram).wrapping_add(d3));
    }

    fn roll_block(&self, ram: &mut Ram, o: Obj) {
        let speed = ((o.inertia(ram) as i32).unsigned_abs() & 0xFFFF) as i32;
        let script = if speed >= 0x600 {
            self.roll2
        } else {
            self.roll
        };
        let mut dur = -speed + 0x400;
        if (dur as i16) < 0 {
            dur = 0;
        }
        o.set_anim_frame_duration(ram, ((dur & 0xFFFF) >> 8) as u8);
        o.set_render_flags(ram, (o.render_flags(ram) & !3) | (o.status(ram) & 1));
        self.step_block(ram, o, &self.tails_block, script);
    }

    fn directional(&self, ram: &mut Ram, o: Obj, parent: Obj) {
        let mut d0 = crate::math::calc_angle(parent.x_vel(ram), parent.y_vel(ram));
        if o.id(ram) != 5 {
            d0 = d0.wrapping_add(ram.u8(vars::OBJECT_RAM_BLOCK_INDICES));
        }
        let mut d1 = 0;
        let d2 = o.status(ram) & 1;
        d0 = if d2 == 0 { !d0 } else { d0.wrapping_add(0x80) };
        d0 = d0.wrapping_add(0x10);
        if (d0 as i8) < 0 {
            d1 = 3;
        }
        o.set_render_flags(ram, (o.render_flags(ram) & !3) | (d2 ^ d1));
        let d3 = (d0 >> 3) & 0x0C;
        o.set_anim_frame_duration(ram, 3);
        self.step_block(ram, o, &self.tails_block, self.tails_directional);
        o.set_mapping_frame(ram, o.mapping_frame(ram).wrapping_add(d3));
    }

    fn byte(&self, script: usize, index: usize) -> u8 {
        self.block.get(script + index).copied().unwrap_or(0)
    }

    fn script(&self, anim: u8) -> usize {
        let raw = ((self.byte(self.table, anim as usize * 2) as u16) << 8)
            | self.byte(self.table, anim as usize * 2 + 1) as u16;
        (self.table as isize + (raw as i16) as isize).max(0) as usize
    }

    fn step(&self, ram: &mut Ram, o: Obj, script: usize) {
        let d1 = o.anim_frame(ram);
        let d0 = self.byte(script, 1 + d1 as usize);
        if d0 < 0xF0 {
            show(ram, o, d0);
            return;
        }
        match d0 {
            0xFF => {
                o.set_anim_frame(ram, 0);
                show(ram, o, self.byte(script, 1));
            }
            0xFE => {
                let back = self.byte(script, 2 + d1 as usize);
                let frame = d1.wrapping_sub(back);
                o.set_anim_frame(ram, frame);
                show(ram, o, self.byte(script, 1 + frame as usize));
            }
            0xFD => o.set_anim(ram, self.byte(script, 2 + d1 as usize)),
            _ => {}
        }
    }

    fn walk_run(&self, ram: &mut Ram, o: Obj) {
        if o.flip_angle(ram) != 0 {
            flip(ram, o, 0x75);
            return;
        }
        let mut d1 = 0;
        let mut d0 = o.angle(ram);
        if (d0 as i8) > 0 {
            d0 = d0.wrapping_sub(1);
        }
        let d2 = o.status(ram) & 1;
        if d2 == 0 {
            d0 = !d0;
        }
        d0 = d0.wrapping_add(0x10);
        if (d0 as i8) < 0 {
            d1 = 3;
        }
        o.set_render_flags(ram, (o.render_flags(ram) & !3) | (d2 ^ d1));
        if o.pushing(ram) {
            push_body(ram, o, self.push, self);
            return;
        }
        d0 = (d0 >> 4) & 6;
        let mut speed = ((o.inertia(ram) as i32).unsigned_abs() & 0xFFFF) as i32;
        if (o.status_secondary(ram) as i8) < 0 {
            speed = (speed * 2) & 0xFFFF;
        }
        let mut d3 = (d0 as u32 * 4) as u8;
        let mut script = self.walk;
        if speed >= 0x600 {
            script = self.run;
            let half = d0 >> 1;
            d0 = d0.wrapping_add(half).wrapping_mul(2);
            d3 = d0;
            if speed >= 0x700 {
                script = self.fastest;
            }
        }
        let mut dur = -speed + 0x800;
        if (dur as i16) < 0 {
            dur = 0;
        }
        o.set_anim_frame_duration(ram, ((dur & 0xFFFF) >> 8) as u8);
        self.step(ram, o, script);
        o.set_mapping_frame(ram, o.mapping_frame(ram).wrapping_add(d3));
    }

    fn roll(&self, ram: &mut Ram, o: Obj) {
        let speed = ((o.inertia(ram) as i32).unsigned_abs() & 0xFFFF) as i32;
        let script = if speed >= 0x600 {
            self.roll2
        } else {
            self.roll
        };
        let mut dur = -speed + 0x400;
        if (dur as i16) < 0 {
            dur = 0;
        }
        o.set_anim_frame_duration(ram, ((dur & 0xFFFF) >> 8) as u8);
        set_flip_from_status(ram, o);
        self.step(ram, o, script);
    }
}

impl PlayerAnimation for TailsAnimation {
    fn animate(&self, ram: &mut Ram, o: Obj) {
        TailsAnimation::run(self, ram, o);
    }
}

impl TailsAnimation {
    /// Tails_Animate's interpreter (0x20C2A). The tails object's directional scripts are a display-only path we do
    /// not run (object  is inert), so a negative non-walk/roll byte leaves the frame alone.
    fn run(&self, ram: &mut Ram, o: Obj) {
        let anim = o.anim(ram);
        if anim != o.prev_anim(ram) {
            o.set_prev_anim(ram, anim);
            o.set_anim_frame(ram, 0);
            o.set_anim_frame_duration(ram, 0);
            o.set_pushing(ram, false);
        }
        let script = self.script(anim);
        let d0 = self.byte(script, 0);
        if (d0 as i8) >= 0 {
            set_flip_from_status(ram, o);
            let next = o.anim_frame_duration(ram).wrapping_sub(1);
            o.set_anim_frame_duration(ram, next);
            if (next as i8) >= 0 {
                return;
            }
            o.set_anim_frame_duration(ram, d0);
            self.step(ram, o, script);
            return;
        }
        let next = o.anim_frame_duration(ram).wrapping_sub(1);
        o.set_anim_frame_duration(ram, next);
        if (next as i8) >= 0 {
            return;
        }
        match d0 {
            0xFF => self.walk_run(ram, o),
            0xFE => self.roll(ram, o),
            _ => {}
        }
    }
}

fn push_body(ram: &mut Ram, o: Obj, script: usize, anim: &TailsAnimation) {
    let mut d2 = o.inertia(ram) as i32;
    if d2 >= 0 {
        d2 = -d2;
    }
    d2 += 0x800;
    if (d2 as i16) < 0 {
        d2 = 0;
    }
    o.set_anim_frame_duration(ram, ((d2 & 0xFFFF) >> 6) as u8);
    set_flip_from_status(ram, o);
    anim.step(ram, o, script);
}

fn flip(ram: &mut Ram, o: Obj, base: u8) {
    let mut d0 = o.flip_angle(ram);
    if (o.status(ram) & 1) == 0 {
        o.set_render_flags(ram, o.render_flags(ram) & 0xFC);
        d0 = d0.wrapping_add(0x0B);
    } else {
        o.set_render_flags(ram, o.render_flags(ram) & 0xFC);
        if o.flip_turned(ram) != 0 {
            o.set_render_flags(ram, o.render_flags(ram) | 1);
            d0 = d0.wrapping_add(0x0B);
        } else {
            o.set_render_flags(ram, o.render_flags(ram) | 3);
            d0 = 0x8Fu8.wrapping_sub(d0);
        }
    }
    o.set_mapping_frame(ram, d0 / 0x16 + base);
    o.set_anim_frame_duration(ram, 0);
}

/// One script-table offset per animation (offsets are signed, relative to the table start).
fn table(block: &[u8], offset: usize) -> Vec<usize> {
    let read = |i: usize| block.get(i).copied().unwrap_or(0) as usize;
    let count = ((read(offset) << 8) | read(offset + 1)) / 2;
    (0..count)
        .map(|i| {
            let raw = ((read(offset + i * 2) << 8) | read(offset + i * 2 + 1)) as u16;
            (offset as isize + (raw as i16) as isize).max(0) as usize
        })
        .collect()
}

fn set_flip_from_status(ram: &mut Ram, o: Obj) {
    let flags = (o.render_flags(ram) & !3) | (o.status(ram) & 1);
    o.set_render_flags(ram, flags);
}

fn show(ram: &mut Ram, o: Obj, frame: u8) {
    o.set_mapping_frame(ram, frame);
    o.set_anim_frame(ram, o.anim_frame(ram).wrapping_add(1));
}

/// 0x1F308: corkscrew/flip frames from the flip angle.
fn flip_anim(ram: &mut Ram, o: Obj) {
    let mut d0 = o.flip_angle(ram);
    if (o.status(ram) & 1) == 0 {
        o.set_render_flags(ram, o.render_flags(ram) & 0xFC);
        d0 = d0.wrapping_add(0x0B);
    } else {
        o.set_render_flags(ram, o.render_flags(ram) & 0xFC);
        if o.flip_turned(ram) != 0 {
            o.set_render_flags(ram, o.render_flags(ram) | 1);
            d0 = d0.wrapping_add(0x0B);
        } else {
            o.set_render_flags(ram, o.render_flags(ram) | 3);
            d0 = 0x8Fu8.wrapping_sub(d0);
        }
    }
    o.set_mapping_frame(ram, d0 / 0x16 + 0x5F);
    o.set_anim_frame_duration(ram, 0);
}

#[cfg(test)]
mod tests {
    use super::*;

    fn obj(ram: &mut Ram) -> Obj {
        let o = Obj::new(vars::MAIN_CHARACTER);
        o.set_id(ram, 1);
        o
    }

    /// Six script entries (the count word doubles as entry 0's offset): walk at $0C, wait at $16.
    fn block() -> Vec<u8> {
        let mut block = vec![0u8; 32];
        block[0..2].copy_from_slice(&[0, 12]); // 6 entries
        block[3] = 14; // entries 1..4 (unused)
        block[5] = 16;
        block[7] = 16;
        block[9] = 16;
        block[11] = 22; // entry 5: wait
        block[12..15].copy_from_slice(&[0xFF, 15, 0xFF]); // walk
        block[22..26].copy_from_slice(&[5, 1, 8, 0xFF]); // wait
        block
    }

    #[test]
    fn wait_script_shows_its_first_frame_and_counts_down() {
        let anim = SonicAnimation::from_block(block(), 0, 0);
        let mut ram = Ram::new();
        let o = obj(&mut ram);
        o.set_anim(&mut ram, 5);
        anim.animate(&mut ram, o);
        assert_eq!(o.mapping_frame(&ram), 1);
        assert_eq!(o.anim_frame(&ram), 1);
        assert_eq!(o.anim_frame_duration(&ram), 5);
        for _ in 0..6 {
            anim.animate(&mut ram, o);
        }
        assert_eq!(o.mapping_frame(&ram), 8);
        assert_eq!(o.anim_frame(&ram), 2, "the timer expires after 6 frames");
    }

    #[test]
    fn changing_animation_clears_pushing() {
        let anim = SonicAnimation::from_block(block(), 0, 0);
        let mut ram = Ram::new();
        let o = obj(&mut ram);
        o.set_anim(&mut ram, 5);
        o.set_pushing(&mut ram, true);
        anim.animate(&mut ram, o);
        assert!(!o.pushing(&ram));
    }
}
