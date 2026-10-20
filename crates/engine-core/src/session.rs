//! One frame of a playing act in `World.Step`'s order: input, `Level_frame_counter`, Sonic, Tails, `RunObjects`,
//! `DeformBgLayer`, the rings manager, the synced animation counters, `BuildSprites` (on-screen flags), the object
//! manager, then the VBlank's `Vint_runcount` and level timer.
//!
//! Still no-ops (later milestones): the giant ring art stream, the PLC queue, oscillation, palette cycling, the
//! signpost art, the HUD's VRAM digits, the sprite piece writer/VRAM and sound.
//!
//! A death requests a level restart (`$FE02`); as in `LevelSession` with title cards off, the act is rebuilt from the
//! previous RAM in the same tick, keeping lives/score and the level-start variables.

use crate::animation::{SonicAnimation, TailsAnimation};
use crate::camera::Camera;
use crate::collision::CollisionSets;
use crate::objects::sprites;
use crate::player::Unported;
use crate::rings::sync_animations;
use crate::world::{LevelSource, World};
use crate::{vars, Ram};

pub struct Session {
    level: Box<dyn LevelSource>,
    sets: CollisionSets,
    sonic_animation: SonicAnimation,
    tails_animation: TailsAnimation,
    zone: u8,
    act: u8,
    pub world: World,
    camera: Camera,
    pub camera_x: i32,
    pub camera_y: i32,
    /// Frames stepped (the trace's `tick` before the current call).
    pub ticks: u32,
    /// The legacy `LevelSession.Stopped`: an unported path aborted the frame; later frames do nothing.
    pub stopped: Option<Unported>,
}

impl Session {
    pub fn new(
        level: Box<dyn LevelSource>,
        zone: u8,
        act: u8,
        sets: CollisionSets,
        sonic_animation: SonicAnimation,
        tails_animation: TailsAnimation,
    ) -> Result<Self, Unported> {
        Self::new_with_start(
            level,
            zone,
            act,
            sets,
            sonic_animation,
            tails_animation,
            None,
        )
    }

    /// As [`Session::new`], but with a debug start for the first load only (the legacy `--debug-start`).
    #[allow(clippy::too_many_arguments)]
    pub fn new_with_start(
        level: Box<dyn LevelSource>,
        zone: u8,
        act: u8,
        sets: CollisionSets,
        sonic_animation: SonicAnimation,
        tails_animation: TailsAnimation,
        debug_start: Option<crate::world::Start>,
    ) -> Result<Self, Unported> {
        let world = World::new_at(level.as_ref(), zone, act, None, debug_start)?;
        let camera_x = world.camera_x();
        let camera_y = world.camera_y();
        Ok(Self {
            level,
            sets,
            sonic_animation,
            tails_animation,
            zone,
            act,
            world,
            camera: Camera,
            camera_x,
            camera_y,
            ticks: 0,
            stopped: None,
        })
    }

    /// The act the session is playing (`act` is 1-based, as `LevelSession.CurrentAct`).
    pub fn current_act(&self) -> (u8, u8) {
        (self.zone, self.act)
    }

    /// One frame. `buttons` is the held Mega Drive pad byte (Up 0, Down 1, Left 2, Right 3, B 4, C 5, A 6, Start 7).
    pub fn step(&mut self, buttons: u8) -> Result<(), Unported> {
        // LevelSession.StepPlaying: once an unported exception has stopped the session, every later frame is a
        // no-op and the trace keeps recording the frozen state.
        if self.stopped.is_some() {
            self.ticks += 1;
            return Ok(());
        }
        let result = self.step_frame(buttons);
        if let Err(e) = result {
            self.stopped = Some(e);
        }
        self.ticks += 1;
        Ok(())
    }

