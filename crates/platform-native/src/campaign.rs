//! Campaign mode: world maps, map-panel entry, campaign area sessions, and map return.

use engine_core::animation::{SonicAnimation, TailsAnimation};
use engine_core::audio::AudioState;
use engine_core::collision::CollisionSets;
use engine_core::frontend::{
    Card, FrontendAssets, GameFlow, MapAction, MapProgress, ScreenState, Start, SPADE_TILE,
};
use engine_core::session::Session;
use engine_core::{vars, Ram};
use rom_assets::{
    load_audio_assets_for_area, load_audio_assets_for_zone, load_audio_assets_with_song, Archive,
    CampaignData, LevelData,
};

const CAMPAIGN_ZONE: u8 = 0x43;

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
            ticks: 0,
            last_error: None,
        };
        if matches!(start, Start::Map) {
            runner.start_map_audio(world);
        }
        Ok(runner)
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

    fn seed_progress(&self, session: &mut Session) {
        let p = &self.flow.progress;
        let ram = &mut session.world.ram;
        ram.w8(vars::LIFE_COUNT, p.lives.clamp(1, 99) as u8);
        ram.w32(vars::SCORE, (p.score / 10).max(0) as u32);
        ram.w8(vars::UPDATE_HUD_LIVES, 1);
        ram.w8(vars::UPDATE_HUD_SCORE, 1);
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
        }
        self.map_audio = None;
        self.session = Some(session);
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
        }
        self.map_audio = None;
        self.session = Some(session);
        self.last_error = None;
        Ok(())
    }

    /// One game frame. Map actions load a level; an exited level returns to the map.
    pub fn step(&mut self, buttons: u8) {
        self.ticks += 1;
        if let Some(session) = self.session.as_mut() {
            session.step(buttons).ok();
            if session.exited() {
                self.finish_level();
            }
            return;
        }
        if let Some(audio) = self.map_audio.as_mut() {
            audio.update(&mut self.map_ram);
        }
        let action = self.flow.step(buttons);
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
            Some(MapAction::StartArea { act }) => self.start_area(act),
            Some(MapAction::StartZone { zone, act }) => self.start_zone(zone, act),
            Some(MapAction::StartMatch) | Some(MapAction::BackToMap { .. }) | None => Ok(()),
        };
        if let Err(e) = result {
            self.last_error = Some(e);
        }
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
