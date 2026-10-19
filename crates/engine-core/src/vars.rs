//! Work-RAM addresses, ported from `legacy/src/SonicBros3/Logic/Core/Vars.cs`.
//!
//! Names follow the Sonic 2 disassembly in PascalCase there; here they are `SCREAMING_SNAKE_CASE` with the same
//! meaning. Everything lives in [`crate::Ram`] at these addresses so records can be compared byte for byte.

// ---- Level data ----
pub const CHUNK_TABLE: u16 = 0x0000; // 128x128 chunk mappings
pub const LEVEL_LAYOUT: u16 = 0x8000; // foreground/background rows interleaved
pub const BLOCK_TABLE: u16 = 0x9000; // 16x16 block mappings
pub const TEMP_ARRAY_LAYER_DEF: u16 = 0xA800;
pub const OBJECT_DISPLAY_LISTS: u16 = 0xAC00; // 8 priority lists of $40 words

// ---- Object slots ----
pub const MAIN_CHARACTER: u16 = 0xB000; // Sonic
pub const SIDEKICK: u16 = 0xB040; // Tails
pub const TITLE_CARD_ZONE_NAME: u16 = 0xB080;
pub const TITLE_CARD_ZONE: u16 = 0xB0C0;
pub const TITLE_CARD_ACT_NUMBER: u16 = 0xB100;
pub const TITLE_CARD_BACKGROUND: u16 = 0xB140;
pub const TITLE_CARD_BOTTOM: u16 = 0xB180;
pub const TITLE_CARD_LEFT: u16 = 0xB1C0;
pub const FIRST_DYNAMIC_SLOT: u16 = 0xB400;
pub const DYNAMIC_OBJECT_RAM_END: u16 = 0xD000;
pub const OBJECT_RAM_END: u16 = 0xD400;
pub const TAILS_TAILS: u16 = 0xD000;
pub const SONIC_DUST: u16 = 0xD100;
pub const TAILS_DUST: u16 = 0xD140;
pub const SONIC_SHIELD: u16 = 0xD180;
pub const TAILS_SHIELD: u16 = 0xD1C0;
pub const SONIC_INVINCIBILITY_STARS: u16 = 0xD200;
pub const TAILS_INVINCIBILITY_STARS: u16 = 0xD300;
pub const PRIMARY_COLLISION: u16 = 0xD600;
pub const SECONDARY_COLLISION: u16 = 0xD900;

