use engine_core::session::Session;
use rom_assets::{load_audio_assets_for_zone, Archive, LevelData};

#[test]
fn smb3_levels_are_non_silent_and_audio_is_side_effect_free() {
    let root = std::path::PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../..");
    let ap = root.join("sonicbros3.bin");
    if !ap.exists() {
        eprintln!("skip: sonicbros3.bin not present");
        return;
    }
    let a = Archive::open(&ap).unwrap();
    let sets = rom_assets::load_collision_sets(&a).unwrap();
    let sa = rom_assets::load_sonic_animation(&a).unwrap();
    let ta = rom_assets::load_tails_animation(&a).unwrap();
    for (z, name) in [(0x40, "1-1"), (0x41, "1-2"), (0x42, "1-3")] {
        let level = LevelData::load(&a, z as u16, 1).unwrap();
        let audio = load_audio_assets_for_zone(&a, z, level.music_file.as_deref()).unwrap();
        let music = audio
            .extra_music
            .unwrap_or_else(|| panic!("{name} should use its SMB3 song"));
        let mut dry = Session::new(
            Box::new(level.clone()),
            z,
            1,
            sets.clone(),
            sa.clone(),
            ta.clone(),
        )
        .unwrap();
        let mut wet =
            Session::new(Box::new(level), z, 1, sets.clone(), sa.clone(), ta.clone()).unwrap();
        wet.enable_audio(audio);
        let mut energy = 0i64;
        let mut peak = 0i16;
        let mut buffer = vec![0i16; 800 * 2];
        for _ in 0..60 {
            dry.step(0).unwrap();
            wet.step(0).unwrap();
            wet.render_audio(&mut buffer, 800);
            energy += buffer.iter().map(|s| (*s as i64).pow(2)).sum::<i64>();
            peak = peak.max(buffer.iter().map(|s| s.abs()).max().unwrap_or(0));
        }
        assert!(energy > 0 && peak > 32, "{name} audio is silent");
        assert!(
            buffer.windows(2).any(|w| w[0] != w[1]),
            "{name} audio is a constant tone"
        );
        assert_eq!(
            dry.render_hash(),
            wet.render_hash(),
            "{name} audio changed the framebuffer"
        );
        assert_eq!(
            dry.camera_x, wet.camera_x,
            "{name} audio changed the camera"
        );
        assert_eq!(
            dry.world.ram.u16(engine_core::vars::LEVEL_FRAME_COUNTER),
            wet.world.ram.u16(engine_core::vars::LEVEL_FRAME_COUNTER)
        );
        assert_eq!(wet.world.ram.u16(engine_core::vars::LEVEL_MUSIC), music);
        assert_eq!(wet.world.ram.u8(engine_core::vars::SOUND_DRIVER_SELECT), 1);
    }
}
