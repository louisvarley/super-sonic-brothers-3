//! The SMB3 campaign manifest: all 8 worlds, their 19 map screens and the 257 converted areas.

use crate::archive::Archive;
use crate::level::LevelData;
use serde_json::Value;

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct CampaignSource {
    pub id: String,
    pub tileset: u8,
    pub layout_bank: u8,
    pub layout_address: u16,
    pub object_bank: u8,
    pub object_address: u16,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct CampaignArea {
    pub act: u16,
    pub source: CampaignSource,
    pub directory: String,
    pub status: String,
    pub unmapped_objects: Vec<u8>,
    pub blockers: Vec<String>,
    pub width: i32,
    pub height: i32,
    pub alternate_area_id: Option<String>,
    /// First map owner of this area (world 1-8, screen 0+, row/column in the 16x9 layout).
    pub world: u8,
    pub screen: u8,
    pub row: i32,
    pub column: i32,
}

impl CampaignArea {
    pub fn available(&self) -> bool {
        self.status != "failed" && self.status != "blocked"
    }

    /// The 16x9 layout tile this area occupies on its owning screen.
    pub fn map_tile(&self) -> i32 {
        self.row * 16 + self.column
    }

    pub fn archive_dir(&self) -> String {
        format!("zones/{}/act_1", self.directory)
    }

    pub fn load_level(&self, archive: &Archive) -> Result<LevelData, String> {
        if !self.available() {
            return Err(format!(
                "{} is unavailable ({}): {}",
                self.source.id,
                self.status,
                self.blockers.join(" ")
            ));
        }
        LevelData::load_dir(archive, &self.archive_dir())
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct CampaignEntry {
    pub index: i32,
    pub screen: usize,
    pub row: i32,
    pub column: i32,
    pub tileset: u8,
    pub layout_address: u16,
    pub object_address: u16,
    pub area_id: Option<String>,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct CampaignWorld {
    pub number: u8,
    /// 16x9 layout cells for each screen, concatenated.
    pub map: Vec<u8>,
    pub entries: Vec<CampaignEntry>,
}

impl CampaignWorld {
    pub fn screen_count(&self) -> usize {
        self.map.len() / 144
    }

    pub fn screen(&self, screen: usize) -> Option<&[u8]> {
        let start = screen.checked_mul(144)?;
        self.map.get(start..start + 144)
    }

    pub fn entries_on(&self, screen: usize) -> impl Iterator<Item = &CampaignEntry> {
        self.entries.iter().filter(move |e| e.screen == screen)
    }
}

#[derive(Clone, Debug)]
pub struct CampaignData {
    pub version: i32,
    pub rom_sha1: String,
    pub base_sha256: String,
    pub worlds: Vec<CampaignWorld>,
    pub areas: Vec<CampaignArea>,
    pub blockers: Vec<String>,
    pub complete: bool,
}

impl CampaignData {
    pub fn load(archive: &Archive) -> Result<Self, String> {
        let root = archive.read_json("campaign/campaign.json")?;
        let worlds = root["worlds"]
            .as_array()
            .ok_or("campaign.json: worlds missing")?
            .iter()
            .map(parse_world)
            .collect::<Result<Vec<_>, String>>()?;
        let mut areas = root["areas"]
            .as_array()
            .ok_or("campaign.json: areas missing")?
            .iter()
            .map(parse_area)
            .collect::<Result<Vec<_>, String>>()?;

        // The manifest lists areas globally; the map entries are the ownership record.
        for world in &worlds {
            for entry in &world.entries {
                let Some(id) = &entry.area_id else { continue };
                if let Some(area) = areas.iter_mut().find(|a| &a.source.id == id) {
                    if area.world == 0 {
                        area.world = world.number;
                        area.screen = entry.screen as u8;
                        area.row = entry.row;
                        area.column = entry.column;
                    }
                }
            }
        }

        Ok(Self {
            version: int(&root, "version"),
            rom_sha1: text(&root, "romSha1"),
            base_sha256: text(&root, "baseSha256"),
            worlds,
            areas,
            blockers: string_array(&root["blockers"]),
            complete: root["complete"].as_bool().unwrap_or(false),
        })
    }

    pub fn world(&self, number: u8) -> Option<&CampaignWorld> {
        self.worlds.iter().find(|w| w.number == number)
    }

    pub fn area_for_act(&self, act: u16) -> Option<&CampaignArea> {
        self.areas.iter().find(|a| a.act == act)
    }

    pub fn area_for_id(&self, id: &str) -> Option<&CampaignArea> {
        self.areas.iter().find(|a| a.source.id == id)
    }

    pub fn available_areas(&self) -> impl Iterator<Item = &CampaignArea> {
        self.areas.iter().filter(|a| a.available())
    }

    pub fn area_at(&self, world: u8, screen: u8, tile: i32) -> Option<&CampaignArea> {
        self.areas.iter().find(|a| {
            a.world == world && a.screen == screen && a.map_tile() == tile && a.available()
        })
    }

    pub fn entry_at(&self, world: u8, screen: u8, tile: i32) -> Option<&CampaignEntry> {
        self.world(world)?.entries.iter().find(|e| {
            e.screen == screen as usize && e.row * 16 + e.column == tile && e.area_id.is_some()
        })
    }

    pub fn load_area(&self, archive: &Archive, act: u16) -> Result<LevelData, String> {
        self.area_for_act(act)
            .ok_or_else(|| format!("unknown campaign act {act}"))?
            .load_level(archive)
    }
}

fn parse_world(v: &Value) -> Result<CampaignWorld, String> {
    let number = v["number"].as_u64().unwrap_or(0) as u8;
    let map = v["map"]
        .as_array()
        .ok_or_else(|| format!("world {number}: map missing"))?
        .iter()
        .map(|c| c.as_u64().unwrap_or(0) as u8)
        .collect::<Vec<_>>();
    if map.len() % 144 != 0 {
        return Err(format!(
            "world {number}: map length {} is not 16x9 screens",
            map.len()
        ));
    }
    let entries = v["entries"]
        .as_array()
        .into_iter()
        .flatten()
        .map(|e| CampaignEntry {
            index: int(e, "index"),
            screen: e["screen"].as_u64().unwrap_or(0) as usize,
            row: int(e, "row"),
            column: int(e, "column"),
            tileset: e["tileset"].as_u64().unwrap_or(0) as u8,
            layout_address: e["layoutAddress"].as_u64().unwrap_or(0) as u16,
            object_address: e["objectAddress"].as_u64().unwrap_or(0) as u16,
            area_id: e["areaId"].as_str().map(str::to_owned),
        })
        .collect();
    Ok(CampaignWorld {
        number,
        map,
        entries,
    })
}

fn parse_area(v: &Value) -> Result<CampaignArea, String> {
    let source = &v["source"];
    Ok(CampaignArea {
        act: v["act"].as_u64().unwrap_or(0) as u16,
        source: CampaignSource {
            id: text(source, "id"),
            tileset: source["tileset"].as_u64().unwrap_or(0) as u8,
            layout_bank: source["layoutBank"].as_u64().unwrap_or(0) as u8,
            layout_address: source["layoutAddress"].as_u64().unwrap_or(0) as u16,
            object_bank: source["objectBank"].as_u64().unwrap_or(0) as u8,
            object_address: source["objectAddress"].as_u64().unwrap_or(0) as u16,
        },
        directory: text(v, "directory"),
        status: text(v, "status"),
        unmapped_objects: v["unmappedObjects"]
            .as_array()
            .into_iter()
            .flatten()
            .map(|x| x.as_u64().unwrap_or(0) as u8)
            .collect(),
        blockers: string_array(&v["blockers"]),
        width: int(v, "width"),
        height: int(v, "height"),
        alternate_area_id: v["alternateAreaId"].as_str().map(str::to_owned),
        world: 0,
        screen: 0,
        row: 0,
        column: 0,
    })
}

fn text(v: &Value, key: &str) -> String {
    v[key].as_str().unwrap_or("").to_string()
}

fn int(v: &Value, key: &str) -> i32 {
    v[key].as_i64().unwrap_or(0) as i32
}

fn string_array(v: &Value) -> Vec<String> {
    v.as_array()
        .into_iter()
        .flatten()
        .filter_map(|s| s.as_str().map(str::to_owned))
        .collect()
}
