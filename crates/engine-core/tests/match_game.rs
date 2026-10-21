//! The N-Spade card game: the bonus room gives way to an 18-card deck and the shuffler keeps it between visits.
//!
//! The legacy oracle is `MatchGameScreen.cs`; `frontend-match` is now a strict framebuffer gate too.

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
    for _ in 0..20 {
        screen.step(&assets, 0);
    }
    let deck = screen.progress().deck.clone().expect("the deck is dealt");
    assert_eq!(deck.len(), 18);
    assert!(deck.iter().all(|c| (0..=5).contains(&(c & 0x0F))));
    assert!(!screen
        .render_rgba(&mut engine_core::vdp::Vdp::new())
        .is_empty());

    // Flip the first card and its partner; the pair pays out a Sonic item.
    let before_lives = screen.progress().lives;
    let before_rings = screen.progress().rings;
    let before_items = screen.progress().power_ups.len();
    let partner = deck
        .iter()
        .enumerate()
        .find(|(i, c)| *i != 0 && (**c & 0x7F) == (deck[0] & 0x7F))
        .map(|(i, _)| i)
        .expect("every face has a partner");
    let mut at = 0usize;
    screen.step(&assets, 0x40);
    for _ in 0..40 {
        screen.step(&assets, 0);
    }
    while at != partner {
        let (row, col) = (at / 6, at % 6);
        let (want_row, want_col) = (partner / 6, partner % 6);
        let button = if want_row > row {
            0x02
        } else if want_row < row {
            0x01
        } else if want_col > col {
            0x08
        } else {
            0x04
        };
        screen.step(&assets, button);
        for _ in 0..9 {
            screen.step(&assets, 0);
        }
        at = if want_row != row {
            (at + if want_row > row { 6 } else { 12 }) % 18
        } else {
            (at + if want_col > col { 1 } else { 17 }) % 18
        };
    }
    screen.step(&assets, 0x40);
    for _ in 0..40 {
        screen.step(&assets, 0);
    }
    let progress = screen.progress();
    assert!(
        progress.lives > before_lives
            || progress.rings > before_rings
            || progress.power_ups.len() > before_items,
        "the matched pair awards a life, rings or a Sonic item"
    );
}
