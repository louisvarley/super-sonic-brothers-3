//! A read-only derived asset archive (the ZIP the legacy engine calls `data.bin`/`sonicbros3.bin`).
//!
//! The legacy `DataFile` lets paths point through the archive (`…/data.bin/zones/zone_40/act_1/level.json`). Here the
//! archive is opened explicitly and entries are addressed by their forward-slash names; [`resolve`] implements the
//! `..` handling the JSON files use.

use std::collections::HashMap;
use std::fs::File;
use std::io::Read;
use std::path::Path;

pub struct Archive {
    entries: HashMap<String, Vec<u8>>,
}

impl Archive {
    pub fn open(path: impl AsRef<Path>) -> Result<Self, String> {
        let file =
            File::open(path.as_ref()).map_err(|e| format!("{}: {e}", path.as_ref().display()))?;
        let mut zip = zip::ZipArchive::new(file)
            .map_err(|e| format!("{}: not a ZIP archive: {e}", path.as_ref().display()))?;
        let mut entries = HashMap::new();
        for i in 0..zip.len() {
            let mut entry = zip.by_index(i).map_err(|e| format!("zip entry {i}: {e}"))?;
            if entry.is_dir() {
                continue;
            }
            let name = entry.name().replace('\\', "/");
            let mut data = Vec::with_capacity(entry.size() as usize);
            entry
                .read_to_end(&mut data)
                .map_err(|e| format!("{name}: {e}"))?;
            entries.insert(name, data);
        }
        Ok(Self { entries })
    }

    pub fn exists(&self, path: &str) -> bool {
        self.entries.contains_key(&normalize(path))
    }

    pub fn read(&self, path: &str) -> Result<&[u8], String> {
        let key = normalize(path);
        self.entries
            .get(&key)
            .map(Vec::as_slice)
            .ok_or_else(|| format!("{key}: not in the asset archive"))
    }

    pub fn read_text(&self, path: &str) -> Result<String, String> {
        let bytes = self.read(path)?;
        String::from_utf8(bytes.to_vec()).map_err(|e| format!("{path}: not UTF-8: {e}"))
    }

    pub fn read_json(&self, path: &str) -> Result<serde_json::Value, String> {
        let text = self.read_text(path)?;
        // The extractor writes a BOM on some files (DataFile trims it).
        let text = text.strip_prefix('\u{feff}').unwrap_or(&text);
        serde_json::from_str(text).map_err(|e| format!("{path}: invalid JSON: {e}"))
    }

    pub fn names(&self) -> impl Iterator<Item = &str> {
        self.entries.keys().map(String::as_str)
    }
}

fn normalize(path: &str) -> String {
    path.replace('\\', "/")
}

/// Resolve `rel` against a directory inside the archive, collapsing `.`/`..`.
pub fn resolve(base: &str, rel: &str) -> String {
    let mut parts: Vec<&str> = Vec::new();
    for part in base.split('/').chain(rel.split('/')) {
        match part {
            "" | "." => {}
            ".." => {
                parts.pop();
            }
            _ => parts.push(part),
        }
    }
    parts.join("/")
}

/// `Path.ChangeExtension`: the extractor writes `tiles.png` next to `tiles.json`.
pub fn change_extension(path: &str, extension: &str) -> String {
    match path.rfind('.') {
        Some(i) if !path[i..].contains('/') => format!("{}.{extension}", &path[..i]),
        _ => format!("{path}.{extension}"),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn resolves_parent_segments_inside_the_archive() {
        assert_eq!(
            resolve("zones/zone_40/act_1", "../tiles.png"),
            "zones/zone_40/tiles.png"
        );
        assert_eq!(
            resolve("zones/zone_40/act_1", "../../../palettes/pal_03.json"),
            "palettes/pal_03.json"
        );
        assert_eq!(resolve("a/b", "./c"), "a/b/c");
    }

    #[test]
    fn changes_only_the_file_extension() {
        assert_eq!(
            change_extension("zones/x/tiles.png", "json"),
            "zones/x/tiles.json"
        );
        assert_eq!(change_extension("noext", "json"), "noext.json");
    }
}
