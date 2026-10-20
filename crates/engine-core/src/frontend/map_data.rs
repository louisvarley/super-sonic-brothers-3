//! Map-only campaign data used by the front-end. `rom-assets` fills this from `campaign/campaign.json`.

#[derive(Clone, Debug, Default)]
pub struct MapArea {
    pub act: u16,
    pub world: u8,
    pub screen: u8,
    pub row: i32,
    pub column: i32,
    pub status: String,
}

impl MapArea {
    pub fn available(&self) -> bool {
        self.status != "failed" && self.status != "blocked"
    }
    pub fn tile(&self) -> i32 {
        self.row * 16 + self.column
    }
}

#[derive(Clone, Debug, Default)]
pub struct MapWorld {
    pub number: u8,
    pub map: Vec<u8>,
    pub areas: Vec<MapArea>,
}

impl MapWorld {
    pub fn screen_count(&self) -> usize {
        self.map.len() / 144
    }

    pub fn screen(&self, screen: usize) -> Option<&[u8]> {
        let start = screen.checked_mul(144)?;
        self.map.get(start..start + 144)
    }
}

#[derive(Clone, Debug, Default)]
pub struct CampaignMap {
    pub worlds: Vec<MapWorld>,
}

impl CampaignMap {
    pub fn world(&self, number: u8) -> Option<&MapWorld> {
        self.worlds.iter().find(|w| w.number == number)
    }
    pub fn screen_count(&self, number: u8) -> usize {
        self.world(number).map(|w| w.map.len() / 144).unwrap_or(0)
    }

    pub fn area_at(&self, world: u8, screen: u8, tile: i32) -> Option<&MapArea> {
        self.world(world)?
            .areas
            .iter()
            .find(|a| a.screen == screen && a.tile() == tile && a.available())
    }
}

pub fn world_start_tile(world: u8) -> i32 {
    let pixel = [0x40, 0xA0, 0xA0, 0x40, 0x80, 0x60, 0x30, 0x50]
        .get(world.saturating_sub(1) as usize)
        .copied()
        .unwrap_or(0x40);
    (pixel / 16 - 2) * 16 + 2
}
