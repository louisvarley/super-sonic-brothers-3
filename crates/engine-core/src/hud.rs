//! The HUD's VRAM digits (Hud_Base / HudUpdate) and the timer update.

use crate::graphics::HudData;
use crate::player::Unported;
use crate::{vars, Ram};

pub fn base(ram: &mut Ram, vram: &mut [u8], data: &HudData) {
    write_lives(ram, vram, data);
    write_tile_list(vram, data, 0xDC40, &data.base_tiles);
}

pub fn update(ram: &mut Ram, vram: &mut [u8], data: &HudData) -> Result<(), Unported> {
    if ram.u16(vars::TWO_PLAYER_MODE) != 0 {
        return Err(Unported::new(0x4551E, "2-player HUD"));
    }
    if ram.u16(vars::DEBUG_MODE_FLAG) != 0 {
        return Err(Unported::new(0x45468, "debug-mode HUD"));
    }
    if ram.u8(vars::UPDATE_HUD_SCORE) != 0 {
        ram.w8(vars::UPDATE_HUD_SCORE, 0);
        number(
            vram,
            data,
            0xDC80,
            ram.u32(vars::SCORE) as i64,
            &[100000, 10000, 1000, 100, 10, 1],
            true,
        );
    }
    if ram.u8(vars::UPDATE_HUD_RINGS) != 0 {
        if (ram.u8(vars::UPDATE_HUD_RINGS) as i8) < 0 {
            write_tile_list(vram, data, 0xDF40, &data.init_rings_tiles);
        }
        ram.w8(vars::UPDATE_HUD_RINGS, 0);
        number(
            vram,
            data,
            0xDF40,
            ram.u16(vars::RING_COUNT) as i64,
            &[100, 10, 1],
            true,
        );
    }
    if ram.u8(vars::UPDATE_HUD_TIMER) != 0 && ram.u16(vars::GAME_PAUSED) == 0 && advance_timer(ram)?
    {
        number(
            vram,
            data,
            0xDE40,
            ram.u8(vars::TIMER_MINUTE) as i64,
            &[1],
            false,
        );
        number(
            vram,
            data,
            0xDEC0,
            ram.u8(vars::TIMER_SECOND) as i64,
            &[10, 1],
            false,
        );
    }
    if ram.u8(vars::UPDATE_HUD_LIVES) != 0 {
        ram.w8(vars::UPDATE_HUD_LIVES, 0);
        write_lives(ram, vram, data);
    }
    if ram.u8(vars::UPDATE_BONUS_SCORE) != 0 {
        ram.w8(vars::UPDATE_BONUS_SCORE, 0);
        let mut vram_addr = 0xA400u16;
        for a in [
            vars::TOTAL_BONUS_COUNTDOWN,
            vars::BONUS_COUNTDOWN1,
            vars::BONUS_COUNTDOWN2,
            vars::BONUS_COUNTDOWN3,
        ] {
            bonus(vram, data, vram_addr, ram.u16(a));
            vram_addr = vram_addr.wrapping_add(0x100);
        }
    }
    Ok(())
}

fn advance_timer(ram: &mut Ram) -> Result<bool, Unported> {
    if ram.u8(vars::TIMED_MODE) == 0 {
        if ram.u32(vars::TIMER) == 0x0009_3B3B {
            ram.w8(vars::UPDATE_HUD_TIMER, 0);
            return Err(Unported::new(0x45452, "time over (KillCharacter)"));
        }
        let f = ram.u8(vars::TIMER_FRAME).wrapping_add(1);
        ram.w8(vars::TIMER_FRAME, f);
        if f < 60 {
            return Ok(false);
        }
        ram.w8(vars::TIMER_FRAME, 0);
        let s = ram.u8(vars::TIMER_SECOND).wrapping_add(1);
        ram.w8(vars::TIMER_SECOND, s);
        if s < 60 {
            return Ok(true);
        }
        ram.w8(vars::TIMER_SECOND, 0);
        let m = ram.u8(vars::TIMER_MINUTE).wrapping_add(1);
        ram.w8(vars::TIMER_MINUTE, if m >= 9 { 9 } else { m });
        return Ok(true);
    }
    if ram.u32(vars::TIMER) == 0x3B {
        ram.w8(vars::UPDATE_HUD_TIMER, 0);
        return Err(Unported::new(0x45452, "time over (KillCharacter)"));
    }
    let f = ram.u8(vars::TIMER_FRAME).wrapping_sub(1);
    ram.w8(vars::TIMER_FRAME, f);
    if (f as i8) >= 0 {
        return Ok(false);
    }
    ram.w8(vars::TIMER_FRAME, 0x3B);
    let s = ram.u8(vars::TIMER_SECOND).wrapping_sub(1);
    ram.w8(vars::TIMER_SECOND, s);
    if (s as i8) >= 0 {
        return Ok(true);
    }
    ram.w8(vars::TIMER_SECOND, 0x3B);
    let m = ram.u8(vars::TIMER_MINUTE).wrapping_sub(1);
    ram.w8(vars::TIMER_MINUTE, if (m as i8) < 0 { 0 } else { m });
    Ok(true)
}

fn number(
    vram: &mut [u8],
    data: &HudData,
    mut at: u16,
    mut value: i64,
    places: &[i64],
    suppress: bool,
) {
    let mut started = false;
    for &place in places {
        let digit = value / place;
        value %= place;
        if digit != 0 {
            started = true;
        }
        if started || !suppress {
            copy(vram, &data.digits, digit as usize * 2, 2, at);
        }
        at = at.wrapping_add(0x40);
    }
}

fn bonus(vram: &mut [u8], data: &HudData, mut at: u16, mut value: u16) {
    let mut started = false;
    for place in [1000, 100, 10, 1] {
        let digit = value / place;
        value %= place;
        if digit != 0 {
            started = true;
        }
        if started {
            copy(vram, &data.digits, digit as usize * 2, 2, at);
        } else {
            clear(vram, at, 64);
        }
        at = at.wrapping_add(0x40);
    }
}

fn write_lives(ram: &Ram, vram: &mut [u8], data: &HudData) {
    let mut at = 0xFBA0u16;
    let mut value = ram.u8(vars::LIFE_COUNT) as u16;
    let mut started = false;
    for (i, place) in [10u16, 1].iter().enumerate() {
        let digit = value / place;
        value %= place;
        if digit != 0 {
            started = true;
        }
        if started || i == 1 {
            copy(vram, &data.lives_digits, digit as usize, 1, at);
        } else {
            clear(vram, at, 32);
        }
        at = at.wrapping_add(0x40);
    }
}

fn write_tile_list(vram: &mut [u8], data: &HudData, mut at: u16, tiles: &[u8]) {
    for &t in tiles {
        if t >= 0x80 {
            clear(vram, at, 64);
        } else {
            copy(vram, &data.digits, t as usize, 2, at);
        }
        at = at.wrapping_add(64);
    }
}

fn copy(vram: &mut [u8], art: &[u8], tile: usize, count: usize, at: u16) {
    let src = tile * 32;
    let dst = at as usize;
    let n = (count * 32)
        .min(art.len().saturating_sub(src))
        .min(vram.len().saturating_sub(dst));
    if n > 0 {
        vram[dst..dst + n].copy_from_slice(&art[src..src + n]);
    }
}

fn clear(vram: &mut [u8], at: u16, count: usize) {
    let dst = at as usize;
    let n = count.min(vram.len().saturating_sub(dst));
    vram[dst..dst + n].fill(0);
}
