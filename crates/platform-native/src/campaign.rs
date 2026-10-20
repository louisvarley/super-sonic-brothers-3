//! Campaign mode: world maps, map-panel entry, campaign area sessions, and map return.

use engine_core::animation::{SonicAnimation, TailsAnimation};
use engine_core::audio::AudioState;
use engine_core::collision::CollisionSets;
use engine_core::frontend::{
    Card, FrontendAssets, GameFlow, MapAction, MapProgress, ScreenState, Start, SPADE_TILE,
};
use engine_core::session::{PipeExit, Session};
use engine_core::world::Start as LevelStart;
use engine_core::{vars, Ram};
use rom_assets::{
    load_audio_assets_for_area, load_audio_assets_for_zone, load_audio_assets_with_song, Archive,
    CampaignData, LevelData,
};

const CAMPAIGN_ZONE: u8 = 0x43;

const ENTRY_FRAMES: u8 = 32;

enum PendingLevel {
    Area(u16),
    Zone(u8, u8),
}

struct EntryTransition {
    frames: u8,
    pending: PendingLevel,
}

pub struct CampaignRunner {
    archive: Archive,
    pub campaign: CampaignData,
    sets: CollisionSets,
    sonic: SonicAnimation,
    tails: TailsAnimation,
    pub flow: GameFlow,
    pub session: Option<Session>,
    map_audio: Option<Box<AudioState>>,
    map_ram: Ram,
    map_music: u16,
    entry: Option<EntryTransition>,
    current_act: Option<u16>,
    force_two_player: bool,
    pub ticks: u32,
    pub last_error: Option<String>,
}

impl CampaignRunner {
    #[allow(dead_code)]
    #[allow(clippy::too_many_arguments)]
    pub fn new(
        archive: Archive,
        assets: FrontendAssets,
        sets: CollisionSets,
        sonic: SonicAnimation,
        tails: TailsAnimation,
        world: u8,
        screen: usize,
    ) -> Result<Self, String> {
        Self::new_with_start(
            archive,
            assets,
            sets,
            sonic,
            tails,
            world,
            screen,
            Start::Map,
        )
    }

    #[allow(clippy::too_many_arguments)]
    pub fn new_with_start(
        archive: Archive,
        assets: FrontendAssets,
        sets: CollisionSets,
        sonic: SonicAnimation,
        tails: TailsAnimation,
        world: u8,
        screen: usize,
        start: Start,
    ) -> Result<Self, String> {
        let campaign = CampaignData::load(&archive)?;
        if !(1..=8).contains(&world) {
            return Err(format!("world {world} out of range (1-8)"));
        }
        let flow = GameFlow::new_at(assets.clone(), start, world, screen);
        let mut runner = Self {
            archive,
            campaign,
            sets,
            sonic,
            tails,
            flow,
            session: None,
            map_audio: None,
            map_ram: Ram::new(),
            map_music: 0x94,
            entry: None,
            current_act: None,
            force_two_player: false,
            ticks: 0,
            last_error: None,
        };
        if matches!(start, Start::Map | Start::Match) {
            runner.start_map_audio(world);
        }
        Ok(runner)
    }

    /// `--two-player`: keep Tails as player 2 across title/map/level changes.
    pub fn set_two_player(&mut self, enabled: bool) {
        self.force_two_player = enabled;
        if enabled {
            self.flow.progress.two_player = true;
            if matches!(self.flow.screen, ScreenState::Map(_)) {
                self.flow
                    .set_progress_map(self.flow.progress.clone(), false);
            }
        }
    }

    /// Direct `--campaign-act`: mark the area's map owner, then enter it.
    pub fn start_act(&mut self, act: u16) -> Result<(), String> {
        let owner = self
            .campaign
            .area_for_act(act)
            .ok_or_else(|| format!("unknown campaign act {act}"))?
            .clone();
        if owner.world != 0 {
            self.flow.progress.world = owner.world as i32;
            self.flow.progress.map_screen = owner.screen as i32;
            self.flow.progress.position = owner.map_tile();
            self.flow
                .set_progress_map(self.flow.progress.clone(), false);
            self.start_map_audio(owner.world);
        }
        self.start_area(act)
    }

