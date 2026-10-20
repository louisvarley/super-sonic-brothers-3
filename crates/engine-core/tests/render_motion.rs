//! Phase 2 M5a: the software VDP framebuffer hash against the legacy traces.
//!
//! All 24 required level traces must match every 30-tick framebuffer hash. Run it with:
//!   cargo test -p engine-core --test render_motion -- --nocapture

use engine_core::session::Session;
use engine_core::vars;
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

fn rust_tick(session: &Session, tick: u32, region_count: usize, screen_hash: u64) -> Tick {
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
        screen_hash,
    }
}

fn describe(t: &Tick) -> String {
    format!(
        "tick {} screen=${:016X} cam ({}, {}) sonic x={} y={} frame={} | rings={} score={} timer=${:X} lives={}",
        t.tick,
        t.screen_hash,
        t.camera_x,
        t.camera_y,
        t.sonic.x,
        t.sonic.y,
        t.sonic.frame,
        t.rings,
        t.score,
        t.timer,
        t.lives
    )
}

#[allow(clippy::too_many_arguments)]
fn replay(
    name: &str,
    zone: u8,
    act: u8,
    root: &Path,
    level: &LevelData,
    sets: &engine_core::collision::CollisionSets,
    sonic_animation: &engine_core::animation::SonicAnimation,
    tails_animation: &engine_core::animation::TailsAnimation,
) -> (Trace, Trace) {
    let trace_path = root.join(format!("tests/golden/{name}.sb3g.gz"));
    let legacy =
        Trace::load(&trace_path).unwrap_or_else(|e| panic!("{}: {e}", trace_path.display()));
    let script_name = match name.rsplit('-').next().unwrap_or("idle") {
        "idle" => "idle",
        "right" => "right0",
        "jump" => "jump0",
        _ => "spindash0",
    };
    let script = load_script(&root.join(format!("tests/golden/inputs/{script_name}.txt")));
    let region_count = legacy.header.regions.len();

    let mut session = Session::new(
        Box::new(level.clone()),
        zone,
        act,
        sets.clone(),
        sonic_animation.clone(),
        tails_animation.clone(),
    )
    .unwrap_or_else(|e| panic!("{name}: world setup: {e}"));
    let mut ticks = Vec::with_capacity(legacy.ticks.len());
    for i in 1..=legacy.ticks.len() as u32 {
        session
            .step(buttons_at(&script, i))
            .unwrap_or_else(|e| panic!("{name}: tick {i}: {e}"));
        let hash = if i % legacy.header.hash_every == 0 {
            session.render_hash()
        } else {
            0
        };
        ticks.push(rust_tick(&session, i, region_count, hash));
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
fn level_framebuffer_hashes_match_the_legacy_traces() {
    let root = repo_root();
    let archive_path = root.join("sonicbros3.bin");
    if !archive_path.exists() {
        eprintln!("skip: {} is not present", archive_path.display());
        return;
    }
    let archive = Archive::open(&archive_path).expect("open sonicbros3.bin");
    let sets = rom_assets::load_collision_sets(&archive).expect("collision sets");
    let sonic_animation = rom_assets::load_sonic_animation(&archive).expect("Sonic animations");
    let tails_animation = rom_assets::load_tails_animation(&archive).expect("Tails animations");

    let cases: &[(&str, u8, u8)] = &[
        ("smb3-1-1", 0x40, 1),
        ("smb3-1-2", 0x41, 1),
        ("smb3-1-3", 0x42, 1),
        ("sonic-ehz-1", 0x00, 1),
        ("sonic-ghz-1", 0x11, 1),
        ("sonic-mz-1", 0x13, 1),
    ];
    let mut failures = Vec::new();
    for (level_name, zone, act) in cases {
        let level = LevelData::load(&archive, *zone as u16, *act as u16)
            .unwrap_or_else(|e| panic!("load {level_name}: {e}"));
        for script in ["idle", "right", "jump", "spindash"] {
            let name = format!("{level_name}-playing-{script}");
            let (legacy, port) = replay(
                &name,
                *zone,
                *act,
                &root,
                &level,
                &sets,
                &sonic_animation,
                &tails_animation,
            );
            let mask = CompareMask {
                ram: false,
                regions: false,
                screen: true,
                tails: false,
            };
            match compare_masked(&legacy, &port, mask) {
                None => println!("{name}: screen matches for all 600 ticks"),
                Some(divergence) => {
                    println!("{name}: first divergence: {divergence}");
                    if let Some(index) = legacy.ticks.iter().position(|t| t.tick == divergence.tick)
                    {
                        for i in index.saturating_sub(2)..(index + 3).min(legacy.ticks.len()) {
                            println!("  legacy {}", describe(&legacy.ticks[i]));
                            println!("  port   {}", describe(&port.ticks[i]));
                        }
                    }
                    failures.push(name);
                }
            }
        }
    }
    assert!(
        failures.is_empty(),
        "framebuffer hashes diverged for: {}",
        failures.join(", ")
    );
}
