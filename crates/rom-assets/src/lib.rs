//! Asset loading and (later) stock-ROM extraction.
//!
//! Milestone 2 reads the legacy derived archive (`sonicbros3.bin`, a ZIP) exactly as the C# `DataFile` does: entry
//! names are ordinary forward-slash paths, `..` resolves inside the archive, and JSON/PNG payloads are the extractor's
//! formats. The stock Sonic 2 + SMB3 importer is workstream 2.

pub mod archive;
pub mod level;
pub mod png;
pub mod tiles;

pub use archive::Archive;
pub use level::{Bounds, LevelData, ObjectPlacement, RingGroup, Start};