    fn start_map_audio(&mut self, world: u8) {
        self.map_audio = None;
        let path = format!("conversion/music/set2A_{:02X}.json", world as u16 * 16);
        let Ok(assets) = load_audio_assets_with_song(&self.archive, Some(&path)) else {
            return;
        };
        self.map_music = assets.extra_music.unwrap_or(0x94);
        let mut audio = Box::new(AudioState::new(assets));
        audio.start_music(&mut self.map_ram, self.map_music);
        self.map_audio = Some(audio);
    }

    fn seed_progress(&mut self, session: &mut Session) {
        let p = &self.flow.progress;
        let ram = &mut session.world.ram;
        ram.w8(vars::LIFE_COUNT, p.lives.clamp(1, 99) as u8);
        ram.w32(vars::SCORE, (p.score / 10).max(0) as u32);
        ram.w8(vars::UPDATE_HUD_LIVES, 1);
        ram.w8(vars::UPDATE_HUD_SCORE, 1);
        // Card-game prizes: each Sonic monitor item rises at Sonic as the act starts, as the legacy GameFlow did.
        let items = p.power_ups.clone();
        for item in items {
            if let Some(slot) = engine_core::objects::find_free_slot(ram) {
                slot.set_id(ram, 0x2E);
                slot.set_x(ram, session.world.sonic.obj.x(ram));
                slot.set_y(ram, session.world.sonic.obj.y(ram).wrapping_sub(0x18));
                slot.set_anim(ram, item as u8);
                slot.set_word(ram, 0x3E, vars::MAIN_CHARACTER);
            }
        }
        self.flow.progress.power_ups.clear();
    }

    fn start_area(&mut self, act: u16) -> Result<(), String> {
        let area = self
            .campaign
            .area_for_act(act)
            .ok_or_else(|| format!("unknown campaign act {act}"))?
            .clone();
        let level = area.load_level(&self.archive)?;
        let act_byte = if area.act <= 255 { area.act as u8 } else { 1 };
        let mut session = Session::new(
            Box::new(level),
            CAMPAIGN_ZONE,
            act_byte,
            self.sets.clone(),
            self.sonic.clone(),
            self.tails.clone(),
        )
        .map_err(|e| format!("area {}: {e}", area.act))?;
        session.set_exit_on_restart(true);
        self.seed_progress(&mut session);
        if let Ok(audio) = load_audio_assets_for_area(&self.archive, &area.directory) {
            session.enable_audio(audio);
            session.play_effect("enter-level");
        }
        self.map_audio = None;
        self.session = Some(session);
        self.current_act = Some(act);
        self.last_error = None;
        Ok(())
    }

    fn start_zone(&mut self, zone: u8, act: u8) -> Result<(), String> {
        let level = LevelData::load(&self.archive, zone as u16, act as u16)?;
        let mut session = Session::new(
            Box::new(level),
            zone,
            act,
            self.sets.clone(),
            self.sonic.clone(),
            self.tails.clone(),
        )
        .map_err(|e| format!("zone ${zone:02X} act {act}: {e}"))?;
        session.set_exit_on_restart(true);
        self.seed_progress(&mut session);
        if let Ok(audio) = load_audio_assets_for_zone(&self.archive, zone, None) {
            session.enable_audio(audio);
            session.play_effect("enter-level");
        }
        self.map_audio = None;
        self.session = Some(session);
        self.current_act = None;
        self.last_error = None;
        Ok(())
    }

