//! `RunObjects` (ROM 0x19960) and the object registry.
//!
//! The game keeps every object in a 64-byte slot of [`Ram`]; this module dispatches an occupied slot to its ported
//! handler and reproduces the shared slot bookkeeping (`MarkObjGone`/`DeleteObject`, the dynamic-slot scan and the
//! `SlotCounter` many objects read). Object types that are not ported yet stay inert and despawn like the original
//! instead of filling every slot.

pub mod common;
pub mod ehz;
pub mod manager;
pub mod smb3;
pub mod solid;
pub mod sprites;
pub mod touch;

pub use manager::ObjectManager;

use crate::collision::CollisionSets;
use crate::player;
use crate::{vars, Obj, Ram};

use crate::assets::{BridgeData, SpiralData, SpringSlopes};

/// The SMB3 object ids (`Smb3` in the legacy `Smb3Objects.cs`).
pub mod ids {
    pub const BLOCK: u8 = 0xE0;
    pub const GOOMBA: u8 = 0xE1;
    pub const PARAGOOMBA: u8 = 0xE2;
    pub const KOOPA: u8 = 0xE3;
    pub const PARATROOPA: u8 = 0xE4;
    pub const PIRANHA: u8 = 0xE5;
    pub const FIREBALL: u8 = 0xE6;
    pub const GOOMBA_PIPE: u8 = 0xE8;
    pub const MICRO_GOOMBA: u8 = 0xE9;
    pub const BOOMERANG_BRO: u8 = 0xEA;
    pub const BOOMERANG: u8 = 0xEB;
    pub const GOAL_TEXT: u8 = 0xE7;
}

/// Object ids shared with Sonic 2 (`Ids.cs`, `Monitor.cs`, `Animal.cs`).
pub mod common_ids {
    pub const TAILS_TAILS: u8 = 0x05;
    pub const DUST: u8 = 0x08;
    pub const EXPLOSION: u8 = 0x27;
    pub const ANIMAL: u8 = 0x28;
    pub const POINTS: u8 = 0x29;
    pub const LOST_RINGS: u8 = 0x37;
    pub const GOAL: u8 = 0x0D;
}

pub struct Objects {
    /// `RunObjects`' d7 for the slot being run ($8F at $B000, counting down); some objects read it.
    pub slot_counter: i32,
    /// Ids that ran without a port (kept for diagnostics; the legacy records these too).
    pub unported_seen: Vec<u8>,
    /// Set when the SMB3 goal roulette's message sequence ends (the legacy World.GoalCard).
    pub goal_card: Option<i32>,
    /// Emerald Hill's bridge sag tables (`objects/bridge/bridge.json`).
    pub bridge: Option<BridgeData>,
    /// The diagonal springs' height tables.
    pub spring_slopes: Option<SpringSlopes>,
    /// The spiral's height and flip-angle tables.
    pub spiral: Option<SpiralData>,
}

impl Default for Objects {
    fn default() -> Self {
        Self::new()
    }
}

impl Objects {
    pub fn new() -> Self {
        Self {
            slot_counter: 0,
            unported_seen: Vec::new(),
            goal_card: None,
            bridge: None,
            spring_slopes: None,
            spiral: None,
        }
    }

    pub fn with_assets(
        bridge: Option<BridgeData>,
        spring_slopes: Option<SpringSlopes>,
        spiral: Option<SpiralData>,
    ) -> Self {
        Self {
            bridge,
            spring_slopes,
            spiral,
            ..Self::new()
        }
    }

    /// An object slot's number: `(address - $B000) / $40`, masked with `$7F`.
    pub fn slot_index(address: u16) -> i32 {
        (((address.wrapping_sub(vars::MAIN_CHARACTER)) >> 6) & 0x7F) as i32
    }

    /// The slot with a given number.
    pub fn slot(index: i32) -> Obj {
        Obj::new(vars::MAIN_CHARACTER.wrapping_add((index as u16) << 6))
    }

