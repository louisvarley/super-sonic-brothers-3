use engine_core::session::Session;
use rom_assets::{
    load_collision_sets, load_sonic_animation, load_tails_animation, Archive, CampaignData,
};
use std::path::PathBuf;

const CAMPAIGN_ZONE: u8 = 0x43;

#[test]
fn every_available_campaign_area_loads_and_steps() {
    let path = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../web-data/campaign.bin");
    if !path.exists() {
        eprintln!("skip: campaign.bin not present");
        return;
    }
    let archive = Archive::open(&path).unwrap();
    let campaign = CampaignData::load(&archive).unwrap();
    let sets = load_collision_sets(&archive).unwrap();
    let sonic = load_sonic_animation(&archive).unwrap();
    let tails = load_tails_animation(&archive).unwrap();
    let mut failures = Vec::new();
    let mut stepped = 0;
    for area in campaign.available_areas() {
        let loaded = area.load_level(&archive);
        let level = match loaded {
            Ok(l) => l,
            Err(e) => {
                failures.push(format!("area {} load: {e}", area.act));
                continue;
            }
        };
        // The campaign's area number is not a Mega Drive act byte; the directory is the identity.
        let act = if area.act <= 255 { area.act as u8 } else { 1 };
        match Session::new(
            Box::new(level),
            CAMPAIGN_ZONE,
            act,
            sets.clone(),
            sonic.clone(),
            tails.clone(),
        ) {
            Ok(mut session) => {
                for tick in 0..60 {
                    if let Err(e) = session.step(0) {
                        failures.push(format!("area {} step {tick}: {e}", area.act));
                        break;
                    }
                }
                stepped += 1;
            }
            Err(e) => failures.push(format!("area {} session: {e}", area.act)),
        }
    }
    eprintln!(
        "campaign areas: {} available, {stepped} stepped, {} blocked/failed",
        campaign.available_areas().count(),
        campaign.areas.len() - campaign.available_areas().count()
    );
    assert!(
        failures.is_empty(),
        "campaign failures:\n{}",
        failures.join("\n")
    );
    assert_eq!(stepped, 255);
}