    fn handle_pipe_exit(&mut self, exit: PipeExit) {
        let Some(session) = self.session.take() else {
            return;
        };
        let mut p = self.flow.progress.clone();
        p.lives = session.world.ram.u8(vars::LIFE_COUNT) as i32;
        p.score = ((session.world.ram.u32(vars::SCORE) as i64) * 10).min(9_999_990) as i32;
        let source = self
            .current_act
            .and_then(|act| self.campaign.area_for_act(act).cloned());
        if exit.kind == "map" {
            p.rings = (p.rings + session.world.ram.u16(vars::RING_COUNT) as i32).min(99);
            if exit.far {
                if let Some(source) = source.as_ref() {
                    if let Some(world) = self
                        .campaign
                        .worlds
                        .iter()
                        .find(|w| w.number == p.world as u8)
                    {
                        let destinations: Vec<_> = world
                            .entries
                            .iter()
                            .filter(|e| {
                                e.area_id.as_ref().is_some_and(|id| {
                                    self.campaign
                                        .areas
                                        .iter()
                                        .find(|a| &a.source.id == id)
                                        .is_some_and(|a| {
                                            a.source.object_address == source.source.object_address
                                        })
                                })
                            })
                            .filter(|e| {
                                e.screen != p.map_screen as usize
                                    || e.row * 16 + e.column != p.position
                            })
                            .collect();
                        if let [destination] = destinations.as_slice() {
                            p.map_screen = destination.screen as i32;
                            p.position = destination.row * 16 + destination.column;
                            p.last_move = 0;
                        }
                    }
                }
            }
            self.flow.set_progress_map(p, false);
            self.start_map_audio(self.flow.progress.world as u8);
            return;
        }
        let Some(source) = source else {
            return;
        };
        let Some(target) = self.pipe_target(&exit.kind, &source) else {
            self.last_error = Some(format!(
                "pipe exit from {} ({}) has no destination",
                source.source.id, exit.kind
            ));
            return;
        };
        let previous = session.world.ram.clone();
        if let Err(e) = self.start_area_seamless(
            target.act,
            exit.target_x,
            exit.target_y,
            exit.exit_direction,
            &previous,
        ) {
            self.last_error = Some(e);
        }
    }

    fn pipe_target(
        &self,
        kind: &str,
        source: &rom_assets::CampaignArea,
    ) -> Option<rom_assets::CampaignArea> {
        let world = self.flow.progress.world.clamp(1, 8) as usize;
        let id = match kind {
            "common-exit" => {
                if world == 4 {
                    "ts01-15-BDB8-C560".to_string()
                } else {
                    "ts01-15-BD68-C560".to_string()
                }
            }
            "bonus" => {
                const BONUS_LAYOUTS: [u16; 8] = [
                    0xB1AF, 0xB1BC, 0xB28B, 0xB372, 0xB3D8, 0xB470, 0xB5E2, 0xB65B,
                ];
                let address = BONUS_LAYOUTS[world - 1];
                self.campaign
                    .areas
                    .iter()
                    .find(|a| a.source.layout_address == address && a.source.tileset == 14)
                    .map(|a| a.source.id.clone())?
            }
            _ => source.alternate_area_id.clone()?,
        };
        self.campaign
            .areas
            .iter()
            .find(|a| a.source.id == id && a.available())
            .cloned()
    }

