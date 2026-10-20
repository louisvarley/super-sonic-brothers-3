//! One act's level data, loaded from the derived archive exactly as the legacy `LevelLoader` does.
//!
//! Milestone 2 covers terrain (tiles/blocks/chunks/layout), palette, collision indexes and placements. PLC/animated
//! art and the runtime sound/frontend pieces follow in later milestones.

use crate::archive::{change_extension, resolve, Archive};
use crate::{png, tiles};
use engine_core::assets::{BridgeData, GlobalAssets, PaletteCycle, SpiralData, SpringSlopes};
use engine_core::oscillation::OscillationData;
pub use engine_core::world::{Bounds, Start};
use engine_core::{vars, Ram};
use serde_json::Value;

pub const LAYOUT_WIDTH: usize = 128;
pub const LAYOUT_HEIGHT: usize = 16;
pub const BLOCKS_PER_CHUNK_SIDE: usize = 8;
pub const RING_SPACING: i32 = 0x18;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ObjectPlacement {
    pub x: i32,
    pub y: i32,
    pub id: u8,
    pub subtype: u8,
    pub flip_x: bool,
    pub flip_y: bool,
    pub remember: bool,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct RingGroup {
    pub x: i32,
    pub y: i32,
    pub count: u8,
    pub vertical: bool,
}

#[derive(Debug, Clone)]
pub struct LevelData {
    /// Raw Mega Drive 4bpp level art (32 bytes per tile).
    pub art: Vec<u8>,
    pub block_words: Vec<u16>,
    pub chunk_words: Vec<u16>,
    pub foreground: Vec<u8>,
    pub background: Vec<u8>,
    /// 64 CRAM words, the base palette then the act's palettes.
    pub cram: [u16; 64],
    pub collision_primary: Option<Vec<u8>>,
    pub collision_secondary: Option<Vec<u8>>,
    pub bounds: Bounds,
    pub start: Start,
    pub objects: Vec<ObjectPlacement>,
    pub object_list_address: u32,
    pub object_boundary_x: i32,
    pub ring_groups: Vec<RingGroup>,
    /// SMB3 water/pipe metadata (zones `$40+`), if present.
    pub gameplay: Option<Value>,
    /// Archive path of the act's music manifest, if present.
    pub music_file: Option<String>,
    /// Data shared by every act (oscillation, bridge, ripple table, palette cycle).
    pub globals: GlobalAssets,
}

impl LevelData {
    pub fn tile_count(&self) -> usize {
        self.art.len() / tiles::BYTES_PER_TILE
    }

    pub fn block_count(&self) -> usize {
        self.block_words.len() / 4
    }

    pub fn loaded_chunk_count(&self) -> usize {
        self.chunk_words.len() / 64
    }

    pub fn load(archive: &Archive, zone: u16, act: u16) -> Result<Self, String> {
        let dir = format!("zones/zone_{zone:02X}/act_{act}");
        let level = archive.read_json(&format!("{dir}/level.json"))?;

        // ---- tiles ----
        let tiles_rel = text(&level, "tiles")?;
        let tiles_png = resolve(&dir, tiles_rel);
        let tiles_meta = archive.read_json(&change_extension(&tiles_png, "json"))?;
        let tile_count = tiles_meta["tileCount"]
            .as_u64()
            .ok_or_else(|| format!("{tiles_png}: tileCount missing"))?
            as usize;
        let (width, _, pixels) = png::read_indexed(archive.read(&tiles_png)?)?;
        let art = tiles::from_sheet(&pixels, width as usize, tile_count);

        // ---- blocks ----
        let blocks = archive.read_json(&resolve(&dir, text(&level, "blocks")?))?;
        let mut block_words = Vec::new();
        for block in array(&blocks, "blocks")? {
            for pattern in block.as_array().ok_or("blocks: pattern is not an array")? {
                block_words.push(packed_word(
                    pattern
                        .as_array()
                        .ok_or("blocks: fields are not an array")?,
                    &[0, 13, 11, 12, 15],
                ));
            }
        }

        // ---- chunks ----
        let chunks = archive.read_json(&resolve(&dir, text(&level, "chunks")?))?;
        let mut chunk_words = Vec::new();
        for chunk in array(&chunks, "chunks")? {
            for row in chunk.as_array().ok_or("chunks: row is not an array")? {
                for cell in row.as_array().ok_or("chunks: cell is not an array")? {
                    chunk_words.push(packed_word(
                        cell.as_array().ok_or("chunks: fields are not an array")?,
                        &[0, 10, 11, 12, 14],
                    ));
                }
            }
        }

        // ---- layout ----
        let layout = archive.read_json(&resolve(&dir, text(&level, "layout")?))?;
        let foreground = flatten_plane(&layout["foreground"])?;
        let background = flatten_plane(&layout["background"])?;

        // ---- palette (base palettes then the act's own) ----
        let mut cram = [0u16; 64];
        if let Some(palettes) = level["palettes"].as_array() {
            for p in palettes {
                let path = resolve(&dir, p.as_str().ok_or("palettes: entry is not a string")?);
                let pal = archive.read_json(&path)?;
                let start = pal["startColour"].as_u64().unwrap_or(0) as usize;
                if let Some(colours) = pal["colours"].as_array() {
                    for (i, c) in colours.iter().enumerate() {
                        let word = c["word"].as_str().ok_or("palette colour word missing")?;
                        let value = u16::from_str_radix(word.trim_start_matches("0x"), 16)
                            .map_err(|e| format!("{path}: colour {word}: {e}"))?;
                        if start + i < cram.len() {
                            cram[start + i] = value;
                        }
                    }
                }
            }
        }

        // ---- collision indexes ----
        let (mut collision_primary, mut collision_secondary) = (None, None);
        if let Some(col_ref) = level["collision"].as_str() {
            let col = archive.read_json(&resolve(&dir, col_ref))?;
            collision_primary = Some(u8_array(&col["primary"])?);
            collision_secondary = Some(u8_array(&col["secondary"])?);
        }

        // ---- bounds / start ----
        let b = &level["bounds"];
        let bounds = Bounds {
            min_x: int(b, "minX"),
            max_x: int(b, "maxX"),
            min_y: int(b, "minY"),
            max_y: int(b, "maxY"),
        };
        let st = &level["start"];
        let start = Start {
            x: int(st, "x"),
            y: int(st, "y"),
        };

        // ---- placements ----
        let mut objects = Vec::new();
        let (mut object_list_address, mut object_boundary_x) = (0, 0);
        if let Some(obj_ref) = level["objects"].as_str() {
            let obj = archive.read_json(&resolve(&dir, obj_ref))?;
            object_list_address = obj["listAddress"].as_u64().unwrap_or(0) as u32;
            object_boundary_x = obj["boundaryX"].as_i64().unwrap_or(0) as i32;
            for o in array(&obj, "objects")? {
                let a = o.as_array().ok_or("objects: entry is not an array")?;
                objects.push(ObjectPlacement {
                    x: field_i32(a, 0),
                    y: field_i32(a, 1),
                    id: field_i32(a, 2) as u8,
                    subtype: field_i32(a, 3) as u8,
                    flip_x: field_i32(a, 4) != 0,
                    flip_y: field_i32(a, 5) != 0,
                    remember: field_i32(a, 6) != 0,
                });
            }
        }

        let mut ring_groups = Vec::new();
        if let Some(ring_ref) = level["rings"].as_str() {
            let rings = archive.read_json(&resolve(&dir, ring_ref))?;
            for g in array(&rings, "groups")? {
                let a = g.as_array().ok_or("rings: group is not an array")?;
                ring_groups.push(RingGroup {
                    x: field_i32(a, 0),
                    y: field_i32(a, 1),
                    count: field_i32(a, 2) as u8,
                    vertical: field_i32(a, 3) != 0,
                });
            }
        }

        let gameplay = level["gameplay"]
            .as_str()
            .map(|p| archive.read_json(&resolve(&dir, p)))
            .transpose()?;
        let music_file = level["musicFile"].as_str().map(|p| resolve(&dir, p));

        // ---- data shared by every act ----
        let mut globals = GlobalAssets::default();
        if let Ok(osc) = archive.read_json("objects/oscillation.json") {
            let section = if zone >= 0x11 {
                &osc["hack"]
            } else {
                &osc["normal"]
            };
            globals.oscillation = Some(OscillationData {
                initial: word_array(&section["initial"]).unwrap_or_default(),
                parameters: word_array(&section["parameters"]).unwrap_or_default(),
            });
        }
        if let Ok(bridge) = archive.read_json("objects/bridge/bridge.json") {
            globals.bridge = Some(BridgeData {
                depression: u8_array(&bridge["depression"]).unwrap_or_default(),
                bend: u8_array(&bridge["bend"]).unwrap_or_default(),
                depression_first_subtype: bridge["depressionFirstSubtype"].as_i64().unwrap_or(0)
                    as i32,
            });
        }
        if let (Ok(data), Ok(edge)) = (
            archive.read_json("objects/spring/diagonal-slopes.json"),
            archive.read_json("objects/spring/diagonal-slopes-edge.json"),
        ) {
            let bytes = u8_array(&data["data"]).unwrap_or_default();
            let edge_bytes = u8_array(&edge["data"]).unwrap_or_default();
            let mut up = vec![0i8; 0x8000];
            let mut down = vec![0i8; 0x8000];
            for i in 0..0x1C.min(bytes.len()) {
                up[i] = bytes[i] as i8;
                if let Some(b) = bytes.get(0x1C + i) {
                    down[i] = *b as i8;
                }
            }
            up[0x7FFF] = edge_bytes.first().copied().unwrap_or(0) as i8;
            down[0x7FFF] = edge_bytes.get(0x1C).copied().unwrap_or(0) as i8;
            globals.spring_slopes = Some(SpringSlopes { up, down });
        }
        if let Ok(wave) = archive.read_json("objects/frontend/title-scroll.json") {
            globals.wave_table = u8_array(&wave["data"]).unwrap_or_default();
        }
        if let Ok(heights) = archive.read_json("objects/mz-grass-platform/heights.json") {
            globals.mz_heights = u8_array(&heights["data"]).unwrap_or_default();
        }
        if let Ok(wave) = archive.read_json("objects/mz-caterkiller/wave.json") {
            globals.mz_wave = u8_array(&wave["data"]).unwrap_or_default();
        }
        if let Ok(order) = archive.read_json("objects/signpost/level-order.json") {
            globals.level_order = u8_array(&order["data"]).unwrap_or_default();
        }
        if let Ok(lava) = archive.read_json("objects/lava-line/colours.json") {
            globals.lava_colours = u8_array(&lava["data"]).unwrap_or_default();
        }
        if let Ok(ledge) = archive.read_json("objects/ledge/ledge.json") {
            globals.ledge_slope = i8_array(&ledge["slope"]).unwrap_or_default();
            globals.ledge_delays = u8_array(&ledge["delays"]).unwrap_or_default();
        }
        if let (Ok(heights), Ok(angles)) = (
            archive.read_json("objects/spiral/heights.json"),
            archive.read_json("objects/spiral/flip-angles.json"),
        ) {
            globals.spiral = Some(SpiralData {
                heights: u8_array(&heights["data"]).unwrap_or_default(),
                flip_angles: u8_array(&angles["data"]).unwrap_or_default(),
            });
        }
        if let Some(cycle_ref) = level["paletteCycle"].as_str() {
            let cycle = archive.read_json(&resolve(&dir, cycle_ref))?;
            globals.palette_cycle = Some(PaletteCycle {
                colours: cycle["colours"]
                    .as_array()
                    .map(|a| a.iter().map(|v| v.as_u64().unwrap_or(0) as usize).collect())
                    .unwrap_or_default(),
                timer_reload: cycle["timerReload"].as_i64().unwrap_or(0) as i16,
                frames: cycle["frames"]
                    .as_array()
                    .map(|rows| {
                        rows.iter()
                            .map(|r| word_array(r).unwrap_or_default())
                            .collect()
                    })
                    .unwrap_or_default(),
            });
        }

        Ok(Self {
            art,
            block_words,
            chunk_words,
            foreground,
            background,
            cram,
            collision_primary,
            collision_secondary,
            bounds,
            start,
            objects,
            object_list_address,
            object_boundary_x,
            ring_groups,
            gameplay,
            music_file,
            globals,
        })
    }

    /// The level-data part of `World`'s RAM setup (chunks, layout, blocks, collision, palette, zone/act, hack flag).
    /// This is the region the milestone-2 oracle hashes.
    pub fn fill_level_ram(&self, ram: &mut Ram, zone: u8, act: u8) {
        for (i, w) in self.chunk_words.iter().enumerate() {
            if i * 2 < 0x8000 {
                ram.w16(vars::CHUNK_TABLE + (i as u16) * 2, *w);
            }
        }
        for row in 0..LAYOUT_HEIGHT {
            for x in 0..LAYOUT_WIDTH {
                ram.w8(
                    vars::LEVEL_LAYOUT + (row * 0x100 + x) as u16,
                    self.foreground[row * LAYOUT_WIDTH + x],
                );
                ram.w8(
                    vars::LEVEL_LAYOUT + (row * 0x100 + 0x80 + x) as u16,
                    self.background[row * LAYOUT_WIDTH + x],
                );
            }
        }
        for (i, w) in self.block_words.iter().enumerate() {
            ram.w16(vars::BLOCK_TABLE + (i as u16) * 2, *w);
        }
        if let Some(primary) = &self.collision_primary {
            for (i, b) in primary.iter().take(0x300).enumerate() {
                ram.w8(vars::PRIMARY_COLLISION + i as u16, *b);
            }
        }
        if let Some(secondary) = &self.collision_secondary {
            for (i, b) in secondary.iter().take(0x300).enumerate() {
                ram.w8(vars::SECONDARY_COLLISION + i as u16, *b);
            }
        }
        ram.w16(
            vars::CURRENT_ZONE_AND_ACT,
            ((zone as u16) << 8) | ((act.wrapping_sub(1)) as u16 & 0xFF),
        );
        for (i, c) in self.cram.iter().enumerate() {
            ram.w16(vars::NORMAL_PALETTE + (i as u16) * 2, *c);
        }
        ram.w8(vars::HACK_ZONE_FLAG, if zone >= 0x11 { 1 } else { 0 });
    }
}

impl engine_core::world::LevelSource for LevelData {
    fn fill_level_ram(&self, ram: &mut Ram, zone: u8, act: u8) {
        LevelData::fill_level_ram(self, ram, zone, act);
    }

    fn bounds(&self) -> Bounds {
        self.bounds
    }

    fn start(&self) -> Start {
        self.start
    }

    fn water_map(&self) -> Option<engine_core::player::WaterMap> {
        let gameplay = self.gameplay.as_ref()?;
        let columns = gameplay["columns"].as_i64()? as i32;
        let rows = gameplay["rows"].as_i64()? as i32;
        let tileset = gameplay["tileset"].as_i64().unwrap_or(0) as i32;
        let tiles = gameplay["tiles"]
            .as_array()?
            .iter()
            .map(|t| t.as_u64().unwrap_or(0) as u8)
            .collect();
        Some(engine_core::player::WaterMap {
            columns,
            rows,
            tileset,
            tiles,
        })
    }

    fn objects(&self) -> Vec<engine_core::world::ObjectSpawn> {
        self.objects
            .iter()
            .map(|o| engine_core::world::ObjectSpawn {
                x: o.x,
                y: o.y,
                id: o.id,
                subtype: o.subtype,
                flip_x: o.flip_x,
                flip_y: o.flip_y,
                remember: o.remember,
            })
            .collect()
    }

    fn object_list_address(&self) -> u32 {
        self.object_list_address
    }

    fn object_boundary_x(&self) -> i32 {
        self.object_boundary_x
    }

    fn rings(&self) -> Vec<engine_core::world::RingSpawn> {
        self.ring_groups
            .iter()
            .map(|g| engine_core::world::RingSpawn {
                x: g.x,
                y: g.y,
                count: g.count,
                vertical: g.vertical,
            })
            .collect()
    }

    fn globals(&self) -> GlobalAssets {
        self.globals.clone()
    }
}

fn text<'a>(v: &'a Value, key: &str) -> Result<&'a str, String> {
    v[key]
        .as_str()
        .ok_or_else(|| format!("level.json: {key} is not a string"))
}

