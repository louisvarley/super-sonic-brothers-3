//! Exact parity with the legacy 68k SMPS driver and its YM2612/PSG mixer.
//!
//! The expected values were captured from `legacy/src/SonicBros3/Audio` by driving it with the same archive, queue
//! byte and VBlank count (zone 1-1's extra song $94) and hashing RAM `$F000-$F2FF` and each stretch of rendered
//! 48 kHz stereo samples. `$F009 = $80`/`$F00A = $94` is what `World`/`RamSound.PlayMusic` leave after level setup.

use engine_core::audio::{Smps68kDriver, SoundMixer};
use engine_core::Ram;
use rom_assets::{load_audio_assets_for_zone, Archive};

const CHECKPOINTS: [u32; 13] = [1, 2, 5, 10, 30, 60, 120, 240, 600, 900, 1200, 1800, 3000];
const AUDIO_HASHES: [u64; 13] = [
    0x13F6_31EF_6A6F_DD25,
    0x3559_0FBE_EEB4_AFF9,
    0x6913_08CE_4C0C_EB49,
    0x49C9_2010_6DA3_6801,
    0xC29C_134E_02CF_0549,
    0x77BC_9DEB_F879_ADF9,
    0xA535_2836_26A6_A09D,
    0xECD5_C84E_B668_1759,
    0x1DB6_A72F_E00E_45D9,
    0x89F7_7836_C2BE_F58D,
    0xC9E3_80F4_D9C2_54E1,
    0xBE8D_D321_5CF6_3899,
    0xC406_854D_43AC_D60D,
];
const RAM_HASHES: [u64; 13] = [
    0x55D3_B87A_2B3F_CD38,
    0x6174_FCAE_AD94_4A41,
    0xF56C_CD70_96E3_D111,
    0x5DD2_F55E_5C9C_5699,
    0x7355_7A69_9211_32BD,
    0xC4CA_C476_2C13_BCA2,
    0x336A_B836_1106_C86C,
    0x468C_8B5C_17D9_43BF,
    0xFD58_AE15_A72C_63C5,
    0xBDFB_7A9D_A0BF_14B3,
    0xF3B1_6D43_505E_5D14,
    0xF2BD_C10A_195D_B205,
    0xF663_901C_E1CB_8EB3,
];

fn fnv(bytes: impl IntoIterator<Item = u8>) -> u64 {
    let mut hash = 14_695_981_039_346_656_037u64;
    for byte in bytes {
        hash = (hash ^ byte as u64).wrapping_mul(1_099_511_628_211);
    }
    hash
}

#[test]
fn driver_ram_and_audio_match_the_legacy_oracle() {
    let root = std::path::PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../..");
    let path = root.join("sonicbros3.bin");
    if !path.exists() {
        eprintln!("skip: sonicbros3.bin not present");
        return;
    }
    let archive = Archive::open(&path).unwrap();
    let assets =
        load_audio_assets_for_zone(&archive, 0x40, Some("zones/zone_40/music.json")).unwrap();
    assert_eq!(assets.extra_music, Some(0x94));
    let mut mixer = SoundMixer::new(
        assets.dac_samples.clone(),
        assets.dac_sample_pitches.clone(),
        48_000,
    );
    let mut driver = Smps68kDriver::new(assets);
    let mut ram = Ram::new();
    let mut buffer = vec![0i16; 800 * 2];
    ram.w8(0xF009, 0x80);
    ram.w8(0xF00A, 0x94);

    let mut frames = 0u32;
    for (i, target) in CHECKPOINTS.iter().copied().enumerate() {
        let mut audio = Vec::new();
        while frames < target {
            driver.update(&mut ram, &mut mixer);
            mixer.render(&mut buffer, 800);
            for sample in &buffer {
                audio.extend_from_slice(&sample.to_le_bytes());
            }
            frames += 1;
        }
        assert_eq!(fnv(audio), AUDIO_HASHES[i], "audio after frame {target}");
        let memory: Vec<u8> = (0..0x300)
            .map(|offset| ram.u8(0xF000u16 + offset as u16))
            .collect();
        assert_eq!(
            fnv(memory),
            RAM_HASHES[i],
            "driver RAM after frame {target}"
        );
    }
}
