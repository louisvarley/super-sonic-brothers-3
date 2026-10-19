//! Loads the extracted angle/height/width collision sets (`collision/{normal,alternate}.json`).

use crate::archive::Archive;
use engine_core::collision::{CollisionSet, CollisionSets};
use serde_json::Value;

pub fn load_collision_sets(archive: &Archive) -> Result<CollisionSets, String> {
    Ok(CollisionSets {
        normal: load_set(archive, "collision/normal.json")?,
        alternate: load_set(archive, "collision/alternate.json")?,
    })
}

fn load_set(archive: &Archive, path: &str) -> Result<CollisionSet, String> {
    let value = archive.read_json(path)?;
    let angles = u8_flat(&value["angles"])?;
    let heights = sbyte_flat(&value["heights"])?;
    let widths = sbyte_flat(&value["widths"])?;
    if angles.len() != 256 {
        return Err(format!("{path}: {} angles, expected 256", angles.len()));
    }
    if heights.len() != 256 * 16 || widths.len() != 256 * 16 {
        return Err(format!(
            "{path}: heights/widths {},{}; expected 4096 each",
            heights.len(),
            widths.len()
        ));
    }
    Ok(CollisionSet {
        name: path.to_string(),
        angles,
        heights,
        widths,
    })
}

fn u8_flat(value: &Value) -> Result<Vec<u8>, String> {
    value
        .as_array()
        .ok_or("expected an array")?
        .iter()
        .map(|v| {
            v.as_u64()
                .map(|n| n as u8)
                .ok_or_else(|| "expected an integer".to_string())
        })
        .collect()
}

fn sbyte_flat(value: &Value) -> Result<Vec<i8>, String> {
    let rows = value.as_array().ok_or("expected a row array")?;
    let mut out = Vec::new();
    for row in rows {
        for v in row.as_array().ok_or("expected a row array")? {
            out.push(v.as_i64().ok_or("expected an integer")? as i8);
        }
    }
    Ok(out)
}
