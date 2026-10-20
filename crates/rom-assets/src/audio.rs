//! Loader for the 68k SMPS player data and the SMB3 songs arranged for it.

use crate::archive::{resolve, Archive};
use engine_core::audio::{AudioAssets, PcmEffect};

fn wav_data(b: &[u8]) -> Vec<u8> {
    let mut i = 12;
    while i + 8 <= b.len() {
        let size = u32::from_le_bytes([b[i + 4], b[i + 5], b[i + 6], b[i + 7]]) as usize;
        if &b[i..i + 4] == b"data" {
            return b[i + 8..(i + 8 + size).min(b.len())].to_vec();
        }
        i += 8 + size + (size & 1);
    }
    Vec::new()
}

fn bytes(root: &serde_json::Value, key: &str) -> Vec<u8> {
    root[key]
        .as_array()
        .into_iter()
        .flatten()
        .map(|v| v.as_u64().unwrap_or(0) as u8)
        .collect()
}

fn words(root: &serde_json::Value, key: &str) -> Vec<u16> {
    root[key]
        .as_array()
        .into_iter()
        .flatten()
        .map(|v| v.as_u64().unwrap_or(0) as u16)
        .collect()
}

/// Loads `audio/smps68k/player.json` and the DAC samples listed in `driver.json`.
pub fn load_audio_assets(archive: &Archive) -> Result<AudioAssets, String> {
    let root = archive.read_json("audio/smps68k/player.json")?;
    let mut a = AudioAssets {
        fm_frequencies: words(&root, "fmFrequencies"),
        psg_frequencies: words(&root, "psgFrequencies"),
        dac_pitches: bytes(&root, "dacPitches"),
        carrier_masks: bytes(&root, "carrierMasks"),
        voice_registers: bytes(&root, "voiceRegisters"),
        level_music: bytes(&root, "levelMusic"),
        level_music2p: bytes(&root, "levelMusic2P"),
        ..AudioAssets::default()
    };
    for s in root["segments"].as_array().into_iter().flatten() {
        let file = s["file"].as_str().unwrap_or("");
        let base = s["baseAddress"].as_u64().unwrap_or(0) as u32;
        a.segments.push((
            base,
            archive.read(&format!("audio/smps68k/{file}"))?.to_vec(),
        ));
    }

    let drv = archive.read_json("audio/smps68k/driver.json")?;
    if let Some(ss) = drv["dac"]["samples"].as_array() {
        for s in ss {
            let p = s["wav"].as_str().unwrap_or("");
            let b = archive.read(&resolve("audio/smps68k", p))?;
            a.dac_samples.push(wav_data(b));
            a.dac_sample_pitches
                .push(s["pitch"].as_i64().unwrap_or(0) as i32);
        }
    }
    a.pcm_effects = load_pcm_effects(archive);
    Ok(a)
}

/// Reads every `campaign/effects/*.pcm` in the archive (signed 16-bit little-endian, 48 kHz mono).
pub fn load_pcm_effects(archive: &Archive) -> Vec<PcmEffect> {
    let mut effects = load_pcm_effects_from_archive(archive);
    // A locally generated `web-data/effects` set adds/overrides the two archive effects.
    for generated in load_pcm_effects_from_dir() {
        if let Some(existing) = effects.iter_mut().find(|e| e.name == generated.name) {
            *existing = generated;
        } else {
            effects.push(generated);
        }
    }
    effects.sort_by(|a, b| a.name.cmp(&b.name));
    effects
}

fn load_pcm_effects_from_archive(archive: &Archive) -> Vec<PcmEffect> {
    let mut names: Vec<&str> = archive
        .names()
        .filter(|n| n.starts_with("campaign/effects/") && n.ends_with(".pcm"))
        .collect();
    names.sort_unstable();
    names
        .into_iter()
        .filter_map(|path| {
            let bytes = archive.read(path).ok()?;
            let name = path
                .rsplit('/')
                .next()
                .unwrap_or(path)
                .trim_end_matches(".pcm")
                .to_string();
            let samples = bytes
                .chunks_exact(2)
                .map(|b| i16::from_le_bytes([b[0], b[1]]))
                .collect();
            Some(PcmEffect { name, samples })
        })
        .collect()
}

fn load_pcm_effects_from_dir() -> Vec<PcmEffect> {
    let candidates = [
        std::path::PathBuf::from("web-data/effects"),
        std::path::PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../web-data/effects"),
    ];
    for dir in candidates {
        let Ok(entries) = std::fs::read_dir(&dir) else {
            continue;
        };
        let mut effects = Vec::new();
        for entry in entries.flatten() {
            let path = entry.path();
            if path.extension().and_then(|e| e.to_str()) != Some("pcm") {
                continue;
            }
            let Some(name) = path.file_stem().and_then(|s| s.to_str()) else {
                continue;
            };
            let Ok(bytes) = std::fs::read(&path) else {
                continue;
            };
            let samples = bytes
                .chunks_exact(2)
                .map(|b| i16::from_le_bytes([b[0], b[1]]))
                .collect();
            effects.push(PcmEffect {
                name: name.to_string(),
                samples,
            });
        }
        if !effects.is_empty() {
            effects.sort_by(|a, b| a.name.cmp(&b.name));
            return effects;
        }
    }
    Vec::new()
}

/// As [`load_audio_assets`], plus the act's SMB3 song (`zones/zone_XX/music.json`) when present.
///
/// `music_file` is the archive path from `level.json`; the Easter-egg zones fall back to their zone directory, as
/// `Camera.IsEasterEgg` does in the legacy world.
pub fn load_audio_assets_for_zone(
    archive: &Archive,
    zone: u8,
    music_file: Option<&str>,
) -> Result<AudioAssets, String> {
    let fallback = format!("zones/zone_{zone:02X}/music.json");
    let path = match music_file {
        Some(p) => Some(p.to_string()),
        None if archive.exists(&fallback) => Some(fallback),
        None => None,
    };
    load_audio_assets_with_song(archive, path.as_deref())
}

/// Loads a campaign area's `music.json` beside `zones/<area.directory>`.
pub fn load_audio_assets_for_area(
    archive: &Archive,
    directory: &str,
) -> Result<AudioAssets, String> {
    load_audio_assets_with_song(archive, Some(&format!("zones/{directory}/music.json")))
}

/// Loads the base driver data plus one converted SMB3 song manifest, if present.
pub fn load_audio_assets_with_song(
    archive: &Archive,
    path: Option<&str>,
) -> Result<AudioAssets, String> {
    let mut a = load_audio_assets(archive)?;
    if let Some(path) = path {
        if archive.exists(path) {
            let manifest = archive.read_json(path)?;
            if let Some(song) = manifest["songs"].as_array().and_then(|s| s.first()) {
                let id = song["id"].as_u64().unwrap_or(0) as u8;
                let address = song["address"].as_u64().unwrap_or(0) as u32;
                let file = song["file"].as_str().unwrap_or("music.bin");
                let dir = path.rsplit_once('/').map(|(d, _)| d).unwrap_or("");
                let data = archive.read(&resolve(dir, file))?.to_vec();
                a.add_extra_song(id, address, data);
                a.extra_music = Some(id as u16);
            }
        }
    }
    Ok(a)
}
