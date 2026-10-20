//! One act's level data, loaded from the derived archive exactly as the legacy `LevelLoader` does.
//!
//! Milestone 2 covers terrain (tiles/blocks/chunks/layout), palette, collision indexes and placements. PLC/animated
//! art and the runtime sound/frontend pieces follow in later milestones.

use crate::archive::{change_extension, resolve, Archive};
use crate::{png, tiles};
use engine_core::assets::{BridgeData, GlobalAssets, PaletteCycle, SpiralData, SpringSlopes};
use engine_core::graphics::{
    AnimatedScript, Graphics, HudData, MappingPiece, PlcEntry, PlcList, RenderGeometry, SpriteBank,
    SpriteData,
};
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
    /// Software-VDP graphics data.
    pub graphics: Graphics,
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
        Self::load_dir(archive, &dir)
    }

    /// Loads one act from an explicit archive directory (campaign areas use `zones/area_*/act_1`).
    pub fn load_dir(archive: &Archive, dir: &str) -> Result<Self, String> {
        let level = archive.read_json(&format!("{dir}/level.json"))?;
        let zone = level["zone"].as_u64().unwrap_or(0) as u16;

        // ---- tiles ----
        let tiles_rel = text(&level, "tiles")?;
        let tiles_png = resolve(dir, tiles_rel);
        let tiles_meta = archive.read_json(&change_extension(&tiles_png, "json"))?;
        let tile_count = tiles_meta["tileCount"]
            .as_u64()
            .ok_or_else(|| format!("{tiles_png}: tileCount missing"))?
            as usize;
        let (width, _, pixels) = png::read_indexed(archive.read(&tiles_png)?)?;
        let art = tiles::from_sheet(&pixels, width as usize, tile_count);

        // ---- blocks ----
        let blocks = archive.read_json(&resolve(dir, text(&level, "blocks")?))?;
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
        let chunks = archive.read_json(&resolve(dir, text(&level, "chunks")?))?;
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
        let layout = archive.read_json(&resolve(dir, text(&level, "layout")?))?;
        let foreground = flatten_plane(&layout["foreground"])?;
        let background = flatten_plane(&layout["background"])?;

        // ---- palette (base palettes then the act's own) ----
        let mut cram = [0u16; 64];
        if let Some(palettes) = level["palettes"].as_array() {
            for p in palettes {
                let path = resolve(dir, p.as_str().ok_or("palettes: entry is not a string")?);
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
            let col = archive.read_json(&resolve(dir, col_ref))?;
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
            let obj = archive.read_json(&resolve(dir, obj_ref))?;
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
            let rings = archive.read_json(&resolve(dir, ring_ref))?;
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
            .map(|p| archive.read_json(&resolve(dir, p)))
            .transpose()?;
        let music_file = level["musicFile"].as_str().map(|p| resolve(dir, p));

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
            let cycle = archive.read_json(&resolve(dir, cycle_ref))?;
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

        let mut graphics = load_graphics(archive, dir, &level, &art)?;
        graphics.geometry = RenderGeometry {
            foreground: foreground.clone(),
            background: background.clone(),
            chunks: chunk_words.clone(),
            blocks: block_words.clone(),
        };

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
            graphics,
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

    fn gameplay(&self) -> Option<engine_core::world::Gameplay> {
        let gameplay = self.gameplay.as_ref()?;
        let pipes = gameplay["pipes"]
            .as_array()
            .map(|pipes| {
                pipes
                    .iter()
                    .map(|p| engine_core::world::GameplayPipe {
                        x: p["x"].as_i64().unwrap_or(0) as i32,
                        y: p["y"].as_i64().unwrap_or(0) as i32,
                        direction: p["direction"].as_i64().unwrap_or(0) as i32,
                        kind: p["kind"].as_str().unwrap_or("").to_string(),
                        target_x: p["targetX"].as_i64().unwrap_or(0) as i32,
                        target_y: p["targetY"].as_i64().unwrap_or(0) as i32,
                        exit_direction: p["exitDirection"].as_i64().unwrap_or(0) as i32,
                        path: p["path"]
                            .as_array()
                            .map(|path| {
                                path.iter()
                                    .map(|p| {
                                        [
                                            p[0].as_i64().unwrap_or(0) as i32,
                                            p[1].as_i64().unwrap_or(0) as i32,
                                        ]
                                    })
                                    .collect()
                            })
                            .unwrap_or_default(),
                    })
                    .collect()
            })
            .unwrap_or_default();
        Some(engine_core::world::Gameplay {
            pipes,
            initial_exit_direction: gameplay["initialExitDirection"].as_i64().unwrap_or(0) as i32,
            map_pipeway: gameplay["mapPipeway"].as_bool().unwrap_or(false),
        })
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

    fn graphics(&self) -> Graphics {
        self.graphics.clone()
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

fn load_graphics(
    archive: &Archive,
    dir: &str,
    level: &Value,
    art: &[u8],
) -> Result<Graphics, String> {
    let mut graphics = Graphics {
        vram: vec![0u8; 0x10000],
        ..Graphics::default()
    };
    // The level art always forms tiles 0.., whether or not the act has a PLC list.
    let n = art.len().min(0x10000);
    graphics.vram[..n].copy_from_slice(&art[..n]);

    if let Some(plcs) = level["plcs"].as_object() {
        let lists_ref = plcs
            .get("lists")
            .and_then(Value::as_str)
            .ok_or("plcs.lists missing")?;
        let lists_path = resolve(dir, lists_ref);
        graphics.plc_lists = load_plc_lists(archive, &lists_path)?;
        let mut ids = Vec::new();
        for key in ["fromTitleScreen", "onLoad", "afterTitleCard"] {
            if let Some(a) = plcs.get(key).and_then(Value::as_array) {
                ids.extend(a.iter().filter_map(|v| v.as_u64().map(|n| n as usize)));
            }
        }
        for id in ids {
            apply_plc(&mut graphics.vram, &graphics.plc_lists, id);
        }
    }

    if let Some(ani_ref) = level["animatedTiles"].as_str() {
        let ani_path = resolve(dir, ani_ref);
        let ani_dir = ani_path.rsplit_once('/').map(|(d, _)| d).unwrap_or("");
        let ani = archive.read_json(&ani_path)?;
        if let Some(mg) = ani["magma"].as_object() {
            if let Some(art_path) = mg.get("art").and_then(Value::as_str) {
                let bytes = archive.read(&resolve(ani_dir, art_path))?;
                graphics.magma_art = bytes.to_vec();
                graphics.magma_vram_tile =
                    mg.get("vramTile").and_then(Value::as_u64).unwrap_or(0) as usize;
            }
        }
        if let Some(scripts) = ani["scripts"].as_array() {
            for sc in scripts {
                let art_path = sc["art"].as_str().ok_or("animated script art missing")?;
                let tiles = sc["artTiles"].as_u64().unwrap_or(0) as usize;
                let script_art = load_tile_art(archive, &resolve(ani_dir, art_path), tiles)?;
                graphics.animated.push(AnimatedScript {
                    art: script_art,
                    vram_tile: sc["vramTile"].as_u64().unwrap_or(0) as usize,
                    tiles_per_frame: sc["tilesPerFrame"].as_u64().unwrap_or(0) as usize,
                    frames: sc["frames"]
                        .as_array()
                        .map(|fs| {
                            fs.iter()
                                .map(|f| {
                                    (
                                        f.get(0).and_then(Value::as_u64).unwrap_or(0) as usize,
                                        f.get(1).and_then(Value::as_u64).unwrap_or(0) as u8,
                                    )
                                })
                                .collect()
                        })
                        .unwrap_or_default(),
                });
            }
        }
    }
    for script in &graphics.animated {
        if let Some((offset, _)) = script.frames.first() {
            let src = offset * 32;
            let dst = script.vram_tile * 32;
            let count = script.tiles_per_frame * 32;
            if src + count <= script.art.len() && dst + count <= graphics.vram.len() {
                graphics.vram[dst..dst + count].copy_from_slice(&script.art[src..src + count]);
            }
        }
    }

    graphics.hud = load_hud(archive)?;
    graphics.sonic = load_sprite_file(archive, "objects/sonic/sprites.json")?;
    graphics.tails = load_sprite_file(archive, "objects/tails/sprites.json")?;
    graphics.rings = load_sprite_file(archive, "objects/rings/sprites.json")?;
    let animals = archive.read_json("objects/animal/animals.json")?;
    graphics.animals = engine_core::graphics::AnimalData {
        zone_types: animals["zoneTypes"]
            .as_array()
            .map(|a| a.iter().map(|v| v.as_u64().unwrap_or(0) as u8).collect())
            .unwrap_or_default(),
        types: animals["types"]
            .as_array()
            .map(|a| {
                a.iter()
                    .map(|t| {
                        (
                            t.get(0).and_then(Value::as_i64).unwrap_or(0) as i16,
                            t.get(1).and_then(Value::as_i64).unwrap_or(0) as i16,
                            t.get(2).and_then(Value::as_i64).unwrap_or(0) as u32,
                        )
                    })
                    .collect()
            })
            .unwrap_or_default(),
    };

    let mut bank = SpriteBank::default();
    let names: Vec<String> = archive
        .names()
        .filter(|n| n.starts_with("objects/") && n.ends_with("/sprites.json"))
        .map(ToOwned::to_owned)
        .collect();
    for name in names {
        if let Ok(data) = load_sprite_file(archive, &name) {
            if data.rom_pointer != 0 {
                bank.add(data);
            }
        }
    }
    graphics.bank = bank;
    let _ = level;
    Ok(graphics)
}

fn load_plc_lists(archive: &Archive, path: &str) -> Result<Vec<PlcList>, String> {
    let root = archive.read_json(path)?;
    let lists = root["lists"].as_array().ok_or("PLC lists missing")?;
    let dir = path.rsplit_once('/').map(|(d, _)| d).unwrap_or("");
    let mut out = Vec::with_capacity(lists.len());
    for list in lists {
        let mut entries = Vec::new();
        for e in list["entries"].as_array().into_iter().flatten() {
            let art_path = e["art"].as_str().unwrap_or("");
            let tiles = e["tiles"].as_u64().unwrap_or(0) as usize;
            let art = load_tile_art(archive, &resolve(dir, art_path), tiles)?;
            let stem = art_path
                .rsplit('/')
                .next()
                .unwrap_or("")
                .split('.')
                .next()
                .unwrap_or("");
            let address = if let Some(hex) = stem.strip_prefix("nem_") {
                u32::from_str_radix(hex, 16).unwrap_or(0xF00000)
            } else {
                0xF00000
            };
            entries.push(PlcEntry {
                art,
                tiles,
                vram_tile: e["vramTile"].as_u64().unwrap_or(0) as usize,
                address,
            });
        }
        out.push(PlcList { entries });
    }
    Ok(out)
}

fn apply_plc(vram: &mut [u8], lists: &[PlcList], id: usize) {
    let Some(list) = lists.get(id) else {
        return;
    };
    for entry in &list.entries {
        let dst = entry.vram_tile * 32;
        let count = entry.art.len().min(vram.len().saturating_sub(dst));
        if count > 0 {
            vram[dst..dst + count].copy_from_slice(&entry.art[..count]);
        }
    }
}

pub(crate) fn load_tile_art(
    archive: &Archive,
    path: &str,
    tiles: usize,
) -> Result<Vec<u8>, String> {
    let (width, _, pixels) = crate::png::read_indexed(archive.read(path)?)?;
    Ok(crate::tiles::from_sheet(&pixels, width as usize, tiles))
}

pub(crate) fn load_sprite_file(archive: &Archive, path: &str) -> Result<SpriteData, String> {
    let root = archive.read_json(path)?;
    let dir = path.rsplit_once('/').map(|(d, _)| d).unwrap_or("");
    let frames = root["mappings"]
        .as_array()
        .ok_or("mappings missing")?
        .iter()
        .map(|frame| {
            frame
                .as_array()
                .into_iter()
                .flatten()
                .map(|p| {
                    let a = p.as_array();
                    let get = |i: usize| a.and_then(|a| a.get(i));
                    MappingPiece {
                        y: get(0).and_then(Value::as_i64).unwrap_or(0) as i16,
                        size: get(1).and_then(Value::as_u64).unwrap_or(0) as u8,
                        pattern: get(2).and_then(Value::as_u64).unwrap_or(0) as u16,
                        x: get(3).and_then(Value::as_i64).unwrap_or(0) as i16,
                    }
                })
                .collect()
        })
        .collect();
    let dplc = root["dplc"].as_array().map(|frames| {
        frames
            .iter()
            .map(|f| {
                f.as_array()
                    .into_iter()
                    .flatten()
                    .map(|p| {
                        (
                            p.get(0).and_then(Value::as_u64).unwrap_or(0) as usize,
                            p.get(1).and_then(Value::as_u64).unwrap_or(0) as usize,
                        )
                    })
                    .collect()
            })
            .collect()
    });
    let mut art = Vec::new();
    if let Some(art_name) = root["art"].as_str() {
        let tiles = root["artTiles"].as_u64().unwrap_or(0) as usize;
        if tiles > 0 {
            art = load_tile_art(archive, &resolve(dir, art_name), tiles)?;
        } else {
            art = archive.read(&resolve(dir, art_name))?.to_vec();
        }
    }
    Ok(SpriteData {
        frames,
        dplc,
        art,
        rom_pointer: root["romPointer"].as_u64().unwrap_or(0) as u32,
        frame_offsets: root["frameOffsets"]
            .as_array()
            .map(|a| a.iter().map(|v| v.as_u64().unwrap_or(0) as usize).collect()),
        animations: root["animations"]
            .as_array()
            .map(|a| a.iter().map(|v| v.as_u64().unwrap_or(0) as u8).collect())
            .unwrap_or_default(),
    })
}

fn load_hud(archive: &Archive) -> Result<HudData, String> {
    let root = archive.read_json("objects/hud/hud.json")?;
    let dir = "objects/hud";
    let digits = load_tile_art(
        archive,
        &resolve(dir, root["digits"].as_str().unwrap_or("digits.png")),
        root["digitTiles"].as_u64().unwrap_or(24) as usize,
    )?;
    let lives_digits = load_tile_art(
        archive,
        &resolve(
            dir,
            root["livesDigits"].as_str().unwrap_or("lives-digits.png"),
        ),
        root["livesDigitTiles"].as_u64().unwrap_or(10) as usize,
    )?;
    let u8s = |key: &str| {
        root[key]
            .as_array()
            .map(|a| a.iter().map(|v| v.as_u64().unwrap_or(0) as u8).collect())
            .unwrap_or_default()
    };
    Ok(HudData {
        digits,
        lives_digits,
        base_tiles: u8s("baseTiles"),
        init_rings_tiles: u8s("initRingsTiles"),
        sprites: load_sprite_file(archive, "objects/hud/sprites.json")?,
    })
}