    /// Runs every occupied slot after the players (`$B080`-`$D3FF`) in slot order.
    ///
    /// `player_dying` is Sonic's routine >= 6 at the start of `RunObjects` (0x1997E): then the dynamic slots are
    /// skipped (a redraw only) and the level freezes until the restart.
    pub fn run_after_players(
        &mut self,
        ram: &mut Ram,
        sets: &CollisionSets,
        player_dying: bool,
    ) -> Result<(), crate::player::Unported> {
        let mut a = vars::TITLE_CARD_ZONE_NAME;
        while a < vars::OBJECT_RAM_END {
            let id = ram.u8(a);
            if id != 0 {
                let o = Obj::new(a);
                if player_dying && ram.u16(vars::TWO_PLAYER_MODE) == 0 {
                    if (vars::FIRST_DYNAMIC_SLOT..vars::DYNAMIC_OBJECT_RAM_END).contains(&a) {
                        if o.on_screen(ram) {
                            sprites::display_sprite(ram, o); // 0x199B8
                        }
                        a = a.wrapping_add(solid::SLOT_SIZE);
                        continue;
                    }
                    self.slot_counter = if a < vars::FIRST_DYNAMIC_SLOT {
                        0x0F - Self::slot_index(a)
                    } else {
                        0x0F - (((a - vars::DYNAMIC_OBJECT_RAM_END) >> 6) as i32)
                    };
                } else {
                    self.slot_counter = 0x8F - Self::slot_index(a);
                }
                if !self.run_object(ram, sets, o, id)? {
                    mark_obj_gone(ram, o);
                }
            }
            a = a.wrapping_add(solid::SLOT_SIZE);
        }
        Ok(())
    }

    /// Runs the occupied slots in `[start, end)` (the title-card loop).
    pub fn run_range(
        &mut self,
        ram: &mut Ram,
        sets: &CollisionSets,
        start: u16,
        end: u16,
    ) -> Result<(), crate::player::Unported> {
        let mut a = start;
        while a < end {
            let id = ram.u8(a);
            if id != 0 {
                self.slot_counter = 0x8F - Self::slot_index(a);
                let o = Obj::new(a);
                if !self.run_object(ram, sets, o, id)? {
                    mark_obj_gone(ram, o);
                }
            }
            a = a.wrapping_add(solid::SLOT_SIZE);
        }
        Ok(())
    }

    /// Dispatch one object. Returns false when the id has no port in this zone (the caller marks it gone).
    fn run_object(
        &mut self,
        ram: &mut Ram,
        sets: &CollisionSets,
        o: Obj,
        id: u8,
    ) -> Result<bool, crate::player::Unported> {
        if ram.u8(vars::HACK_ZONE_FLAG) == 0 {
            return self.run_s2_object(ram, sets, o, id);
        }
        match id {
            ids::BLOCK => smb3::block(ram, sets, o)?,
            ids::GOOMBA | ids::PARAGOOMBA => smb3::goomba(ram, sets, o),
            ids::KOOPA | ids::PARATROOPA => smb3::koopa(ram, sets, o),
            ids::PIRANHA => smb3::piranha(ram, sets, o),
            ids::FIREBALL => smb3::fireball(ram, o),
            ids::GOOMBA_PIPE => smb3::goomba_pipe(ram, o),
            ids::MICRO_GOOMBA => smb3::micro_goomba(ram, o),
            ids::BOOMERANG_BRO => smb3::boomerang_bro(ram, sets, o),
            ids::BOOMERANG => smb3::boomerang(ram, o),
            ids::GOAL_TEXT => {}
            common_ids::LOST_RINGS => common::lost_rings(ram, sets, o, self.slot_counter),
            common_ids::EXPLOSION => common::explosion(ram, o),
            common_ids::ANIMAL => common::animal(ram, o),
            common_ids::POINTS => common::points(ram, o),
            common_ids::GOAL => smb3::roulette_goal(ram, o, &mut self.goal_card),
            // Tails' tails (object $05) and the players' dust are display-only in the legacy code; they never
            // write to a player or an interactive object.
            common_ids::TAILS_TAILS | common_ids::DUST => {}
            _ => {
                if !self.unported_seen.contains(&id) {
                    self.unported_seen.push(id);
                }
                return Ok(false);
            }
        }
        Ok(true)
    }

