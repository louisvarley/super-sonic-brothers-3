use crate::archive::{resolve, Archive};
use crate::level::{load_sprite_file, load_tile_art};
use engine_core::frontend::assets::{FrontendArt, FrontendAssets};
use engine_core::frontend::map_data::{CampaignMap, MapArea, MapWorld};

const PRG: [u8; 7] = [10, 12, 22, 24, 25, 27, 30];
const CHR: [u8; 31] = [
    0x04, 0x14, 0x15, 0x16, 0x17, 0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x28, 0x29, 0x2A, 0x2B, 0x5A,
    0x5C, 0x5D, 0x5E, 0x5F, 0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x78, 0x79, 0x7A, 0x7B, 0x7F,
];

pub fn load_frontend_assets(archive: &Archive) -> Result<FrontendAssets, String> {
    let mut a = FrontendAssets::default();
    for bank in PRG {
        a.prg.insert(
            bank,
            archive.read(&format!("sb3/prg_{bank:02}.bin"))?.to_vec(),
        );
    }
    for bank in CHR {
        a.chr.insert(
            bank,
            archive.read(&format!("sb3/chr_{bank:02X}.bin"))?.to_vec(),
        );
    }
    a.bonus_room = archive.read("sb3/bonus_room.bin")?.to_vec();
    let art = archive.read_json("frontend/art.json")?;
    for e in art["art"].as_array().into_iter().flatten() {
        let screen = e["screen"].as_str().unwrap_or("").to_string();
        let tiles = e["tiles"].as_u64().unwrap_or(0) as usize;
        let path = resolve("frontend", e["art"].as_str().unwrap_or(""));
        a.art.push(FrontendArt {
            screen,
            tiles: load_tile_art(archive, &path, tiles)?,
            vram_tile: e["vramTile"].as_u64().unwrap_or(0) as usize,
        });
    }
    a.sonic = load_sprite_file(archive, "objects/sonic/sprites.json")?;
    a.tails = load_sprite_file(archive, "objects/tails/sprites.json")?;
    a.monitor = load_sprite_file(archive, "objects/monitor/sprites.json")?;
    a.goomba = load_sprite_file(archive, "objects/smb3-goomba/sprites.json")?;
    a.koopa = load_sprite_file(archive, "objects/smb3-koopa-green/sprites.json")?;
    a.monitor_icon_art = load_tile_art(archive, "art/nemesis/nem_080B08.png", 60)?;
    let life = load_tile_art(archive, "art/nemesis/nem_0808FE.png", 12)?;
    a.life_icon_art = life[..4 * 32].to_vec();
    let plc = archive.read_json("plc/plc_lists_zone40.json")?;
    let e = &plc["lists"][94]["entries"][0];
    a.smb3_objects_art = load_tile_art(
        archive,
        &resolve("plc", e["art"].as_str().unwrap_or("")),
        e["tiles"].as_u64().unwrap_or(0) as usize,
    )?;
    let pal = archive.read_json("palettes/pal_03.json")?;
    a.palette_03 = pal["colours"]
        .as_array()
        .into_iter()
        .flatten()
        .map(|c| {
            u16::from_str_radix(
                c["word"].as_str().unwrap_or("0").trim_start_matches("0x"),
                16,
            )
            .unwrap_or(0)
        })
        .collect();
    if archive.exists("campaign/campaign.json") {
        let c = crate::campaign::CampaignData::load(archive)?;
        let worlds = c
            .worlds
            .iter()
            .map(|w| MapWorld {
                number: w.number,
                map: w.map.clone(),
                areas: w
                    .entries
                    .iter()
                    .filter_map(|e| {
                        let id = e.area_id.as_deref()?;
                        let a = c.area_for_id(id)?;
                        Some(MapArea {
                            act: a.act,
                            world: w.number,
                            screen: e.screen as u8,
                            row: e.row,
                            column: e.column,
                            status: a.status.clone(),
                        })
                    })
                    .collect(),
            })
            .collect();
        a.campaign = Some(CampaignMap { worlds });
    }
    Ok(a)
}
