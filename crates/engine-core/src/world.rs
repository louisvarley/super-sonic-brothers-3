//! One act's RAM setup, the way `legacy/src/SonicBros3/Logic/Level/World.cs` does it: `LevelDataLoad`,
//! `LevelSizeLoad`, `InitCameraValues`, `InitPlayers` and the first `ObjectManager`/`RingsManager` pass.
//!
//! Everything downstream reads the 64 KiB image in [`Ram`]; the level data itself arrives through [`LevelSource`]
//! so this crate stays free of asset I/O (the `rom-assets` crate implements the trait for its `LevelData`).
//!
//! Still left to later milestones: the PLC queue, the HUD, sprite art/VRAM, the SMPS driver and the title card.

use crate::camera::is_easter_egg;
use crate::player::{Player, Unported, WaterMap};
use crate::{vars, Obj, Ram};

#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct Bounds {
    pub min_x: i32,
    pub max_x: i32,
    pub min_y: i32,
    pub max_y: i32,
}

#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct Start {
    pub x: i32,
    pub y: i32,
}

/// One entry of the act's object placement list (`zones/zone_XX/act_N/objects.json`).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ObjectSpawn {
    pub x: i32,
    pub y: i32,
    pub id: u8,
    pub subtype: u8,
    pub flip_x: bool,
    pub flip_y: bool,
    pub remember: bool,
}

/// One ring group (`rings.json`): `count` rings 24 px apart, horizontally or vertically.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct RingSpawn {
    pub x: i32,
    pub y: i32,
    pub count: u8,
    pub vertical: bool,
}

/// What [`World`] needs from one act. `rom-assets` implements this for its `LevelData`.
pub trait LevelSource {
    /// Chunks, layout, blocks, collision indexes and palette (the legacy `LevelDataLoad`).
    fn fill_level_ram(&self, ram: &mut Ram, zone: u8, act: u8);
    fn bounds(&self) -> Bounds;
    fn start(&self) -> Start;
    /// The SMB3 water map, when the act has gameplay metadata (`Smb3Gameplay.IsWater`).
    fn water_map(&self) -> Option<WaterMap> {
        None
    }
    /// The act's object placement list (the `ObjectsManager` records).
    fn objects(&self) -> Vec<ObjectSpawn> {
        Vec::new()
    }
    /// The list's address in the original ROM (`objects.json` `listAddress`).
    fn object_list_address(&self) -> u32 {
        0
    }
    /// The x of the record before the list (`objects.json` `boundaryX`).
    fn object_boundary_x(&self) -> i32 {
        0
    }
    /// The act's ring groups.
    fn rings(&self) -> Vec<RingSpawn> {
        Vec::new()
    }
}

pub struct World {
    pub ram: Ram,
    pub sonic: Player,
    pub tails: Player,
    /// Present when `Sonic.WaterAt` is set in the legacy World (SMB3 acts with gameplay metadata).
    pub water: Option<WaterMap>,
    pub object_manager: crate::objects::ObjectManager,
    pub objects: crate::objects::Objects,
    pub rings: crate::rings::RingsManager,
}

