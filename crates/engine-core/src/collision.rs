//! Terrain collision (ROM 0x22416-0x22F98): `Find_Tile`, `FindFloor`/`FindWall` and the paired probes.
//!
//! Reads level data from [`Ram`] exactly where the original keeps it (layout `$8000`, chunks `$0000`, collision
//! indexes `$D600`/`$D900` via `Collision_addr`) and the angle/height/width tables from the extracted collision set.
//! Probes report the surface angle by writing a byte to `$F768` or `$F76A` (the original's a4).

use crate::{vars, Obj, Ram};

#[derive(Clone, Debug, Default)]
pub struct CollisionSet {
    pub name: String,
    pub angles: Vec<u8>,
    /// 256 collision IDs x 16 columns, flattened.
    pub heights: Vec<i8>,
    /// 256 collision IDs x 16 rows, flattened.
    pub widths: Vec<i8>,
}

#[derive(Clone, Debug, Default)]
pub struct CollisionSets {
    pub normal: CollisionSet,
    pub alternate: CollisionSet,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Pair {
    pub distance: i32,
    pub other: i32,
    pub angle: u8,
}

fn set<'a>(ram: &Ram, sets: &'a CollisionSets) -> &'a CollisionSet {
    if ram.u8(vars::HACK_ZONE_FLAG) != 0 {
        &sets.alternate
    } else {
        &sets.normal
    }
}

/// `Find_Tile` (0x22416): address of the block word under `(x, y)`, foreground plane.
pub fn find_tile(ram: &Ram, y: i32, x: i32) -> u16 {
    let mut d0 = (y.wrapping_mul(2)) & 0xF00;
    let mut d1 = ((x as u32 & 0xFFFF) >> 3) as i32;
    let d4 = d1;
    d1 = (d1 >> 4) & 0x7F;
    d0 += d1;
    let chunk = ram.u8(vars::LEVEL_LAYOUT.wrapping_add(d0 as u16)) as i32;
    let mut addr = chunk * 0x80;
    addr = (addr + (y & 0x70)) & 0xFFFF;
    addr = (addr + (d4 & 0xE)) & 0xFFFF;
    addr as u16
}

/// Shared body of the four finders. `None` means "no solid tile here".
fn probe(
    ram: &mut Ram,
    sets: &CollisionSets,
    y: i32,
    x: i32,
    solid_bit: u8,
    angle_addr: u16,
    vertical: bool,
) -> Option<(u16, i32)> {
    let word = ram.u16(find_tile(ram, y, x));
    let block = (word & 0x3FF) as i32;
    let bit = 1u32 << (solid_bit & 31);
    if block == 0 || (word as u32 & bit) == 0 {
        return None;
    }
    let col_addr = (ram.u32(vars::COLLISION_ADDR) & 0xFFFF) as u16;
    let id = ram.u8(col_addr.wrapping_add(block as u16));
    if id == 0 {
        return None;
    }
    let table = set(ram, sets);
    let mut angle = table.angles[id as usize] as i32;
    let mut d1: i32;
    let index: usize;
    if vertical {
        d1 = x;
        if (word & 0x400) != 0 {
            // `not.w` then use the low nibble.
            d1 = !d1;
            angle = -angle;
        }
        if (word & 0x800) != 0 {
            angle = -(angle + 0x40) - 0x40;
        }
        index = id as usize * 16 + (d1 & 0xF) as usize;
        ram.w8(angle_addr, angle as u8);
        Some((word, table.heights[index] as i32))
    } else {
        d1 = y;
        if (word & 0x800) != 0 {
            d1 = !d1;
            angle = -(angle + 0x40) - 0x40;
        }
        if (word & 0x400) != 0 {
            angle = -angle;
        }
        index = id as usize * 16 + (d1 & 0xF) as usize;
        ram.w8(angle_addr, angle as u8);
        Some((word, table.widths[index] as i32))
    }
}

