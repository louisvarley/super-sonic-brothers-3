//! The solid-object family (ROM `0x1D4AE-0x1DCEC`): objects a player can stand on, push against or hit from below.
//!
//! Register conventions follow the original: `d1` = half width, `d2` = half height (jumping), `d3` = half height
//! (walking), `d4` = the object's x before it moved this frame. `d6` is the player's status bit in the object
//! (3 for Sonic, 4 for Tails) and collects contact flags in its upper bits: bottom 18/19, top 20/21, side 16/17.
//! Returns `d4`: 0 none, 1 side, -1 top, -2 bottom.

use crate::collision::CollisionSets;
use crate::player::{self, Player};
use crate::{vars, Obj, Ram};

pub const SLOT_SIZE: u16 = 0x40;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct SolidResult {
    pub d4: i32,
    pub d6: i32,
}

fn bit(ram: &Ram, addr: u16, bit: i32) -> bool {
    ram.u8(addr) & (1u8 << ((bit & 7) as u8)) != 0
}

fn bset(ram: &mut Ram, addr: u16, bit: i32) {
    ram.w8(addr, ram.u8(addr) | (1u8 << ((bit & 7) as u8)));
}

fn bclr(ram: &mut Ram, addr: u16, bit: i32) {
    ram.w8(addr, ram.u8(addr) & !(1u8 << ((bit & 7) as u8)));
}

/// `SolidObject` (0x1D4AE): Sonic always, Tails only while both are on screen.
pub fn solid_object(
    ram: &mut Ram,
    sets: &CollisionSets,
    o: Obj,
    d1: i32,
    d2: i32,
    d3: i32,
    d4: i32,
) -> SolidResult {
    let mut d6 = 3;
    let r = per_player(
        ram,
        sets,
        o,
        Obj::new(vars::MAIN_CHARACTER),
        d1,
        d2,
        d3,
        d4,
        &mut d6,
        true,
    );
    if (ram.u8(vars::SIDEKICK + 1) as i8) >= 0 || (o.render_flags(ram) as i8) >= 0 {
        let _ = r;
        return SolidResult { d4, d6 };
    }
    d6 = (d6 & !0xFF) | ((d6 + 1) & 0xFF);
    let r = per_player(
        ram,
        sets,
        o,
        Obj::new(vars::SIDEKICK),
        d1,
        d2,
        d3,
        d4,
        &mut d6,
        true,
    );
    SolidResult { d4: r, d6 }
}

/// `SolidObject_Always` (0x1D516): no on-screen test.
pub fn solid_object_always(
    ram: &mut Ram,
    sets: &CollisionSets,
    o: Obj,
    d1: i32,
    d2: i32,
    d3: i32,
    d4: i32,
) -> SolidResult {
    let mut d6 = 3;
    per_player(
        ram,
        sets,
        o,
        Obj::new(vars::MAIN_CHARACTER),
        d1,
        d2,
        d3,
        d4,
        &mut d6,
        false,
    );
    d6 = (d6 & !0xFF) | ((d6 + 1) & 0xFF);
    per_player(
        ram,
        sets,
        o,
        Obj::new(vars::SIDEKICK),
        d1,
        d2,
        d3,
        d4,
        &mut d6,
        false,
    );
    SolidResult { d4: 0, d6 }
}

/// The per-player body of `SolidObject_Always` (0x1D52C).
#[allow(clippy::too_many_arguments)]
fn per_player(
    ram: &mut Ram,
    sets: &CollisionSets,
    o: Obj,
    p: Obj,
    d1: i32,
    d2: i32,
    d3: i32,
    d4: i32,
    d6: &mut i32,
    on_screen_test: bool,
) -> i32 {
    if !bit(ram, o.address().wrapping_add(0x22), *d6) {
        if on_screen_test && (o.render_flags(ram) as i8) >= 0 {
            return not_touching(ram, o, p, d6);
        }
        return solid_object_cont(ram, sets, o, p, d1, d2, d3, d6);
    }
    if !still_on(ram, o, p, d1, *d6) {
        return 0;
    }
    move_on_platform(ram, o, p, d4, d3);
    0
}

