//! Asset loading and (later) stock-ROM extraction.
//!
//! Milestone 2 reads the legacy derived archive (`sonicbros3.bin`, a ZIP) exactly as the C# `DataFile` does: entry
//! names are ordinary forward-slash paths, `..` resolves inside the archive, and JSON/PNG payloads are the extractor's
//! formats. The stock Sonic 2 + SMB3 importer is workstream 2.

pub mod animation;
pub mod archive;
pub mod audio;
pub mod campaign;
pub mod collision;
pub mod level;
pub mod png;
pub mod tiles;

pub use animation::{load_sonic_animation, load_tails_animation};
pub use archive::Archive;
pub use audio::{
    load_audio_assets, load_audio_assets_for_area, load_audio_assets_for_zone,
    load_audio_assets_with_song,
};
pub use campaign::{CampaignArea, CampaignData, CampaignEntry, CampaignSource, CampaignWorld};
pub use collision::load_collision_sets;
pub use frontend::load_frontend_assets;
pub use level::{Bounds, LevelData, ObjectPlacement, RingGroup, Start};
pub mod frontend;