// ---- Scroll, rings, camera ----
pub const HORIZ_SCROLL_BUF: u16 = 0xE000;
pub const SONIC_STAT_RECORD_BUF: u16 = 0xE400;
pub const SONIC_POS_RECORD_BUF: u16 = 0xE500;
pub const TAILS_POS_RECORD_BUF: u16 = 0xE600;
pub const CLOUD_COUNTERS: u16 = 0xE700;
pub const RING_POSITIONS: u16 = 0xE800;
pub const RING_POSITIONS_FIRST: u16 = 0xE806;
pub const CAMERA_X_POS: u16 = 0xEE00;
pub const CAMERA_Y_POS: u16 = 0xEE04;
pub const CAMERA_BG_X_POS: u16 = 0xEE08;
pub const CAMERA_BG_Y_POS: u16 = 0xEE0C;
pub const CAMERA_BG2_X_POS: u16 = 0xEE10;
pub const CAMERA_BG2_Y_POS: u16 = 0xEE14;
pub const CAMERA_BG3_X_POS: u16 = 0xEE18;
pub const CAMERA_BG3_Y_POS: u16 = 0xEE1C;
pub const CAMERA_X_POS_P2: u16 = 0xEE20;
pub const CAMERA_Y_POS_P2: u16 = 0xEE24;
pub const CAMERA_BG_X_POS_P2: u16 = 0xEE28;
pub const CAMERA_BG_Y_POS_P2: u16 = 0xEE2C;
pub const CAMERA_BG2_X_POS_P2: u16 = 0xEE30;
pub const CAMERA_BG2_Y_POS_P2: u16 = 0xEE34;
pub const CAMERA_BG3_X_POS_P2: u16 = 0xEE38;
pub const CAMERA_BG3_Y_POS_P2: u16 = 0xEE3C;
pub const HORIZ_BLOCK_CROSSED_FLAG: u16 = 0xEE40;
pub const VERTI_BLOCK_CROSSED_FLAG: u16 = 0xEE41;
pub const HORIZ_BLOCK_CROSSED_FLAG_BG: u16 = 0xEE42;
pub const VERTI_BLOCK_CROSSED_FLAG_BG: u16 = 0xEE43;
pub const HORIZ_BLOCK_CROSSED_FLAG_BG2: u16 = 0xEE44;
pub const HORIZ_BLOCK_CROSSED_FLAG_BG3: u16 = 0xEE46;
pub const SCROLL_FLAGS: u16 = 0xEE50;
pub const SCROLL_FLAGS_BG: u16 = 0xEE52;
pub const SCROLL_FLAGS_BG2: u16 = 0xEE54;
pub const SCROLL_FLAGS_BG3: u16 = 0xEE56;
pub const CAMERA_X_POS_DIFF: u16 = 0xEEB0;
pub const CAMERA_Y_POS_DIFF: u16 = 0xEEB2;
pub const CAMERA_X_POS_DIFF_P2: u16 = 0xEEB8;
pub const CAMERA_Y_POS_DIFF_P2: u16 = 0xEEBA;
pub const SCROLL_LOCK: u16 = 0xEEBE;
pub const SCROLL_LOCK_P2: u16 = 0xEEBF;
pub const CAMERA_MAX_Y_POS_TARGET: u16 = 0xEEC6;
pub const CAMERA_MIN_X_POS: u16 = 0xEEC8;
pub const CAMERA_MAX_X_POS: u16 = 0xEECA;
pub const CAMERA_MIN_Y_POS: u16 = 0xEECC;
pub const CAMERA_MAX_Y_POS: u16 = 0xEECE;
pub const HORIZ_SCROLL_DELAY_VAL: u16 = 0xEED0;
pub const HORIZ_SCROLL_DELAY_VAL_P2: u16 = 0xEED4;
pub const SONIC_POS_RECORD_INDEX: u16 = 0xEED2;
pub const TAILS_POS_RECORD_INDEX: u16 = 0xEED6;
pub const CAMERA_Y_POS_BIAS: u16 = 0xEED8;
pub const CAMERA_Y_POS_BIAS_P2: u16 = 0xEEDA;
pub const DEFORM_LOCK: u16 = 0xEEDC;
pub const CAMERA_MAX_Y_POS_CHANGING: u16 = 0xEEDE;
pub const DYNAMIC_RESIZE_ROUTINE: u16 = 0xEEDF;
pub const CAMERA_X_POS_COPY: u16 = 0xEEF0;
pub const CAMERA_Y_POS_COPY: u16 = 0xEEF4;
pub const TAILS_MIN_X_POS: u16 = 0xEEF8;
pub const TAILS_MAX_X_POS: u16 = 0xEEFA;
pub const TAILS_MIN_Y_POS: u16 = 0xEEFC;
pub const TAILS_MAX_Y_POS: u16 = 0xEEFE;
pub const RING_CONSUMPTION_TABLE: u16 = 0xEF80;
pub const RING_CONSUMPTION_LIST: u16 = 0xEF82;

// ---- Sound (the hack's 68k SMPS driver RAM is at $F000) ----
pub const SMPS_QUEUE1: u16 = 0xF00A;
pub const SMPS_QUEUE2: u16 = 0xF00B;