    #[allow(clippy::too_many_arguments)]
    fn start_area_seamless(
        &mut self,
        act: u16,
        x: i32,
        y: i32,
        exit_direction: i32,
        previous: &Ram,
    ) -> Result<(), String> {
        let area = self
            .campaign
            .area_for_act(act)
            .ok_or_else(|| format!("unknown pipe destination act {act}"))?
            .clone();
        let level = area.load_level(&self.archive)?;
        let act_byte = if area.act <= 255 { area.act as u8 } else { 1 };
        let mut session = Session::new_with_start(
            Box::new(level),
            CAMPAIGN_ZONE,
            act_byte,
            self.sets.clone(),
            self.sonic.clone(),
            self.tails.clone(),
            Some(LevelStart { x, y }),
        )
        .map_err(|e| format!("pipe area {}: {e}", area.act))?;
        session.set_exit_on_restart(true);
        self.seed_progress(&mut session);
        {
            let ram = &mut session.world.ram;
            ram.w16(vars::RING_COUNT, previous.u16(vars::RING_COUNT));
            ram.w32(vars::TIMER, previous.u32(vars::TIMER));
            ram.w8(vars::LIFE_COUNT, previous.u8(vars::LIFE_COUNT));
            ram.w32(vars::SCORE, previous.u32(vars::SCORE));
            ram.w8(vars::EXTRA_LIFE_FLAGS, previous.u8(vars::EXTRA_LIFE_FLAGS));
            ram.w8(vars::LIFE_COUNT_P2, previous.u8(vars::LIFE_COUNT_P2));
            ram.w16(vars::RING_COUNT_P2, previous.u16(vars::RING_COUNT_P2));
            ram.w32(vars::TIMER_P2, previous.u32(vars::TIMER_P2));
            ram.w8(
                vars::EXTRA_LIFE_FLAGS_P2,
                previous.u8(vars::EXTRA_LIFE_FLAGS_P2),
            );
            for slot in [
                vars::SONIC_SHIELD,
                vars::TAILS_SHIELD,
                vars::SONIC_INVINCIBILITY_STARS,
                vars::TAILS_INVINCIBILITY_STARS,
            ] {
                ram.bytes_mut()[slot as usize..slot as usize + 0x40]
                    .copy_from_slice(&previous.bytes()[slot as usize..slot as usize + 0x40]);
            }
        }
        for slot in [vars::MAIN_CHARACTER, vars::SIDEKICK] {
            let old = engine_core::Obj::new(slot);
            let new = engine_core::Obj::new(slot);
            new.set_status_secondary(&mut session.world.ram, old.status_secondary(previous));
            new.set_invulnerable_time(&mut session.world.ram, old.invulnerable_time(previous));
            new.set_invincibility_time(&mut session.world.ram, old.invincibility_time(previous));
            new.set_speed_shoes_time(&mut session.world.ram, old.speed_shoes_time(previous));
        }
        session.set_pipe_exit_direction(exit_direction);
        if let Ok(audio) = load_audio_assets_for_area(&self.archive, &area.directory) {
            session.enable_audio(audio);
            session.play_effect("pipe");
        }
        self.map_audio = None;
        self.session = Some(session);
        self.current_act = Some(act);
        self.last_error = None;
        Ok(())
    }

    /// One game frame. Map actions load a level; an exited level returns to the map.
    #[allow(dead_code)]
    pub fn step(&mut self, buttons: u8) {
        self.step_p2(buttons, 0);
    }

    /// One game frame with Tails' pad. In 1-player mode player 2 is ignored.
    pub fn step_p2(&mut self, buttons: u8, buttons_p2: u8) {
        self.ticks += 1;
        if let Some(mut entry) = self.entry.take() {
            if let Some(audio) = self.map_audio.as_mut() {
                audio.update(&mut self.map_ram);
            }
            entry.frames += 1;
            if entry.frames >= ENTRY_FRAMES {
                let result = match entry.pending {
                    PendingLevel::Area(act) => self.start_area(act),
                    PendingLevel::Zone(zone, act) => self.start_zone(zone, act),
                };
                if let Err(e) = result {
                    self.last_error = Some(e);
                }
            } else {
                self.entry = Some(entry);
            }
            return;
        }
        if let Some(session) = self.session.as_mut() {
            let p2 = if self.flow.progress.two_player {
                buttons_p2
            } else {
                0
            };
            session.step_with_p2(buttons, p2).ok();
            if let Some(exit) = session.take_pipe_exit() {
                self.handle_pipe_exit(exit);
                return;
            }
            if session.exited() {
                self.finish_level();
            }
            return;
        }
        if let Some(audio) = self.map_audio.as_mut() {
            audio.update(&mut self.map_ram);
        }
        let action = self.flow.step(buttons);
        if self.force_two_player
            && matches!(self.flow.screen, ScreenState::Map(_))
            && !self.flow.progress.two_player
        {
            self.flow.progress.two_player = true;
            self.flow
                .set_progress_map(self.flow.progress.clone(), false);
        }
        if matches!(self.flow.screen, ScreenState::Map(_)) && self.map_audio.is_none() {
            self.start_map_audio(self.flow.progress.world as u8);
        }
        let effects = self.flow.take_map_sound_effects();
        if let Some(audio) = self.map_audio.as_mut() {
            for effect in effects {
                audio.play_effect(effect);
            }
        }
        let result = match action {
            Some(MapAction::StartArea { act }) => {
                self.begin_entry(PendingLevel::Area(act));
                Ok(())
            }
            Some(MapAction::StartZone { zone, act }) => {
                self.begin_entry(PendingLevel::Zone(zone, act));
                Ok(())
            }
            Some(MapAction::StartMatch) | Some(MapAction::BackToMap { .. }) | None => Ok(()),
        };
        if let Err(e) = result {
            self.last_error = Some(e);
        }
    }

