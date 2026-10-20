use engine_core::audio::{sfx, AudioState};
use engine_core::session::Session;
use engine_core::{vars, Ram};
use rom_assets::{load_audio_assets_for_zone, Archive, LevelData};
use std::path::PathBuf;

fn assets() -> Option<engine_core::audio::AudioAssets> {
    let root = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../..");
    let archive = Archive::open(root.join("web-data/campaign.bin")).ok()?;
    load_audio_assets_for_zone(&archive, 0x40, Some("zones/zone_40/music.json")).ok()
}

fn render(state: &mut AudioState, ram: &mut Ram) -> Vec<i16> {
    let mut out = vec![0i16; 800 * 2];
    for _ in 0..4 {
        state.update(ram);
        state.render(&mut out, 800);
    }
    out
}

#[test]
fn sonic_sfx_ids_produce_output() {
    let Some(assets) = assets() else { return };
    for id in [sfx::ids::JUMP, sfx::ids::RING, sfx::ids::HURT] {
        let mut ram = Ram::new();
        ram.w8(vars::SOUND_DRIVER_SELECT, 1);
        let mut state = AudioState::new(assets.clone());
        state.start_music(&mut ram, 0);
        sfx::sonic(&mut ram, id);
        let out = render(&mut state, &mut ram);
        assert!(
            out.iter().any(|s| *s != 0),
            "Sonic SFX ${id:02X} was silent"
        );
    }
}

#[test]
fn pipe_pcm_produces_output() {
    let Some(assets) = assets() else { return };
    assert!(assets.pcm_effects.iter().any(|e| e.name == "pipe"));
    let mut ram = Ram::new();
    ram.w8(vars::SOUND_DRIVER_SELECT, 1);
    let mut state = AudioState::new(assets);
    state.start_music(&mut ram, 0);
    assert!(state.play_effect("pipe"));
    let out = render(&mut state, &mut ram);
    assert!(out.iter().any(|s| *s != 0), "pipe PCM was silent");
}

#[test]
fn mario_pcm_effects_produce_output() {
    let Some(assets) = assets() else {
        return;
    };
    // Generated locally by `tools/Smb3Effects`; skip names the checkout has not rendered.
    for name in ["mario-bump", "mario-coin", "mario-powerup"] {
        if !assets.pcm_effects.iter().any(|e| e.name == name) {
            continue;
        }
        let mut ram = Ram::new();
        ram.w8(vars::SOUND_DRIVER_SELECT, 1);
        let mut state = AudioState::new(assets.clone());
        state.start_music(&mut ram, 0);
        assert!(state.play_effect(name), "{name} missing");
        let out = render(&mut state, &mut ram);
        assert!(out.iter().any(|s| *s != 0), "{name} was silent");
    }
}

#[test]
fn effect_requests_do_not_change_the_motion_or_framebuffer() {
    let root = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../..");
    let Ok(archive) = Archive::open(root.join("web-data/campaign.bin")) else {
        return;
    };
    let level = LevelData::load(&archive, 0x40, 1).unwrap();
    let sets = rom_assets::load_collision_sets(&archive).unwrap();
    let sonic = rom_assets::load_sonic_animation(&archive).unwrap();
    let tails = rom_assets::load_tails_animation(&archive).unwrap();
    let audio = load_audio_assets_for_zone(&archive, 0x40, None).unwrap();
    let mut dry = Session::new(
        Box::new(level.clone()),
        0x40,
        1,
        sets.clone(),
        sonic.clone(),
        tails.clone(),
    )
    .unwrap();
    let mut wet = Session::new(Box::new(level), 0x40, 1, sets, sonic, tails).unwrap();
    wet.enable_audio(audio);
    for i in 0..60 {
        if i == 5 {
            wet.play_sound(sfx::ids::JUMP);
            assert!(wet.play_effect("pipe"));
        }
        dry.step(0).unwrap();
        wet.step(0).unwrap();
        let mut out = vec![0i16; 1600];
        wet.render_audio(&mut out, 800);
    }
    assert_eq!(dry.render_hash(), wet.render_hash());
}
