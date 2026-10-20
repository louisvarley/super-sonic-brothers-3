use engine_core::frontend::{GameFlow, Start};
use rom_assets::{load_frontend_assets, Archive};
use std::path::PathBuf;

#[test]
fn title_hashing_is_independent_of_the_campaign_manifest() {
    let root = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../..");
    let base = root.join("sonicbros3.bin");
    let camp = root.join("web-data/campaign.bin");
    if !base.exists() || !camp.exists() {
        eprintln!("skip: archives not present");
        return;
    }
    let mut a = GameFlow::new(
        load_frontend_assets(&Archive::open(&base).unwrap()).unwrap(),
        Start::Title,
    );
    let mut b = GameFlow::new(
        load_frontend_assets(&Archive::open(&camp).unwrap()).unwrap(),
        Start::Title,
    );
    for tick in 1..=600 {
        a.step(0);
        b.step(0);
        if tick % 30 == 0 {
            let ha = a.render_hash();
            let hb = b.render_hash();
            assert_eq!(ha, hb, "tick {tick}");
        }
    }
}
