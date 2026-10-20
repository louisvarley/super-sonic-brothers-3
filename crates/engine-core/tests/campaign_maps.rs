use engine_core::frontend::{GameFlow, Start};
use rom_assets::{load_frontend_assets, Archive};
use std::path::PathBuf;

#[test]
fn every_campaign_map_screen_renders_non_empty() {
    let path = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../web-data/campaign.bin");
    if !path.exists() {
        eprintln!("skip: campaign.bin not present");
        return;
    }
    let archive = Archive::open(&path).unwrap();
    let assets = load_frontend_assets(&archive).unwrap();
    assert!(assets.campaign.is_some());
    let campaign = assets.campaign.as_ref().unwrap();
    let mut checked = 0;
    for world in &campaign.worlds {
        for screen in 0..world.screen_count() {
            let mut flow = GameFlow::new_at(assets.clone(), Start::Map, world.number, screen);
            for _ in 0..24 {
                flow.step(0);
            }
            let rgba = flow.render_rgba();
            let non_black = rgba
                .chunks_exact(4)
                .filter(|p| p[0] != 0 || p[1] != 0 || p[2] != 0)
                .count();
            assert!(
                non_black > 320 * 224 / 8,
                "world {} screen {} rendered mostly empty ({non_black} px)",
                world.number,
                screen
            );
            checked += 1;
        }
    }
    eprintln!("campaign map screens rendered: {checked}");
    assert_eq!(checked, 19);
}
