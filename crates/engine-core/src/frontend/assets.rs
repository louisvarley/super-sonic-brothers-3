//! SMB3 front-end data and screens.
use super::map_data::CampaignMap;
use crate::graphics::SpriteData;
use std::collections::HashMap;

#[derive(Clone, Debug, Default)]
pub struct FrontendArt {
    pub screen: String,
    pub tiles: Vec<u8>,
    pub vram_tile: usize,
}

#[derive(Clone, Debug, Default)]
pub struct FrontendAssets {
    pub prg: HashMap<u8, Vec<u8>>,
    pub chr: HashMap<u8, Vec<u8>>,
    pub bonus_room: Vec<u8>,
    pub art: Vec<FrontendArt>,
    pub sonic: SpriteData,
    pub tails: SpriteData,
    pub monitor: SpriteData,
    pub goomba: SpriteData,
    pub koopa: SpriteData,
    pub smb3_objects_art: Vec<u8>,
    pub monitor_icon_art: Vec<u8>,
    pub life_icon_art: Vec<u8>,
    pub palette_03: Vec<u16>,
    /// All-world map data when the archive has `campaign/campaign.json`.
    pub campaign: Option<CampaignMap>,
}

impl FrontendAssets {
    pub fn prg(&self, bank: u8, address: usize) -> u8 {
        self.prg
            .get(&bank)
            .and_then(|b| b.get(address & 0x1FFF))
            .copied()
            .unwrap_or(0)
    }
    pub fn prg_word(&self, bank: u8, address: usize) -> u16 {
        u16::from_be_bytes([self.prg(bank, address), self.prg(bank, address + 1)])
    }
    /// SMB3 assembler tables are little-endian (PrgWord in Smb3Assets).
    pub fn prg_word_le(&self, bank: u8, address: usize) -> u16 {
        u16::from_le_bytes([self.prg(bank, address), self.prg(bank, address + 1)])
    }
    pub fn chr(&self, bank: u8, tile: usize) -> &[u8] {
        self.chr
            .get(&bank)
            .and_then(|b| b.get(tile * 16..tile * 16 + 16))
            .unwrap_or(&[0; 16])
    }
}
