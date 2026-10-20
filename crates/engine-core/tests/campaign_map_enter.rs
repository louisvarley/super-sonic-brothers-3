use engine_core::frontend::{GameFlow, MapAction, Start};
use rom_assets::{load_frontend_assets, Archive};
use std::path::PathBuf;

#[test]
fn world_1_map_moves_to_the_1_1_panel() {
    let path = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../web-data/campaign.bin");
    if !path.exists() {
        eprintln!("skip: campaign.bin not present");
        return;
    }
    let archive = Archive::open(&path).unwrap();
    let assets = load_frontend_assets(&archive).unwrap();
    let mut flow = GameFlow::new_at(assets, Start::Map, 1, 0);
    // 22 fade frames.
    for _ in 0..22 {
        assert!(flow.step(0).is_none());
    }
    // Let the "WORLD 1" intro box time out without entering the area on the start tile.
    for _ in 0..120 {
        assert!(flow.step(0).is_none());
    }
    // Right one panel, up two, then A on the 1-1 panel.
    for _ in 0..32 {
        flow.step(0x08);
    }
    for _ in 0..32 {
        flow.step(0x01);
    }
    for _ in 0..16 {
        flow.step(0x08);
    }
    let mut action = None;
    for _ in 0..32 {
        if let Some(a) = flow.step(0x40) {
            action = Some(a);
            break;
        }
    }
    assert_eq!(action, Some(MapAction::StartZone { zone: 0x40, act: 1 }));
}