impl World {
    /// Build one act. `previous` is the RAM of the run being restarted (lives, score, checkpoints): the legacy
    /// `World` keeps `$FE00-$FE5F` and `$FEB0-$FFFF` across a restart.
    pub fn new(
        level: &dyn LevelSource,
        zone: u8,
        act: u8,
        previous: Option<Ram>,
    ) -> Result<Self, Unported> {
        let mut ram = Ram::new();
        let new_game = previous.is_none();
        if let Some(prev) = previous {
            ram.bytes_mut()[0xFE00..0xFE60].copy_from_slice(&prev.bytes()[0xFE00..0xFE60]);
            ram.bytes_mut()[0xFEB0..0x10000].copy_from_slice(&prev.bytes()[0xFEB0..0x10000]);
            // The 68k SMPS driver's RAM ($F000-$F5C0) is copied too, but sound is a later milestone.
            // Level init (0x4FFC) without a checkpoint: rings, time and the extra-life flags reset.
            if ram.u8(vars::LAST_STAR_POLE_HIT) == 0 {
                ram.w16(vars::RING_COUNT, 0);
                if ram.u8(vars::TIMED_MODE) == 0 {
                    ram.w32(vars::TIMER, 0);
                }
                ram.w8(vars::EXTRA_LIFE_FLAGS, 0);
                ram.w16(vars::RING_COUNT_P2, 0);
                ram.w32(vars::TIMER_P2, 0);
                ram.w8(vars::EXTRA_LIFE_FLAGS_P2, 0);
            }
            for a in [
                vars::TIME_OVER_FLAG,
                vars::TIME_OVER_FLAG_P2,
                vars::SLOT_MACHINE_ROUTINE,
                vars::TELEPORT_TIMER,
                vars::TELEPORT_FLAG,
            ] {
                ram.w8(a, 0);
            }
            for a in [
                vars::SLOT_MACHINE_IN_USE,
                vars::DEBUG_PLACEMENT_MODE,
                vars::LEVEL_INACTIVE_FLAG,
                vars::RINGS_COLLECTED,
                vars::RINGS_COLLECTED_P2,
                vars::MONITORS_BROKEN,
                vars::MONITORS_BROKEN_P2,
                vars::LOSER_TIME_LEFT,
            ] {
                ram.w16(a, 0);
            }
            ram.w16(vars::LEVEL_FRAME_COUNTER, 0);
        }

        level.fill_level_ram(&mut ram, zone, act);

        // LevelSizeLoad (0xD784): bounds, default camera bias, start position and initial camera.
        let bounds = level.bounds();
        ram.w16(vars::CAMERA_MIN_X_POS, bounds.min_x as u16);
        ram.w16(vars::CAMERA_MAX_X_POS, bounds.max_x as u16);
        ram.w16(vars::CAMERA_MIN_Y_POS, bounds.min_y as u16);
        ram.w16(vars::CAMERA_MAX_Y_POS, bounds.max_y as u16);
        ram.w16(vars::CAMERA_Y_POS_BIAS, 0x60);
        ram.w8(vars::MAIN_CHARACTER, 1);
        let sonic = Player::sonic();
        let start = level.start();
        sonic.obj.set_x(&mut ram, start.x as u16);
        sonic.obj.set_y(&mut ram, start.y as u16);
        let start_x = sonic.obj.x(&ram) as i32;
        let start_y = sonic.obj.y(&ram) as i32;

        // Tails' copies of the camera limits (equal to Sonic's in Sonic-and-Tails mode).
        for i in (0..8).step_by(2) {
            ram.w16(
                vars::TAILS_MIN_X_POS.wrapping_add(i),
                ram.u16(vars::CAMERA_MIN_X_POS.wrapping_add(i)),
            );
        }
        // InitPlayers (0x52CE): the dust slots, and Tails 32 px behind Sonic.
        ram.w8(vars::SONIC_DUST, 8);
        if !matches!(zone, 0x06 | 0x0E | 0x10) {
            ram.w8(vars::SIDEKICK, 2);
            let tails = Obj::new(vars::SIDEKICK);
            tails.set_x(&mut ram, start_x.wrapping_sub(0x20) as u16);
            tails.set_y(&mut ram, start_y.wrapping_add(4) as u16);
            ram.w8(vars::TAILS_DUST, 8);
        }

        let mut cam_x = start_x - 0xA0;
        if cam_x < 0 {
            cam_x = 0;
        }
        if (cam_x as u32) >= bounds.max_x as u32 {
            cam_x = bounds.max_x;
        }
        let mut cam_y = start_y - 0x60;
        if cam_y < 0 {
            cam_y = 0;
        }
        let max_y = ram.u16(vars::CAMERA_MAX_Y_POS) as i16 as i32;
        if cam_y >= max_y {
            cam_y = max_y;
        }

        // Rings.Setup (0x1B03A): expand and sort the act's ring groups into the ring-manager table.
        let rings_data = level.rings();
        let mut rings = crate::rings::RingsManager::new();
        rings.setup(&mut ram, &rings_data);

        if new_game {
            // A new game (title screen): 3 lives, the first extra life at 50000 points.
            ram.w8(vars::LIFE_COUNT, 3);
            ram.w32(vars::NEXT_EXTRA_LIFE_SCORE, 5000);
        }

        // The collision sets are passed to the player by the session; World keeps only the RAM.

        ram.w16(vars::CAMERA_X_POS, cam_x as u16);
        ram.w16(vars::CAMERA_Y_POS, cam_y as u16);
        ram.w16(vars::HORIZ_BLOCK_CROSSED_FLAG, 0x1010); // scroll-flag toggles (LevelSizeLoad)
        ram.w16(vars::CAMERA_MAX_Y_POS_TARGET, max_y as u16);
        init_camera_values(&mut ram, cam_x, cam_y, zone)?;

        // 0x5F1E: the SMB3 acts have no signpost.
        ram.w16(vars::LEVEL_HAS_SIGNPOST, 0);
        // Level init (0x505A): the HUD redraws score, rings and time on the first VBlank.
        ram.w8(vars::UPDATE_HUD_SCORE, 1);
        ram.w8(vars::UPDATE_HUD_RINGS, 1);
        ram.w8(vars::UPDATE_HUD_TIMER, 1);
        ram.w8(vars::UPDATE_HUD_TIMER_P2, 1);
        // ObjectManager.Run() and Rings.Run() lay out the initial objects and the ring window.
        ram.w8(vars::LEVEL_STARTED_FLAG, 1); // ring/HUD drawing enabled

        // No title card (the traces record `titleCards: false`), so the controls start unlocked and game mode is
        // level mode. Obj01_Init sets the speeds again on the first step, as in the original.
        ram.w8(vars::GAME_MODE, 0x0C);
        ram.w8(vars::CONTROL_LOCKED, 0);
        ram.w8(vars::SONIC_LAST_LOADED_DPLC, 0xFF); // no DPLC frame loaded yet
        set_normal_speeds(&mut ram);
        // The probes write these on the first frame; a fresh act starts with them clear, as the original does.
        ram.w8(vars::PRIMARY_ANGLE, 0);
        ram.w8(vars::SECONDARY_ANGLE, 0);
        ram.w32(vars::COLLISION_ADDR, 0);

        let water = level.water_map();
        let tails = Player::tails();
        let mut object_manager = crate::objects::ObjectManager::new(
            level.objects(),
            level.object_list_address(),
            level.object_boundary_x(),
        );
        object_manager.run(&mut ram); // level init runs the object manager once before the first frame
        rings.run(&mut ram); // and lays out the ring window

        Ok(Self {
            ram,
            sonic,
            tails,
            water,
            object_manager,
            objects: crate::objects::Objects::new(),
            rings,
        })
    }

