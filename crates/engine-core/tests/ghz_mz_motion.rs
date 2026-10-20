//! Phase 2 M4d: Green Hill and Marble Zone acts 1-2 against the legacy `.sb3g` traces.

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
        "tick {} z/a={}/{} cam ({}, {}) sonic x={} y={} xv={} yv={} inertia={} angle=${:02X} status=${:02X} \
         status2=${:02X} routine={} anim={} frame={} sub={} | rings={} score={} timer=${:X} lives={} | tails x={} y={}",
        t.tick,
        t.zone,
        t.act,
        t.camera_x,
        t.camera_y,
        t.sonic.x,
        t.sonic.y,
        t.sonic.x_vel,
        t.sonic.y_vel,
        t.sonic.inertia,
        t.sonic.angle,
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
        t.tails.x,
        t.tails.y,
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
fn green_hill_and_marble_zone_acts_1_and_2_match_the_legacy_traces() {
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
    for (zone, level_name) in [(0x11u8, "ghz"), (0x13, "mz")] {
        for act in [1u8, 2] {
            let level = LevelData::load(&archive, zone as u16, act as u16)
                .unwrap_or_else(|e| panic!("load {level_name} act {act}: {e}"));
            for script in ["idle", "right", "jump", "spindash"] {
                let name = format!("sonic-{level_name}-{act}-playing-{script}");
                let (legacy, port) = replay(
                    &name,
                    zone,
                    act,
                    &root,
                    &level,
                    &sets,
                    &sonic_animation,
                    &tails_animation,
                );
                match compare_masked(&legacy, &port, CompareMask::MOTION) {
                    None => println!(
                        "{}: MOTION matches for all {} ticks",
                        name,
                        legacy.ticks.len()
                    ),
                    Some(divergence) => {
                        println!("{name}: first MOTION divergence: {divergence}");
                        if let Some(index) =
                            legacy.ticks.iter().position(|t| t.tick == divergence.tick)
                        {
                            for i in index.saturating_sub(5)..(index + 6).min(legacy.ticks.len()) {
                                println!("  legacy {}", describe(&legacy.ticks[i]));
                                println!("  port   {}", describe(&port.ticks[i]));
                            }
                        }
                        failures.push(name);
                    }
                }
            }
        }
    }
    assert!(
        failures.is_empty(),
        "MOTION diverged for: {}",
        failures.join(", ")
    );
}
