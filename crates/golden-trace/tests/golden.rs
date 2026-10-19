//! Phase 1 integrity tests: every committed legacy trace must parse, match its manifest hash, cover exactly the
//! recorded number of ticks, and carry a framebuffer hash exactly on the recorded cadence.

use golden_trace::{Manifest, Trace};
use sha2::{Digest, Sha256};
use std::path::{Path, PathBuf};

fn repo_root() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../..")
}

fn manifest() -> Manifest {
    let path = repo_root().join("tests/golden/manifest.json");
    let text =
        std::fs::read_to_string(&path).unwrap_or_else(|e| panic!("read {}: {e}", path.display()));
    serde_json::from_str(&text).unwrap_or_else(|e| panic!("parse {}: {e}", path.display()))
}

fn hex_digest(path: &Path) -> String {
    let bytes = std::fs::read(path).unwrap_or_else(|e| panic!("read {}: {e}", path.display()));
    let mut hasher = Sha256::new();
    hasher.update(&bytes);
    hasher
        .finalize()
        .iter()
        .map(|b| format!("{b:02x}"))
        .collect()
}

#[test]
fn every_trace_parses_and_matches_its_manifest() {
    let manifest = manifest();
    assert_eq!(manifest.format, "sb3.golden-manifest/1");
    assert!(!manifest.scenarios.is_empty(), "no golden traces committed");

    let mut seen = std::collections::HashSet::new();
    for scenario in &manifest.scenarios {
        assert!(
            seen.insert(scenario.name.clone()),
            "duplicate scenario {}",
            scenario.name
        );
        let path = repo_root().join("tests/golden").join(&scenario.file);
        assert_eq!(
            hex_digest(&path),
            scenario.sha256,
            "{} changed since the manifest was written",
            scenario.file
        );
        let trace = Trace::load(&path).unwrap_or_else(|e| panic!("{}: {e}", path.display()));
        assert_eq!(trace.header.scenario, scenario.name);
        assert_eq!(trace.header.frames, scenario.frames);
        assert_eq!(
            trace.ticks.len(),
            scenario.frames as usize,
            "{}",
            scenario.name
        );
        assert_eq!(trace.end_tick, scenario.frames, "{}", scenario.name);
        assert_eq!(trace.header.regions.len(), trace.ticks[0].regions.len());

        let hash_every = trace.header.hash_every.max(1);
        for tick in &trace.ticks {
            let want_screen = tick.tick % hash_every == 0;
            assert_eq!(
                tick.screen_hash != 0,
                want_screen,
                "{} tick {} screen-hash cadence",
                scenario.name,
                tick.tick
            );
        }
        assert!(
            trace.snapshots.len()
                >= scenario.frames as usize / trace.header.snapshot_every as usize
        );
        assert!(
            Trace::compare(&trace, &trace).is_none(),
            "{} does not equal itself",
            scenario.name
        );
    }
}

#[test]
fn divergence_reports_the_first_field() {
    let manifest = manifest();
    let path = repo_root()
        .join("tests/golden")
        .join(&manifest.scenarios[0].file);
    let base = Trace::load(path).expect("load first trace");
    let index = 17.min(base.ticks.len() - 1);

    let mut changed = base.clone();
    changed.ticks[index].sonic.x = changed.ticks[index].sonic.x.wrapping_add(1);
    let d = Trace::compare(&base, &changed).expect("divergence");
    assert_eq!(d.field, "sonic.x");
    assert_eq!(d.tick, changed.ticks[index].tick);

    let mut changed = base.clone();
    changed.ticks[index].regions[0] ^= 1;
    let d = Trace::compare(&base, &changed).expect("divergence");
    assert_eq!(d.field, format!("regions.{}", base.header.regions[0]));

    let mut shorter = base.clone();
    shorter.ticks.pop();
    let d = Trace::compare(&base, &shorter).expect("length divergence");
    assert_eq!(d.field, "tickCount");
}
