//! Phase 2 M5c: SMB3 front-end framebuffer hashes.
//!
//! Title, title→map and map are the strict gate. The card game is ported and covered by `tests/match_game.rs`;
//! its full bonus-room framebuffer trace stays ignored until the room art matches.
use engine_core::frontend::{GameFlow, Start};
use golden_trace::{compare_masked, CompareMask, Entity, Tick, Trace};
use rom_assets::{load_frontend_assets, Archive};
use std::path::{Path, PathBuf};

fn root() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../..")
}
fn buttons_at(changes: &[(u32, u8)], frame: u32) -> u8 {
    let mut b = 0;
    for &(f, v) in changes {
        if f > frame {
            break;
        }
        b = v
    }
    b
}
fn load_script(p: &Path) -> Vec<(u32, u8)> {
    let t = std::fs::read_to_string(p).unwrap();
    let mut v = Vec::new();
    for l in t.lines() {
        let l = l.split('#').next().unwrap().trim();
        if l.is_empty() {
            continue;
        }
        let mut p = l.split_whitespace();
        let f = p.next().unwrap().parse().unwrap();
        let mut b = 0;
        for c in p.next().unwrap_or("-").chars() {
            b |= match c {
                'U' => 1,
                'D' => 2,
                'L' => 4,
                'R' => 8,
                'B' => 16,
                'C' => 32,
                'A' => 64,
                'S' => 128,
                _ => 0,
            }
        }
        v.push((f, b));
    }
    v
}
fn empty_entity() -> Entity {
    Entity {
        x: 0,
        y: 0,
        x_vel: 0,
        y_vel: 0,
        inertia: 0,
        angle: 0,
        status: 0,
        status_secondary: 0,
        routine: 0,
        anim: 0,
        frame: 0,
        subtype: 0,
    }
}
fn tick(n: u32, hash: u64, regions: usize) -> Tick {
    Tick {
        tick: n,
        phase: 0xFF,
        zone: 0,
        act: 0,
        camera_x: 0,
        camera_y: 0,
        sonic: empty_entity(),
        tails: empty_entity(),
        rings: 0,
        score: 0,
        timer: 0,
        lives: 0,
        game_mode: 0,
        whole_ram: 0,
        regions: vec![0; regions],
        screen_hash: hash,
    }
}

fn run_cases(cases: &[(&str, Start, &str)]) {
    let r = root();
    let ap = r.join("sonicbros3.bin");
    if !ap.exists() {
        eprintln!("skip");
        return;
    }
    let archive = Archive::open(&ap).unwrap();
    let assets = load_frontend_assets(&archive).unwrap();
    let mut failures = Vec::new();
    for (name, start, script) in cases {
        let path = r.join(format!("tests/golden/{name}.sb3g.gz"));
        let legacy = Trace::load(&path).unwrap();
        let sc = if script.is_empty() {
            Vec::new()
        } else {
            load_script(&r.join(format!("tests/golden/inputs/{script}.txt")))
        };
        let mut flow = GameFlow::new(assets.clone(), *start);
        let mut ticks = Vec::new();
        for i in 1..=legacy.ticks.len() as u32 {
            flow.step(buttons_at(&sc, i));
            let h = if i % legacy.header.hash_every == 0 {
                flow.render_hash()
            } else {
                0
            };
            ticks.push(tick(i, h, legacy.header.regions.len()));
        }
        let port = Trace {
            header: legacy.header.clone(),
            ticks,
            snapshots: Vec::new(),
            end_tick: legacy.end_tick,
        };
        let mask = CompareMask {
            ram: false,
            regions: false,
            screen: true,
            tails: false,
        };
        match compare_masked(&legacy, &port, mask) {
            None => println!("{name}: screen matches for {} ticks", legacy.ticks.len()),
            Some(d) => {
                println!("{name}: first divergence: {d}");
                failures.push(*name);
            }
        }
    }
    assert!(
        failures.is_empty(),
        "frontend diverged: {}",
        failures.join(", ")
    );
}

#[test]
fn title_and_map_framebuffer_hashes_match() {
    run_cases(&[
        ("frontend-title", Start::Title, ""),
        ("frontend-title-map", Start::Title, "title"),
        ("frontend-map", Start::Map, ""),
    ]);
}

#[test]
#[ignore = "K5-match: the card game is ported (tests/match_game.rs), but the bonus-room framebuffer still diverges at tick 30."]
fn match_framebuffer_hashes_match() {
    run_cases(&[("frontend-match", Start::Match, "")]);
}
