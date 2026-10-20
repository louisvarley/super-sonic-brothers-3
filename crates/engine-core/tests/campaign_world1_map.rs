use engine_core::frontend::{GameFlow, Start};
use rom_assets::{load_frontend_assets, Archive};
use std::path::PathBuf;

#[test]
fn world_1_map_is_identical_with_and_without_campaign_metadata() {
    let root = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../..");
    let base = root.join("sonicbros3.bin");
    let camp = root.join("web-data/campaign.bin");
    if !base.exists() || !camp.exists() {
        eprintln!("skip: archives not present");
        return;
    }
    let base_assets = load_frontend_assets(&Archive::open(&base).unwrap()).unwrap();
    let camp_assets = load_frontend_assets(&Archive::open(&camp).unwrap()).unwrap();
    assert!(base_assets.campaign.is_none());
    assert!(camp_assets.campaign.is_some());
    let mut a = GameFlow::new(base_assets, Start::Map);
    let mut b = GameFlow::new(camp_assets, Start::Map);
    for tick in 1..=400 {
        a.step(0);
        b.step(0);
        if tick % 30 == 0 {
            assert_eq!(a.render_hash(), b.render_hash(), "tick {tick}");
        }
    }
}
