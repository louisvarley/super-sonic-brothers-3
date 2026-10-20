//! Native windowed front-end for the Rust port.
//!
//! It opens the derived archive, then either starts directly in a level (`--level`/`--zone`) or, when the archive has
//! `campaign/campaign.json`, in the SMB3 world map (`--map`, `--world`, `--map-screen`, `--campaign-act`). The 68k
//! SMPS path plays through `cpal` at 48 kHz stereo; `--mute` keeps the window silent.

mod audio;
mod campaign;

use campaign::CampaignRunner;
use engine_core::session::Session;
use minifb::{Key, Scale, Window, WindowOptions};
use rom_assets::{
    load_audio_assets_for_zone, load_collision_sets, load_frontend_assets, load_sonic_animation,
    load_tails_animation, Archive, CampaignData, LevelData,
};
use sha2::{Digest, Sha256};
use std::fs::File;
use std::io::BufWriter;
use std::path::{Path, PathBuf};

const WIDTH: usize = 320;
const HEIGHT: usize = 224;

struct Options {
    data: PathBuf,
    zone: u8,
    act: u8,
    level_explicit: bool,
    map: bool,
    world: u8,
    map_screen: usize,
    campaign_act: Option<u16>,
    list_campaign: bool,
    scale: Scale,
    frames: Option<u32>,
    screenshot: Option<PathBuf>,
    mute: bool,
}

fn default_data() -> PathBuf {
    let mut candidates = Vec::new();
    if let Ok(exe) = std::env::current_exe() {
        if let Some(dir) = exe.parent() {
            candidates.push(dir.join("campaign.bin"));
        }
    }
    candidates.push(PathBuf::from("web-data/campaign.bin"));
    if let Ok(exe) = std::env::current_exe() {
        if let Some(dir) = exe.parent() {
            candidates.push(dir.join("sonicbros3.bin"));
        }
    }
    candidates.push(PathBuf::from("sonicbros3.bin"));
    candidates
        .iter()
        .find(|p| p.exists())
        .cloned()
        .unwrap_or_else(|| PathBuf::from("web-data/campaign.bin"))
}

fn usage() -> ! {
    eprintln!(
        "Sonic Bros. 3 (Rust port)\n\
         \n\
         Usage: sonic-bros-3 [--data web-data/campaign.bin] [--map] [--world 1-8] [--map-screen N]\n\
         \x20      [--campaign-act N] [--level 1-1|1-2|1-3] [--zone 0xNN --act N] [--scale 1|2|4|8]\n\
         \x20      [--frames N --screenshot out.png] [--mute] [--list-campaign]\n\
         \n\
         With campaign.bin and no --level/--zone, starts on the world map. --campaign-act enters one area directly.\n\
         Default level when using sonicbros3.bin: 1-1 (zone 0x40, act 1).\n\
         Controls: arrows move, Z/X/C or Space = A/B/C, Enter = Start, P = pause, Esc = quit.\n\
         Audio: 48 kHz stereo (68k SMPS); --mute disables output."
    );
    std::process::exit(2);
}

fn parse_args() -> Options {
    let mut data = default_data();
    let mut zone: u8 = 0x40;
    let mut act: u8 = 1;
    let mut level_explicit = false;
    let mut map = false;
    let mut world = 1u8;
    let mut map_screen = 0usize;
    let mut campaign_act = None;
    let mut list_campaign = false;
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
                level_explicit = true;
            }
            "--zone" => {
                i += 1;
                let text = args.get(i).unwrap_or_else(|| usage());
                zone = u8::from_str_radix(text.trim_start_matches("0x"), 16)
                    .unwrap_or_else(|_| usage());
                level_explicit = true;
            }
            "--act" => {
                i += 1;
                act = args
                    .get(i)
                    .and_then(|s| s.parse().ok())
                    .unwrap_or_else(|| usage());
                level_explicit = true;
            }
            "--map" => map = true,
            "--world" => {
                i += 1;
                world = args
                    .get(i)
                    .and_then(|s| s.parse().ok())
                    .filter(|w| (1..=8).contains(w))
                    .unwrap_or_else(|| usage());
                map = true;
            }
            "--map-screen" => {
                i += 1;
                map_screen = args
                    .get(i)
                    .and_then(|s| s.parse().ok())
                    .unwrap_or_else(|| usage());
                map = true;
            }
            "--campaign-act" => {
                i += 1;
                campaign_act = Some(
                    args.get(i)
                        .and_then(|s| s.parse().ok())
                        .unwrap_or_else(|| usage()),
                );
            }
            "--list-campaign" => list_campaign = true,
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
        level_explicit,
        map,
        world,
        map_screen,
        campaign_act,
        list_campaign,
        scale,
        frames,
        screenshot,
        mute,
    }
}

