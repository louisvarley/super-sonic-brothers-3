//! A named window onto one 64-byte object slot in [`crate::Ram`].
//!
//! Positions are 16.16 fixed point stored as longs at `$08`/`$0C`; velocities and inertia are 8.8. Byte fields read
//! back as 0-255 and accept only the low 8 bits, like a `move.b`. The object's own variables are read with
//! [`Obj::byte`]/[`Obj::word`]/[`Obj::long`] at an offset (objects name those in their own modules).

use crate::ram::Ram;
use crate::vars;

pub const SLOT_SIZE: u16 = 0x40;

#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct Obj(pub u16);

macro_rules! byte_field {
    ($get:ident, $set:ident, $off:expr) => {
        pub fn $get(self, ram: &Ram) -> u8 {
            ram.u8(self.0.wrapping_add($off))
        }
        pub fn $set(self, ram: &mut Ram, v: u8) {
            ram.w8(self.0.wrapping_add($off), v);
        }
    };
}

macro_rules! signed_byte_field {
    ($get:ident, $set:ident, $off:expr) => {
        pub fn $get(self, ram: &Ram) -> i8 {
            ram.s8(self.0.wrapping_add($off))
        }
        pub fn $set(self, ram: &mut Ram, v: i8) {
            ram.w8(self.0.wrapping_add($off), v as u8);
        }
    };
}

macro_rules! word_field {
    ($get:ident, $set:ident, $off:expr) => {
        pub fn $get(self, ram: &Ram) -> u16 {
            ram.u16(self.0.wrapping_add($off))
        }
        pub fn $set(self, ram: &mut Ram, v: u16) {
            ram.w16(self.0.wrapping_add($off), v);
        }
    };
}

macro_rules! signed_word_field {
    ($get:ident, $set:ident, $off:expr) => {
        pub fn $get(self, ram: &Ram) -> i16 {
            ram.s16(self.0.wrapping_add($off))
        }
        pub fn $set(self, ram: &mut Ram, v: i16) {
            ram.w16(self.0.wrapping_add($off), v as u16);
        }
    };
}

macro_rules! long_field {
    ($get:ident, $set:ident, $off:expr) => {
        pub fn $get(self, ram: &Ram) -> u32 {
            ram.u32(self.0.wrapping_add($off))
        }
        pub fn $set(self, ram: &mut Ram, v: u32) {
            ram.w32(self.0.wrapping_add($off), v);
        }
    };
}

impl Obj {
    pub const fn new(address: u16) -> Self {
        Self(address)
    }

    pub const fn address(self) -> u16 {
        self.0
    }

    /// An object slot's number: `(address - $B000) / $40`, masked with `$7F` as the original does.
    pub fn slot_index(self) -> u8 {
        ((self.0.wrapping_sub(vars::MAIN_CHARACTER) >> 6) & 0x7F) as u8
    }

    pub fn from_slot(index: u8) -> Self {
        Self(vars::MAIN_CHARACTER.wrapping_add((index as u16) << 6))
    }

    // ---- Standard fields ----
    byte_field!(id, set_id, 0x00);
    byte_field!(render_flags, set_render_flags, 0x01);
    word_field!(art_tile, set_art_tile, 0x02);
    long_field!(mappings, set_mappings, 0x04);
    long_field!(x_long, set_x_long, 0x08);
    word_field!(x, set_x, 0x08);
    word_field!(x_sub, set_x_sub, 0x0A);
    long_field!(y_long, set_y_long, 0x0C);
    word_field!(y, set_y, 0x0C);
    word_field!(y_sub, set_y_sub, 0x0E);
    signed_word_field!(x_vel, set_x_vel, 0x10);
    signed_word_field!(y_vel, set_y_vel, 0x12);
    signed_word_field!(inertia, set_inertia, 0x14);
    signed_byte_field!(y_radius, set_y_radius, 0x16);
    signed_byte_field!(x_radius, set_x_radius, 0x17);
    byte_field!(priority, set_priority, 0x18);
    byte_field!(width_pixels, set_width_pixels, 0x19);
    byte_field!(mapping_frame, set_mapping_frame, 0x1A);
    byte_field!(anim_frame, set_anim_frame, 0x1B);
    byte_field!(anim, set_anim, 0x1C);
    byte_field!(prev_anim, set_prev_anim, 0x1D);
    byte_field!(anim_frame_duration, set_anim_frame_duration, 0x1E);
    byte_field!(collision_flags, set_collision_flags, 0x20);
    byte_field!(collision_property, set_collision_property, 0x21);
    byte_field!(status, set_status, 0x22);
    byte_field!(respawn_index, set_respawn_index, 0x23);
    byte_field!(routine, set_routine, 0x24);
    byte_field!(routine_secondary, set_routine_secondary, 0x25);
    byte_field!(angle, set_angle, 0x26);
    byte_field!(subtype, set_subtype, 0x28);

