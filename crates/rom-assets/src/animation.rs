//! Loads Sonic's animation block and script tables (`objects/sonic/animations.json`).
//!
//! The JSON carries both the raw `data` block and pre-parsed scripts. The engine uses the raw block with the
//! `normalTable`/`superTable` offsets, exactly as the legacy `SonicAnimation.Load` does, because script windows may
//! legally run into the following script's bytes.

use crate::archive::Archive;
use engine_core::animation::SonicAnimation;

pub fn load_sonic_animation(archive: &Archive) -> Result<SonicAnimation, String> {
    let path = "objects/sonic/animations.json";
    let value = archive.read_json(path)?;
    let block: Vec<u8> = value["data"]
        .as_array()
        .ok_or_else(|| format!("{path}: data is not an array"))?
        .iter()
        .map(|v| {
            v.as_u64()
                .map(|n| n as u8)
                .ok_or_else(|| format!("{path}: data holds a non-integer"))
        })
        .collect::<Result<_, _>>()?;
    let normal = value["normalTable"]
        .as_u64()
        .ok_or_else(|| format!("{path}: normalTable missing"))? as usize;
    let super_ = value["superTable"]
        .as_u64()
        .ok_or_else(|| format!("{path}: superTable missing"))? as usize;
    Ok(SonicAnimation::from_block(block, normal, super_))
}