    fn begin_entry(&mut self, pending: PendingLevel) {
        if let Some(audio) = self.map_audio.as_mut() {
            audio.play_effect("enter-level");
        }
        self.entry = Some(EntryTransition { frames: 0, pending });
    }

    fn finish_level(&mut self) {
        let Some(session) = self.session.take() else {
            return;
        };
        let ram = &session.world.ram;
        let mut p: MapProgress = self.flow.progress.clone();
        p.lives = ram.u8(vars::LIFE_COUNT) as i32;
        p.score = ((ram.u32(vars::SCORE) as i64) * 10).min(9_999_990) as i32;
        if let Some(card) = session.goal_card() {
            p.rings = (p.rings + ram.u16(vars::RING_COUNT) as i32).min(99);
            p.clear_panel(p.position);
            let spade_key = p.panel_key(SPADE_TILE);
            p.cleared.retain(|k| *k != spade_key);
            p.cards.push(match card {
                0 => Card::Ring,
                1 => Card::Shield,
                _ => Card::Invincibility,
            });
            if p.cards.len() == 3 {
                let lives = if p.cards.iter().any(|c| *c != p.cards[0]) {
                    1
                } else {
                    match p.cards[0] {
                        Card::Ring => 2,
                        Card::Shield => 3,
                        Card::Invincibility => 5,
                    }
                };
                p.lives = (p.lives + lives).min(99);
                p.cards.clear();
            }
        }
        if p.lives <= 0 {
            // A new adventure rather than the still-placeholder title screen (K5).
            p = MapProgress::default();
        }
        self.flow.set_progress_map(p, false);
        self.start_map_audio(self.flow.progress.world as u8);
    }

    pub fn render(&mut self) -> Vec<u8> {
        if let Some(entry) = &self.entry {
            let mut frame = self.flow.render_rgba();
            draw_entry_border(&mut frame, entry.frames);
            return frame;
        }
        match self.session.as_mut() {
            Some(s) => s.render(),
            None => self.flow.render_rgba(),
        }
    }

    pub fn render_audio(&mut self, out: &mut [i16], frames: usize) {
        if let Some(s) = self.session.as_mut() {
            s.render_audio(out, frames);
        } else if let Some(a) = self.map_audio.as_mut() {
            a.render(out, frames);
        } else {
            out.fill(0);
        }
    }

    pub fn describe(&self) -> String {
        if let Some(s) = &self.session {
            let (zone, act) = s.current_act();
            return format!("level zone ${zone:02X} act {act} tick {}", s.ticks);
        }
        if matches!(self.flow.screen, ScreenState::Title(_)) {
            return "title".to_string();
        }
        let p = &self.flow.progress;
        format!(
            "map world {} screen {} tile {} lives {} cards {}",
            p.world,
            p.map_screen,
            p.position,
            p.lives,
            p.cards.len()
        )
    }
}

