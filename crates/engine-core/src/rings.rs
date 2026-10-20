//! `RingsManager` (ROM 0x1AD00) and the ring animation counters (`ChangeRingFrame`, 0x5EB0).
//!
//! The ring table at `$E800` holds 6-byte entries `{state, x, y}`, sorted by x and ended by `$FFFF`; a window
//! `[RingStartAddr, RingEndAddr)` slides with the camera. Numerical rings are not objects: the manager advances their
//! sparkles and `TouchResponse` collects them.

use crate::world::RingSpawn;
use crate::{vars, Ram};

pub const SPARKLING: u16 = 0x0604;
const GONE: u16 = 0xFFFF;
const WINDOW_WIDTH: i32 = 0x150;
const ENTRY_SIZE: u16 = 6;

pub struct RingsManager;

impl Default for RingsManager {
    fn default() -> Self {
        Self::new()
    }
}

impl RingsManager {
    pub fn new() -> Self {
        Self
    }

    /// The setup routine (0x1B03A): expand and sort the act's groups, then write the table.
    pub fn setup(&mut self, ram: &mut Ram, groups: &[RingSpawn]) {
        for i in 0..0x600 {
            ram.w8(vars::RING_POSITIONS.wrapping_add(i), 0);
        }
        for i in 0..0x40 {
            ram.w8(vars::RING_CONSUMPTION_TABLE.wrapping_add(i), 0);
        }

        let mut expanded = Vec::new();
        for g in groups {
            for i in 0..g.count as i32 {
                if g.vertical {
                    expanded.push((g.x as u16, (g.y + i * 24) as u16));
                } else {
                    expanded.push(((g.x + i * 24) as u16, g.y as u16));
                }
            }
        }

        // slots: [sentinel(0,0), rings..., terminator(x=$FFFF), zeros...]
        let mut slots = vec![(0u16, 0u16); 256.max(expanded.len() + 2)];
        for (i, ring) in expanded.iter().enumerate() {
            slots[i + 1] = *ring;
        }
        slots[expanded.len() + 1] = (0xFFFF, 0xFFFF);

        // The ROM's exchange sort (0x1B0B6); it is not stable, so equal-x rings keep this exact order.
        for i in 0..=254 {
            for j in (i + 1)..=255 {
                if slots[j].0 == 0 {
                    continue;
                }
                if slots[i].0 > slots[j].0 {
                    slots.swap(i, j);
                }
            }
        }

        let mut entry = vars::RING_POSITIONS_FIRST;
        for ring in slots.iter().skip(1).take(expanded.len()) {
            ram.w16(entry, 0);
            ram.w16(entry.wrapping_add(2), ring.0);
            ram.w16(entry.wrapping_add(4), ring.1);
            entry = entry.wrapping_add(ENTRY_SIZE);
        }
        ram.w32(entry, 0xFFFF_FFFF);
        ram.w16(
            vars::PERFECT_RINGS_LEFT,
            if expanded.is_empty() {
                0xFFFF
            } else {
                expanded.len() as u16
            },
        );
        ram.w16(vars::PERFECT_RINGS_FLAG, 0);
    }

    /// `RingsManager.Run` (0x1AD00).
    pub fn run(&mut self, ram: &mut Ram) {
        if ram.u8(vars::RINGS_MANAGER_ROUTINE) == 0 {
            self.init(ram);
        } else {
            self.update(ram);
        }
    }

    /// 0x1AD12: the first window, from the camera.
    fn init(&mut self, ram: &mut Ram) {
        ram.w8(
            vars::RINGS_MANAGER_ROUTINE,
            ram.u8(vars::RINGS_MANAGER_ROUTINE).wrapping_add(2),
        );
        let left = left_edge(ram);
        let mut entry = vars::RING_POSITIONS;
        while left > x(ram, entry) as i32 {
            entry = entry.wrapping_add(ENTRY_SIZE);
        }
        ram.w16(vars::RING_START_ADDR, entry);
        ram.w16(vars::RING_START_ADDR_P2, entry);
        let right = left + WINDOW_WIDTH;
        while right > x(ram, entry) as i32 {
            entry = entry.wrapping_add(ENTRY_SIZE);
        }
        ram.w16(vars::RING_END_ADDR, entry);
        ram.w16(vars::RING_END_ADDR_P2, entry);
    }