    // ---- Player fields ----
    byte_field!(flip_angle, set_flip_angle, 0x27);
    byte_field!(air_left, set_air_left, 0x28);
    byte_field!(flip_turned, set_flip_turned, 0x29);
    byte_field!(obj_control, set_obj_control, 0x2A);
    byte_field!(status_secondary, set_status_secondary, 0x2B);
    byte_field!(flips_remaining, set_flips_remaining, 0x2C);
    byte_field!(flip_speed, set_flip_speed, 0x2D);
    word_field!(move_lock, set_move_lock, 0x2E);
    word_field!(invulnerable_time, set_invulnerable_time, 0x30);
    word_field!(invincibility_time, set_invincibility_time, 0x32);
    word_field!(speed_shoes_time, set_speed_shoes_time, 0x34);
    byte_field!(next_tilt_copy, set_next_tilt_copy, 0x36);
    byte_field!(tilt_copy, set_tilt_copy, 0x37);
    byte_field!(stick_to_convex, set_stick_to_convex, 0x38);
    byte_field!(spindash_flag, set_spindash_flag, 0x39);
    word_field!(spindash_counter, set_spindash_counter, 0x3A);
    byte_field!(jumping, set_jumping, 0x3C);
    byte_field!(interact, set_interact, 0x3D);
    word_field!(solid_bits, set_solid_bits, 0x3E);
    byte_field!(top_solid_bit, set_top_solid_bit, 0x3E);
    byte_field!(lrb_solid_bit, set_lrb_solid_bit, 0x3F);

    /// The object the player stands on (slot number at `$3D`).
    pub fn interact_object(self, ram: &Ram) -> Obj {
        Self::from_slot(self.interact(ram))
    }

    /// Drawn last frame (render flag bit 7, set by BuildSprites).
    pub fn on_screen(self, ram: &Ram) -> bool {
        self.render_flags(ram) & 0x80 != 0
    }

    // ---- Player status bits ($22) ----
    fn status_bit(self, ram: &Ram, bit: u8) -> bool {
        ram.bit(self.0.wrapping_add(0x22), bit)
    }

    fn set_status_bit(self, ram: &mut Ram, bit: u8, on: bool) {
        let a = self.0.wrapping_add(0x22);
        if on {
            ram.set_bit(a, bit);
        } else {
            ram.clear_bit(a, bit);
        }
    }

    pub fn facing_left(self, ram: &Ram) -> bool {
        self.status_bit(ram, 0)
    }
    pub fn set_facing_left(self, ram: &mut Ram, v: bool) {
        self.set_status_bit(ram, 0, v);
    }
    pub fn in_air(self, ram: &Ram) -> bool {
        self.status_bit(ram, 1)
    }
    pub fn set_in_air(self, ram: &mut Ram, v: bool) {
        self.set_status_bit(ram, 1, v);
    }
    pub fn rolling(self, ram: &Ram) -> bool {
        self.status_bit(ram, 2)
    }
    pub fn set_rolling(self, ram: &mut Ram, v: bool) {
        self.set_status_bit(ram, 2, v);
    }
    pub fn on_object(self, ram: &Ram) -> bool {
        self.status_bit(ram, 3)
    }
    pub fn set_on_object(self, ram: &mut Ram, v: bool) {
        self.set_status_bit(ram, 3, v);
    }
    pub fn roll_jumping(self, ram: &Ram) -> bool {
        self.status_bit(ram, 4)
    }
    pub fn set_roll_jumping(self, ram: &mut Ram, v: bool) {
        self.set_status_bit(ram, 4, v);
    }
    pub fn pushing(self, ram: &Ram) -> bool {
        self.status_bit(ram, 5)
    }
    pub fn set_pushing(self, ram: &mut Ram, v: bool) {
        self.set_status_bit(ram, 5, v);
    }
    pub fn underwater(self, ram: &Ram) -> bool {
        self.status_bit(ram, 6)
    }
    pub fn set_underwater(self, ram: &mut Ram, v: bool) {
        self.set_status_bit(ram, 6, v);
    }

    // ---- Player secondary status bits ($2B) ----
    fn secondary_bit(self, ram: &Ram, bit: u8) -> bool {
        ram.bit(self.0.wrapping_add(0x2B), bit)
    }

    fn set_secondary_bit(self, ram: &mut Ram, bit: u8, on: bool) {
        let a = self.0.wrapping_add(0x2B);
        if on {
            ram.set_bit(a, bit);
        } else {
            ram.clear_bit(a, bit);
        }
    }