    fn step_frame(&mut self, buttons: u8) -> Result<(), Unported> {
        let previous = self.world.ram.u8(vars::CTRL1_HELD);
        self.world.ram.w8(vars::CTRL1_HELD, buttons);
        self.world.ram.w8(vars::CTRL1_PRESS, buttons & !previous);
        let counter = self.world.ram.u16(vars::LEVEL_FRAME_COUNTER);
        self.world
            .ram
            .w16(vars::LEVEL_FRAME_COUNTER, counter.wrapping_add(1));

        let dying = self.world.ram.u8(vars::MAIN_CHARACTER) != 0
            && self.world.sonic.obj.routine(&self.world.ram) >= crate::player::routine::DEAD;
        if self.world.ram.u8(vars::MAIN_CHARACTER) != 0 {
            let water = self.world.water.as_ref();
            self.world.sonic.step(
                &mut self.world.ram,
                &self.sets,
                &self.sonic_animation,
                water,
            )?;
        }
        if self.world.ram.u8(vars::SIDEKICK) == 2 {
            let water = self.world.water.as_ref();
            self.world.tails.step(
                &mut self.world.ram,
                &self.sets,
                &self.tails_animation,
                water,
            )?;
        }
        self.world
            .objects
            .run_after_players(&mut self.world.ram, &self.sets, dying)?;

        if self.world.ram.u16(vars::LEVEL_INACTIVE_FLAG) != 0 {
            // 0x51C4: the Act is rebuilt straight away when title cards are off.
            self.restart()?;
            return Ok(());
        }

        self.camera
            .deform_bg_layer(&mut self.world.ram, &self.world.globals.wave_table)?;
        // StreamGiantRingArt, Plc.Run and SignpostArtLoad are later milestones; oscillation and the palette
        // cycle keep their original place in the frame.
        self.world.rings.run(&mut self.world.ram);
        self.world.oscillation.step(&mut self.world.ram);
        palette_cycle(
            &mut self.world.ram,
            self.world.globals.palette_cycle.as_ref(),
        );
        sync_animations(&mut self.world.ram);
        sprites::build_sprites(&mut self.world.ram);
        self.world.object_manager.run(&mut self.world.ram);

        // VBlank: Vint_runcount, then the HUD (0x81E).
        let vint = self.world.ram.u32(vars::VINT_RUNCOUNT);
        self.world
            .ram
            .w32(vars::VINT_RUNCOUNT, vint.wrapping_add(1));
        Self::hud_timer(&mut self.world.ram)?;
        // Plc.Process and UpdateSound are no-ops.

        self.camera_x = self.world.camera_x();
        self.camera_y = self.world.camera_y();
        Ok(())
    }

    fn restart(&mut self) -> Result<(), Unported> {
        // LevelSession.StepPlaying: a restart adopts the act stored in $FE10/$FE11 first.
        self.zone = self.world.ram.u8(vars::CURRENT_ZONE_AND_ACT);
        self.act = self
            .world
            .ram
            .u8(vars::CURRENT_ZONE_AND_ACT.wrapping_add(1))
            .wrapping_add(1);
        let previous = std::mem::take(&mut self.world.ram);
        self.world = World::new(self.level.as_ref(), self.zone, self.act, Some(previous))?;
        self.camera_x = self.world.camera_x();
        self.camera_y = self.world.camera_y();
        Ok(())
    }

    /// `HudUpdate` (0x4531E)'s RAM side: the redraw flags and the level timer. The digit writes go to VRAM and are
    /// not part of this milestone.
    fn hud_timer(ram: &mut Ram) -> Result<(), Unported> {
        if ram.u16(vars::TWO_PLAYER_MODE) != 0 {
            return Err(Unported::new(0x4551E, "2-player HUD"));
        }
        if ram.u16(vars::DEBUG_MODE_FLAG) != 0 {
            return Err(Unported::new(0x45468, "debug-mode HUD"));
        }
        if ram.u8(vars::UPDATE_HUD_SCORE) != 0 {
            ram.w8(vars::UPDATE_HUD_SCORE, 0);
        }
        if ram.u8(vars::UPDATE_HUD_RINGS) != 0 {
            ram.w8(vars::UPDATE_HUD_RINGS, 0);
        }
        if ram.u8(vars::UPDATE_HUD_TIMER) != 0 && ram.u16(vars::GAME_PAUSED) == 0 {
            advance_timer(ram)?;
        }
        if ram.u8(vars::UPDATE_HUD_LIVES) != 0 {
            ram.w8(vars::UPDATE_HUD_LIVES, 0);
        }
        Ok(())
    }
}

/// `PaletteCycle` (0x1C04 etc.): every `TimerReload+1` frames, copy the next frame's colours.
fn palette_cycle(ram: &mut Ram, cycle: Option<&crate::assets::PaletteCycle>) {
    let Some(cycle) = cycle else {
        return;
    };
    let timer = (ram.u16(vars::PAL_CYCLE_TIMER) as i16).wrapping_sub(1);
    ram.w16(vars::PAL_CYCLE_TIMER, timer as u16);
    if timer >= 0 {
        return;
    }
    ram.w16(vars::PAL_CYCLE_TIMER, cycle.timer_reload as u16);
    let frame = ram.u16(vars::PAL_CYCLE_FRAME);
    ram.w16(vars::PAL_CYCLE_FRAME, frame.wrapping_add(1));
    let Some(colours) = cycle.frames.get((frame & 3) as usize) else {
        return;
    };
    for (colour, value) in cycle.colours.iter().zip(colours) {
        ram.w16(
            vars::NORMAL_PALETTE.wrapping_add((*colour as u16) * 2),
            *value,
        );
    }
}