    /// 0x1AD56: sparkles, then slide both ends of the window.
    fn update(&mut self, ram: &mut Ram) {
        // The collected rings' sparkles: a frame every 6 frames, gone after frame 7.
        let count = ram.u16(vars::RING_CONSUMPTION_TABLE);
        let mut list = vars::RING_CONSUMPTION_LIST;
        for _ in 0..count {
            let mut entry;
            loop {
                entry = ram.u16(list);
                list = list.wrapping_add(2);
                if entry != 0 {
                    break;
                }
            }
            let timer = ram.u8(entry).wrapping_sub(1);
            ram.w8(entry, timer);
            if timer != 0 {
                continue;
            }
            ram.w8(entry, 6);
            ram.w8(
                entry.wrapping_add(1),
                ram.u8(entry.wrapping_add(1)).wrapping_add(1),
            );
            if ram.u8(entry.wrapping_add(1)) != 8 {
                continue;
            }
            ram.w16(entry, GONE);
            ram.w16(list.wrapping_sub(2), 0);
            let count = ram.u16(vars::RING_CONSUMPTION_TABLE);
            ram.w16(vars::RING_CONSUMPTION_TABLE, count.wrapping_sub(1));
        }

        let left = left_edge(ram);
        let mut start = ram.u16(vars::RING_START_ADDR);
        while left > x(ram, start) as i32 {
            start = start.wrapping_add(ENTRY_SIZE);
        }
        while left <= x(ram, start.wrapping_sub(ENTRY_SIZE)) as i32 {
            start = start.wrapping_sub(ENTRY_SIZE);
        }
        ram.w16(vars::RING_START_ADDR, start);
        let mut end = ram.u16(vars::RING_END_ADDR);
        let right = left + WINDOW_WIDTH;
        while right > x(ram, end) as i32 {
            end = end.wrapping_add(ENTRY_SIZE);
        }
        while right <= x(ram, end.wrapping_sub(ENTRY_SIZE)) as i32 {
            end = end.wrapping_sub(ENTRY_SIZE);
        }
        ram.w16(vars::RING_END_ADDR, end);
        ram.w16(vars::RING_START_ADDR_P2, start);
        ram.w16(vars::RING_END_ADDR_P2, end);
    }
}

fn x(ram: &Ram, entry: u16) -> u16 {
    ram.u16(entry.wrapping_add(2))
}

fn left_edge(ram: &Ram) -> i32 {
    let left = ram.u16(vars::CAMERA_X_POS) as i32 - 8;
    if left <= 0 {
        1
    } else {
        left
    }
}

/// `SyncAnimations.Step` (ChangeRingFrame, 0x5EB0): the shared animation counters.
pub fn sync_animations(ram: &mut Ram) {
    if count_down(ram, vars::LOGSPIKE_ANIM_COUNTER, 0x0B) {
        let f = ram.u8(vars::LOGSPIKE_ANIM_FRAME).wrapping_sub(1) & 7;
        ram.w8(vars::LOGSPIKE_ANIM_FRAME, f);
    }
    if count_down(ram, vars::RINGS_ANIM_COUNTER, 7) {
        let f = ram.u8(vars::RINGS_ANIM_FRAME).wrapping_add(1) & 3;
        ram.w8(vars::RINGS_ANIM_FRAME, f);
    }
    if count_down(ram, vars::UNKNOWN_ANIM_COUNTER, 7) {
        let frame = ram.u8(vars::UNKNOWN_ANIM_FRAME).wrapping_add(1);
        ram.w8(vars::UNKNOWN_ANIM_FRAME, if frame >= 6 { 0 } else { frame });
    }
    let t = ram.u8(vars::RING_SPILL_ANIM_COUNTER);
    if t != 0 {
        let acc = (t as u16).wrapping_add(ram.u16(vars::RING_SPILL_ANIM_ACCUM));
        ram.w16(vars::RING_SPILL_ANIM_ACCUM, acc);
        ram.w8(vars::RING_SPILL_ANIM_FRAME, (acc.rotate_left(7) & 3) as u8);
        ram.w8(vars::RING_SPILL_ANIM_COUNTER, t - 1);
    }
}

fn count_down(ram: &mut Ram, counter: u16, reload: u8) -> bool {
    let v = ram.u8(counter).wrapping_sub(1);
    if (v as i8) < 0 {
        ram.w8(counter, reload);
        true
    } else {
        ram.w8(counter, v);
        false
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn setup_expands_sorts_and_terminates_the_ring_table() {
        let mut ram = Ram::new();
        let mut rings = RingsManager::new();
        rings.setup(
            &mut ram,
            &[
                RingSpawn {
                    x: 100,
                    y: 50,
                    count: 2,
                    vertical: false,
                },
                RingSpawn {
                    x: 40,
                    y: 20,
                    count: 1,
                    vertical: true,
                },
            ],
        );
        // Sorted by x: 40, 100, 124; the sentinel at $E800 stays zero.
        assert_eq!(ram.u16(vars::RING_POSITIONS), 0);
        assert_eq!(ram.u16(vars::RING_POSITIONS_FIRST + 2), 40);
        assert_eq!(ram.u16(vars::RING_POSITIONS_FIRST + 6 + 2), 100);
        assert_eq!(ram.u16(vars::RING_POSITIONS_FIRST + 12 + 2), 124);
        let terminator = vars::RING_POSITIONS_FIRST + 18;
        assert_eq!(ram.u16(terminator), 0xFFFF);
        assert_eq!(ram.u16(vars::PERFECT_RINGS_LEFT), 3);
    }

    #[test]
    fn the_window_starts_at_the_first_ring_at_or_after_the_camera() {
        let mut ram = Ram::new();
        let mut rings = RingsManager::new();
        rings.setup(
            &mut ram,
            &[RingSpawn {
                x: 100,
                y: 50,
                count: 1,
                vertical: false,
            }],
        );
        ram.w16(vars::CAMERA_X_POS, 0);
        rings.run(&mut ram);
        assert_eq!(ram.u8(vars::RINGS_MANAGER_ROUTINE), 2);
        assert_eq!(ram.u16(vars::RING_START_ADDR), vars::RING_POSITIONS_FIRST);
        assert_eq!(
            ram.u16(vars::RING_START_ADDR_P2),
            vars::RING_POSITIONS_FIRST
        );
    }
}
