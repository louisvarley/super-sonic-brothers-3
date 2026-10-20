//! Deterministic Sonic Bros. 3 engine core.
//!
//! The legacy engine keeps all game state in a 64 KiB big-endian image of 68k work RAM and names fields by
//! address. This crate mirrors that model: [`Ram`] owns the bytes, [`Obj`] is a named window onto one 64-byte
//! object slot, and [`vars`] holds the addresses. There is no I/O, no wall clock and no randomness beyond the
//! seeded/derived values the original uses.
//!
//! Port order: primitives (this milestone), then level loading, player/camera, objects, frontend, audio.

pub mod animated_tiles;
pub mod animation;
pub mod assets;
pub mod audio;
pub mod background_plane;
pub mod camera;
pub mod collision;
pub mod graphics;
pub mod hud;
pub mod lava_line;
pub mod math;
pub mod math_tables;
pub mod object;
pub mod objects;
pub mod oscillation;
pub mod player;
pub mod plc;
pub mod ram;
pub mod rings;
pub mod session;
pub mod sprite_builder;
pub mod vars;
pub mod vdp;
pub mod world;

pub use object::Obj;
pub use ram::{Ram, RAM_SIZE};
pub use session::Session;
pub use world::World;
pub mod frontend;