/// `FindFloor` (0x22650). `angle_addr` is `$F768` for the primary probe, `$F76A` for the secondary.
// The eight arguments are the original's d2/d3/a3/d5/d6/a4 plus the RAM/set context; a struct would hide the port.
#[allow(clippy::too_many_arguments)]
pub fn find_floor(
    ram: &mut Ram,
    sets: &CollisionSets,
    y: i32,
    x: i32,
    step: i32,
    solid_bit: u8,
    flip: i32,
    angle_addr: u16,
) -> i32 {
    if let Some((word, mut d0)) = probe(ram, sets, y, x, solid_bit, angle_addr, true) {
        if ((word as i32 ^ flip) & 0x800) != 0 {
            d0 = -d0;
        }
        if d0 == 0 {
            return find_floor2(ram, sets, y + step, x, solid_bit, flip, angle_addr) + 0x10;
        }
        if d0 > 0 {
            if (d0 & 0xFF) == 0x10 {
                return find_floor2(ram, sets, y - step, x, solid_bit, flip, angle_addr) - 0x10;
            }
            return 0xF - (d0 + (y & 0xF));
        }
        d0 += y & 0xF;
        if d0 >= 0 {
            return find_floor2(ram, sets, y + step, x, solid_bit, flip, angle_addr) + 0x10;
        }
        return find_floor2(ram, sets, y - step, x, solid_bit, flip, angle_addr) - 0x10;
    }
    find_floor2(ram, sets, y + step, x, solid_bit, flip, angle_addr) + 0x10
}

/// `FindFloor2` (0x22710): the second-block probe; never recurses.
pub fn find_floor2(
    ram: &mut Ram,
    sets: &CollisionSets,
    y: i32,
    x: i32,
    solid_bit: u8,
    flip: i32,
    angle_addr: u16,
) -> i32 {
    let y = y as i16 as i32;
    if let Some((word, mut d0)) = probe(ram, sets, y, x, solid_bit, angle_addr, true) {
        if ((word as i32 ^ flip) & 0x800) != 0 {
            d0 = -d0;
        }
        if d0 == 0 {
            return 0xF - (y & 0xF);
        }
        if d0 > 0 {
            return 0xF - (d0 + (y & 0xF));
        }
        let d1 = y & 0xF;
        if d0 + d1 >= 0 {
            return 0xF - (y & 0xF);
        }
        return !d1;
    }
    0xF - (y & 0xF)
}

/// `RingCheckFloorDist` (0x227C0): single-block probe for rings.
#[allow(clippy::too_many_arguments)]
pub fn ring_find_floor(
    ram: &mut Ram,
    sets: &CollisionSets,
    y: i32,
    x: i32,
    step: i32,
    solid_bit: u8,
    flip: i32,
    angle_addr: u16,
) -> i32 {
    if let Some((word, mut d0)) = probe(ram, sets, y, x, solid_bit, angle_addr, true) {
        if ((word as i32 ^ flip) & 0x800) != 0 {
            d0 = -d0;
        }
        if d0 == 0 {
            return 0x10;
        }
        if d0 > 0 {
            if (d0 & 0xFF) == 0x10 {
                return find_floor2(ram, sets, y - step, x, solid_bit, flip, angle_addr) - 0x10;
            }
            return 0xF - (d0 + (y & 0xF));
        }
        if d0 + (y & 0xF) >= 0 {
            return 0x10;
        }
        return find_floor2(ram, sets, y - step, x, solid_bit, flip, angle_addr) - 0x10;
    }
    0x10
}

/// `FindWall` (0x22878): as `FindFloor` with axes swapped.
#[allow(clippy::too_many_arguments)]
pub fn find_wall(
    ram: &mut Ram,
    sets: &CollisionSets,
    y: i32,
    x: i32,
    step: i32,
    solid_bit: u8,
    flip: i32,
    angle_addr: u16,
) -> i32 {
    if let Some((word, mut d0)) = probe(ram, sets, y, x, solid_bit, angle_addr, false) {
        if ((word as i32 ^ flip) & 0x400) != 0 {
            d0 = -d0;
        }
        if d0 == 0 {
            return find_wall2(ram, sets, y, x + step, solid_bit, flip, angle_addr) + 0x10;
        }
        if d0 > 0 {
            if (d0 & 0xFF) == 0x10 {
                return find_wall2(ram, sets, y, x - step, solid_bit, flip, angle_addr) - 0x10;
            }
            return 0xF - (d0 + (x & 0xF));
        }
        d0 += x & 0xF;
        if d0 >= 0 {
            return find_wall2(ram, sets, y, x + step, solid_bit, flip, angle_addr) + 0x10;
        }
        return find_wall2(ram, sets, y, x - step, solid_bit, flip, angle_addr) - 0x10;
    }
    find_wall2(ram, sets, y, x + step, solid_bit, flip, angle_addr) + 0x10
}

