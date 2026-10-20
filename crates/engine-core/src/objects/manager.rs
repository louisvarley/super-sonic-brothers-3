//! `ObjectsManager` (ROM 0x1B824, 1-player path): spawns the act's placement list as the camera moves.
//!
//! The list is kept as the original's 6-byte records so the pointer arithmetic (right/left list pointers at
//! `$F770`/`$F774`) and the respawn table (`$FC00`) match. List addresses are relative to the ROM list, exactly as
//! the legacy `ObjectManager` keeps them.

use crate::world::ObjectSpawn;
use crate::{vars, Ram};

/// A small pointer into the record image: index of a record, including its 6-byte sentinels.
type Record = usize;

/// `Vars::ObjectRespawnTable` is $FC00.
const LIST_BASE: Record = 6; // the first real record (a leading boundary record precedes it)

pub struct ObjectManager {
    list: Vec<u8>,
    pointer_base: i32,
}

impl ObjectManager {
    pub fn new(placements: Vec<ObjectSpawn>, list_address: u32, boundary_x: i32) -> Self {
        let pointer_base = if list_address == 0 {
            LIST_BASE as i32
        } else {
            list_address as i32
        };
        let mut list = vec![0u8; LIST_BASE + placements.len() * 6 + 6];
        let mut i = LIST_BASE;
        for p in placements {
            let y = (p.y as u16
                | if p.flip_x { 0x2000 } else { 0 }
                | if p.flip_y { 0x4000 } else { 0 }
                | if p.remember { 0x8000 } else { 0 }) as i32;
            write16(&mut list, i, p.x as u16);
            write16(&mut list, i + 2, y as u16);
            list[i + 4] = p.id;
            list[i + 5] = p.subtype;
            i += 6;
        }
        write16(&mut list, i, 0xFFFF);
        if list_address != 0 {
            write16(&mut list, 0, boundary_x as u16);
        }
        Self { list, pointer_base }
    }

    fn x(&self, rec: Record) -> u16 {
        ((self.list[rec] as u16) << 8) | self.list[rec + 1] as u16
    }

    fn remember(&self, rec: Record) -> bool {
        (self.list[rec + 2] & 0x80) != 0
    }

    fn read_ptr(&self, ram: &Ram, addr: u16) -> Record {
        (ram.u32(addr) as i64 - self.pointer_base as i64 + LIST_BASE as i64) as Record
    }

    fn write_ptr(&self, ram: &mut Ram, addr: u16, v: Record) {
        ram.w32(
            addr,
            (v as i64 - LIST_BASE as i64 + self.pointer_base as i64) as u32,
        );
    }

    pub fn run(&mut self, ram: &mut Ram) {
        if ram.u16(vars::TWO_PLAYER_MODE) != 0 {
            // ObjectsManager 2P (0x1B8FC) is a later milestone; SMB3 1-1 is 1-player.
            return;
        }
        if ram.u8(vars::OBJ_PLACEMENT_ROUTINE) == 0 {
            self.init(ram);
        }
        self.main(ram);
    }

    /// `ObjectsManager_Init` (0x1B83C).
    fn init(&mut self, ram: &mut Ram) {
        ram.w8(
            vars::OBJ_PLACEMENT_ROUTINE,
            ram.u8(vars::OBJ_PLACEMENT_ROUTINE).wrapping_add(2),
        );
        for a in [
            vars::OBJ_LOAD_ADDR_RIGHT,
            vars::OBJ_LOAD_ADDR_LEFT,
            vars::OBJ_LOAD_ADDR_RIGHT_P2,
            vars::OBJ_LOAD_ADDR_LEFT_P2,
        ] {
            self.write_ptr(ram, a, LIST_BASE);
        }
        ram.w16(vars::OBJECT_RESPAWN_TABLE, 0x0101);
        for i in 2..0x180 {
            ram.w8(vars::OBJECT_RESPAWN_TABLE.wrapping_add(i), 0);
        }
        let mut d6 = (ram.u16(vars::CAMERA_X_POS) as i32 - 0x80).max(0);
        d6 &= 0xFF80;
        let mut a0 = self.read_ptr(ram, vars::OBJ_LOAD_ADDR_RIGHT);
        while d6 as u16 > self.x(a0) {
            if self.remember(a0) {
                ram.w8(
                    vars::OBJECT_RESPAWN_TABLE,
                    ram.u8(vars::OBJECT_RESPAWN_TABLE).wrapping_add(1),
                );
            }
            a0 += 6;
        }
        self.write_ptr(ram, vars::OBJ_LOAD_ADDR_RIGHT, a0);
        self.write_ptr(ram, vars::OBJ_LOAD_ADDR_RIGHT_P2, a0);
        a0 = self.read_ptr(ram, vars::OBJ_LOAD_ADDR_LEFT);
        d6 -= 0x80;
        if d6 >= 0 {
            while d6 as u16 > self.x(a0) {
                if self.remember(a0) {
                    ram.w8(
                        vars::OBJECT_RESPAWN_TABLE + 1,
                        ram.u8(vars::OBJECT_RESPAWN_TABLE + 1).wrapping_add(1),
                    );
                }
                a0 += 6;
            }
        }
        self.write_ptr(ram, vars::OBJ_LOAD_ADDR_LEFT, a0);
        self.write_ptr(ram, vars::OBJ_LOAD_ADDR_LEFT_P2, a0);
        ram.w16(vars::CAMERA_X_POS_LAST, 0xFFFF);
        ram.w16(vars::CAMERA_X_POS_LAST_P2, 0xFFFF);
    }

