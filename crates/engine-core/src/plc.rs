//! The PLC queue: LoadPLC appends entries; RunPLC_RAM starts an entry; ProcessDPLC advances 3 tiles a frame.
//! Art is copied to VRAM immediately (the legacy port does the same) while the RAM counters stay frame-exact.

use crate::graphics::PlcList;
use crate::{vars, Ram};
use std::collections::HashMap;

pub struct PlcQueue {
    lists: Vec<HashMap<u32, usize>>,
}

impl PlcQueue {
    pub fn new(lists: &[PlcList]) -> Self {
        let lists = lists
            .iter()
            .map(|list| {
                list.entries
                    .iter()
                    .map(|e| (e.address, e.tiles))
                    .collect::<HashMap<_, _>>()
            })
            .collect();
        Self { lists }
    }

    pub fn load(&self, ram: &mut Ram, vram: &mut [u8], lists: &[PlcList], id: usize) {
        let Some(list) = lists.get(id) else {
            return;
        };
        let mut slot = vars::PLC_BUFFER;
        while ram.u32(slot) != 0 {
            slot = slot.wrapping_add(6);
        }
        for entry in &list.entries {
            if slot >= vars::PLC_BUFFER + 16 * 6 {
                break;
            }
            ram.w32(slot, entry.address);
            ram.w16(slot + 4, (entry.vram_tile * 32) as u16);
            let dst = entry.vram_tile * 32;
            let count = entry.art.len().min(vram.len().saturating_sub(dst));
            if count > 0 {
                vram[dst..dst + count].copy_from_slice(&entry.art[..count]);
            }
            slot = slot.wrapping_add(6);
        }
    }

    pub fn run(&self, ram: &mut Ram) {
        let address = ram.u32(vars::PLC_BUFFER);
        if address == 0 || ram.u16(0xF6F8) != 0 {
            return;
        }
        let mut tiles = 1;
        for list in &self.lists {
            if let Some(v) = list.get(&address) {
                tiles = *v;
                break;
            }
        }
        ram.w16(0xF6F8, (tiles & 0x7FFF) as u16);
    }

    pub fn process(&self, ram: &mut Ram) {
        if ram.u16(0xF6F8) == 0 {
            return;
        }
        ram.w16(0xF6FA, 3);
        let a = ram.u16(vars::PLC_BUFFER + 4).wrapping_add(3 * 32);
        ram.w16(vars::PLC_BUFFER + 4, a);
        loop {
            let left = ram.u16(0xF6F8).wrapping_sub(1);
            ram.w16(0xF6F8, left);
            if left == 0 {
                for i in 0..0x16 * 4 {
                    ram.w8(vars::PLC_BUFFER + i, ram.u8(vars::PLC_BUFFER + 6 + i));
                }
                return;
            }
            let batch = ram.u16(0xF6FA).wrapping_sub(1);
            ram.w16(0xF6FA, batch);
            if batch == 0 {
                return;
            }
        }
    }
}