/// `FindWall2` (0x22938).
pub fn find_wall2(
    ram: &mut Ram,
    sets: &CollisionSets,
    y: i32,
    x: i32,
    solid_bit: u8,
    flip: i32,
    angle_addr: u16,
) -> i32 {
    let x = x as i16 as i32;
    if let Some((word, mut d0)) = probe(ram, sets, y, x, solid_bit, angle_addr, false) {
        if ((word as i32 ^ flip) & 0x400) != 0 {
            d0 = -d0;
        }
        if d0 == 0 {
            return 0xF - (x & 0xF);
        }
        if d0 > 0 {
            return 0xF - (d0 + (x & 0xF));
        }
        let d1 = x & 0xF;
        if d0 + d1 >= 0 {
            return 0xF - (x & 0xF);
        }
        return !d1;
    }
    0xF - (x & 0xF)
}

/// Sets `Collision_addr` from the object's layer byte at `$3E` (`$0C` = primary/layer A).
pub fn select_layer(ram: &mut Ram, o: Obj) {
    let addr = if o.top_solid_bit(ram) == 0x0C {
        vars::PRIMARY_COLLISION
    } else {
        vars::SECONDARY_COLLISION
    };
    ram.w32(vars::COLLISION_ADDR, 0xFFFF_0000 | addr as u32);
}

fn nearer(ram: &Ram, first: i32, second: i32, cardinal: u8) -> Pair {
    let (mut d0, mut d1) = (first, second);
    let mut d3 = ram.u8(vars::SECONDARY_ANGLE);
    if d1 > d0 {
        d3 = ram.u8(vars::PRIMARY_ANGLE);
        std::mem::swap(&mut d0, &mut d1);
    }
    if (d3 & 1) != 0 {
        d3 = cardinal;
    }
    Pair {
        distance: d1,
        other: d0,
        angle: d3,
    }
}

fn single(ram: &Ram, cardinal: u8) -> u8 {
    let d3 = ram.u8(vars::PRIMARY_ANGLE);
    if (d3 & 1) != 0 {
        cardinal
    } else {
        d3
    }
}

/// `Sonic_CheckFloor` (0x22B46): two floor probes below the feet; sets `Collision_addr` and uses layer A.
pub fn check_floor(ram: &mut Ram, sets: &CollisionSets, o: Obj) -> Pair {
    select_layer(ram, o);
    let solid = o.top_solid_bit(ram);
    let y = o.y(ram) as i32 + o.y_radius(ram) as i32;
    let x = o.x(ram) as i32;
    let xr = o.x_radius(ram) as i32;
    let d1a = find_floor(ram, sets, y, x + xr, 0x10, solid, 0, vars::PRIMARY_ANGLE);
    let d1b = find_floor(ram, sets, y, x - xr, 0x10, solid, 0, vars::SECONDARY_ANGLE);
    nearer(ram, d1a, d1b, 0x00)
}

/// 0x22E26: two ceiling probes.
pub fn check_ceiling(ram: &mut Ram, sets: &CollisionSets, o: Obj, solid: u8) -> Pair {
    let y = (o.y(ram) as i32 - o.y_radius(ram) as i32) ^ 0xF;
    let x = o.x(ram) as i32;
    let xr = o.x_radius(ram) as i32;
    let d1a = find_floor(
        ram,
        sets,
        y,
        x + xr,
        -0x10,
        solid,
        0x800,
        vars::PRIMARY_ANGLE,
    );
    let d1b = find_floor(
        ram,
        sets,
        y,
        x - xr,
        -0x10,
        solid,
        0x800,
        vars::SECONDARY_ANGLE,
    );
    nearer(ram, d1a, d1b, 0x80)
}