    /// `ObjectsManager_Main` (0x1B904).
    fn main(&mut self, ram: &mut Ram) {
        let cam = ram.u16(vars::CAMERA_X_POS);
        ram.w16(vars::CAMERA_X_POS_COARSE, cam.wrapping_sub(0x80) & 0xFF80);
        let d6 = cam & 0xFF80;
        let last = ram.u16(vars::CAMERA_X_POS_LAST) as i16 as i32;
        if d6 as i16 as i32 == last {
            return;
        }
        let mut d2 = 0i32;
        if d6 as i16 as i32 >= last {
            // Moving right (0x1B98A): load up to camera + $280, drop behind camera - $80.
            ram.w16(vars::CAMERA_X_POS_LAST, d6);
            let mut a0 = self.read_ptr(ram, vars::OBJ_LOAD_ADDR_RIGHT);
            let limit = d6 as i32 + 0x280;
            while limit > self.x(a0) as i32 {
                if self.remember(a0) {
                    d2 = ram.u8(vars::OBJECT_RESPAWN_TABLE) as i32;
                    ram.w8(
                        vars::OBJECT_RESPAWN_TABLE,
                        ram.u8(vars::OBJECT_RESPAWN_TABLE).wrapping_add(1),
                    );
                }
                if !self.chk_load_obj(ram, &mut a0, d2) {
                    break;
                }
            }
            self.write_ptr(ram, vars::OBJ_LOAD_ADDR_RIGHT, a0);
            a0 = self.read_ptr(ram, vars::OBJ_LOAD_ADDR_LEFT);
            let back = d6 as i32 + 0x280 - 0x300;
            if back >= 0 {
                while back > self.x(a0) as i32 {
                    if self.remember(a0) {
                        ram.w8(
                            vars::OBJECT_RESPAWN_TABLE + 1,
                            ram.u8(vars::OBJECT_RESPAWN_TABLE + 1).wrapping_add(1),
                        );
                    }
                    a0 += 6;
                }
            }
            self.write_ptr(ram, vars::OBJ_LOAD_ADDR_LEFT, a0);
            return;
        }

        // Moving left (0x1B92C): load down to camera - $80, drop beyond camera + $280.
        ram.w16(vars::CAMERA_X_POS_LAST, d6);
        let mut a0 = self.read_ptr(ram, vars::OBJ_LOAD_ADDR_LEFT);
        let limit = d6 as i32 - 0x80;
        if limit >= 0 {
            while limit < self.x(a0 - 6) as i16 as i32 {
                a0 -= 6;
                if self.remember(a0) {
                    let left = ram.u8(vars::OBJECT_RESPAWN_TABLE + 1).wrapping_sub(1);
                    ram.w8(vars::OBJECT_RESPAWN_TABLE + 1, left);
                    d2 = left as i32;
                }
                let mut at = a0;
                if !self.chk_load_obj(ram, &mut at, d2) {
                    if self.remember(a0) {
                        ram.w8(
                            vars::OBJECT_RESPAWN_TABLE + 1,
                            ram.u8(vars::OBJECT_RESPAWN_TABLE + 1).wrapping_add(1),
                        );
                    }
                    a0 += 6;
                    break;
                }
            }
        }
        self.write_ptr(ram, vars::OBJ_LOAD_ADDR_LEFT, a0);
        a0 = self.read_ptr(ram, vars::OBJ_LOAD_ADDR_RIGHT);
        let far = limit + 0x300;
        while far as i16 as i32 <= self.x(a0 - 6) as i16 as i32 {
            if self.remember(a0 - 6) {
                ram.w8(
                    vars::OBJECT_RESPAWN_TABLE,
                    ram.u8(vars::OBJECT_RESPAWN_TABLE).wrapping_sub(1),
                );
            }
            a0 -= 6;
        }
        self.write_ptr(ram, vars::OBJ_LOAD_ADDR_RIGHT, a0);
    }