/// The standing check shared by every entry point (`StillOn`).
pub fn still_on(ram: &mut Ram, o: Obj, p: Obj, d1: i32, d6: i32) -> bool {
    let d2 = (d1 * 2) & 0xFFFF;
    if !p.in_air(ram) {
        let d0 = (p.x(ram) as i32 - o.x(ram) as i32 + d1) & 0xFFFF;
        if (d0 as i16) >= 0 && d0 < d2 {
            return true;
        }
    }
    bclr(ram, p.address().wrapping_add(0x22), 3);
    bset(ram, p.address().wrapping_add(0x22), 1);
    bclr(ram, o.address().wrapping_add(0x22), d6);
    false
}

/// `SolidObject_cont` (0x1D78E): one player against the object's box.
#[allow(clippy::too_many_arguments)] // the original's d1..d6 registers
fn solid_object_cont(
    ram: &mut Ram,
    sets: &CollisionSets,
    o: Obj,
    p: Obj,
    d1: i32,
    d2: i32,
    _d3: i32,
    d6: &mut i32,
) -> i32 {
    let _ = sets;
    let mut d0 = (p.x(ram) as i32 - o.x(ram) as i32 + d1) & 0xFFFF;
    if (d0 as i16) < 0 {
        return not_touching(ram, o, p, d6);
    }
    let d3 = (d1 * 2) & 0xFFFF;
    if d0 > d3 {
        return not_touching(ram, o, p, d6);
    }
    let d2 = (d2 + p.y_radius(ram) as i32) & 0xFFFF;
    let mut d3 = (p.y(ram) as i32 - o.y(ram) as i32 + 4 + d2) & 0xFFFF;
    if (d3 as i16) < 0 {
        return not_touching(ram, o, p, d6);
    }
    d3 &= 0x7FF;
    let d4 = (d2 * 2) & 0xFFFF;
    if d3 >= d4 {
        return not_touching(ram, o, p, d6);
    }
    d0 &= 0xFFFF;
    touching(ram, o, p, d0, d1, d2, d3, d4, d6)
}

/// 0x1D7CC: the player overlaps the box; resolve by the shallower axis.
#[allow(clippy::too_many_arguments)]
fn touching(
    ram: &mut Ram,
    o: Obj,
    p: Obj,
    d0: i32,
    d1: i32,
    d2: i32,
    d3: i32,
    d4: i32,
    d6: &mut i32,
) -> i32 {
    if (p.obj_control(ram) as i8) < 0 {
        return not_touching(ram, o, p, d6);
    }
    if p.routine(ram) >= 6 || ram.u16(vars::DEBUG_PLACEMENT_MODE) != 0 {
        return 0;
    }
    let mut d5 = d0;
    let mut d0 = d0;
    let mut d1v = d1;
    if d1v < d0 {
        d1v = (d1v * 2) & 0xFFFF;
        d0 = (d0 - d1v) & 0xFFFF;
        d5 = (-d0) & 0xFFFF;
    }
    d1v = d3;
    let mut d3v = d3;
    if d2 < d3v {
        d3v = (d3v - 4 - d4) & 0xFFFF;
        d1v = (-d3v) & 0xFFFF;
    }
    if d5 > d1v {
        return vertical(ram, o, p, d0, d1v, d3v, d6);
    }
    side(ram, o, p, d0, d1v, d6)
}

/// 0x1D808: side contact (or a shallow overlap of at most 4 px, which only clears pushing).
pub fn side(ram: &mut Ram, o: Obj, p: Obj, d0: i32, d1: i32, d6: &mut i32) -> i32 {
    if d1 <= 4 {
        clear_push(ram, o, p, *d6);
        *d6 |= 1 << ((*d6 + 0x0D) & 31);
        return 1;
    }
    let stop = (d0 as i16) != 0
        && if (d0 as i16) < 0 {
            p.x_vel(ram) < 0
        } else {
            p.x_vel(ram) >= 0
        };
    if stop {
        p.set_inertia(ram, 0);
        p.set_x_vel(ram, 0);
    }
    p.set_x(ram, (p.x(ram) as i32 - d0) as u16);
    if p.in_air(ram) {
        clear_push(ram, o, p, *d6);
        *d6 |= 1 << ((*d6 + 0x0D) & 31);
        return 1;
    }
    bset(ram, o.address().wrapping_add(0x22), *d6 + 2);
    bset(ram, p.address().wrapping_add(0x22), 5);
    *d6 |= 1 << ((*d6 + 0x0D) & 31);
    1
}

