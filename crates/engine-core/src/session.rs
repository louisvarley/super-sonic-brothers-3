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
use crate::background_plane::BackgroundPlane;
use crate::camera::Camera;
use crate::collision::CollisionSets;
use crate::graphics::Graphics;
use crate::player::Unported;
use crate::plc::PlcQueue;
use crate::rings::sync_animations;
use crate::vdp::Vdp;
use crate::world::{LevelSource, World};
use crate::{vars, Obj, Ram};

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
    /// Graphics data and the mutable VRAM image used by the software VDP.
    graphics: Graphics,
    vram: Vec<u8>,
    background: BackgroundPlane,
    plc: PlcQueue,
    sonic_dplc: u8,
    tails_dplc: u8,
    tails_tails_dplc: u8,
    game_over_plc_loaded: bool,
    pub vdp: Vdp,
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
        let graphics = level.graphics();
        let mut world = world;
        world.objects.tails_animation = Some(tails_animation.clone());
        world.objects.dust_sprites = graphics.bank.table(0x21DDE).cloned();
        if let Some(d) = graphics.bank.table(0x15C7A) {
            world.objects.monitor_animations = d.animations.clone();
            world.objects.monitor_frame_offsets = d.frame_offsets.clone().unwrap_or_default();
        }
        if let Some(d) = graphics.bank.table(0x151A58) {
            world.objects.motobug_animations = d.animations.clone();
        }
        if let Some(d) = graphics.bank.table(0x152314) {
            world.objects.chopper_animations = d.animations.clone();
        }
        if let Some(d) = graphics.bank.table(0x3BF94) {
            world.objects.coconuts_animations = d.animations.clone();
        }
        if let Some(d) = graphics.bank.table(0x151ED4) {
            world.objects.missile_animations = d.animations.clone();
        }
        if let Some(d) = graphics.bank.table(0x316A6) {
            world.objects.masher_animations = d.animations.clone();
        }
        if let Some(d) = graphics.bank.table(0x3154E) {
            world.objects.buzzer_animations = d.animations.clone();
        }
        if let Some(d) = graphics.bank.table(0x152C4) {
            world.objects.lost_rings_animations = d.animations.clone();
        }
        world.objects.animal_data = Some(graphics.animals.clone());
        if let Some(d) = graphics.bank.table(0x151C88) {
            world.objects.buzz_animations = d.animations.clone();
        }
        let mut vram = graphics.vram.clone();
        vram.resize(0x10000, 0);
        let mut background = BackgroundPlane::new();
        background.draw_initial(&world.ram, &graphics.geometry);
        crate::hud::base(&mut world.ram, &mut vram, &graphics.hud);
        let plc = PlcQueue::new(&graphics.plc_lists);
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
            graphics,
            vram,
            background,
            plc,
            sonic_dplc: 0xFF,
            tails_dplc: 0xFF,
            tails_tails_dplc: 0xFF,
            game_over_plc_loaded: false,
            vdp: Vdp::new(),
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
        crate::lava_line::step(&mut self.world.ram);

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
        self.update_player_vram();
        if !self.game_over_plc_loaded
            && Obj::new(vars::TITLE_CARD_ZONE_NAME).id(&self.world.ram) == 0x39
        {
            self.plc.load(
                &mut self.world.ram,
                &mut self.vram,
                &self.graphics.plc_lists,
                3,
            );
            self.game_over_plc_loaded = true;
        }
        self.world
            .objects
            .run_after_players(&mut self.world.ram, &self.sets, dying)?;
        self.update_tails_tails_vram();
        self.update_dust_vram();

        if self.world.ram.u16(vars::LEVEL_INACTIVE_FLAG) != 0 {
            // 0x51C4: the Act is rebuilt straight away when title cards are off.
            self.restart()?;
            return Ok(());
        }

        self.camera
            .deform_bg_layer(&mut self.world.ram, &self.world.globals.wave_table)?;
        self.world.rings.run(&mut self.world.ram);
        self.plc.run(&mut self.world.ram);
        self.world.oscillation.step(&mut self.world.ram);
        palette_cycle(
            &mut self.world.ram,
            self.world.globals.palette_cycle.as_ref(),
        );
        sync_animations(&mut self.world.ram);
        crate::sprite_builder::build_sprites(
            &mut self.world.ram,
            &self.graphics.bank,
            &self.graphics.hud,
            &self.graphics.rings,
        )?;
        self.world.object_manager.run(&mut self.world.ram);

        // VBlank: Vint_runcount, then the HUD (0x81E), then ProcessDPLC.
        let vint = self.world.ram.u32(vars::VINT_RUNCOUNT);
        self.world
            .ram
            .w32(vars::VINT_RUNCOUNT, vint.wrapping_add(1));
        crate::hud::update(&mut self.world.ram, &mut self.vram, &self.graphics.hud)?;
        self.plc.process(&mut self.world.ram);

        // LevelSession.StepPlaying: animated tiles and LoadTilesAsYouMove after World.Step.
        crate::animated_tiles::AnimatedTiles::step(
            &mut self.world.ram,
            &self.graphics.animated,
            &self.graphics.magma_art,
            self.graphics.magma_vram_tile,
            &mut self.vram,
        );
        self.background
            .update(&mut self.world.ram, &self.graphics.geometry);

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
        self.world.objects.tails_animation = Some(self.tails_animation.clone());
        self.world.objects.dust_sprites = self.graphics.bank.table(0x21DDE).cloned();
        if let Some(d) = self.graphics.bank.table(0x15C7A) {
            self.world.objects.monitor_animations = d.animations.clone();
            self.world.objects.monitor_frame_offsets = d.frame_offsets.clone().unwrap_or_default();
        }
        if let Some(d) = self.graphics.bank.table(0x151A58) {
            self.world.objects.motobug_animations = d.animations.clone();
        }
        if let Some(d) = self.graphics.bank.table(0x152314) {
            self.world.objects.chopper_animations = d.animations.clone();
        }
        if let Some(d) = self.graphics.bank.table(0x3BF94) {
            self.world.objects.coconuts_animations = d.animations.clone();
        }
        if let Some(d) = self.graphics.bank.table(0x151ED4) {
            self.world.objects.missile_animations = d.animations.clone();
        }
        if let Some(d) = self.graphics.bank.table(0x316A6) {
            self.world.objects.masher_animations = d.animations.clone();
        }
        if let Some(d) = self.graphics.bank.table(0x3154E) {
            self.world.objects.buzzer_animations = d.animations.clone();
        }
        if let Some(d) = self.graphics.bank.table(0x152C4) {
            self.world.objects.lost_rings_animations = d.animations.clone();
        }
        self.world.objects.animal_data = Some(self.graphics.animals.clone());
        if let Some(d) = self.graphics.bank.table(0x151C88) {
            self.world.objects.buzz_animations = d.animations.clone();
        }
        self.reset_graphics();
        self.camera_x = self.world.camera_x();
        self.camera_y = self.world.camera_y();
        Ok(())
    }

    fn reset_graphics(&mut self) {
        self.vram.clear();
        self.vram.extend_from_slice(&self.graphics.vram);
        self.vram.resize(0x10000, 0);
        self.background = BackgroundPlane::new();
        self.background
            .draw_initial(&self.world.ram, &self.graphics.geometry);
        crate::hud::base(&mut self.world.ram, &mut self.vram, &self.graphics.hud);
        self.sonic_dplc = 0xFF;
        self.tails_dplc = 0xFF;
        self.tails_tails_dplc = 0xFF;
        self.game_over_plc_loaded = false;
    }

    /// LoadSonicDynPLC / LoadTailsDynPLC: copy the current mapping frame's tiles into VRAM.
    fn update_player_vram(&mut self) {
        let sonic = self.world.sonic.obj;
        let frame = sonic.mapping_frame(&self.world.ram);
        if frame != self.sonic_dplc {
            self.sonic_dplc = frame;
            self.world.ram.w8(vars::SONIC_LAST_LOADED_DPLC, frame);
            if let Some(dplc) = self.graphics.sonic.dplc.as_ref() {
                if let Some(entries) = dplc.get(frame as usize) {
                    let mut dest = 0xF000usize;
                    for &(first, count) in entries {
                        let src = first * 32;
                        let n = (count * 32).min(self.graphics.sonic.art.len().saturating_sub(src));
                        if dest + n <= self.vram.len() {
                            self.vram[dest..dest + n]
                                .copy_from_slice(&self.graphics.sonic.art[src..src + n]);
                        }
                        dest += count * 32;
                    }
                }
            }
        }
        let tails = self.world.tails.obj;
        let frame = tails.mapping_frame(&self.world.ram);
        if frame != self.tails_dplc {
            self.tails_dplc = frame;
            self.world.ram.w8(vars::TAILS_LAST_LOADED_DPLC, frame);
            if let Some(dplc) = self.graphics.tails.dplc.as_ref() {
                if let Some(entries) = dplc.get(frame as usize) {
                    let mut dest = 0xF400usize;
                    for &(first, count) in entries {
                        let src = first * 32;
                        let n = (count * 32).min(self.graphics.tails.art.len().saturating_sub(src));
                        if dest + n <= self.vram.len() {
                            self.vram[dest..dest + n]
                                .copy_from_slice(&self.graphics.tails.art[src..src + n]);
                        }
                        dest += count * 32;
                    }
                }
            }
        }
    }

    fn update_dust_vram(&mut self) {
        for address in [vars::SONIC_DUST, vars::TAILS_DUST] {
            let o = Obj::new(address);
            if o.id(&self.world.ram) != 8 {
                continue;
            }
            let frame = o.mapping_frame(&self.world.ram);
            if frame == o.byte(&self.world.ram, 0x30) {
                continue;
            }
            o.set_byte(&mut self.world.ram, 0x30, frame);
            let Some(data) = self.graphics.bank.table(o.mappings(&self.world.ram)) else {
                continue;
            };
            let Some(dplc) = data.dplc.as_ref().and_then(|d| d.get(frame as usize)) else {
                continue;
            };
            let mut dest = o.word(&self.world.ram, 0x3C) as usize;
            for &(first, count) in dplc {
                let src = first * 32;
                let n = (count * 32).min(data.art.len().saturating_sub(src));
                if dest + n <= self.vram.len() {
                    self.vram[dest..dest + n].copy_from_slice(&data.art[src..src + n]);
                }
                dest += count * 32;
            }
        }
    }

    fn update_tails_tails_vram(&mut self) {
        let o = Obj::new(vars::TAILS_TAILS);
        if o.id(&self.world.ram) != 5 {
            return;
        }
        let frame = o.mapping_frame(&self.world.ram);
        if frame == self.tails_tails_dplc {
            return;
        }
        self.tails_tails_dplc = frame;
        if let Some(dplc) = self.graphics.tails.dplc.as_ref() {
            if let Some(entries) = dplc.get(frame as usize) {
                let mut dest = 0xF600usize;
                for &(first, count) in entries {
                    let src = first * 32;
                    let n = (count * 32).min(self.graphics.tails.art.len().saturating_sub(src));
                    if dest + n <= self.vram.len() {
                        self.vram[dest..dest + n]
                            .copy_from_slice(&self.graphics.tails.art[src..src + n]);
                    }
                    dest += count * 32;
                }
            }
        }
    }

    /// The mutable VRAM image (diagnostics/tests).
    pub fn vram(&self) -> &[u8] {
        &self.vram
    }

    pub fn render(&mut self) -> Vec<u8> {
        for i in 0..64u16 {
            self.vdp.cram[i as usize] = self.world.ram.u16(vars::NORMAL_PALETTE + i * 2);
        }
        if let Some((line, below)) = crate::lava_line::split(&self.world.ram, &self.vdp.cram) {
            self.vdp.split_line = line as i32;
            self.vdp.cram_below = below;
        } else {
            self.vdp.split_line = -1;
            self.vdp.cram_below = self.vdp.cram;
        }
        self.vdp.render_level(
            &self.graphics,
            &self.vram,
            &self.background.cells,
            &self.world.ram,
            true,
        );
        self.vdp.to_rgba()
    }

    /// The trace's `screenHash`: low 64 bits of SHA-256 over the RGBA bytes.
    pub fn render_hash(&mut self) -> u64 {
        use sha2::{Digest, Sha256};
        let rgba = self.render();
        let digest = Sha256::digest(&rgba);
        u64::from_le_bytes(digest[..8].try_into().expect("digest length"))
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