    /// `ChkLoadObj` (0x1BCB6). Returns false only when no object slot is free (record not consumed).
    fn chk_load_obj(&mut self, ram: &mut Ram, a0: &mut Record, d2: i32) -> bool {
        if self.remember(*a0) {
            let entry = vars::OBJECT_RESPAWN_TABLE
                .wrapping_add(2)
                .wrapping_add(d2 as u16);
            if ram.test_set(entry, 7) {
                *a0 += 6;
                return true; // destroyed earlier: skip
            }
        }
        let Some(slot) = super::find_free_slot(ram) else {
            return false;
        };
        let y = ((self.list[*a0 + 2] as u16) << 8) | self.list[*a0 + 3] as u16;
        slot.set_x(ram, self.x(*a0));
        if (y & 0x8000) != 0 {
            slot.set_respawn_index(ram, d2 as u8);
        }
        slot.set_y(ram, y & 0xFFF);
        let flags = (y.rotate_left(3) & 3) as u8;
        slot.set_render_flags(ram, flags);
        slot.set_status(ram, flags);
        slot.set_id(ram, self.list[*a0 + 4]);
        slot.set_subtype(ram, self.list[*a0 + 5]);
        *a0 += 6;
        true
    }
}

fn write16(list: &mut [u8], at: usize, v: u16) {
    list[at] = (v >> 8) as u8;
    list[at + 1] = v as u8;
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::Obj;

    fn placement(x: i32, id: u8) -> ObjectSpawn {
        ObjectSpawn {
            x,
            y: 100,
            id,
            subtype: 0,
            flip_x: false,
            flip_y: false,
            remember: false,
        }
    }

    #[test]
    fn the_manager_spawns_up_to_camera_plus_280() {
        let mut ram = Ram::new();
        let mut manager = ObjectManager::new(
            vec![
                placement(100, 0xE0),
                placement(300, 0xE1),
                placement(700, 0xE2),
            ],
            0,
            0,
        );
        manager.run(&mut ram);
        assert_eq!(ram.u8(vars::FIRST_DYNAMIC_SLOT), 0xE0);
        assert_eq!(ram.u8(vars::FIRST_DYNAMIC_SLOT + 0x40), 0xE1);
        assert_eq!(ram.u8(vars::FIRST_DYNAMIC_SLOT + 0x80), 0, "700 > $280");

        // Moving the camera to $400 loads the third record (limit $680).
        ram.w16(vars::CAMERA_X_POS, 0x400);
        manager.run(&mut ram);
        assert_eq!(ram.u8(vars::FIRST_DYNAMIC_SLOT + 0x80), 0xE2);
    }

    #[test]
    fn a_remembered_object_that_was_destroyed_is_not_respawned() {
        let mut ram = Ram::new();
        let mut p = placement(100, 0xE0);
        p.remember = true;
        let mut manager = ObjectManager::new(vec![p], 0, 0);
        manager.run(&mut ram);
        let slot = Obj::new(vars::FIRST_DYNAMIC_SLOT);
        assert_eq!(slot.id(&ram), 0xE0);
        assert_eq!(slot.respawn_index(&ram), 1);
        // The next load pass uses respawn index 2, i.e. entry $FC04.
        ram.set_bit(vars::OBJECT_RESPAWN_TABLE + 4, 7);
        ram.w8(slot.address(), 0);
        ram.w16(vars::CAMERA_X_POS_LAST, 0xFFFF);
        ram.w16(vars::CAMERA_X_POS, 0x80);
        ram.w32(vars::OBJ_LOAD_ADDR_RIGHT, 6);
        manager.run(&mut ram);
        assert_eq!(
            ram.u8(slot.address()),
            0,
            "destroyed remember entry skipped"
        );
    }
}