// ---- Game state ----
pub const GAME_MODE: u16 = 0xF600;
pub const CTRL1_HELD_LOGICAL: u16 = 0xF602;
pub const CTRL1_PRESS_LOGICAL: u16 = 0xF603;
pub const CTRL1_HELD: u16 = 0xF604;
pub const CTRL1_PRESS: u16 = 0xF605;
pub const CTRL2_HELD: u16 = 0xF606;
pub const CTRL2_PRESS: u16 = 0xF607;
pub const DEMO_TIME_LEFT: u16 = 0xF614;
pub const VSCROLL_FACTOR_FG: u16 = 0xF616;
pub const VSCROLL_FACTOR_BG: u16 = 0xF618;
pub const VSCROLL_FACTOR_P2_FG: u16 = 0xF61E;
pub const TELEPORT_TIMER: u16 = 0xF622;
pub const TELEPORT_FLAG: u16 = 0xF623;
pub const LAVA_LINE_SCANLINE: u16 = 0xF625;
pub const PAL_FADE_START: u16 = 0xF626;
pub const PAL_FADE_LENGTH: u16 = 0xF627;
pub const SPRITE_COUNT: u16 = 0xF62C;
pub const PAL_CYCLE_FRAME: u16 = 0xF632;
pub const PAL_CYCLE_TIMER: u16 = 0xF634;
pub const RNG_SEED: u16 = 0xF636;
pub const GAME_PAUSED: u16 = 0xF63A;
pub const WATER_LEVEL1: u16 = 0xF646;
pub const WATER_LEVEL2: u16 = 0xF648;
pub const WATER_ROUTINE: u16 = 0xF64D;
pub const WATER_FULLSCREEN_FLAG: u16 = 0xF64E;
pub const SUPER_SONIC_PALETTE: u16 = 0xF65F;
pub const CTRL2_HELD_LOGICAL: u16 = 0xF66A;
pub const CTRL2_PRESS_LOGICAL: u16 = 0xF66B;
pub const SONIC_LOOK_DELAY_COUNTER: u16 = 0xF66C;
pub const TAILS_LOOK_DELAY_COUNTER: u16 = 0xF66E;
pub const PLC_BUFFER: u16 = 0xF680;
pub const TAILS_CONTROL_COUNTER: u16 = 0xF702;
pub const TAILS_RESPAWN_COUNTER: u16 = 0xF704;
pub const TAILS_CPU_ROUTINE: u16 = 0xF708;
pub const TAILS_CPU_TARGET_X: u16 = 0xF70A;
pub const TAILS_CPU_TARGET_Y: u16 = 0xF70C;
pub const TAILS_INTERACT_ID: u16 = 0xF70E;
pub const TAILS_CPU_JUMPING: u16 = 0xF70F;
pub const RINGS_MANAGER_ROUTINE: u16 = 0xF710;
pub const LEVEL_STARTED_FLAG: u16 = 0xF711;
pub const RING_START_ADDR: u16 = 0xF712;
pub const RING_END_ADDR: u16 = 0xF714;
pub const RING_START_ADDR_P2: u16 = 0xF716;
pub const RING_END_ADDR_P2: u16 = 0xF718;
pub const WATER_FLAG: u16 = 0xF730;
pub const BOSS_SPAWN_DELAY: u16 = 0xF73A;
pub const SONIC_TOP_SPEED: u16 = 0xF760;
pub const SONIC_ACCELERATION: u16 = 0xF762;
pub const SONIC_DECELERATION: u16 = 0xF764;
pub const SONIC_LAST_LOADED_DPLC: u16 = 0xF766;
pub const PRIMARY_ANGLE: u16 = 0xF768;
pub const SECONDARY_ANGLE: u16 = 0xF76A;
pub const OBJ_PLACEMENT_ROUTINE: u16 = 0xF76C;
pub const CAMERA_X_POS_LAST: u16 = 0xF76E;
pub const OBJ_LOAD_ADDR_RIGHT: u16 = 0xF770;
pub const OBJ_LOAD_ADDR_LEFT: u16 = 0xF774;
pub const OBJ_LOAD_ADDR_RIGHT_P2: u16 = 0xF778;
pub const OBJ_LOAD_ADDR_LEFT_P2: u16 = 0xF77C;
pub const OBJECT_RAM_BLOCK_INDICES: u16 = 0xF780;
pub const CAMERA_X_POS_LAST_P2: u16 = 0xF78C;
pub const COLLISION_ADDR: u16 = 0xF796;
pub const CHAIN_STOMPER_Y: u16 = 0xF7A4;
pub const BOSS_DEFEATED_FLAG: u16 = 0xF7A7;
pub const CURRENT_BOSS_ID: u16 = 0xF7AA;
pub const BIG_RING_ART_LEFT: u16 = 0xF7BE;
pub const WIND_TUNNEL_FLAG: u16 = 0xF7C7;
pub const CONTROL_LOCKED: u16 = 0xF7CC;
pub const SPECIAL_STAGE_REQUESTED: u16 = 0xF7CE;
pub const CONTROL_LOCKED_P2: u16 = 0xF7CF;
pub const CHAIN_BONUS_COUNTER: u16 = 0xF7D0;
pub const BONUS_COUNTDOWN1: u16 = 0xF7D2;
pub const BONUS_COUNTDOWN2: u16 = 0xF7D4;
pub const UPDATE_BONUS_SCORE: u16 = 0xF7D6;
pub const CAMERA_X_POS_COARSE: u16 = 0xF7DA;
pub const TAILS_LAST_LOADED_DPLC: u16 = 0xF7DE;
pub const TAILS_TAILS_LAST_LOADED_DPLC: u16 = 0xF7DF;
pub const BUTTON_VINE_TRIGGER: u16 = 0xF7E0;
pub const ANIM_COUNTERS: u16 = 0xF7F0;
pub const SPRITE_TABLE: u16 = 0xF800;
pub const NORMAL_PALETTE: u16 = 0xFB00;
pub const NORMAL_PALETTE_LINE2: u16 = 0xFB20;
pub const NORMAL_PALETTE_LINE3: u16 = 0xFB40;
pub const NORMAL_PALETTE_LINE4: u16 = 0xFB60;
pub const TARGET_PALETTE: u16 = 0xFB80;
pub const OBJECT_RESPAWN_TABLE: u16 = 0xFC00;