fn int(v: &Value, key: &str) -> i32 {
    v[key].as_i64().unwrap_or(0) as i32
}

fn array<'a>(v: &'a Value, key: &str) -> Result<&'a Vec<Value>, String> {
    v[key]
        .as_array()
        .ok_or_else(|| format!("{key} is not an array"))
}

fn field_i32(a: &[Value], index: usize) -> i32 {
    a.get(index).and_then(Value::as_i64).unwrap_or(0) as i32
}

fn packed_word(fields: &[Value], shifts: &[u32; 5]) -> u16 {
    let mut word = 0u32;
    for (i, shift) in shifts.iter().enumerate() {
        let value = fields.get(i).and_then(Value::as_u64).unwrap_or(0) as u32;
        word |= value << shift;
    }
    word as u16
}

fn flatten_plane(v: &Value) -> Result<Vec<u8>, String> {
    let rows = v.as_array().ok_or("layout plane is not an array")?;
    let mut out = Vec::with_capacity(LAYOUT_WIDTH * LAYOUT_HEIGHT);
    for row in rows {
        for cell in row.as_array().ok_or("layout row is not an array")? {
            out.push(cell.as_u64().unwrap_or(0) as u8);
        }
    }
    if out.len() != LAYOUT_WIDTH * LAYOUT_HEIGHT {
        return Err(format!(
            "layout plane has {} cells, expected {}",
            out.len(),
            LAYOUT_WIDTH * LAYOUT_HEIGHT
        ));
    }
    Ok(out)
}

fn u8_array(v: &Value) -> Result<Vec<u8>, String> {
    v.as_array()
        .ok_or("expected an array of bytes")?
        .iter()
        .map(|x| {
            x.as_u64()
                .map(|n| n as u8)
                .ok_or_else(|| "expected a byte value".to_string())
        })
        .collect()
}

fn word_array(v: &Value) -> Result<Vec<u16>, String> {
    v.as_array()
        .ok_or("expected an array of words")?
        .iter()
        .map(|x| {
            x.as_u64()
                .map(|n| n as u16)
                .ok_or_else(|| "expected a word value".to_string())
        })
        .collect()
}

fn i8_array(v: &Value) -> Result<Vec<i8>, String> {
    v.as_array()
        .ok_or("expected an array of signed bytes")?
        .iter()
        .map(|x| {
            x.as_i64()
                .map(|n| n as i8)
                .ok_or_else(|| "expected a signed byte value".to_string())
        })
        .collect()
}
