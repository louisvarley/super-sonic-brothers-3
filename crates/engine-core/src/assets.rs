//! Data shared by every act that used to live in the legacy `ObjectArt`/`LevelData`: the oscillator start
//! values, the bridge sag tables, the title-screen ripple table (Emerald Hill's background) and the zone's
//! palette cycle.

use crate::oscillation::OscillationData;

#[derive(Clone, Debug, Default)]
pub struct BridgeData {
    /// How deep each log sags, per bridge length and standing log.
    pub depression: Vec<u8>,
    /// The bend profile: how much of the sag each log gets.
    pub bend: Vec<u8>,
    pub depression_first_subtype: i32,
}

/// The diagonal springs' height tables (0x1CD26 up, 0x1CD42 down). Only the first $1C bytes matter; the
/// $7FFF entry is the original's out-of-bounds read for a flipped spring touched at exactly its right edge.
#[derive(Clone, Debug, Default)]
pub struct SpringSlopes {
    pub up: Vec<i8>,
    pub down: Vec<i8>,
}

#[derive(Clone, Debug, Default)]
pub struct PaletteCycle {
    pub colours: Vec<usize>,
    pub timer_reload: i16,
    pub frames: Vec<Vec<u16>>,
}

#[derive(Clone, Debug, Default)]
pub struct SpiralData {
    /// The height of the path per pixel along the spiral (0x256BC).
    pub heights: Vec<u8>,
    /// The player's flip angle per 8 px along it (0x25688).
    pub flip_angles: Vec<u8>,
}

#[derive(Clone, Debug, Default)]
pub struct GlobalAssets {
    pub oscillation: Option<OscillationData>,
    pub bridge: Option<BridgeData>,
    pub spring_slopes: Option<SpringSlopes>,
    pub spiral: Option<SpiralData>,
    /// Marble Zone's grass-platform height profiles (`objects/mz-grass-platform/heights.json`).
    pub mz_heights: Vec<u8>,
    /// Caterkiller's rise animation (`objects/mz-caterkiller/wave.json`).
    pub mz_wave: Vec<u8>,
    /// The results screen's level order (`objects/signpost/level-order.json`).
    pub level_order: Vec<u8>,
    /// The lava-line palette colours (`objects/lava-line/colours.json`).
    pub lava_colours: Vec<u8>,
    /// The collapsing ledge's top profile and fragment delays (`objects/ledge/ledge.json`).
    pub ledge_slope: Vec<i8>,
    pub ledge_delays: Vec<u8>,
    /// `objects/frontend/title-scroll.json`; indexed from 0xDFA8.
    pub wave_table: Vec<u8>,
    pub palette_cycle: Option<PaletteCycle>,
}