/// 0x22D74: two right-wall probes.
pub fn check_right_wall_pair(ram: &mut Ram, sets: &CollisionSets, o: Obj, solid: u8) -> Pair {
    let x = o.x(ram) as i32 + o.y_radius(ram) as i32;
    let y = o.y(ram) as i32;
    let xr = o.x_radius(ram) as i32;
    let d1a = find_wall(ram, sets, y - xr, x, 0x10, solid, 0, vars::PRIMARY_ANGLE);
    let d1b = find_wall(ram, sets, y + xr, x, 0x10, solid, 0, vars::SECONDARY_ANGLE);
    nearer(ram, d1a, d1b, 0xC0)
}

/// 0x22EEE: two left-wall probes.
pub fn check_left_wall_pair(ram: &mut Ram, sets: &CollisionSets, o: Obj, solid: u8) -> Pair {
    let x = (o.x(ram) as i32 - o.y_radius(ram) as i32) ^ 0xF;
    let y = o.y(ram) as i32;
    let xr = o.x_radius(ram) as i32;
    let d1a = find_wall(
        ram,
        sets,
        y - xr,
        x,
        -0x10,
        solid,
        0x400,
        vars::PRIMARY_ANGLE,
    );
    let d1b = find_wall(
        ram,
        sets,
        y + xr,
        x,
        -0x10,
        solid,
        0x400,
        vars::SECONDARY_ANGLE,
    );
    nearer(ram, d1a, d1b, 0x40)
}

/// 0x22BDE: one floor probe 10 px below `(y, x)`.
pub fn floor_dist(ram: &mut Ram, sets: &CollisionSets, y: i32, x: i32, solid: u8) -> (i32, u8) {
    let d1 = find_floor(ram, sets, y + 0xA, x, 0x10, solid, 0, vars::PRIMARY_ANGLE);
    (d1, single(ram, 0x00))
}

/// 0x22E96: one ceiling probe 10 px above.
pub fn ceiling_dist(ram: &mut Ram, sets: &CollisionSets, y: i32, x: i32, solid: u8) -> (i32, u8) {
    let d1 = find_floor(
        ram,
        sets,
        (y - 0xA) ^ 0xF,
        x,
        -0x10,
        solid,
        0x800,
        vars::PRIMARY_ANGLE,
    );
    (d1, single(ram, 0x80))
}

/// `CheckRightWallDist` (0x22DD4).
pub fn right_wall_dist(
    ram: &mut Ram,
    sets: &CollisionSets,
    y: i32,
    x: i32,
    solid: u8,
) -> (i32, u8) {
    let d1 = find_wall(ram, sets, y, x + 0xA, 0x10, solid, 0, vars::PRIMARY_ANGLE);
    (d1, single(ram, 0xC0))
}

/// `CheckLeftWallDist` (0x22F56).
pub fn left_wall_dist(ram: &mut Ram, sets: &CollisionSets, y: i32, x: i32, solid: u8) -> (i32, u8) {
    let d1 = find_wall(
        ram,
        sets,
        y,
        (x - 0xA) ^ 0xF,
        -0x10,
        solid,
        0x400,
        vars::PRIMARY_ANGLE,
    );
    (d1, single(ram, 0x40))
}

/// `ChkFloorEdge` (0x22C4E): one floor probe under the feet at `x`.
pub fn floor_edge(ram: &mut Ram, sets: &CollisionSets, o: Obj, x: i32) -> (i32, u8) {
    select_layer(ram, o);
    ram.w8(vars::PRIMARY_ANGLE, 0);
    let y = o.y(ram) as i32 + o.y_radius(ram) as i32;
    let d1 = find_floor(
        ram,
        sets,
        y,
        x,
        0x10,
        o.top_solid_bit(ram),
        0,
        vars::PRIMARY_ANGLE,
    );
    let d3 = ram.u8(vars::PRIMARY_ANGLE);
    (d1, if (d3 & 1) != 0 { 0 } else { d3 })
}