    /// The Sonic 2 column (`HackZoneFlag == 0`, `RegisterS2`/the object table's first column). The ids whose
    /// routine is shared with the hack's column call the same handler; the EHZ-specific entries override it.
    fn run_s2_object(
        &mut self,
        ram: &mut Ram,
        sets: &CollisionSets,
        o: Obj,
        id: u8,
    ) -> Result<bool, crate::player::Unported> {
        match id {
            0x03 => ehz::plane_switcher(ram, o),
            0x06 => ehz::spiral(ram, o, self.spiral.as_ref())?,
            0x11 => ehz::bridge(ram, o, self.bridge.as_ref())?,
            0x18 => ehz::platform(ram, sets, o)?,
            0x1C => ehz::scenery_s2(ram, o)?,
            0x26 => ehz::monitor(ram, sets, o)?,
            0x2E => ehz::monitor_contents(ram, o)?,
            0x36 => ehz::spikes_s2(ram, sets, o)?,
            0x41 => ehz::spring(ram, sets, o, self.spring_slopes.as_ref())?,
            0x49 => ehz::waterfall(ram, o),
            0x4B => ehz::buzzer(ram, o)?,
            0x5C => ehz::masher(ram, o),
            0x98 => ehz::coconut(ram, o)?,
            0x9D => ehz::coconuts(ram, o)?,
            common_ids::LOST_RINGS => common::lost_rings(ram, sets, o, self.slot_counter),
            common_ids::EXPLOSION => common::explosion(ram, o),
            common_ids::ANIMAL => common::animal(ram, o),
            common_ids::POINTS => common::points(ram, o),
            common_ids::TAILS_TAILS | common_ids::DUST => {}
            _ => {
                if !self.unported_seen.contains(&id) {
                    self.unported_seen.push(id);
                }
                return Ok(false);
            }
        }
        Ok(true)
    }
}

/// `SingleObjLoad` (0x1BD56): the first free dynamic slot, or none.
pub fn find_free_slot(ram: &Ram) -> Option<Obj> {
    let count = if ram.u16(vars::TWO_PLAYER_MODE) != 0 {
        0x28
    } else {
        0x70
    };
    for i in 0..count {
        let a = vars::FIRST_DYNAMIC_SLOT.wrapping_add((i as u16) * solid::SLOT_SIZE);
        if ram.u8(a) == 0 {
            return Some(Obj::new(a));
        }
    }
    None
}

/// `SingleObjLoad2` (0x1BD76): the first free slot after `o`, up to `$D000`.
pub fn find_free_slot_after(ram: &Ram, o: Obj) -> Option<Obj> {
    let count = (vars::DYNAMIC_OBJECT_RAM_END - o.address()) >> 6;
    for i in 0..count {
        let a = o.address().wrapping_add(i * solid::SLOT_SIZE);
        if ram.u8(a) == 0 {
            return Some(Obj::new(a));
        }
    }
    None
}

/// `MarkObjGone` (0x1A184): delete the object once it is more than $280 px outside the load window.
pub fn mark_obj_gone(ram: &mut Ram, o: Obj) {
    if ram.u16(vars::TWO_PLAYER_MODE) != 0 {
        return;
    }
    let d0 = (o.x(ram) & 0xFF80).wrapping_sub(ram.u16(vars::CAMERA_X_POS_COARSE));
    if d0 <= 0x280 {
        return;
    }
    let index = o.respawn_index(ram);
    if index != 0 {
        ram.clear_bit(
            vars::OBJECT_RESPAWN_TABLE
                .wrapping_add(2)
                .wrapping_add(index as u16),
            7,
        );
    }
    delete_object(ram, o);
}

/// `MarkObjGone` variant that displays the object while it is in range (`ObjectKit.MarkObjGoneDisplay`).
pub fn display_or_mark_gone(ram: &mut Ram, o: Obj) {
    if ram.u16(vars::TWO_PLAYER_MODE) != 0 {
        sprites::display_sprite(ram, o);
        return;
    }
    let d0 = (o.x(ram) & 0xFF80).wrapping_sub(ram.u16(vars::CAMERA_X_POS_COARSE));
    if d0 <= 0x280 {
        sprites::display_sprite(ram, o);
        return;
    }
    let index = o.respawn_index(ram);
    if index != 0 {
        ram.clear_bit(
            vars::OBJECT_RESPAWN_TABLE
                .wrapping_add(2)
                .wrapping_add(index as u16),
            7,
        );
    }
    delete_object(ram, o);
}

/// The despawn test objects share: x more than $280 px past the object manager's window.
pub fn out_of_range(ram: &Ram, x: u16) -> bool {
    ((x & 0xFF80).wrapping_sub(ram.u16(vars::CAMERA_X_POS_COARSE))) > 0x280
}