    pub fn camera_x(&self) -> i32 {
        self.ram.u16(vars::CAMERA_X_POS) as i32
    }

    pub fn camera_y(&self) -> i32 {
        self.ram.u16(vars::CAMERA_Y_POS) as i32
    }
}

fn set_normal_speeds(ram: &mut Ram) {
    ram.w16(vars::SONIC_TOP_SPEED, 0x600);
    ram.w16(vars::SONIC_ACCELERATION, 0x0C);
    ram.w16(vars::SONIC_DECELERATION, 0x80);
}

/// `InitCameraValues` (0xDAEC) and the zone's own part (0xDC7C). The SMB3 zones have no part of their own.
fn init_camera_values(ram: &mut Ram, cam_x: i32, cam_y: i32, zone: u8) -> Result<(), Unported> {
    if ram.u8(vars::LAST_STAR_POLE_HIT) == 0 {
        for a in [
            vars::CAMERA_BG_Y_POS,
            vars::CAMERA_BG2_Y_POS,
            vars::CAMERA_BG_Y_POS_P2,
            vars::CAMERA_BG2_Y_POS_P2,
        ] {
            ram.w16(a, cam_y as u16);
        }
        for a in [
            vars::CAMERA_BG_X_POS,
            vars::CAMERA_BG2_X_POS,
            vars::CAMERA_BG3_X_POS,
            vars::CAMERA_BG_X_POS_P2,
            vars::CAMERA_BG2_X_POS_P2,
            vars::CAMERA_BG3_X_POS_P2,
        ] {
            ram.w16(a, cam_x as u16);
        }
    }
    if zone == 0x13 || zone == 0x14 || is_easter_egg(zone) {
        return Ok(()); // 0xDC98: Marble Zone and the SMB3 zones have no part of their own
    }
    Err(Unported::new(
        0xDB1A,
        format!("camera init for zone {zone:02X}"),
    ))
}

#[cfg(test)]
mod tests {
    use super::*;

    struct Fixture;

    impl LevelSource for Fixture {
        fn fill_level_ram(&self, ram: &mut Ram, zone: u8, act: u8) {
            ram.w16(
                vars::CURRENT_ZONE_AND_ACT,
                ((zone as u16) << 8) | act.wrapping_sub(1) as u16,
            );
            ram.w8(vars::HACK_ZONE_FLAG, 1);
        }
        fn bounds(&self) -> Bounds {
            Bounds {
                min_x: 0,
                max_x: 0x400,
                min_y: 0,
                max_y: 0xD0,
            }
        }
        fn start(&self) -> Start {
            Start { x: 0x20, y: 0x18C }
        }
    }

    #[test]
    fn world_places_sonic_and_the_camera_like_level_size_load() {
        let world = World::new(&Fixture, 0x40, 1, None).expect("easter egg world");
        let ram = &world.ram;
        assert_eq!(ram.u8(vars::MAIN_CHARACTER), 1);
        assert_eq!(world.sonic.obj.x(ram), 0x20);
        assert_eq!(world.sonic.obj.y(ram), 0x18C);
        assert_eq!(ram.u16(vars::CAMERA_X_POS), 0);
        assert_eq!(ram.u16(vars::CAMERA_Y_POS), 0xD0);
        assert_eq!(ram.u16(vars::CAMERA_MAX_Y_POS_TARGET), 0xD0);
        assert_eq!(ram.u8(vars::GAME_MODE), 0x0C);
        assert_eq!(ram.u8(vars::LEVEL_STARTED_FLAG), 1);
        assert_eq!(ram.u8(vars::LIFE_COUNT), 3);
        assert_eq!(ram.u16(vars::SONIC_TOP_SPEED), 0x600);
    }

    #[test]
    fn restart_keeps_lives_and_resets_the_timer() {
        let mut first = World::new(&Fixture, 0x40, 1, None).expect("easter egg world");
        first.ram.w8(vars::LIFE_COUNT, 2);
        first.ram.w32(vars::TIMER, 0x1234);
        first.ram.w16(vars::SCORE, 0x0456);
        let old = std::mem::take(&mut first.ram);
        let second = World::new(&Fixture, 0x40, 1, Some(old)).expect("restart");
        assert_eq!(second.ram.u8(vars::LIFE_COUNT), 2);
        assert_eq!(second.ram.u16(vars::SCORE), 0x0456);
        assert_eq!(second.ram.u32(vars::TIMER), 0);
        assert_eq!(second.ram.u16(vars::LEVEL_FRAME_COUNTER), 0);
    }
}