/// `CalcRoomInFront` (0x22A7C): probe in the direction of travel after this frame's move.
pub fn calc_room_in_front(ram: &mut Ram, sets: &CollisionSets, o: Obj, d0: i32) -> (i32, u8) {
    select_layer(ram, o);
    let solid = o.lrb_solid_bit(ram);
    let x = ((o.x_long(ram) as i64 + ((o.x_vel(ram) as i64) << 8)) >> 16) as i32;
    let mut y = ((o.y_long(ram) as i64 + ((o.y_vel(ram) as i64) << 8)) >> 16) as i32;
    ram.w8(vars::PRIMARY_ANGLE, d0 as u8);
    ram.w8(vars::SECONDARY_ANGLE, d0 as u8);
    let quadrant = rounded_quadrant(d0);
    if quadrant == 0x00 {
        return floor_dist(ram, sets, y as i16 as i32, x as i16 as i32, solid);
    }
    if quadrant == 0x80 {
        return ceiling_dist(ram, sets, y as i16 as i32, x as i16 as i32, solid);
    }
    if (d0 & 0x38) == 0 {
        y += 8;
    }
    if quadrant == 0x40 {
        left_wall_dist(ram, sets, y as i16 as i32, x as i16 as i32, solid)
    } else {
        right_wall_dist(ram, sets, y as i16 as i32, x as i16 as i32, solid)
    }
}

/// `CalcRoomOverHead` (0x22B02).
pub fn calc_room_over_head(ram: &mut Ram, sets: &CollisionSets, o: Obj, d0: i32) -> i32 {
    select_layer(ram, o);
    let solid = o.lrb_solid_bit(ram);
    ram.w8(vars::PRIMARY_ANGLE, d0 as u8);
    ram.w8(vars::SECONDARY_ANGLE, d0 as u8);
    match (d0 + 0x20) & 0xC0 {
        0x40 => check_left_wall_pair(ram, sets, o, solid).distance,
        0x80 => check_ceiling(ram, sets, o, solid).distance,
        0xC0 => check_right_wall_pair(ram, sets, o, solid).distance,
        _ => check_floor(ram, sets, o).distance,
    }
}

/// Rev01 quadrant rounding; ties go toward the floor/ceiling.
pub fn rounded_quadrant(angle: i32) -> i32 {
    let a = angle as i8;
    let d0 = if (angle.wrapping_add(0x20) as i8) >= 0 {
        (if a < 0 { angle + 1 } else { angle }) + 0x1F
    } else {
        (if a < 0 { angle - 1 } else { angle }) + 0x20
    };
    d0 & 0xC0
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn quadrant_rounding() {
        assert_eq!(rounded_quadrant(0x00), 0x00);
        assert_eq!(rounded_quadrant(0x1F), 0x00);
        assert_eq!(rounded_quadrant(0x20), 0x00);
        assert_eq!(rounded_quadrant(0x3F), 0x40);
        assert_eq!(rounded_quadrant(0x40), 0x40);
        assert_eq!(rounded_quadrant(0x80), 0x80);
        assert_eq!(rounded_quadrant(0xC0), 0xC0);
        assert_eq!(rounded_quadrant(0xFF), 0x00);
    }

    #[test]
    fn find_tile_uses_the_layout_chunk() {
        let mut ram = Ram::new();
        // Row 0 (y 0..15), x 0..7: layout cell 0; chunk id 3.
        ram.w8(vars::LEVEL_LAYOUT, 3);
        let addr = find_tile(&ram, 0, 0);
        assert_eq!(addr, 3 * 0x80);
        // y 0x10 selects row 2 (0x20) and x 0x08 adds 0x00 (d4 & 0xE = 0).
        // The layout cell changes every 128 px vertically: y 0x10 is still row 0 (cell 0).
        ram.w8(vars::LEVEL_LAYOUT, 4);
        assert_eq!(find_tile(&ram, 0x10, 0x08), 4 * 0x80 + 0x10);
        // y 0x80 is row 1 (0x100).
        ram.w8(vars::LEVEL_LAYOUT + 0x100, 5);
        assert_eq!(find_tile(&ram, 0x80, 0), 5 * 0x80);
    }

    #[test]
    fn select_layer_writes_collision_addr() {
        let mut ram = Ram::new();
        let o = Obj::new(vars::MAIN_CHARACTER);
        o.set_top_solid_bit(&mut ram, 0x0C);
        select_layer(&mut ram, o);
        assert_eq!(ram.u32(vars::COLLISION_ADDR), 0xFFFF_D600);
        o.set_top_solid_bit(&mut ram, 0x0E);
        select_layer(&mut ram, o);
        assert_eq!(ram.u32(vars::COLLISION_ADDR), 0xFFFF_D900);
    }
}