enum Running {
    Direct(Box<Session>),
    Campaign(Box<CampaignRunner>),
}

impl Running {
    fn step(&mut self, buttons: u8) -> Result<(), String> {
        match self {
            Running::Direct(s) => s.step(buttons).map_err(|e| e.to_string()),
            Running::Campaign(c) => {
                c.step(buttons);
                match c.last_error.take() {
                    Some(e) => Err(e),
                    None => Ok(()),
                }
            }
        }
    }

    fn render(&mut self) -> Vec<u8> {
        match self {
            Running::Direct(s) => s.render(),
            Running::Campaign(c) => c.render(),
        }
    }

    fn render_audio(&mut self, out: &mut [i16], frames: usize) {
        match self {
            Running::Direct(s) => s.render_audio(out, frames),
            Running::Campaign(c) => c.render_audio(out, frames),
        }
    }

    fn hash(&mut self) -> u64 {
        let rgba = self.render();
        let d = Sha256::digest(&rgba);
        u64::from_le_bytes(d[..8].try_into().unwrap())
    }

    fn ticks(&self) -> u32 {
        match self {
            Running::Direct(s) => s.ticks,
            Running::Campaign(c) => c.ticks,
        }
    }

    fn describe(&self) -> String {
        match self {
            Running::Direct(s) => {
                let (zone, act) = s.current_act();
                let state = if s.stopped.is_some() {
                    "stopped"
                } else {
                    "playing"
                };
                format!("zone ${zone:02X} act {act} {state}")
            }
            Running::Campaign(c) => c.describe(),
        }
    }
}

