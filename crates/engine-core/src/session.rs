//! One frame of a playing act, in `World.Step`'s order for the parts that affect Sonic and the camera:
//! input, `Level_frame_counter`, `Sonic.Step`, `DeformBgLayer`, the VBlank's `Vint_runcount` and the level timer.
//!
//! `RunObjects`, the giant ring art stream, the rings manager, the PLC queue, oscillation, palette cycling, synced
//! animations, the signpost, sprite building, the object manager, the HUD's VRAM writes and sound are later
//! milestones (M4 onward) and are explicit no-ops here.
//!
//! A death requests a level restart (`$FE02`); as in `LevelSession` with title cards off, the act is rebuilt from the
//! previous RAM in the same tick, keeping lives/score and the level-start variables.

use crate::animation::SonicAnimation;
use crate::camera::Camera;
use crate::collision::CollisionSets;
use crate::player::Unported;
use crate::world::{LevelSource, World};
use crate::{vars, Ram};

pub struct Session {
    level: Box<dyn LevelSource>,
    sets: CollisionSets,
    animation: SonicAnimation,
    zone: u8,
    act: u8,
    pub world: World,
    camera: Camera,
    pub camera_x: i32,
    pub camera_y: i32,
    /// Frames stepped (the trace's `tick` before the current call).
    pub ticks: u32,
}

impl Session {
    pub fn new(
        level: Box<dyn LevelSource>,
        zone: u8,
        act: u8,
        sets: CollisionSets,
        animation: SonicAnimation,
    ) -> Result<Self, Unported> {
        let world = World::new(level.as_ref(), zone, act, None)?;
        let camera_x = world.camera_x();
        let camera_y = world.camera_y();
        Ok(Self {
            level,
            sets,
            animation,
            zone,
            act,
            world,
            camera: Camera,
            camera_x,
            camera_y,
            ticks: 0,
        })
    }

    /// The act the session is playing (`act` is 1-based, as `LevelSession.CurrentAct`).
    pub fn current_act(&self) -> (u8, u8) {
        (self.zone, self.act)
    }

    /// One frame. `buttons` is the held Mega Drive pad byte (Up 0, Down 1, Left 2, Right 3, B 4, C 5, A 6, Start 7).
    pub fn step(&mut self, buttons: u8) -> Result<(), Unported> {
        let previous = self.world.ram.u8(vars::CTRL1_HELD);
        self.world.ram.w8(vars::CTRL1_HELD, buttons);
        self.world.ram.w8(vars::CTRL1_PRESS, buttons & !previous);
        let counter = self.world.ram.u16(vars::LEVEL_FRAME_COUNTER);
        self.world
            .ram
            .w16(vars::LEVEL_FRAME_COUNTER, counter.wrapping_add(1));

        if self.world.ram.u8(vars::MAIN_CHARACTER) != 0 {
            let water = self.world.water.as_ref();
            self.world
                .sonic
                .step(&mut self.world.ram, &self.sets, &self.animation, water)?;
        }
        // Objects.RunAfterPlayers (0x51A6) is M4.

        if self.world.ram.u16(vars::LEVEL_INACTIVE_FLAG) != 0 {
            // 0x51C4: the Act is rebuilt straight away when title cards are off.
            self.restart()?;
            self.ticks += 1;
            return Ok(());
        }

        self.camera.deform_bg_layer(&mut self.world.ram)?;
        // StreamGiantRingArt, Rings.Run, Plc.Run, Oscillation.Step, PaletteCycle, SyncAnimations.Step,
        // SignpostArtLoad, Sprites.BuildSprites, ObjectManager.Run: no-ops (M4+).

        // VBlank: Vint_runcount, then the HUD (0x81E).
        let vint = self.world.ram.u32(vars::VINT_RUNCOUNT);
        self.world
            .ram
            .w32(vars::VINT_RUNCOUNT, vint.wrapping_add(1));
        Self::hud_timer(&mut self.world.ram)?;
        // Plc.Process and UpdateSound are no-ops.

        self.camera_x = self.world.camera_x();
        self.camera_y = self.world.camera_y();
        self.ticks += 1;
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
    use crate::animation::SonicAnimation;
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
        Session::new(Box::new(Fixture), 0x40, 1, sets, SonicAnimation::default())
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
