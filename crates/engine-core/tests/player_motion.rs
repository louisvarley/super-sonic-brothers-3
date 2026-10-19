//! Phase 2 M3b: Sonic's player physics and the camera against the legacy `.sb3g` traces.
//!
//! Four title-card-free SMB3 World 1-1 traces (`tests/golden/smb3-1-1-playing-*`) are replayed for 600 ticks with
//! the input scripts the legacy engine used. The comparison is [`CompareMask::MOTION`]: the camera, Sonic's twelve
//! fields and the cheap scalars; Tails, the RAM/region hashes and the framebuffer belong to later milestones.
//!
//! The object manager and `TouchResponse` are M4, so the traces leave pure motion when a dynamic object first
//! touches Sonic. The test pins those known ticks and prints them: before the pin every compared field must match,
//! and the only allowed divergence at the pin is the object's effect. `spindash` matches player+camera for all 600
//! ticks and only the score (a broken brick block) differs.
//!
//! The archive these traces were recorded from (`sonicbros3.bin`) is ROM-derived and deliberately not committed, so
//! the test skips when it is absent.

use engine_core::session::Session;
use engine_core::vars;
use golden_trace::{compare_masked, CompareMask, Entity, Tick, Trace};
use rom_assets::{Archive, LevelData};
use std::path::{Path, PathBuf};