fn main() {
    let opts = parse_args();
    if !opts.data.exists() {
        eprintln!(
            "Asset archive not found: {}.\n\
             Pass --data PATH, or put campaign.bin (or sonicbros3.bin) next to the exe. The Rust port reads the\n\
             derived archive; the stock-ROM importer is not built yet.",
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

    if opts.list_campaign {
        list_campaign(&archive);
        return;
    }

    let sets = match load_collision_sets(&archive) {
        Ok(s) => s,
        Err(e) => {
            eprintln!("collision sets: {e}");
            std::process::exit(1);
        }
    };
    let sonic = match load_sonic_animation(&archive) {
        Ok(a) => a,
        Err(e) => {
            eprintln!("Sonic animation: {e}");
            std::process::exit(1);
        }
    };
    let tails = match load_tails_animation(&archive) {
        Ok(a) => a,
        Err(e) => {
            eprintln!("Tails animation: {e}");
            std::process::exit(1);
        }
    };

    let has_campaign = archive.exists("campaign/campaign.json");
    let campaign_mode =
        has_campaign && (opts.campaign_act.is_some() || opts.map || !opts.level_explicit);
    let mut running = if campaign_mode {
        let assets = match load_frontend_assets(&archive) {
            Ok(a) => a,
            Err(e) => {
                eprintln!("front-end assets: {e}");
                std::process::exit(1);
            }
        };
        let campaign = match CampaignData::load(&archive) {
            Ok(c) => c,
            Err(e) => {
                eprintln!("campaign: {e}");
                std::process::exit(1);
            }
        };
        let (world, screen) = match opts.campaign_act.and_then(|a| campaign.area_for_act(a)) {
            Some(area) if area.world != 0 => (area.world, area.screen as usize),
            _ => (opts.world, opts.map_screen),
        };
        let mut runner =
            match CampaignRunner::new(archive, assets, sets, sonic, tails, world, screen) {
                Ok(r) => r,
                Err(e) => {
                    eprintln!("campaign setup: {e}");
                    std::process::exit(1);
                }
            };
        if let Some(act) = opts.campaign_act {
            if let Err(e) = runner.start_act(act) {
                eprintln!("campaign act {act}: {e}");
                std::process::exit(1);
            }
        }
        Running::Campaign(Box::new(runner))
    } else {
        let level = match LevelData::load(&archive, opts.zone as u16, opts.act as u16) {
            Ok(l) => l,
            Err(e) => {
                eprintln!("level load: {e}");
                std::process::exit(1);
            }
        };
        let music_file = level.music_file.clone();
        let mut session =
            match Session::new(Box::new(level), opts.zone, opts.act, sets, sonic, tails) {
                Ok(s) => s,
                Err(e) => {
                    eprintln!("world setup: {e}");
                    std::process::exit(1);
                }
            };
        if let Ok(audio) = load_audio_assets_for_zone(&archive, opts.zone, music_file.as_deref()) {
            session.enable_audio(audio);
        }
        Running::Direct(Box::new(session))
    };

    let mut audio = if !opts.mute && opts.frames.is_none() {
        audio::AudioOutput::new()
    } else {
        None
    };

    if let Some(frames) = opts.frames {
        let mut failure = None;
        for _ in 0..frames {
            if let Err(e) = running.step(0) {
                failure = Some(e);
                break;
            }
        }
        let rgba = running.render();
        if let Some(path) = &opts.screenshot {
            if let Err(e) = write_png(path, &rgba) {
                eprintln!("screenshot: {e}");
                std::process::exit(1);
            }
        }
        let hash = running.hash();
        let ticks = running.ticks();
        println!(
            "ticks={ticks} hash={hash:016x} {} failed={}",
            running.describe(),
            failure.as_deref().unwrap_or("none")
        );
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
    let mut reported_error = false;
    let mut title_tick = u32::MAX;

    while window.is_open() && !window.is_key_down(Key::Escape) {
        let p = window.is_key_down(Key::P);
        if p && !prev_p {
            paused = !paused;
        }
        prev_p = p;

        if !paused {
            let buttons = read_buttons(&window);
            if let Err(e) = running.step(buttons) {
                if !reported_error {
                    eprintln!("session stopped: {e}");
                    reported_error = true;
                }
            }
            if let Some(output) = audio.as_mut() {
                let mut samples = [0i16; 800 * 2];
                running.render_audio(&mut samples, 800);
                output.submit(&samples);
            }
        }

        let rgba = running.render();
        for (px, out) in rgba.chunks_exact(4).zip(buffer.iter_mut()) {
            *out = ((px[0] as u32) << 16) | ((px[1] as u32) << 8) | px[2] as u32;
        }
        if let Err(e) = window.update_with_buffer(&buffer, WIDTH, HEIGHT) {
            eprintln!("present: {e}");
            break;
        }

        if running.ticks() / 60 != title_tick {
            title_tick = running.ticks() / 60;
            let state = if paused { "paused" } else { "playing" };
            window.set_title(&format!(
                "Sonic Bros. 3 - Rust port - {} - {state} - {}",
                running.describe(),
                running.ticks()
            ));
        }
    }
}

fn list_campaign(archive: &Archive) {
    let campaign = match CampaignData::load(archive) {
        Ok(c) => c,
        Err(e) => {
            eprintln!("campaign: {e}");
            std::process::exit(1);
        }
    };
    let available = campaign.available_areas().count();
    println!(
        "campaign version {}: {} worlds, {} screens, {} areas ({} available, {} blocked/failed)",
        campaign.version,
        campaign.worlds.len(),
        campaign
            .worlds
            .iter()
            .map(|w| w.screen_count())
            .sum::<usize>(),
        campaign.areas.len(),
        available,
        campaign.areas.len() - available
    );
    for area in &campaign.areas {
        if !area.available() {
            println!(
                "  {:3} {} {}: {}",
                area.act,
                area.source.id,
                area.status,
                area.blockers.join(" ")
            );
        }
    }
    for world in &campaign.worlds {
        println!(
            "world {}: {} screens, {} map entries",
            world.number,
            world.screen_count(),
            world.entries.len()
        );
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
