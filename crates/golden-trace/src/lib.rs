//! Reader and comparator for the legacy engine's Phase 1 golden traces (`.sb3g[.gz]`).
//!
//! The format is written by `legacy/src/SonicBros3/Runtime/GoldenTrace.cs`. It is intentionally small and
//! self-describing: a JSON header, one fixed record per tick, periodic JSON snapshots for diagnosis, and an end record.
//! See `tools/golden/generate.mjs` and `tests/golden/manifest.json`.

use flate2::read::GzDecoder;
use serde::Deserialize;
use std::fmt;
use std::fs;
use std::io::{Cursor, Read};
use std::path::Path;

pub const MAGIC: &[u8; 8] = b"SB3GOLD1";

/// Bytes in one tick record, excluding the leading kind byte.
pub const TICK_BODY_BYTES: usize = 133;
/// Bytes in one tick record including its kind byte.
pub const TICK_BYTES: usize = 1 + TICK_BODY_BYTES;

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct Header {
    pub format: String,
    pub engine: String,
    pub scenario: String,
    pub archive: String,
    pub archive_sha256: String,
    pub start: String,
    pub zone: i32,
    pub act: i32,
    pub world: i32,
    pub map_screen: i32,
    pub level: String,
    pub debug_start: String,
    pub frames: u32,
    pub hash_every: u32,
    pub snapshot_every: u32,
    pub regions: Vec<String>,
    pub fields: Vec<String>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Entity {
    pub x: i32,
    pub y: i32,
    pub x_vel: i16,
    pub y_vel: i16,
    pub inertia: i16,
    pub angle: u8,
    pub status: u8,
    pub status_secondary: u8,
    pub routine: u8,
    pub anim: u8,
    pub frame: u8,
    pub subtype: u8,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Tick {
    pub tick: u32,
    pub phase: u8,
    pub zone: u8,
    pub act: u8,
    pub camera_x: i32,
    pub camera_y: i32,
    pub sonic: Entity,
    pub tails: Entity,
    pub rings: u16,
    pub score: u32,
    pub timer: u32,
    pub lives: u8,
    pub game_mode: u8,
    pub whole_ram: u64,
    /// One hash per `Header::regions`, in the same order.
    pub regions: Vec<u64>,
    pub screen_hash: u64,
}

#[derive(Debug, Clone)]
pub struct Snapshot {
    pub tick: u32,
    pub value: serde_json::Value,
}

#[derive(Debug, Clone)]
pub struct Trace {
    pub header: Header,
    pub ticks: Vec<Tick>,
    pub snapshots: Vec<Snapshot>,
    pub end_tick: u32,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Divergence {
    pub tick: u32,
    /// Scalar field name (as in `Header::fields`) or `regions.<name>`.
    pub field: String,
    pub expected: String,
    pub actual: String,
}

impl fmt::Display for Divergence {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(
            f,
            "first divergence at tick {}: {} (legacy {}, port {})",
            self.tick, self.field, self.expected, self.actual
        )
    }
}

fn err(msg: impl Into<String>) -> String {
    msg.into()
}

struct Reader {
    data: Cursor<Vec<u8>>,
}

impl Reader {
    fn new(data: Vec<u8>) -> Self {
        Self {
            data: Cursor::new(data),
        }
    }
    fn bytes(&mut self, n: usize) -> Result<Vec<u8>, String> {
        let start = self.data.position() as usize;
        let end = start.checked_add(n).ok_or_else(|| err("length overflow"))?;
        let buf = self.data.get_ref();
        if end > buf.len() {
            return Err(err(format!(
                "truncated trace: need {n} bytes at {start}, have {}",
                buf.len()
            )));
        }
        let out = buf[start..end].to_vec();
        self.data.set_position(end as u64);
        Ok(out)
    }
    fn u8(&mut self) -> Result<u8, String> {
        Ok(self.bytes(1)?[0])
    }
    fn u16(&mut self) -> Result<u16, String> {
        let b = self.bytes(2)?;
        Ok(u16::from_le_bytes([b[0], b[1]]))
    }
    fn u32(&mut self) -> Result<u32, String> {
        let b = self.bytes(4)?;
        Ok(u32::from_le_bytes([b[0], b[1], b[2], b[3]]))
    }
    fn i32(&mut self) -> Result<i32, String> {
        Ok(self.u32()? as i32)
    }
    fn i16(&mut self) -> Result<i16, String> {
        Ok(self.u16()? as i16)
    }
    fn u64(&mut self) -> Result<u64, String> {
        let b = self.bytes(8)?;
        Ok(u64::from_le_bytes([
            b[0], b[1], b[2], b[3], b[4], b[5], b[6], b[7],
        ]))
    }
    fn entity(&mut self) -> Result<Entity, String> {
        Ok(Entity {
            x: self.i32()?,
            y: self.i32()?,
            x_vel: self.i16()?,
            y_vel: self.i16()?,
            inertia: self.i16()?,
            angle: self.u8()?,
            status: self.u8()?,
            status_secondary: self.u8()?,
            routine: self.u8()?,
            anim: self.u8()?,
            frame: self.u8()?,
            subtype: self.u8()?,
        })
    }
    fn eof(&self) -> bool {
        self.data.position() as usize >= self.data.get_ref().len()
    }
}

impl Trace {
    pub fn load(path: impl AsRef<Path>) -> Result<Self, String> {
        let bytes =
            fs::read(path.as_ref()).map_err(|e| format!("{}: {e}", path.as_ref().display()))?;
        Self::from_bytes(bytes)
    }

    pub fn from_bytes(bytes: Vec<u8>) -> Result<Self, String> {
        let bytes = if bytes.starts_with(&[0x1f, 0x8b]) {
            let mut out = Vec::new();
            GzDecoder::new(bytes.as_slice())
                .read_to_end(&mut out)
                .map_err(|e| format!("gzip: {e}"))?;
            out
        } else {
            bytes
        };
        let mut r = Reader::new(bytes);
        if r.bytes(MAGIC.len())?.as_slice() != MAGIC {
            return Err(err("not an SB3GOLD1 trace"));
        }
        let header_len = r.u32()? as usize;
        let header: Header = serde_json::from_slice(&r.bytes(header_len)?)
            .map_err(|e| format!("header json: {e}"))?;

        let mut ticks = Vec::new();
        let mut snapshots = Vec::new();
        let end_tick;
        loop {
            if r.eof() {
                return Err(err("trace ended without an end record"));
            }
            let kind = r.u8()?;
            match kind {
                0 => {
                    let tick = Tick {
                        tick: r.u32()?,
                        phase: r.u8()?,
                        zone: r.u8()?,
                        act: r.u8()?,
                        camera_x: r.i32()?,
                        camera_y: r.i32()?,
                        sonic: r.entity()?,
                        tails: r.entity()?,
                        rings: r.u16()?,
                        score: r.u32()?,
                        timer: r.u32()?,
                        lives: r.u8()?,
                        game_mode: r.u8()?,
                        whole_ram: r.u64()?,
                        regions: (0..header.regions.len())
                            .map(|_| r.u64())
                            .collect::<Result<_, _>>()?,
                        screen_hash: r.u64()?,
                    };
                    ticks.push(tick);
                }
                1 => {
                    let tick = r.u32()?;
                    let len = r.u32()? as usize;
                    let value = serde_json::from_slice(&r.bytes(len)?)
                        .map_err(|e| format!("snapshot json at {tick}: {e}"))?;
                    snapshots.push(Snapshot { tick, value });
                }
                2 => {
                    end_tick = r.u32()?;
                    break;
                }
                other => {
                    return Err(err(format!(
                        "unknown record kind {other} at byte {}",
                        r.data.position()
                    )))
                }
            }
        }
        if !r.eof() {
            return Err(err(format!(
                "trailing bytes after end record ({})",
                r.data.position()
            )));
        }
        Ok(Self {
            header,
            ticks,
            snapshots,
            end_tick,
        })
    }

    /// Compare two traces tick-by-tick and field-by-field, naming the first difference.
    /// The legacy trace is `expected`; the port's trace is `actual`.
    pub fn compare(expected: &Trace, actual: &Trace) -> Option<Divergence> {
        let n = expected.ticks.len().min(actual.ticks.len());
        for i in 0..n {
            let e = &expected.ticks[i];
            let a = &actual.ticks[i];
            macro_rules! check {
                ($field:expr, $e:expr, $a:expr) => {
                    if $e != $a {
                        return Some(Divergence {
                            tick: e.tick,
                            field: $field.to_string(),
                            expected: format!("{}", $e),
                            actual: format!("{}", $a),
                        });
                    }
                };
            }
            check!("tick", e.tick, a.tick);
            check!("phase", e.phase, a.phase);
            check!("zone", e.zone, a.zone);
            check!("act", e.act, a.act);
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
            check!("tails.x", e.tails.x, a.tails.x);
            check!("tails.y", e.tails.y, a.tails.y);
            check!("tails.xv", e.tails.x_vel, a.tails.x_vel);
            check!("tails.yv", e.tails.y_vel, a.tails.y_vel);
            check!("tails.inertia", e.tails.inertia, a.tails.inertia);
            check!("tails.angle", e.tails.angle, a.tails.angle);
            check!("tails.status", e.tails.status, a.tails.status);
            check!(
                "tails.status2",
                e.tails.status_secondary,
                a.tails.status_secondary
            );
            check!("tails.routine", e.tails.routine, a.tails.routine);
            check!("tails.anim", e.tails.anim, a.tails.anim);
            check!("tails.frame", e.tails.frame, a.tails.frame);
            check!("tails.sub", e.tails.subtype, a.tails.subtype);
            check!("rings", e.rings, a.rings);
            check!("score", e.score, a.score);
            check!("timer", e.timer, a.timer);
            check!("lives", e.lives, a.lives);
            check!("gameMode", e.game_mode, a.game_mode);
            check!("wholeRam", e.whole_ram, a.whole_ram);
            for ((name, er), ar) in expected
                .header
                .regions
                .iter()
                .zip(&e.regions)
                .zip(&a.regions)
            {
                if er != ar {
                    return Some(Divergence {
                        tick: e.tick,
                        field: format!("regions.{name}"),
                        expected: format!("{er:016x}"),
                        actual: format!("{ar:016x}"),
                    });
                }
            }
            check!("screenHash", e.screen_hash, a.screen_hash);
        }
        if expected.ticks.len() != actual.ticks.len() {
            let tick = expected
                .ticks
                .get(n)
                .or_else(|| actual.ticks.get(n))
                .map(|t| t.tick)
                .unwrap_or(0);
            return Some(Divergence {
                tick,
                field: "tickCount".into(),
                expected: expected.ticks.len().to_string(),
                actual: actual.ticks.len().to_string(),
            });
        }
        None
    }
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Manifest {
    pub format: String,
    pub generator: String,
    pub legacy_commit: String,
    pub archive: ManifestFile,
    pub campaign_archive: ManifestFile,
    pub scenarios: Vec<ManifestScenario>,
}

#[derive(Debug, Clone, Deserialize)]
pub struct ManifestFile {
    pub path: String,
    pub sha256: String,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ManifestScenario {
    pub name: String,
    pub file: String,
    pub sha256: String,
    pub bytes: u64,
    pub level: Option<String>,
    pub zone: Option<u16>,
    pub act: Option<u16>,
    pub world: Option<u16>,
    pub campaign_act: Option<u16>,
    pub debug_start: Option<String>,
    pub inputs: Option<String>,
    pub frames: u32,
}