/// `AdvanceTimer` (0x4538E): increments minute/second/frame counts.
fn advance_timer(ram: &mut Ram) -> Result<(), Unported> {
    if ram.u8(vars::TIMED_MODE) == 0 {
        if ram.u32(vars::TIMER) == 0x0009_3B3B {
            return Err(Unported::new(0x45452, "time over (KillCharacter)"));
        }
        let frame = ram.u8(vars::TIMER_FRAME).wrapping_add(1);
        ram.w8(vars::TIMER_FRAME, frame);
        if frame < 60 {
            return Ok(());
        }
        ram.w8(vars::TIMER_FRAME, 0);
        let second = ram.u8(vars::TIMER_SECOND).wrapping_add(1);
        ram.w8(vars::TIMER_SECOND, second);
        if second < 60 {
            return Ok(());
        }
        ram.w8(vars::TIMER_SECOND, 0);
        let minute = ram.u8(vars::TIMER_MINUTE).wrapping_add(1);
        ram.w8(vars::TIMER_MINUTE, if minute >= 9 { 9 } else { minute });
        return Ok(());
    }
    // Count-down mode (a hack addition): 0x453B8.
    if ram.u32(vars::TIMER) == 0x0000_003B {
        return Err(Unported::new(0x45452, "time over (KillCharacter)"));
    }
    let frame = ram.u8(vars::TIMER_FRAME).wrapping_sub(1);
    ram.w8(vars::TIMER_FRAME, frame);
    if (frame as i8) >= 0 {
        return Ok(());
    }
    ram.w8(vars::TIMER_FRAME, 0x3B);
    let second = ram.u8(vars::TIMER_SECOND).wrapping_sub(1);
    ram.w8(vars::TIMER_SECOND, second);
    if (second as i8) >= 0 {
        return Ok(());
    }
    ram.w8(vars::TIMER_SECOND, 0x3B);
    let minute = ram.u8(vars::TIMER_MINUTE).wrapping_sub(1);
    ram.w8(
        vars::TIMER_MINUTE,
        if (minute as i8) < 0 { 0 } else { minute },
    );
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::animation::{SonicAnimation, TailsAnimation};
    use crate::collision::{CollisionSet, CollisionSets};
    use crate::player::routine;
    use crate::world::{Bounds, LevelSource, Start};

    struct Fixture;

    impl LevelSource for Fixture {
        fn fill_level_ram(&self, ram: &mut Ram, zone: u8, act: u8) {
            ram.w16(
                vars::CURRENT_ZONE_AND_ACT,
                ((zone as u16) << 8) | act.wrapping_sub(1) as u16,
            );
            ram.w8(vars::HACK_ZONE_FLAG, 1);
        }
        fn bounds(&self) -> Bounds {
            Bounds {
                min_x: 0,
                max_x: 0x400,
                min_y: 0,
                max_y: 0xD0,
            }
        }
        fn start(&self) -> Start {
            Start { x: 0x20, y: 0x18C }
        }
    }

    fn flat(name: &str) -> CollisionSet {
        CollisionSet {
            name: name.into(),
            angles: vec![0; 256],
            heights: vec![0; 256 * 16],
            widths: vec![0; 256 * 16],
        }
    }

    fn session() -> Session {
        let sets = CollisionSets {
            normal: flat("normal"),
            alternate: flat("alternate"),
        };
        Session::new(
            Box::new(Fixture),
            0x40,
            1,
            sets,
            SonicAnimation::default(),
            TailsAnimation::default(),
        )
        .expect("world setup")
    }

    #[test]
    fn a_restart_request_rebuilds_the_act_and_keeps_lives() {
        let mut s = session();
        s.world.ram.w8(vars::LIFE_COUNT, 2);
        s.world.ram.w32(vars::TIMER, 0x1234);
        s.world.ram.w16(vars::LEVEL_INACTIVE_FLAG, 1);
        s.step(0).expect("restarting frame");
        assert_eq!(s.world.ram.u8(vars::LIFE_COUNT), 2, "lives carry over");
        assert_eq!(s.world.ram.u32(vars::TIMER), 0, "the timer restarts");
        assert_eq!(s.world.ram.u16(vars::LEVEL_FRAME_COUNTER), 0);
        assert_eq!(s.world.ram.u16(vars::LEVEL_INACTIVE_FLAG), 0);
        assert_eq!(s.current_act(), (0x40, 1));
        assert_eq!(s.camera_x, 0);
        assert_eq!(s.world.sonic.obj.routine(&s.world.ram), routine::INIT);

        // The next frame is the new world's first step.
        s.step(0).expect("first frame of the restarted act");
        assert_eq!(s.world.ram.u16(vars::LEVEL_FRAME_COUNTER), 1);
        assert_eq!(s.world.sonic.obj.routine(&s.world.ram), routine::CONTROL);
    }

    #[test]
    fn the_level_timer_advances_and_rolls_into_seconds() {
        let mut s = session();
        for _ in 0..60 {
            s.step(0).expect("playing frame");
        }
        assert_eq!(s.world.ram.u32(vars::TIMER), 0x0100); // second 1, frame 0
        assert_eq!(s.world.ram.u32(vars::VINT_RUNCOUNT), 60);
    }
}
