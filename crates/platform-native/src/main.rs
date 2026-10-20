//! Native windowed front-end for the Rust port.
//!
//! It loads the derived asset archive (the legacy `sonicbros3.bin` for now), starts a ported level directly (no title
//! card), steps the deterministic engine at 60 Hz, and presents the software VDP framebuffer with `minifb`.
//!
//! The 68k SMPS path plays through `cpal` at 48 kHz stereo; `--mute` keeps the window silent. The stock Sonic 2 +
//! SMB3 importer is also not built yet, so this binary needs the derived archive (ROMs are not read).

mod audio;

use engine_core::session::Session;
use minifb::{Key, Scale, Window, WindowOptions};
use rom_assets::{load_audio_assets_for_zone, Archive, LevelData};
use std::fs::File;
use std::io::BufWriter;
use std::path::{Path, PathBuf};

const WIDTH: usize = 320;
const HEIGHT: usize = 224;

struct Options {
    data: PathBuf,
    zone: u8,
    act: u8,
    scale: Scale,
    frames: Option<u32>,
    screenshot: Option<PathBuf>,
    mute: bool,
}

fn usage() -> ! {
    eprintln!(
        "Sonic Bros. 3 (Rust port)\n\
         \n\
         Usage: sonic-bros-3 [--data sonicbros3.bin] [--level 1-1|1-2|1-3] [--zone 0xNN --act N] [--scale 1|2|4|8]\n\
         \x20      [--frames N --screenshot out.png]   (headless verification)\n\
         \n\
         Default level: 1-1 (zone 0x40, act 1).\n\
         Controls: arrows move, Z/X/C or Space = A/B/C, Enter = Start, P = pause, Esc = quit.\n\
         Audio: 48 kHz stereo (68k SMPS); --mute disables it."
    );
    std::process::exit(2);
}

fn parse_args() -> Options {
    let mut data = PathBuf::from("sonicbros3.bin");
    let mut zone: u8 = 0x40;
    let mut act: u8 = 1;
    let mut scale = Scale::X4;
    let mut frames = None;
    let mut screenshot = None;
    let mut mute = false;
    let args: Vec<String> = std::env::args().skip(1).collect();
    let mut i = 0;
    while i < args.len() {
        match args[i].as_str() {
            "--data" => {
                i += 1;
                data = PathBuf::from(args.get(i).unwrap_or_else(|| usage()));
            }
            "--level" => {
                i += 1;
                match args.get(i).map(String::as_str) {
                    Some("1-1") => zone = 0x40,
                    Some("1-2") => zone = 0x41,
                    Some("1-3") => zone = 0x42,
                    _ => usage(),
                }
                act = 1;
            }
            "--zone" => {
                i += 1;
                let text = args.get(i).unwrap_or_else(|| usage());
                zone = u8::from_str_radix(text.trim_start_matches("0x"), 16)
                    .unwrap_or_else(|_| usage());
            }
            "--act" => {
                i += 1;
                act = args
                    .get(i)
                    .and_then(|s| s.parse().ok())
                    .unwrap_or_else(|| usage());
            }
            "--scale" => {
                i += 1;
                scale = match args.get(i).map(String::as_str) {
                    Some("1") => Scale::X1,
                    Some("2") => Scale::X2,
                    Some("4") => Scale::X4,
                    Some("8") => Scale::X8,
                    _ => usage(),
                };
            }
            "--frames" => {
                i += 1;
                frames = Some(
                    args.get(i)
                        .and_then(|s| s.parse().ok())
                        .unwrap_or_else(|| usage()),
                );
            }
            "--screenshot" => {
                i += 1;
                screenshot = Some(PathBuf::from(args.get(i).unwrap_or_else(|| usage())));
            }
            "--mute" => mute = true,
            "-h" | "--help" => usage(),
            other => {
                eprintln!("unknown option: {other}");
                usage();
            }
        }
        i += 1;
    }
    if screenshot.is_some() && frames.is_none() {
        frames = Some(1);
    }
    Options {
        data,
        zone,
        act,
        scale,
        frames,
        screenshot,
        mute,
    }
}