/// The first divergence a scenario is allowed to have, and its cause. `None` means the field set must match for
/// the whole trace.
struct Known {
    /// The first `CompareMask::MOTION` divergence (tick, field).
    motion: Option<(u32, &'static str)>,
    /// The first divergence of the camera and Sonic's fields alone (tick, field).
    player_camera: Option<(u32, &'static str)>,
    why: &'static str,
}

fn known(name: &str) -> Known {
    match name {
        "idle" => Known {
            motion: Some((368, "sonic.yv")),
            player_camera: Some((368, "sonic.yv")),
            why: "a Goomba reaches the idle Sonic and KillCharacter knocks him back (M4 object contact)",
        },
        "right" => Known {
            motion: Some((79, "sonic.xv")),
            player_camera: Some((79, "sonic.xv")),
            why: "the same Goomba reaches Sonic at x=$B4 (M4 object contact)",
        },
        "jump" => Known {
            motion: Some((86, "cameraX")),
            player_camera: Some((86, "cameraX")),
            why: "Sonic is pushed out of a solid SMB3 block's side (Solid.Side zeroes xv/inertia) (M4)",
        },
        "spindash" => Known {
            motion: Some((70, "score")),
            player_camera: None,
            why: "a rolled-into brick block breaks and awards points; Sonic/camera are unaffected (M4)",
        },
        _ => unreachable!(),
    }
}

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

fn load_script(path: &std::path::Path) -> Vec<(u32, u8)> {
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

/// The script's pad byte for a 1-based frame (`InputScript.ButtonsAt`).
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
        phase: 0, // StartPhase.Playing
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
        "tick {} cam ({}, {}) sonic x={} y={} xv={} yv={} inertia={} angle=${:02X} status=${:02X} \
         status2=${:02X} routine={} anim={} frame={} sub={}",
        t.tick,
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
    )
}

/// Compare only the fields this milestone ports: the camera and Sonic's twelve entity fields, plus the timer and
/// game mode (which the session also runs). Rings, score and lives change on object contact (M4).
fn compare_player_camera(legacy: &Trace, port: &Trace) -> Option<(u32, &'static str)> {
    for (e, a) in legacy.ticks.iter().zip(&port.ticks) {
        macro_rules! check {
            ($field:expr, $e:expr, $a:expr) => {
                if $e != $a {
                    return Some((e.tick, $field));
                }
            };
        }
        check!("cameraX", e.camera_x, a.camera_x);
        check!("cameraY", e.camera_y, a.camera_y);
        check!("sonic.x", e.sonic.x, a.sonic.x);
        check!("sonic.y", e.sonic.y, a.sonic.y);
        check!("sonic.xv", e.sonic.x_vel, a.sonic.x_vel);
        check!("sonic.yv", e.sonic.y_vel, a.sonic.y_vel);
        check!("sonic.inertia", e.sonic.inertia, a.sonic.inertia);
        check!("sonic.angle", e.sonic.angle, a.sonic.angle);
        check!("sonic.status", e.sonic.status, a.sonic.status);
        check!(
            "sonic.status2",
            e.sonic.status_secondary,
            a.sonic.status_secondary
        );
        check!("sonic.routine", e.sonic.routine, a.sonic.routine);
        check!("sonic.anim", e.sonic.anim, a.sonic.anim);
        check!("sonic.frame", e.sonic.frame, a.sonic.frame);
        check!("sonic.sub", e.sonic.subtype, a.sonic.subtype);
        check!("timer", e.timer, a.timer);
        check!("gameMode", e.game_mode, a.game_mode);
    }
    None
}

fn replay(
    name: &str,
    root: &Path,
    level: &LevelData,
    sets: &engine_core::collision::CollisionSets,
    animation: &engine_core::animation::SonicAnimation,
) -> (Trace, Trace) {
    let trace_path = root.join(format!("tests/golden/smb3-1-1-playing-{name}.sb3g.gz"));
    let legacy =
        Trace::load(&trace_path).unwrap_or_else(|e| panic!("{}: {e}", trace_path.display()));
    // The generator's scenario names and input scripts (manifest.json): idle/right0/jump0/spindash0.
    let script_name = match name {
        "idle" => "idle",
        "right" => "right0",
        "jump" => "jump0",
        _ => "spindash0",
    };
    let script = load_script(&root.join(format!("tests/golden/inputs/{script_name}.txt")));
    let region_count = legacy.header.regions.len();

    let mut session = Session::new(
        Box::new(level.clone()),
        0x40,
        1,
        sets.clone(),
        animation.clone(),
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
fn player_and_camera_motion_matches_the_legacy_traces() {
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
    let level = LevelData::load(&archive, 0x40, 1).expect("load SMB3 1-1");
    let sets = rom_assets::load_collision_sets(&archive).expect("collision sets");
    let animation = rom_assets::load_sonic_animation(&archive).expect("Sonic animations");

    let mut report = Vec::new();

    for name in ["idle", "right", "jump", "spindash"] {
        let (legacy, port) = replay(name, &root, &level, &sets, &animation);
        let expected = known(name);

        let motion = compare_masked(&legacy, &port, CompareMask::MOTION);
        let player_camera = compare_player_camera(&legacy, &port);
        let motion_key = motion.as_ref().map(|d| (d.tick, d.field.as_str()));
        let motion_ok = motion_key == expected.motion;
        let player_camera_ok = player_camera == expected.player_camera;

        match &motion {
            None => println!(
                "{name}: MOTION matches for all {} ticks",
                legacy.ticks.len()
            ),
            Some(d) => {
                println!("{name}: first MOTION divergence: {d}");
                if let Some(index) = legacy.ticks.iter().position(|t| t.tick == d.tick) {
                    for i in index.saturating_sub(5)..(index + 6).min(legacy.ticks.len()) {
                        println!("  legacy {}", describe(&legacy.ticks[i]));
                        println!("  port   {}", describe(&port.ticks[i]));
                    }
                }
            }
        }
        match player_camera {
            None => println!(
                "{name}: player + camera fields match for all {} ticks",
                port.ticks.len()
            ),
            Some((tick, field)) => {
                println!("{name}: first player/camera divergence: tick {tick}: {field}")
            }
        }
        println!("{name}: known M4 blocker: {}", expected.why);

        assert!(
            motion_ok,
            "{name}: the first MOTION divergence moved away from the pinned object-contact tick {:?}",
            expected.motion
        );
        assert!(
            player_camera_ok,
            "{name}: the first player/camera divergence moved away from the pinned object-contact tick {:?}",
            expected.player_camera
        );
        report.push((name, legacy.ticks.len(), motion, expected));
    }

    println!("\nsummary (player/camera physics; object side effects are M4):");
    for (name, ticks, motion, expected) in &report {
        println!(
            "  {name:<9} {ticks} ticks  first MOTION {:?}  player/camera {:?}",
            motion.as_ref().map(|d| format!("{} {}", d.tick, d.field)),
            expected.player_camera
        );
    }
}