// ---- Level session ----
pub const LEVEL_INACTIVE_FLAG: u16 = 0xFE02;
pub const LEVEL_FRAME_COUNTER: u16 = 0xFE04;
pub const DEBUG_PLACEMENT_MODE: u16 = 0xFE08;
pub const VINT_RUNCOUNT: u16 = 0xFE0C;
pub const VINT_RUNCOUNT_LOW: u16 = 0xFE0F;
pub const CURRENT_ZONE_AND_ACT: u16 = 0xFE10;
pub const CURRENT_ACT: u16 = 0xFE11;
pub const LIFE_COUNT: u16 = 0xFE12;
pub const CURRENT_SPECIAL_ACT: u16 = 0xFE17;
pub const CONTINUE_COUNT: u16 = 0xFE18;
pub const SUPER_SONIC_FLAG: u16 = 0xFE19;
pub const TIME_OVER_FLAG: u16 = 0xFE1A;
pub const EXTRA_LIFE_FLAGS: u16 = 0xFE1B;
pub const UPDATE_HUD_LIVES: u16 = 0xFE1C;
pub const UPDATE_HUD_RINGS: u16 = 0xFE1D;
pub const UPDATE_HUD_TIMER: u16 = 0xFE1E;
pub const UPDATE_HUD_SCORE: u16 = 0xFE1F;
pub const RING_COUNT: u16 = 0xFE20;
pub const TIMER: u16 = 0xFE22;
pub const TIMER_MINUTE: u16 = 0xFE23;
pub const TIMER_SECOND: u16 = 0xFE24;
pub const TIMER_FRAME: u16 = 0xFE25;
pub const SCORE: u16 = 0xFE26;
pub const LAST_STAR_POLE_HIT: u16 = 0xFE30;
pub const SAVED_LAST_STAR_POLE_HIT: u16 = 0xFE31;
pub const SAVED_X_POS: u16 = 0xFE32;
pub const SAVED_Y_POS: u16 = 0xFE34;
pub const SAVED_RING_COUNT: u16 = 0xFE36;
pub const SAVED_TIMER: u16 = 0xFE38;
pub const SAVED_ART_TILE: u16 = 0xFE3C;
pub const SAVED_SOLID_BITS: u16 = 0xFE3E;
pub const SAVED_CAMERA_X_POS: u16 = 0xFE40;
pub const SAVED_WATER_LEVEL: u16 = 0xFE50;
pub const SAVED_WATER_ROUTINE: u16 = 0xFE52;
pub const SAVED_WATER_MOVE: u16 = 0xFE53;
pub const SAVED_EXTRA_LIFE_FLAGS: u16 = 0xFE54;
pub const SAVED_CAMERA_MAX_Y_POS: u16 = 0xFE56;
pub const SAVED_DYNAMIC_RESIZE_ROUTINE: u16 = 0xFE58;
pub const OSCILLATING_DATA: u16 = 0xFE5E;
pub const LOGSPIKE_ANIM_COUNTER: u16 = 0xFEA0;
pub const LOGSPIKE_ANIM_FRAME: u16 = 0xFEA1;
pub const RINGS_ANIM_COUNTER: u16 = 0xFEA2;
pub const RINGS_ANIM_FRAME: u16 = 0xFEA3;
pub const UNKNOWN_ANIM_COUNTER: u16 = 0xFEA4;
pub const UNKNOWN_ANIM_FRAME: u16 = 0xFEA5;
pub const RING_SPILL_ANIM_COUNTER: u16 = 0xFEA6;
pub const RING_SPILL_ANIM_FRAME: u16 = 0xFEA7;
pub const RING_SPILL_ANIM_ACCUM: u16 = 0xFEA8;
pub const TAILS_TOP_SPEED: u16 = 0xFEC0;
pub const TAILS_ACCELERATION: u16 = 0xFEC2;
pub const TAILS_DECELERATION: u16 = 0xFEC4;
pub const LIFE_COUNT_P2: u16 = 0xFEC6;
pub const EXTRA_LIFE_FLAGS_P2: u16 = 0xFEC7;
pub const UPDATE_HUD_LIVES_P2: u16 = 0xFEC8;
pub const UPDATE_HUD_RINGS_P2: u16 = 0xFEC9;
pub const UPDATE_HUD_TIMER_P2: u16 = 0xFECA;
pub const TIME_OVER_FLAG_P2: u16 = 0xFECC;
pub const RING_COUNT_P2: u16 = 0xFED0;
pub const TIMER_P2: u16 = 0xFED2;
pub const LAST_STAR_POLE_HIT_P2: u16 = 0xFEE0;
pub const SAVED_X_POS_P2: u16 = 0xFEE2;
pub const SAVED_Y_POS_P2: u16 = 0xFEE4;
pub const SAVED_ART_TILE_P2: u16 = 0xFEEC;
pub const SAVED_SOLID_BITS_P2: u16 = 0xFEEE;
pub const RINGS_COLLECTED: u16 = 0xFEF0;
pub const RINGS_COLLECTED_P2: u16 = 0xFEF2;
pub const MONITORS_BROKEN: u16 = 0xFEF4;
pub const MONITORS_BROKEN_P2: u16 = 0xFEF6;
pub const LOSER_TIME_LEFT: u16 = 0xFEF8;
pub const TIMED_MODE: u16 = 0xFF12;
pub const PERFECT_RINGS_LEFT: u16 = 0xFF40;
pub const PERFECT_RINGS_FLAG: u16 = 0xFF42;
pub const SLOT_MACHINE_IN_USE: u16 = 0xFF4C;
pub const SLOT_MACHINE_ROUTINE: u16 = 0xFF4E;
pub const PLAYER_MODE: u16 = 0xFF70;
pub const LEVEL_SELECT_HOLD_TIMER: u16 = 0xFF80;
pub const LEVEL_SELECT_ZONE: u16 = 0xFF82;
pub const SOUND_TEST_SOUND: u16 = 0xFF84;
pub const TITLE_SCREEN_OPTION: u16 = 0xFF86;
pub const TOTAL_BONUS_COUNTDOWN: u16 = 0xFF8E;
pub const LEVEL_MUSIC: u16 = 0xFF90;
pub const BONUS_COUNTDOWN3: u16 = 0xFF92;
pub const SONIC1_EMERALD_COUNT: u16 = 0xFFAF;
pub const EMERALD_COUNT: u16 = 0xFFB1;
pub const NEXT_EXTRA_LIFE_SCORE: u16 = 0xFFC0;
pub const LEVEL_HAS_SIGNPOST: u16 = 0xFFC8;
pub const TWO_PLAYER_MODE: u16 = 0xFFD8;
pub const LAVA_LINE_BELOW: u16 = 0xFFDA;
pub const MUSIC_TO_PLAY: u16 = 0xFFE0;
pub const SFX_TO_PLAY: u16 = 0xFFE1;
pub const SFX_TO_PLAY2: u16 = 0xFFE2;
pub const MUSIC_TO_PLAY2: u16 = 0xFFE4;
pub const SOUND_DRIVER_SELECT: u16 = 0xFFE5;
pub const HACK_ZONE_FLAG: u16 = 0xFFE6;
pub const LAVA_LINE_ENABLED: u16 = 0xFFE7;
pub const LAVA_LINE_HEIGHT: u16 = 0xFFE8;
pub const LAVA_LINE_MODE: u16 = 0xFFEA;
pub const LAVA_LINE_PALETTE_MODE: u16 = 0xFFEB;
pub const LAVA_LINE_EXTRA: u16 = 0xFFEC;
pub const LAVA_LINE_PALETTE_STATE: u16 = 0xFFEE;
pub const GRAPHICS_FLAGS: u16 = 0xFFF8;
pub const DEBUG_MODE_FLAG: u16 = 0xFFFA;

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn slot_layout_is_64_bytes() {
        assert_eq!(SIDEKICK - MAIN_CHARACTER, 0x40);
        assert_eq!(TITLE_CARD_ZONE_NAME - SIDEKICK, 0x40);
        assert_eq!(FIRST_DYNAMIC_SLOT - TITLE_CARD_LEFT, 0x240);
        assert_eq!(OBJECT_RAM_END - FIRST_DYNAMIC_SLOT, 0x2000);
    }

    #[test]
    fn known_addresses_pin_the_ram_map() {
        assert_eq!(CHUNK_TABLE, 0);
        assert_eq!(LEVEL_LAYOUT, 0x8000);
        assert_eq!(BLOCK_TABLE, 0x9000);
        assert_eq!(PRIMARY_COLLISION, 0xD600);
        assert_eq!(CAMERA_X_POS, 0xEE00);
        assert_eq!(RING_POSITIONS, 0xE800);
        assert_eq!(GAME_MODE, 0xF600);
        assert_eq!(NORMAL_PALETTE, 0xFB00);
        assert_eq!(CURRENT_ZONE_AND_ACT, 0xFE10);
        assert_eq!(RING_COUNT, 0xFE20);
        assert_eq!(HACK_ZONE_FLAG, 0xFFE6);
    }
}