/// 0x1D88C: top (landing) or bottom contact.
fn vertical(ram: &mut Ram, o: Obj, p: Obj, d0: i32, d1: i32, d3: i32, d6: &mut i32) -> i32 {
    if (d3 as i16) >= 0 {
        if d3 < 0x10 || (o.id(ram) == 0x85 && d3 < 0x14) {
            return land(ram, o, p, d3, d6);
        }
        return not_touching(ram, o, p, d6);
    }
    if p.y_vel(ram) == 0 {
        if !p.in_air(ram) {
            let d4 = (d0 as i16).unsigned_abs() as i32;
            if d4 < 0x10 {
                return side(ram, o, p, d0, d1, d6);
            }
            // 0x1D8E0: crushed by a solid object (KillCharacter). The SMB3 1-1 objects never do this.
            return 0;
        }
    } else if p.y_vel(ram) < 0 && (d3 as i16) < 0 {
        p.set_y(ram, (p.y(ram) as i32 - d3) as u16);
        p.set_y_vel(ram, 0);
    }
    *d6 |= 1 << ((*d6 + 0x0F) & 31);
    -2
}

/// 0x1D8F4: land on top if within the object's width ($19) and not moving up.
fn land(ram: &mut Ram, o: Obj, p: Obj, d3: i32, d6: &mut i32) -> i32 {
    let d3 = (d3 - 4) & 0xFFFF;
    let d1w = o.width_pixels(ram) as i32;
    let d2 = d1w * 2;
    let d1 = (d1w + p.x(ram) as i32 - o.x(ram) as i32) & 0xFFFF;
    if (d1 as i16) < 0 || d1 >= d2 {
        return 0;
    }
    if p.y_vel(ram) < 0 {
        return 0;
    }
    p.set_y(ram, (p.y(ram) as i32 - d3 - 1) as u16);
    set_ride(ram, o, p, *d6);
    *d6 |= 1 << ((*d6 + 0x11) & 31);
    -1
}

/// 0x1D862: not touching; stop pushing if this player was.
fn not_touching(ram: &mut Ram, o: Obj, p: Obj, d6: &mut i32) -> i32 {
    if bit(ram, o.address().wrapping_add(0x22), *d6 + 2) {
        if p.anim(ram) != player::anim::ROLL {
            p.set_anim(ram, player::anim::WALK);
            p.set_prev_anim(ram, 1);
        }
        clear_push(ram, o, p, *d6);
    }
    0
}

fn clear_push(ram: &mut Ram, o: Obj, p: Obj, d6: i32) {
    bclr(ram, o.address().wrapping_add(0x22), d6 + 2);
    bclr(ram, p.address().wrapping_add(0x22), 5);
}

// ------------------------------------------------------------------ standing

/// `MvSonicOnPtfm` (0x1D930): keep a standing player on top (d3 above y) and carry it by the object's x delta.
pub fn move_on_platform(ram: &mut Ram, o: Obj, p: Obj, old_x: i32, d3: i32) {
    move_on_platform_to(ram, o, p, old_x, (o.y(ram) as i32 - d3) as u16);
}

/// `MvSonicOnPtfm2` (0x1D938): fixed height 9.
pub fn move_on_platform2(ram: &mut Ram, o: Obj, p: Obj, old_x: i32) {
    move_on_platform_to(ram, o, p, old_x, (o.y(ram) as i32 - 9) as u16);
}

fn move_on_platform_to(ram: &mut Ram, o: Obj, p: Obj, old_x: i32, top: u16) {
    if (p.obj_control(ram) as i8) < 0
        || p.routine(ram) >= 6
        || ram.u16(vars::DEBUG_PLACEMENT_MODE) != 0
    {
        return;
    }
    p.set_y(ram, (top as i32 - p.y_radius(ram) as i32) as u16);
    p.set_x(ram, (p.x(ram) as i32 - (old_x - o.x(ram) as i32)) as u16);
}

