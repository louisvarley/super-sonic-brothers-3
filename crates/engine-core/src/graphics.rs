//! Graphics data shared by the software VDP: mappings/DPLC, VRAM composition, animated tiles, HUD digits and PLC
//! lists. The formats mirror the legacy extraction JSON.

use std::collections::HashMap;

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct MappingPiece {
    pub y: i16,
    pub size: u8,
    pub pattern: u16,
    pub x: i16,
}

#[derive(Clone, Debug, Default)]
pub struct SpriteData {
    pub frames: Vec<Vec<MappingPiece>>,
    pub dplc: Option<Vec<Vec<(usize, usize)>>>,
    pub art: Vec<u8>,
    pub rom_pointer: u32,
    pub frame_offsets: Option<Vec<usize>>,
    pub animations: Vec<u8>,
}

#[derive(Clone, Debug, Default)]
pub struct SpriteBank {
    pub data: Vec<SpriteData>,
    pub tables: HashMap<u32, usize>,
    pub statics: HashMap<u32, MappingPiece>,
}

impl SpriteBank {
    pub fn add(&mut self, data: SpriteData) {
        let index = self.data.len();
        let pointer = data.rom_pointer & 0x00FF_FFFF;
        self.data.push(data);
        if pointer != 0 {
            self.tables.insert(pointer, index);
        }
        let data = &self.data[index];
        if data.rom_pointer == 0 {
            return;
        }
        for (frame_index, frame) in data.frames.iter().enumerate() {
            let offset = data
                .frame_offsets
                .as_ref()
                .and_then(|o| o.get(frame_index))
                .copied()
                .unwrap_or_else(|| {
                    data.frames[..frame_index]
                        .iter()
                        .map(|f| 2 + f.len() * 8)
                        .sum()
                });
            for (k, piece) in frame.iter().enumerate() {
                let ptr = (data.rom_pointer as usize + offset + 2 + k * 8) as u32;
                self.statics.entry(ptr & 0x00FF_FFFF).or_insert(*piece);
            }
        }
    }

    pub fn table(&self, pointer: u32) -> Option<&SpriteData> {
        self.tables
            .get(&(pointer & 0x00FF_FFFF))
            .and_then(|i| self.data.get(*i))
    }

    pub fn static_piece(&self, pointer: u32) -> Option<MappingPiece> {
        self.statics.get(&(pointer & 0x00FF_FFFF)).copied()
    }
}

#[derive(Clone, Debug, Default)]
pub struct AnimatedScript {
    pub art: Vec<u8>,
    pub vram_tile: usize,
    pub tiles_per_frame: usize,
    pub frames: Vec<(usize, u8)>,
}

#[derive(Clone, Debug, Default)]
pub struct PlcEntry {
    pub art: Vec<u8>,
    pub tiles: usize,
    pub vram_tile: usize,
    /// The art's ROM address (0xF00000 for art not taken from the ROM).
    pub address: u32,
}

#[derive(Clone, Debug, Default)]
pub struct PlcList {
    pub entries: Vec<PlcEntry>,
}

#[derive(Clone, Debug, Default)]
pub struct HudData {
    pub digits: Vec<u8>,
    pub lives_digits: Vec<u8>,
    pub base_tiles: Vec<u8>,
    pub init_rings_tiles: Vec<u8>,
    pub sprites: SpriteData,
}

#[derive(Clone, Debug, Default)]
pub struct RenderGeometry {
    pub foreground: Vec<u8>,
    pub background: Vec<u8>,
    pub chunks: Vec<u16>,
    pub blocks: Vec<u16>,
}

#[derive(Clone, Debug, Default)]
pub struct AnimalData {
    pub zone_types: Vec<u8>,
    pub types: Vec<(i16, i16, u32)>,
}

#[derive(Clone, Debug, Default)]
pub struct Graphics {
    pub vram: Vec<u8>,
    pub plc_lists: Vec<PlcList>,
    pub animated: Vec<AnimatedScript>,
    pub magma_art: Vec<u8>,
    pub magma_vram_tile: usize,
    pub hud: HudData,
    pub bank: SpriteBank,
    pub sonic: SpriteData,
    pub tails: SpriteData,
    pub rings: SpriteData,
    pub geometry: RenderGeometry,
    pub animals: AnimalData,
}