    pub fn has_shield(self, ram: &Ram) -> bool {
        self.secondary_bit(ram, 0)
    }
    pub fn set_has_shield(self, ram: &mut Ram, v: bool) {
        self.set_secondary_bit(ram, 0, v);
    }
    pub fn invincible(self, ram: &Ram) -> bool {
        self.secondary_bit(ram, 1)
    }
    pub fn set_invincible(self, ram: &mut Ram, v: bool) {
        self.set_secondary_bit(ram, 1, v);
    }
    pub fn has_speed_shoes(self, ram: &Ram) -> bool {
        self.secondary_bit(ram, 2)
    }
    pub fn set_has_speed_shoes(self, ram: &mut Ram, v: bool) {
        self.set_secondary_bit(ram, 2, v);
    }

    // ---- Object-specific variables ----
    pub fn byte(self, ram: &Ram, offset: u16) -> u8 {
        ram.u8(self.0.wrapping_add(offset))
    }
    pub fn set_byte(self, ram: &mut Ram, offset: u16, v: u8) {
        ram.w8(self.0.wrapping_add(offset), v);
    }
    pub fn word(self, ram: &Ram, offset: u16) -> u16 {
        ram.u16(self.0.wrapping_add(offset))
    }
    pub fn set_word(self, ram: &mut Ram, offset: u16, v: u16) {
        ram.w16(self.0.wrapping_add(offset), v);
    }
    pub fn long(self, ram: &Ram, offset: u16) -> u32 {
        ram.u32(self.0.wrapping_add(offset))
    }
    pub fn set_long(self, ram: &mut Ram, offset: u16, v: u32) {
        ram.w32(self.0.wrapping_add(offset), v);
    }

    /// `DeleteObject`: clears the whole 64-byte slot.
    pub fn clear(self, ram: &mut Ram) {
        for i in 0..SLOT_SIZE {
            ram.w8(self.0.wrapping_add(i), 0);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn standard_offsets_match_the_original_slot() {
        let mut ram = Ram::new();
        let o = Obj::new(vars::MAIN_CHARACTER);
        o.set_id(&mut ram, 1);
        o.set_x(&mut ram, 0x1234);
        o.set_y(&mut ram, 0x5678);
        o.set_x_vel(&mut ram, -0x200);
        o.set_inertia(&mut ram, 0x600);
        o.set_angle(&mut ram, 0x40);
        o.set_subtype(&mut ram, 7);
        assert_eq!(ram.u8(0xB000), 1);
        assert_eq!(ram.u16(0xB008), 0x1234);
        assert_eq!(ram.u16(0xB00C), 0x5678);
        assert_eq!(ram.u16(0xB010), 0xFE00);
        assert_eq!(ram.u16(0xB014), 0x600);
        assert_eq!(ram.u8(0xB026), 0x40);
        assert_eq!(ram.u8(0xB028), 7);
        assert_eq!(o.id(&ram), 1);
        assert_eq!(
            o.subtype(&ram),
            7,
            "subtype shares $28 with player air_left"
        );
    }

    #[test]
    fn byte_and_word_views_alias() {
        let mut ram = Ram::new();
        let o = Obj::new(0xB000);
        o.set_move_lock(&mut ram, 0x0900);
        assert_eq!(o.byte(&ram, 0x2E), 0x09);
        assert_eq!(o.byte(&ram, 0x2F), 0x00);
        // The C# convention: `move.w #$900,anim` sets two byte fields.
        o.set_byte(&mut ram, 0x2F, 0x12);
        assert_eq!(o.move_lock(&ram), 0x0912);
    }

    #[test]
    fn status_bits_and_secondary_bits() {
        let mut ram = Ram::new();
        let o = Obj::new(0xB000);
        assert!(!o.in_air(&ram));
        o.set_in_air(&mut ram, true);
        o.set_rolling(&mut ram, true);
        assert!(o.in_air(&ram));
        assert!(o.rolling(&ram));
        assert_eq!(ram.u8(0xB022), 0b0000_0110);
        o.set_invincible(&mut ram, true);
        o.set_has_shield(&mut ram, true);
        assert!(o.invincible(&ram));
        assert!(o.has_shield(&ram));
        assert_eq!(ram.u8(0xB02B), 0b0000_0011);
        o.set_invincible(&mut ram, false);
        assert!(!o.invincible(&ram));
    }

    #[test]
    fn slot_index_and_clear() {
        let mut ram = Ram::new();
        assert_eq!(Obj::new(vars::MAIN_CHARACTER).slot_index(), 0);
        assert_eq!(Obj::from_slot(2), Obj::new(vars::TITLE_CARD_ZONE_NAME));
        assert_eq!(Obj::from_slot(2).slot_index(), 2);
        let o = Obj::new(0xB400);
        o.set_id(&mut ram, 0x26);
        o.set_x(&mut ram, 100);
        o.clear(&mut ram);
        assert_eq!(o.id(&ram), 0);
        assert_eq!(o.x(&ram), 0);
        assert!(ram.bytes()[0xB400..0xB440].iter().all(|&b| b == 0));
    }
}