/// `RideObject_SetRide` (0x1DBB2): the player now stands on this object.
pub fn set_ride(ram: &mut Ram, o: Obj, p: Obj, d6: i32) {
    if p.on_object(ram) {
        let other = p.interact_object(ram);
        bclr(ram, other.address().wrapping_add(0x22), d6);
    }
    p.set_interact(ram, crate::objects::Objects::slot_index(o.address()) as u8);
    p.set_angle(ram, 0);
    p.set_y_vel(ram, 0);
    p.set_inertia(ram, p.x_vel(ram));
    if p.in_air(ram) {
        Player::reset_on_floor_part2(ram, p);
    }
    bset(ram, p.address().wrapping_add(0x22), 3);
    bclr(ram, p.address().wrapping_add(0x22), 1);
    bset(ram, o.address().wrapping_add(0x22), d6);
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::collision::CollisionSet;
    use crate::objects::Objects;

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

    fn block(ram: &mut Ram) -> Obj {
        let o = Obj::new(vars::FIRST_DYNAMIC_SLOT);
        o.set_id(ram, 0xE0);
        o.set_x(ram, 100);
        o.set_y(ram, 100);
        o.set_width_pixels(ram, 8);
        o.set_render_flags(ram, 0x84); // level coordinates, already on screen
        o
    }

    fn sonic(ram: &mut Ram, x: u16, y: u16) -> Obj {
        let p = Obj::new(vars::MAIN_CHARACTER);
        p.set_x(ram, x);
        p.set_y(ram, y);
        p.set_x_radius(ram, 7);
        p.set_y_radius(ram, 0x13);
        p.set_routine(ram, 2);
        p
    }

    #[test]
    fn a_side_overlap_uses_the_wrapped_16_bit_d0() {
        let sets = sets();
        let mut ram = Ram::new();
        let o = block(&mut ram);
        let p = sonic(&mut ram, 120, 100);
        p.set_x_vel(&mut ram, 0x100);
        let r = solid_object(&mut ram, &sets, o, 0x1B, 8, 9, 100);
        // $120 - $100 + $1B = $2F, so d0 (= -7 after the width subtraction) wraps and p.X becomes $7F.
        assert_eq!(p.x(&ram), 0x7F);
        assert_eq!(
            p.x_vel(&ram),
            0x100,
            "moving away from the overlap: not stopped"
        );
        assert!(p.pushing(&ram));
        assert_eq!(o.status(&ram) & 0x20, 0x20, "block records P1 pushing");
        assert_ne!(r.d6 & (1 << 16), 0);
    }

    #[test]
    fn landing_sets_the_ride_and_the_top_contact_bit() {
        let sets = sets();
        let mut ram = Ram::new();
        let o = block(&mut ram);
        let p = sonic(&mut ram, 100, 80);
        p.set_y_vel(&mut ram, 0x100);
        let r = solid_object(&mut ram, &sets, o, 0x1B, 8, 9, 100);
        assert_eq!(p.y(&ram), 72);
        assert!(p.on_object(&ram));
        assert_eq!(p.interact(&ram) as i32, Objects::slot_index(o.address()));
        assert_eq!(o.status(&ram) & 0x08, 0x08, "block records P1 standing");
        assert_ne!(r.d6 & (1 << 20), 0, "top contact bit");
    }

    #[test]
    fn a_bottom_hit_clears_y_velocity_and_sets_bit_18() {
        let sets = sets();
        let mut ram = Ram::new();
        let o = block(&mut ram);
        let p = sonic(&mut ram, 100, 120);
        p.set_y_vel(&mut ram, -0x100);
        let r = solid_object(&mut ram, &sets, o, 0x1B, 8, 9, 100);
        assert_eq!(p.y(&ram), 0x7F);
        assert_eq!(p.y_vel(&ram), 0);
        assert_ne!(r.d6 & (1 << 18), 0, "Sonic hit it from below");
    }
}
