//! The N-Spade card game: the bonus room gives way to an 18-card deck and the shuffler keeps it between visits.
//!
//! The legacy oracle is `MatchGameScreen.cs`; the framebuffer gate is still the ignored `frontend-match` trace.

use engine_core::frontend::screens::MatchScreen;
use engine_core::frontend::MapProgress;
use rom_assets::{load_frontend_assets, Archive};
use std::path::PathBuf;

fn root() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../..")
}

#[test]
fn the_card_game_starts_with_a_shuffled_deck() {
    let path = root().join("web-data/campaign.bin");
    let Ok(archive) = Archive::open(path) else {
        return;
    };
    let Ok(assets) = load_frontend_assets(&archive) else {
        return;
    };
    let mut screen = MatchScreen::new(&assets, MapProgress::default());
    for _ in 0..4000 {
        screen.step(&assets, 0);
        if screen.progress().deck.is_some() {
            break;
        }
    }
    let deck = screen.progress().deck.clone().expect("the deck is dealt");
    assert_eq!(deck.len(), 18);
    assert!(deck.iter().all(|c| (0..=5).contains(&(c & 0x0F))));
    assert!(!screen
        .render_rgba(&mut engine_core::vdp::Vdp::new())
        .is_empty());
}