/// `DeleteObject` (0x1A22C): clear the whole slot.
pub fn delete_object(ram: &mut Ram, o: Obj) {
    o.clear(ram);
}

/// `ObjectMoveAndFall` (0x1A0C6).
pub fn move_and_fall(ram: &mut Ram, o: Obj) {
    let yv = o.y_vel(ram);
    o.set_y_vel(ram, yv.wrapping_add(0x38));
    o.set_x_long(
        ram,
        o.x_long(ram)
            .wrapping_add(((o.x_vel(ram) as i32) << 8) as u32),
    );
    o.set_y_long(ram, o.y_long(ram).wrapping_add(((yv as i32) << 8) as u32));
}

/// `ObjectMove` (0x1A0F2).
pub fn move_object(ram: &mut Ram, o: Obj) {
    o.set_x_long(
        ram,
        o.x_long(ram)
            .wrapping_add(((o.x_vel(ram) as i32) << 8) as u32),
    );
    o.set_y_long(
        ram,
        o.y_long(ram)
            .wrapping_add(((o.y_vel(ram) as i32) << 8) as u32),
    );
}

/// The respawn-table entry for an object (bit 0 "destroyed", bit 7 "loaded").
pub fn respawn_entry(o: Obj, ram: &Ram) -> u16 {
    vars::OBJECT_RESPAWN_TABLE
        .wrapping_add(2)
        .wrapping_add(o.respawn_index(ram) as u16)
}

/// Re-exported for handlers that need the player animation ids (`PlayerAnim`).
pub use player::anim;

#[cfg(test)]
mod tests {
    use super::*;
    use crate::collision::CollisionSet;

    fn sets() -> CollisionSets {
        let set = CollisionSet {
            name: "empty".into(),
            angles: vec![0; 256],
            heights: vec![0; 256 * 16],
            widths: vec![0; 256 * 16],
        };
        CollisionSets {
            normal: set.clone(),
            alternate: set,
        }
    }

    #[test]
    fn find_free_slot_scans_the_dynamic_range() {
        let mut ram = Ram::new();
        assert_eq!(
            find_free_slot(&ram),
            Some(Obj::new(vars::FIRST_DYNAMIC_SLOT))
        );
        ram.w8(vars::FIRST_DYNAMIC_SLOT, 1);
        assert_eq!(
            find_free_slot(&ram),
            Some(Obj::new(vars::FIRST_DYNAMIC_SLOT + 0x40))
        );
    }

    #[test]
    fn mark_obj_gone_clears_the_respawn_bit_and_the_slot() {
        let mut ram = Ram::new();
        let o = Obj::new(vars::FIRST_DYNAMIC_SLOT);
        o.set_id(&mut ram, 0xE1);
        o.set_x(&mut ram, 0x500);
        o.set_respawn_index(&mut ram, 2);
        ram.set_bit(vars::OBJECT_RESPAWN_TABLE + 4, 7);
        mark_obj_gone(&mut ram, o);
        assert_eq!(o.id(&ram), 0);
        assert!(!ram.bit(vars::OBJECT_RESPAWN_TABLE + 4, 7));
    }

    #[test]
    fn dying_freezes_the_dynamic_slots() {
        let mut ram = Ram::new();
        ram.w8(vars::HACK_ZONE_FLAG, 1);
        let sets = sets();
        let o = Obj::new(vars::FIRST_DYNAMIC_SLOT);
        o.set_id(&mut ram, ids::GOOMBA);
        o.set_x(&mut ram, 232);
        o.set_y(&mut ram, 100);
        o.set_x_radius(&mut ram, 8);
        o.set_y_radius(&mut ram, 8);
        o.set_collision_flags(&mut ram, 0x0B);
        o.set_render_flags(&mut ram, 0x84);
        o.set_routine(&mut ram, 2);

        let mut objects = Objects::new();
        objects
            .run_after_players(&mut ram, &sets, true)
            .expect("run");
        assert_eq!(
            o.y_vel(&ram),
            0,
            "dynamic objects do not run while Sonic dies"
        );

        objects
            .run_after_players(&mut ram, &sets, false)
            .expect("run");
        assert_eq!(o.y_vel(&ram), 0x38, "gravity after the first live frame");
    }
}