fn draw_entry_border(rgba: &mut [u8], frames: u8) {
    const WIDTH: usize = 320;
    const HEIGHT: usize = 224;
    let thickness = (frames as usize * 4).min(112);
    if thickness == 0 {
        return;
    }
    for y in 0..HEIGHT {
        for x in 0..WIDTH {
            if x < thickness || x >= WIDTH - thickness || y < thickness || y >= HEIGHT - thickness {
                let i = (y * WIDTH + x) * 4;
                rgba[i] = 0;
                rgba[i + 1] = 0;
                rgba[i + 2] = 0;
                rgba[i + 3] = 255;
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::path::PathBuf;

    fn runner() -> Option<CampaignRunner> {
        let path = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../web-data/campaign.bin");
        if !path.exists() {
            eprintln!("skip: campaign.bin not present");
            return None;
        }
        let archive = Archive::open(&path).ok()?;
        let assets = rom_assets::load_frontend_assets(&archive).ok()?;
        let sets = rom_assets::load_collision_sets(&archive).ok()?;
        let sonic = rom_assets::load_sonic_animation(&archive).ok()?;
        let tails = rom_assets::load_tails_animation(&archive).ok()?;
        CampaignRunner::new(archive, assets, sets, sonic, tails, 1, 0).ok()
    }

    fn runner_with_start(start: Start) -> Option<CampaignRunner> {
        let path = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../web-data/campaign.bin");
        if !path.exists() {
            eprintln!("skip: campaign.bin not present");
            return None;
        }
        let archive = Archive::open(&path).ok()?;
        let assets = rom_assets::load_frontend_assets(&archive).ok()?;
        let sets = rom_assets::load_collision_sets(&archive).ok()?;
        let sonic = rom_assets::load_sonic_animation(&archive).ok()?;
        let tails = rom_assets::load_tails_animation(&archive).ok()?;
        CampaignRunner::new_with_start(archive, assets, sets, sonic, tails, 1, 0, start).ok()
    }

    #[test]
    fn map_music_is_non_silent() {
        let Some(mut runner) = runner() else { return };
        for _ in 0..30 {
            runner.step(0);
        }
        let mut out = vec![0i16; 800 * 2];
        runner.render_audio(&mut out, 800);
        assert!(out.iter().any(|s| *s != 0), "campaign map music is silent");
    }

    #[test]
    fn a_death_in_a_campaign_act_returns_to_the_map() {
        let Some(mut runner) = runner() else { return };
        runner.start_act(2).expect("area 2 starts");
        assert!(runner.session.is_some());
        runner
            .session
            .as_mut()
            .unwrap()
            .world
            .ram
            .w16(vars::LEVEL_INACTIVE_FLAG, 1);
        runner.step(0);
        assert!(runner.session.is_none(), "the level exited");
        assert!(runner.flow.progress.lives > 0);
    }
    #[test]
    fn area_entry_draws_the_closing_border_then_loads() {
        let Some(mut runner) = runner_with_start(Start::Map) else {
            return;
        };
        runner.begin_entry(PendingLevel::Area(2));
        for _ in 0..31 {
            runner.step(0);
            assert!(
                runner.session.is_none(),
                "the area must wait for the border"
            );
        }
        let frame = runner.render();
        assert_eq!(&frame[0..4], &[0, 0, 0, 255], "the border is black");
        // At 31 frames the 112-pixel border covers the whole 320x224 frame.
        assert!(frame.chunks_exact(4).all(|p| p == [0, 0, 0, 255]));
        runner.step(0);
        assert!(runner.session.is_some(), "the area loads after 32 frames");
    }

    #[test]
    fn title_start_enters_the_world_map_after_start() {
        let Some(mut runner) = runner_with_start(Start::Title) else {
            return;
        };
        for frame in 1..=200u32 {
            let buttons = if frame == 101 || frame == 103 {
                0x80
            } else {
                0
            };
            runner.step(buttons);
        }
        assert!(matches!(runner.flow.screen, ScreenState::Map(_)));
        assert!(runner.map_audio.is_some());
    }
}
