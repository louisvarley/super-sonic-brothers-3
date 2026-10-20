//! Phase 2 M4c-2: act completion and Sonic-zone bosses against the legacy `.sb3g` traces.
//!
//! The six new title-card-free traces are replayed with [`CompareMask::MOTION`]. The SMB3 goal traces start at the
//! legacy debug positions; the Sonic-zone boss traces do too.

use engine_core::session::Session;
use engine_core::vars;
use engine_core::world::Start;
use golden_trace::{compare_masked, CompareMask, Entity, Tick, Trace};
use rom_assets::{Archive, LevelData};
use std::path::{Path, PathBuf};

fn repo_root() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../..")
}

fn parse_buttons(text: &str) -> u8 {
    let mut b = 0u8;
    for c in text.chars() {
        b |= match c {
            'U' => 0x01,
            'D' => 0x02,
            'L' => 0x04,
            'R' => 0x08,
            'B' => 0x10,
            'C' => 0x20,
            'A' => 0x40,
            'S' => 0x80,
            _ => 0,
        };
    }
    b
}

fn load_script(path: &Path) -> Vec<(u32, u8)> {
    let text = std::fs::read_to_string(path).unwrap_or_else(|e| panic!("{}: {e}", path.display()));
    let mut changes = Vec::new();
    for raw in text.lines() {
        let line = raw.split('#').next().unwrap_or("").trim();
        if line.is_empty() {
            continue;
        }
        let mut parts = line.split_whitespace();
        let frame: u32 = parts.next().expect("frame").parse().expect("frame number");
        let buttons = parse_buttons(parts.next().unwrap_or("-"));
        changes.push((frame, buttons));
    }
    changes.sort_by_key(|c| c.0);
    changes
}

fn buttons_at(changes: &[(u32, u8)], frame: u32) -> u8 {
    let mut b = 0;
    for &(f, buttons) in changes {
        if f > frame {
            break;
        }
        b = buttons;
    }
    b
}

fn entity(ram: &engine_core::Ram, address: u16) -> Entity {
    let o = engine_core::Obj::new(address);
    Entity {
        x: o.x(ram) as i32,
        y: o.y(ram) as i32,
        x_vel: o.x_vel(ram),
        y_vel: o.y_vel(ram),
        inertia: o.inertia(ram),
        angle: o.angle(ram),
        status: o.status(ram),
        status_secondary: o.status_secondary(ram),
        routine: o.routine(ram),
        anim: o.anim(ram),
        frame: o.mapping_frame(ram),
        subtype: o.subtype(ram),
    }
}

fn rust_tick(session: &Session, tick: u32, region_count: usize) -> Tick {
    let ram = &session.world.ram;
    let (zone, act) = session.current_act();
    Tick {
        tick,
        phase: 0,
        zone,
        act,
        camera_x: session.camera_x,
        camera_y: session.camera_y,
        sonic: entity(ram, vars::MAIN_CHARACTER),
        tails: entity(ram, vars::SIDEKICK),
        rings: ram.u16(vars::RING_COUNT),
        score: ram.u32(vars::SCORE),
        timer: ram.u32(vars::TIMER),
        lives: ram.u8(vars::LIFE_COUNT),
        game_mode: ram.u8(vars::GAME_MODE),
        whole_ram: 0,
        regions: vec![0; region_count],
        screen_hash: 0,
    }
}

fn describe(t: &Tick) -> String {
    format!(
        "tick {} phase={} z/a={}/{} cam ({}, {}) sonic x={} y={} xv={} yv={} inertia={} status=${:02X} \
         status2=${:02X} routine={} anim={} frame={} sub={} | rings={} score={} timer=${:X} lives={} gm=${:02X} | \
         tails x={} y={} routine={} anim={}",
        t.tick,
        t.phase,
        t.zone,
        t.act,
        t.camera_x,
        t.camera_y,
        t.sonic.x,
        t.sonic.y,
        t.sonic.x_vel,
        t.sonic.y_vel,
        t.sonic.inertia,
        t.sonic.status,
        t.sonic.status_secondary,
        t.sonic.routine,
        t.sonic.anim,
        t.sonic.frame,
        t.sonic.subtype,
        t.rings,
        t.score,
        t.timer,
        t.lives,
        t.game_mode,
        t.tails.x,
        t.tails.y,
        t.tails.routine,
        t.tails.anim,
    )
}

struct Case {
    name: &'static str,
    zone: u8,
    act: u8,
    start: Start,
    script: &'static str,
}