fn main() {
    let opts = parse_args();
    if !opts.data.exists() {
        eprintln!(
            "Asset archive not found: {}.\n\
             The Rust port currently reads the derived archive (the legacy `sonicbros3.bin`); the stock-ROM\n\
             importer is not built yet. Put the archive next to the exe or pass --data PATH.",
            opts.data.display()
        );
        std::process::exit(1);
    }

    let archive = match Archive::open(&opts.data) {
        Ok(a) => a,
        Err(e) => {
            eprintln!("{e}");
            std::process::exit(1);
        }
    };
    let level = match LevelData::load(&archive, opts.zone as u16, opts.act as u16) {
        Ok(l) => l,
        Err(e) => {
            eprintln!("level load: {e}");
            std::process::exit(1);
        }
    };
    let sets = match rom_assets::load_collision_sets(&archive) {
        Ok(s) => s,
        Err(e) => {
            eprintln!("collision sets: {e}");
            std::process::exit(1);
        }
    };
    let sonic = match rom_assets::load_sonic_animation(&archive) {
        Ok(a) => a,
        Err(e) => {
            eprintln!("Sonic animation: {e}");
            std::process::exit(1);
        }
    };
    let tails = match rom_assets::load_tails_animation(&archive) {
        Ok(a) => a,
        Err(e) => {
            eprintln!("Tails animation: {e}");
            std::process::exit(1);
        }
    };

    let music_file = level.music_file.clone();
    let mut session = match Session::new(Box::new(level), opts.zone, opts.act, sets, sonic, tails) {
        Ok(s) => s,
        Err(e) => {
            eprintln!("world setup: {e}");
            std::process::exit(1);
        }
    };

    match load_audio_assets_for_zone(&archive, opts.zone, music_file.as_deref()) {
        Ok(assets) => session.enable_audio(assets),
        Err(e) => eprintln!("audio data: {e}; running silent"),
    }
    let mut audio = if session.audio.is_some() && !opts.mute && opts.frames.is_none() {
        audio::AudioOutput::new()
    } else {
        None
    };

    if let Some(frames) = opts.frames {
        let mut stopped = None;
        for _ in 0..frames {
            if let Err(e) = session.step(0) {
                stopped = Some(e);
                break;
            }
        }
        let rgba = session.render();
        if let Some(path) = &opts.screenshot {
            if let Err(e) = write_png(path, &rgba) {
                eprintln!("screenshot: {e}");
                std::process::exit(1);
            }
        }
        let (zone, act) = session.current_act();
        let ticks = session.ticks;
        let hash = session.render_hash();
        let stopped = stopped
            .as_ref()
            .map(ToString::to_string)
            .unwrap_or_else(|| "none".into());
        println!("ticks={ticks} hash={hash:016x} zone=${zone:02X} act={act} stopped={stopped}");
        return;
    }

    let mut window = match Window::new(
        "Sonic Bros. 3 - Rust port",
        WIDTH,
        HEIGHT,
        WindowOptions {
            scale: opts.scale,
            resize: true,
            ..Default::default()
        },
    ) {
        Ok(w) => w,
        Err(e) => {
            eprintln!("window: {e}");
            std::process::exit(1);
        }
    };
    window.set_target_fps(60);

    // minifb wants 0x00RRGGBB; the engine returns RGBA bytes.
    let mut buffer = vec![0u32; WIDTH * HEIGHT];
    let mut paused = false;
    let mut prev_p = false;
    let mut reported_stop = false;
    let mut title_tick = 0u32;

    while window.is_open() && !window.is_key_down(Key::Escape) {
        let p = window.is_key_down(Key::P);
        if p && !prev_p {
            paused = !paused;
        }
        prev_p = p;

        if !paused {
            let buttons = read_buttons(&window);
            if let Err(e) = session.step(buttons) {
                if !reported_stop {
                    eprintln!("session stopped: {e}");
                    reported_stop = true;
                }
            }
            if let Some(output) = audio.as_mut() {
                let mut samples = [0i16; 800 * 2];
                session.render_audio(&mut samples, 800);
                output.submit(&samples);
            }
        }

        let rgba = session.render();
        for (px, out) in rgba.chunks_exact(4).zip(buffer.iter_mut()) {
            *out = ((px[0] as u32) << 16) | ((px[1] as u32) << 8) | px[2] as u32;
        }
        if let Err(e) = window.update_with_buffer(&buffer, WIDTH, HEIGHT) {
            eprintln!("present: {e}");
            break;
        }

        // Update the title once a second, so a freeze/stop is visible without flooding the window manager.
        if session.ticks / 60 != title_tick {
            title_tick = session.ticks / 60;
            let (zone, act) = session.current_act();
            let state = if paused {
                "paused".to_string()
            } else if let Some(stop) = &session.stopped {
                format!("stopped: {stop}")
            } else {
                "playing".to_string()
            };
            window.set_title(&format!(
                "Sonic Bros. 3 - Rust port - zone ${zone:02X} act {act} - {state} - {}",
                session.ticks
            ));
        }
    }
}

fn read_buttons(window: &Window) -> u8 {
    let mut b = 0u8;
    if window.is_key_down(Key::Up) {
        b |= 0x01;
    }
    if window.is_key_down(Key::Down) {
        b |= 0x02;
    }
    if window.is_key_down(Key::Left) {
        b |= 0x04;
    }
    if window.is_key_down(Key::Right) {
        b |= 0x08;
    }
    if window.is_key_down(Key::X) {
        b |= 0x10; // B
    }
    if window.is_key_down(Key::C) {
        b |= 0x20; // C
    }
    if window.is_key_down(Key::Z) || window.is_key_down(Key::Space) {
        b |= 0x40; // A / jump
    }
    if window.is_key_down(Key::Enter) {
        b |= 0x80; // Start
    }
    b
}

fn write_png(path: &Path, rgba: &[u8]) -> Result<(), String> {
    let file = File::create(path).map_err(|e| format!("{}: {e}", path.display()))?;
    let writer = BufWriter::new(file);
    let mut encoder = png::Encoder::new(writer, WIDTH as u32, HEIGHT as u32);
    encoder.set_color(png::ColorType::Rgba);
    encoder.set_depth(png::BitDepth::Eight);
    let mut writer = encoder.write_header().map_err(|e| e.to_string())?;
    writer.write_image_data(rgba).map_err(|e| e.to_string())?;
    Ok(())
}
