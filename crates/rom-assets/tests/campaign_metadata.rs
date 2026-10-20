use rom_assets::{Archive, CampaignData};
use std::path::PathBuf;

#[test]
fn campaign_manifest_has_all_worlds_screens_and_areas() {
    let path = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../web-data/campaign.bin");
    if !path.exists() {
        eprintln!("skip: campaign.bin not present");
        return;
    }
    let archive = Archive::open(&path).unwrap();
    let c = CampaignData::load(&archive).unwrap();
    assert_eq!(c.worlds.len(), 8, "8 worlds");
    assert_eq!(c.worlds.iter().map(|w| w.screen_count()).sum::<usize>(), 19);
    assert_eq!(c.areas.len(), 257);
    assert_eq!(c.available_areas().count(), 255);
    assert_eq!(c.areas.iter().filter(|a| a.status == "failed").count(), 1);
    assert_eq!(c.areas.iter().filter(|a| a.status == "blocked").count(), 1);
    for w in 1..=8 {
        let world = c.world(w).unwrap();
        assert_eq!(world.number, w);
        assert!(!world.map.is_empty());
        assert!(world
            .entries
            .iter()
            .all(|e| e.screen < world.screen_count()));
    }
    // Every map entry with an area resolves to one campaign area, and ownership is populated.
    for world in &c.worlds {
        for entry in &world.entries {
            if let Some(id) = &entry.area_id {
                let area = c.area_for_id(id).expect("area id resolves");
                assert!(!area.directory.is_empty());
                assert!(area.world == 0 || area.world <= 8);
            }
        }
    }
}