const CASES: &[Case] = &[
    Case {
        name: "smb3-1-1-goal-playing",
        zone: 0x40,
        act: 1,
        start: Start { x: 2600, y: 370 },
        script: "goal",
    },
    Case {
        name: "smb3-1-2-goal-playing",
        zone: 0x41,
        act: 1,
        start: Start { x: 2670, y: 370 },
        script: "goal12",
    },
    Case {
        name: "smb3-1-3-goal-playing",
        zone: 0x42,
        act: 1,
        start: Start { x: 2350, y: 396 },
        script: "goal13",
    },
    Case {
        name: "sonic-ehz-2-boss-playing",
        zone: 0x00,
        act: 2,
        start: Start { x: 10704, y: 1008 },
        script: "jump",
    },
    Case {
        name: "sonic-mz-3-boss-playing",
        zone: 0x14,
        act: 1,
        start: Start { x: 6656, y: 700 },
        script: "jump",
    },
    Case {
        name: "sonic-ghz-3-boss-playing",
        zone: 0x12,
        act: 1,
        start: Start { x: 10752, y: 640 },
        script: "jump",
    },
];

fn replay(
    case: &Case,
    root: &Path,
    level: &LevelData,
    sets: &engine_core::collision::CollisionSets,
    sonic_animation: &engine_core::animation::SonicAnimation,
    tails_animation: &engine_core::animation::TailsAnimation,
) -> (Trace, Trace) {
    let trace_path = root.join(format!("tests/golden/{}.sb3g.gz", case.name));
    let legacy =
        Trace::load(&trace_path).unwrap_or_else(|e| panic!("{}: {e}", trace_path.display()));
    let script = load_script(&root.join(format!("tests/golden/inputs/{}.txt", case.script)));
    let region_count = legacy.header.regions.len();

    let mut session = Session::new_with_start(
        Box::new(level.clone()),
        case.zone,
        case.act,
        sets.clone(),
        sonic_animation.clone(),
        tails_animation.clone(),
        Some(case.start),
    )
    .unwrap_or_else(|e| panic!("{}: world setup: {e}", case.name));
    let mut ticks = Vec::with_capacity(legacy.ticks.len());
    for i in 1..=legacy.ticks.len() as u32 {
        session
            .step(buttons_at(&script, i))
            .unwrap_or_else(|e| panic!("{}: tick {i}: {e}", case.name));
        ticks.push(rust_tick(&session, i, region_count));
    }
    let port = Trace {
        header: legacy.header.clone(),
        ticks,
        snapshots: Vec::new(),
        end_tick: legacy.end_tick,
    };
    (legacy, port)
}

#[test]
fn act_completion_and_bosses_match_the_legacy_traces() {
    let root = repo_root();
    let archive_path = root.join("sonicbros3.bin");
    if !archive_path.exists() {
        eprintln!(
            "skip: {} is not present (ROM-derived archive, intentionally not committed)",
            archive_path.display()
        );
        return;
    }
    let archive = Archive::open(&archive_path).expect("open sonicbros3.bin");
    let sets = rom_assets::load_collision_sets(&archive).expect("collision sets");
    let sonic_animation = rom_assets::load_sonic_animation(&archive).expect("Sonic animations");
    let tails_animation = rom_assets::load_tails_animation(&archive).expect("Tails animations");

    let mut failures = Vec::new();
    for case in CASES {
        let level = LevelData::load(&archive, case.zone as u16, case.act as u16)
            .unwrap_or_else(|e| panic!("load {}: {e}", case.name));
        let (legacy, port) = replay(
            case,
            &root,
            &level,
            &sets,
            &sonic_animation,
            &tails_animation,
        );
        match compare_masked(&legacy, &port, CompareMask::MOTION) {
            None => println!(
                "{}: MOTION matches for all {} ticks",
                case.name,
                legacy.ticks.len()
            ),
            Some(divergence) => {
                println!("{}: first MOTION divergence: {divergence}", case.name);
                if let Some(index) = legacy.ticks.iter().position(|t| t.tick == divergence.tick) {
                    for i in index.saturating_sub(5)..(index + 6).min(legacy.ticks.len()) {
                        println!("  legacy {}", describe(&legacy.ticks[i]));
                        println!("  port   {}", describe(&port.ticks[i]));
                    }
                }
                failures.push(case.name);
            }
        }
    }
    assert!(
        failures.is_empty(),
        "MOTION diverged for: {}",
        failures.join(", ")
    );
}
