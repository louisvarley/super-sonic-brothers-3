//! End-of-act objects and the results screen: signpost, results, star post, giant ring (+flash), egg prison and
//! game over. Display/animation state is written where the original writes it; only RAM behaviour is needed here.

use crate::audio::sfx;
use crate::collision::CollisionSets;
use crate::objects::{
    delete_object, display_or_mark_gone, find_free_slot, move_and_fall, out_of_range,
    respawn_entry, sprites, touch,
};
use crate::player::Unported;
use crate::{vars, Obj, Ram};

const LEVEL_COORDS: u8 = 0x04;
const XFLIP: u8 = 0x01;

// ------------------------------------------------------------------ Signpost ($0D)

const SIGNPOST_SPARKLES: [(i8, i8); 8] = [
    (-0x18, -0x10),
    (8, 8),
    (-0x10, 0),
    (0x18, -8),
    (0, -8),
    (0x10, 0),
    (-0x18, 8),
    (0x18, 0x10),
];

pub fn signpost(ram: &mut Ram, o: Obj) {
    if o.routine(ram) == 0 {
        sfx::music(ram, 0x9A);
        if ram.u16(vars::TWO_PLAYER_MODE) != 0 {
            return;
        }
        if ram.u16(vars::LEVEL_HAS_SIGNPOST) == 0 {
            o.set_x(ram, 0);
            return;
        }
        o.set_mappings(ram, 0x1D354);
        o.set_art_tile(ram, 0x434);
        o.set_routine(ram, 2);
        o.set_render_flags(ram, LEVEL_COORDS);
        o.set_width_pixels(ram, 0x18);
        o.set_priority(ram, 4);
    } else {
        signpost_update(ram, o);
    }
    display_or_mark_gone(ram, o);
}

fn signpost_update(ram: &mut Ram, o: Obj) {
    ram.w16(vars::LOSER_TIME_LEFT, 0x3C3C);
    let timer_running = ram.u8(vars::UPDATE_HUD_TIMER) != 0;
    if timer_running {
        let skip = ram.u16(vars::CURRENT_ZONE_AND_ACT) == 0x1501 && !o.on_screen(ram);
        let sonic = Obj::new(vars::MAIN_CHARACTER);
        let past = sonic.x(ram).wrapping_sub(o.x(ram));
        if !skip && sonic.x(ram) >= o.x(ram) && past < 0x20 {
            ram.w8(vars::UPDATE_HUD_TIMER, 0);
            o.set_anim(ram, 0);
            o.set_prev_anim(ram, 1);
            o.set_word(ram, 0x30, 0);
            ram.w16(vars::CAMERA_MIN_X_POS, ram.u16(vars::CAMERA_MAX_X_POS));
            o.set_routine_secondary(ram, 2);
            if o.byte(ram, 0x36) == 0 {
                o.set_byte(
                    ram,
                    0x36,
                    if ram.u16(vars::PLAYER_MODE) == 2 {
                        4
                    } else {
                        3
                    },
                );
            }
        }
    }
    match o.routine_secondary(ram) {
        2 => signpost_spin(ram, o),
        4 => signpost_finish(ram, o),
        _ => {}
    }
}

fn signpost_spin(ram: &mut Ram, o: Obj) {
    let timer = o.word(ram, 0x30).wrapping_sub(1);
    o.set_word(ram, 0x30, timer);
    if (timer as i16) < 0 {
        o.set_word(ram, 0x30, 0x3C);
        o.set_anim(ram, o.anim(ram).wrapping_add(1));
        if o.anim(ram) == 3 {
            o.set_routine_secondary(ram, 4);
            o.set_anim(ram, o.byte(ram, 0x36));
        }
    }
    let sparkle = o.word(ram, 0x32).wrapping_sub(1);
    o.set_word(ram, 0x32, sparkle);
    if (sparkle as i16) >= 0 {
        return;
    }
    o.set_word(ram, 0x32, 0x0B);
    let index = o.byte(ram, 0x34);
    o.set_byte(ram, 0x34, (index + 2) & 0x0E);
    let (dx, dy) = SIGNPOST_SPARKLES[(index >> 1) as usize];
    if let Some(sparkle) = find_free_slot(ram) {
        sparkle.set_id(ram, 0x25);
        sparkle.set_routine(ram, 6);
        sparkle.set_x(ram, o.x(ram).wrapping_add(dx as u16));
        sparkle.set_y(ram, o.y(ram).wrapping_add(dy as u16));
        sparkle.set_mappings(ram, 0x152C4);
        sparkle.set_art_tile(ram, 0x26BC);
        sparkle.set_render_flags(ram, LEVEL_COORDS);
        sparkle.set_priority(ram, 2);
        sparkle.set_width_pixels(ram, 8);
    }
}

fn signpost_finish(ram: &mut Ram, o: Obj) {
    if ram.u16(vars::DEBUG_PLACEMENT_MODE) != 0 {
        return;
    }
    let sonic = Obj::new(vars::MAIN_CHARACTER);
    if !sonic.in_air(ram) {
        ram.w8(vars::CONTROL_LOCKED, 1);
        ram.w16(vars::CTRL1_HELD_LOGICAL, 0x0800);
    }
    if sonic.id(ram) != 0 && sonic.x(ram) < ram.u16(vars::CAMERA_MAX_X_POS).wrapping_add(0x128) {
        return;
    }
    o.set_routine_secondary(ram, 0);
    start_results(ram).ok();
}

/// `Signpost.StartResults` (0x1D1D4).
pub fn start_results(ram: &mut Ram) -> Result<(), Unported> {
    Obj::new(vars::MAIN_CHARACTER).set_status_secondary(ram, 0);
    ram.w8(vars::UPDATE_HUD_TIMER, 0);
    if ram.u8(vars::TIMED_MODE) != 0 {
        return Err(Unported::new(
            0x1D1E6,
            "end of act in the hack's timed mode",
        ));
    }
    if let Some(slot) = find_free_slot(ram) {
        slot.set_id(ram, 0x3A);
    }
    ram.w8(vars::UPDATE_BONUS_SCORE, 1);
    let seconds = ram.u8(vars::TIMER_MINUTE) as usize * 60 + ram.u8(vars::TIMER_SECOND) as usize;
    const TIME_BONUS: [u16; 21] = [
        5000, 5000, 1000, 500, 400, 400, 300, 300, 200, 200, 200, 200, 100, 100, 100, 100, 50, 50,
        50, 50, 0,
    ];
    ram.w16(vars::BONUS_COUNTDOWN1, TIME_BONUS[(seconds / 15).min(0x14)]);
    ram.w16(
        vars::BONUS_COUNTDOWN2,
        ram.u16(vars::RING_COUNT).wrapping_mul(10),
    );
    ram.w16(vars::TOTAL_BONUS_COUNTDOWN, 0);
    ram.w16(vars::BONUS_COUNTDOWN3, 0);
    if ram.u16(vars::PERFECT_RINGS_LEFT) == 0 {
        ram.w16(vars::BONUS_COUNTDOWN3, 5000);
    }
    Ok(())
}

// ------------------------------------------------------------------ Results ($3A)

const RESULTS_ID: u8 = 0x3A;
const RESULTS_NAME: u8 = 2;
const RESULTS_PIECE: u8 = 4;
const RESULTS_ACT_WORD: u8 = 6;
const RESULTS_ACT_NUMBER: u8 = 8;
const RESULTS_WAIT_BEFORE_TALLY: u8 = 0x0A;
const RESULTS_TALLYING: u8 = 0x0C;
const RESULTS_WAIT_AFTER_TALLY: u8 = 0x0E;
const RESULTS_NEXT_ACT: u8 = 0x10;
const RESULTS_CONTINUE_WAIT: u8 = 0x12;
const RESULTS_CONTINUE_BLINK: u8 = 0x14;
const RESULTS_PERFECT_LINE: u8 = 0x16;

const RESULTS_PIECES: [(i32, i32, u16, u8, u8); 8] = [
    (0x20, 0x120, 0xB8, RESULTS_NAME, 0),
    (0x200, 0x100, 0xCA, RESULTS_PIECE, 3),
    (0x240, 0x140, 0xCA, RESULTS_ACT_WORD, 4),
    (0x278, 0x178, 0xBE, RESULTS_ACT_NUMBER, 6),
    (0x350, 0x120, 0x120, RESULTS_PIECE, 9),
    (0x320, 0x120, 0xF0, RESULTS_PIECE, 10),
    (0x330, 0x120, 0x100, RESULTS_PIECE, 11),
    (0x340, 0x120, 0x110, RESULTS_PERFECT_LINE, 14),
];

pub fn results(ram: &mut Ram, o: Obj, level_order: &[u8]) -> Result<(), Unported> {
    match o.routine(ram) {
        0 => {
            create_result_pieces(ram, o);
            results_name(ram, o);
        }
        RESULTS_NAME => results_name(ram, o),
        RESULTS_PIECE => results_slide(ram, o),
        RESULTS_ACT_WORD => {
            if matches!(ram.u8(vars::CURRENT_ZONE_AND_ACT), 0x10 | 0x06 | 0x0E) {
                o.set_mapping_frame(ram, 5);
            }
            results_slide(ram, o);
        }
        RESULTS_ACT_NUMBER => {
            let zone = ram.u8(vars::CURRENT_ZONE_AND_ACT);
            if matches!(zone, 0x10 | 0x06 | 0x0E) {
                delete_object(ram, o);
                return Ok(());
            }
            let act2_only = matches!(zone, 0x05 | 0x12 | 0x14 | 0x16 | 0x18 | 0x1A | 0x1C);
            o.set_mapping_frame(
                ram,
                if act2_only {
                    8
                } else {
                    ram.u8(vars::CURRENT_ZONE_AND_ACT + 1).wrapping_add(6)
                },
            );
            results_slide(ram, o);
        }
        RESULTS_WAIT_BEFORE_TALLY | RESULTS_WAIT_AFTER_TALLY => {
            results_wait(ram, o);
        }
        RESULTS_TALLYING => results_tally(ram, o),
        RESULTS_NEXT_ACT => results_next_act(ram, level_order)?,
        RESULTS_PERFECT_LINE => {
            if ram.u16(vars::PERFECT_RINGS_LEFT) != 0 {
                delete_object(ram, o);
            } else {
                results_slide(ram, o);
            }
        }
        RESULTS_CONTINUE_WAIT | RESULTS_CONTINUE_BLINK => results_continue(ram, o),
        0x18 => {}
        _ => {
            return Err(Unported::new(
                0x17384,
                format!("results routine {:X}", o.routine(ram)),
            ))
        }
    }
    Ok(())
}

fn create_result_pieces(ram: &mut Ram, o: Obj) {
    let mut a = o.address();
    for (sx, tx, sy, routine, frame) in RESULTS_PIECES {
        loop {
            let id = ram.u8(a);
            if id == 0 || id == RESULTS_ID {
                break;
            }
            a = a.wrapping_add(0x40);
            if a >= vars::DYNAMIC_OBJECT_RAM_END {
                return;
            }
        }
        let piece = Obj::new(a);
        piece.set_id(ram, RESULTS_ID);
        piece.set_x(ram, sx as u16);
        piece.set_word(ram, 0x30, tx as u16);
        piece.set_word(ram, 0x0A, sy);
        piece.set_routine(ram, routine);
        piece.set_mapping_frame(ram, frame);
        piece.set_mappings(ram, 0x1825E);
        piece.set_render_flags(ram, 0);
        a = a.wrapping_add(0x40);
    }
}

fn results_name(ram: &mut Ram, o: Obj) {
    let tails_alone = ram.u16(vars::PLAYER_MODE) == 2;
    let overseas = ram.s8(vars::GRAPHICS_FLAGS) < 0;
    o.set_mapping_frame(
        ram,
        if tails_alone {
            if overseas {
                2
            } else {
                1
            }
        } else {
            0
        },
    );
    results_slide(ram, o);
    if o.x(ram) == o.word(ram, 0x30) {
        o.set_routine(ram, RESULTS_WAIT_BEFORE_TALLY);
        o.set_word(ram, 0x1E, 0xB4);
    }
}

fn results_slide(ram: &mut Ram, o: Obj) {
    let x = o.x(ram) as i32;
    let target = o.word(ram, 0x30) as i32;
    if x == target {
        sprites::display_sprite(ram, o);
        return;
    }
    let nx = x + if x > target { -0x10 } else { 0x10 };
    o.set_x(ram, nx as u16);
    if nx <= 0x200 {
        sprites::display_sprite(ram, o);
    }
}

fn results_wait(ram: &mut Ram, o: Obj) {
    let timer = o.word(ram, 0x1E).wrapping_sub(1);
    o.set_word(ram, 0x1E, timer);
    if timer == 0 {
        o.set_routine(ram, o.routine(ram).wrapping_add(2));
    }
    sprites::display_sprite(ram, o);
}

fn results_tally(ram: &mut Ram, o: Obj) {
    sprites::display_sprite(ram, o);
    ram.w8(vars::UPDATE_BONUS_SCORE, 1);
    let mut points = 0i32;
    for bonus in [
        vars::BONUS_COUNTDOWN1,
        vars::BONUS_COUNTDOWN2,
        vars::BONUS_COUNTDOWN3,
    ] {
        let v = ram.u16(bonus);
        if v == 0 {
            continue;
        }
        points += 10;
        ram.w16(bonus, v.wrapping_sub(10));
    }
    ram.w16(
        vars::TOTAL_BONUS_COUNTDOWN,
        ram.u16(vars::TOTAL_BONUS_COUNTDOWN)
            .wrapping_add(points as u16),
    );
    if points != 0 {
        touch::add_points(ram, points);
        return;
    }
    o.set_routine(ram, RESULTS_WAIT_AFTER_TALLY);
    o.set_word(ram, 0x1E, 0xB4);
    if ram.u16(vars::TOTAL_BONUS_COUNTDOWN) < 1000 {
        return;
    }
    o.set_word(ram, 0x1E, 0x12C);
    let mut slot = o.address().wrapping_add(0x40);
    while ram.u8(slot) != 0 {
        slot = slot.wrapping_add(0x40);
    }
    let icon = Obj::new(slot);
    icon.set_id(ram, RESULTS_ID);
    icon.set_routine(ram, RESULTS_CONTINUE_WAIT);
    icon.set_x(ram, 0x188);
    icon.set_word(ram, 0x0A, 0x118);
    icon.set_mappings(ram, 0x1825E);
    icon.set_render_flags(ram, 0);
    icon.set_word(ram, 0x1E, 0x3C);
    ram.w8(
        vars::CONTINUE_COUNT,
        ram.u8(vars::CONTINUE_COUNT).wrapping_add(1),
    );
}

fn results_continue(ram: &mut Ram, o: Obj) {
    if o.routine(ram) == RESULTS_CONTINUE_WAIT {
        let t = o.word(ram, 0x1E);
        if t != 0 {
            o.set_word(ram, 0x1E, t - 1);
            return;
        }
        o.set_routine(ram, RESULTS_CONTINUE_BLINK);
    }
    let t = o.word(ram, 0x1E).wrapping_sub(1);
    o.set_word(ram, 0x1E, t);
    if (t as i16) < 0 {
        o.set_word(ram, 0x1E, 0x13);
        o.set_anim_frame(ram, (o.anim_frame(ram) + 1) & 1);
    }
    o.set_mapping_frame(ram, 0x0C + o.anim_frame(ram));
    if ram.bit(vars::LEVEL_FRAME_COUNTER + 1, 4) {
        sprites::display_sprite(ram, o);
    }
}

fn results_next_act(ram: &mut Ram, order: &[u8]) -> Result<(), Unported> {
    let current = ram.u16(vars::CURRENT_ZONE_AND_ACT);
    if current == 0x1B01 {
        return Err(Unported::new(0x17624, "zone 1B act 2 results"));
    }
    let zone = (current >> 8) as u8;
    if crate::camera::is_easter_egg(zone) {
        ram.w16(vars::CURRENT_ZONE_AND_ACT, 0xFF00);
        ram.w8(vars::LAST_STAR_POLE_HIT, 0);
        ram.w8(vars::LAST_STAR_POLE_HIT_P2, 0);
        ram.w16(vars::LEVEL_INACTIVE_FLAG, 1);
        return Ok(());
    }
    let index = ((zone as usize) * 2 + (current as usize & 1)) * 2;
    if index + 1 >= order.len() {
        return Err(Unported::new(
            0x175B8,
            format!("level order after {current:04X}"),
        ));
    }
    let next = ((order[index] as u16) << 8) | order[index + 1] as u16;
    if (next as i16) < 0 {
        return Err(Unported::new(
            0x175B8,
            format!("level order after {current:04X}"),
        ));
    }
    ram.w16(vars::CURRENT_ZONE_AND_ACT, next);
    ram.w8(vars::LAST_STAR_POLE_HIT, 0);
    ram.w8(vars::LAST_STAR_POLE_HIT_P2, 0);
    ram.w16(vars::LEVEL_INACTIVE_FLAG, 1);
    Ok(())
}

// ------------------------------------------------------------------ Star post ($79)

pub fn starpost(ram: &mut Ram, o: Obj) {
    match o.routine(ram) {
        0 => {
            o.set_routine(ram, 2);
            o.set_mappings(ram, 0x23320);
            o.set_art_tile(ram, 0x47C);
            o.set_render_flags(ram, LEVEL_COORDS);
            o.set_width_pixels(ram, 8);
            o.set_priority(ram, 5);
            let entry = respawn_entry(o, ram);
            ram.clear_bit(entry, 7);
            let number = o.subtype(ram) & 0x7F;
            let last = ram.u8(vars::LAST_STAR_POLE_HIT) & 0x7F;
            if ram.bit(entry, 0) || last >= number {
                ram.w8(entry, 0);
                o.set_anim(ram, 2);
            }
            starpost_check(ram, o);
        }
        2 => starpost_check(ram, o),
        4 => {
            if o.anim(ram) == 0 {
                o.set_anim(ram, 2);
            }
            display_or_mark_gone(ram, o);
        }
        6 => starpost_ball(ram, o),
        _ => {
            delete_object(ram, o);
        }
    }
}

fn starpost_check(ram: &mut Ram, o: Obj) {
    if ram.u16(vars::DEBUG_PLACEMENT_MODE) == 0 {
        let sonic = Obj::new(vars::MAIN_CHARACTER);
        let last = ram.u8(vars::LAST_STAR_POLE_HIT);
        let number = o.subtype(ram) & 0x7F;
        if (last & 0x7F) >= number {
            if o.anim(ram) == 0 {
                o.set_anim(ram, 2);
            }
        } else if sonic.x(ram).wrapping_sub(o.x(ram)).wrapping_add(8) < 0x10
            && sonic.y(ram).wrapping_sub(o.y(ram)).wrapping_add(0x40) < 0x68
        {
            if let Some(ball) = find_free_slot(ram) {
                ball.set_id(ram, 0x79);
                ball.set_routine(ram, 6);
                ball.set_word(ram, 0x30, o.x(ram));
                ball.set_word(ram, 0x32, o.y(ram).wrapping_sub(0x14));
                ball.set_mappings(ram, 0x23320);
                ball.set_art_tile(ram, 0x47C);
                ball.set_render_flags(ram, LEVEL_COORDS);
                ball.set_width_pixels(ram, 8);
                ball.set_priority(ram, 4);
                ball.set_mapping_frame(ram, 2);
                ball.set_word(ram, 0x36, 0x20);
                ball.set_word(ram, 0x3E, o.address());
            }
            sfx::sonic(ram, sfx::ids::CHECKPOINT);
            o.set_anim(ram, 1);
            starpost_save(ram, o);
            ram.w8(respawn_entry(o, ram), 0);
        }
    }
    display_or_mark_gone(ram, o);
}

fn starpost_ball(ram: &mut Ram, o: Obj) {
    let timer = o.word(ram, 0x36).wrapping_sub(1);
    o.set_word(ram, 0x36, timer);
    if (timer as i16) < 0 {
        let post = Obj::new(o.word(ram, 0x3E));
        if post.id(ram) == 0x79 {
            post.set_anim(ram, 2);
            post.set_mapping_frame(ram, 0);
        }
        delete_object(ram, o);
        return;
    }
    let angle = o.angle(ram);
    o.set_angle(ram, angle.wrapping_sub(0x10));
    let (sin, cos) = crate::math::calc_sine(angle.wrapping_sub(0x40));
    o.set_x(
        ram,
        (((cos as i32 * 0xC00) >> 16) + o.word(ram, 0x30) as i32) as u16,
    );
    o.set_y(
        ram,
        (((sin as i32 * 0xC00) >> 16) + o.word(ram, 0x32) as i32) as u16,
    );
    display_or_mark_gone(ram, o);
}

fn starpost_save(ram: &mut Ram, o: Obj) {
    let sonic = Obj::new(vars::MAIN_CHARACTER);
    ram.w8(vars::LAST_STAR_POLE_HIT, o.subtype(ram));
    ram.w8(
        vars::SAVED_LAST_STAR_POLE_HIT,
        ram.u8(vars::LAST_STAR_POLE_HIT),
    );
    ram.w16(vars::SAVED_X_POS, o.x(ram));
    ram.w16(vars::SAVED_Y_POS, o.y(ram));
    ram.w16(vars::SAVED_ART_TILE, sonic.art_tile(ram));
    ram.w16(vars::SAVED_SOLID_BITS, sonic.word(ram, 0x3E));
    ram.w16(vars::SAVED_RING_COUNT, ram.u16(vars::RING_COUNT));
    ram.w8(vars::SAVED_EXTRA_LIFE_FLAGS, ram.u8(vars::EXTRA_LIFE_FLAGS));
    ram.w32(vars::SAVED_TIMER, ram.u32(vars::TIMER));
    ram.w8(
        vars::SAVED_DYNAMIC_RESIZE_ROUTINE,
        ram.u8(vars::DYNAMIC_RESIZE_ROUTINE),
    );
    ram.w16(
        vars::SAVED_CAMERA_MAX_Y_POS,
        ram.u16(vars::CAMERA_MAX_Y_POS),
    );
    for i in 0..8u16 {
        ram.w16(
            vars::SAVED_CAMERA_X_POS + i * 2,
            ram.u16(vars::CAMERA_X_POS + i * 4),
        );
    }
    ram.w16(vars::SAVED_WATER_LEVEL, ram.u16(vars::WATER_LEVEL2));
    ram.w8(vars::SAVED_WATER_ROUTINE, ram.u8(vars::WATER_ROUTINE));
    ram.w8(vars::SAVED_WATER_MOVE, ram.u8(vars::WATER_FULLSCREEN_FLAG));
}

/// `Starpost.LoadData` (0x2325A), used by a restart after a post.
pub fn starpost_load(ram: &mut Ram) {
    ram.w8(
        vars::LAST_STAR_POLE_HIT,
        ram.u8(vars::SAVED_LAST_STAR_POLE_HIT),
    );
    let sonic = Obj::new(vars::MAIN_CHARACTER);
    sonic.set_x(ram, ram.u16(vars::SAVED_X_POS));
    sonic.set_y(ram, ram.u16(vars::SAVED_Y_POS));
    ram.w16(vars::RING_COUNT, ram.u16(vars::SAVED_RING_COUNT));
    ram.w8(vars::EXTRA_LIFE_FLAGS, ram.u8(vars::SAVED_EXTRA_LIFE_FLAGS));
    ram.w16(vars::RING_COUNT, 0);
    ram.w8(vars::EXTRA_LIFE_FLAGS, 0);
    ram.w32(vars::TIMER, ram.u32(vars::SAVED_TIMER));
    ram.w8(vars::TIMER_FRAME, 0x3B);
    ram.w8(
        vars::TIMER_SECOND,
        ram.u8(vars::TIMER_SECOND).wrapping_sub(1),
    );
    sonic.set_art_tile(ram, ram.u16(vars::SAVED_ART_TILE));
    sonic.set_word(ram, 0x3E, ram.u16(vars::SAVED_SOLID_BITS));
    ram.w8(
        vars::DYNAMIC_RESIZE_ROUTINE,
        ram.u8(vars::SAVED_DYNAMIC_RESIZE_ROUTINE),
    );
    ram.w8(vars::WATER_ROUTINE, ram.u8(vars::SAVED_WATER_ROUTINE));
    ram.w16(
        vars::CAMERA_MAX_Y_POS,
        ram.u16(vars::SAVED_CAMERA_MAX_Y_POS),
    );
    ram.w16(
        vars::CAMERA_MAX_Y_POS_TARGET,
        ram.u16(vars::SAVED_CAMERA_MAX_Y_POS),
    );
    for i in 0..8u16 {
        ram.w16(
            vars::CAMERA_X_POS + i * 4,
            ram.u16(vars::SAVED_CAMERA_X_POS + i * 2),
        );
    }
    if ram.u8(vars::WATER_FLAG) != 0 {
        ram.w16(vars::WATER_LEVEL2, ram.u16(vars::SAVED_WATER_LEVEL));
        ram.w8(vars::WATER_ROUTINE, ram.u8(vars::SAVED_WATER_ROUTINE));
        ram.w8(vars::WATER_FULLSCREEN_FLAG, ram.u8(vars::SAVED_WATER_MOVE));
    }
    if (ram.u8(vars::LAST_STAR_POLE_HIT) as i8) < 0 {
        ram.w16(
            vars::CAMERA_MIN_X_POS,
            ram.u16(vars::SAVED_X_POS).wrapping_sub(0xA0),
        );
    }
}

// ------------------------------------------------------------------ Giant ring ($DD) and flash ($DE)

pub fn giant_ring(ram: &mut Ram, o: Obj) {
    match o.routine(ram) {
        0 => {
            if ram.u8(vars::TIMED_MODE) != 0 {
                o.clear(ram);
                return;
            }
            o.set_mappings(ram, 0x15328);
            let zone = ram.u8(vars::CURRENT_ZONE_AND_ACT);
            o.set_art_tile(
                ram,
                if matches!(zone, 0x19 | 0x0F) {
                    0x2200
                } else {
                    0x23D2
                },
            );
            o.set_render_flags(ram, o.render_flags(ram) | LEVEL_COORDS);
            o.set_width_pixels(ram, 0x40);
            if o.on_screen(ram) {
                if ram.u8(vars::SONIC1_EMERALD_COUNT) == 6 {
                    o.clear(ram);
                    return;
                }
                if ram.u16(vars::RING_COUNT) < 50 {
                    return;
                }
                o.set_routine(ram, 2);
                o.set_priority(ram, 2);
                o.set_collision_flags(ram, 0x52);
                ram.w16(vars::BIG_RING_ART_LEFT, 0xC40);
            }
            giant_ring_show(ram, o);
        }
        2 => giant_ring_show(ram, o),
        4 => {
            o.set_routine(ram, 2);
            if ram.u8(vars::UPDATE_HUD_TIMER) == 0 && o.word(ram, 0x3E) != vars::SIDEKICK {
                o.set_collision_flags(ram, 0);
                if let Some(flash) = find_free_slot(ram) {
                    flash.set_id(ram, 0xDE);
                    flash.set_x(ram, o.x(ram));
                    flash.set_y(ram, o.y(ram));
                    flash.set_long(ram, 0x3C, 0xFFFF_0000 | o.address() as u32);
                    if Obj::new(vars::MAIN_CHARACTER).x(ram) >= o.x(ram) {
                        flash.set_render_flags(ram, flash.render_flags(ram) | XFLIP);
                    }
                }
                o.clear(ram);
            } else {
                giant_ring_show(ram, o);
            }
        }
        _ => o.clear(ram),
    }
}

fn giant_ring_show(ram: &mut Ram, o: Obj) {
    o.set_mapping_frame(ram, ram.u8(vars::RINGS_ANIM_FRAME));
    if out_of_range(ram, o.x(ram)) {
        o.clear(ram);
        return;
    }
    sprites::display_sprite(ram, o);
}

pub fn giant_ring_flash(ram: &mut Ram, o: Obj) {
    match o.routine(ram) {
        0 => {
            o.set_routine(ram, 2);
            o.set_mappings(ram, 0x15428);
            let zone = ram.u8(vars::CURRENT_ZONE_AND_ACT);
            o.set_art_tile(
                ram,
                if matches!(zone, 0x19 | 0x0F) {
                    0x2200
                } else {
                    0x23D2
                },
            );
            ram.w16(vars::BIG_RING_ART_LEFT, 0);
            o.set_render_flags(ram, o.render_flags(ram) | LEVEL_COORDS);
            o.set_priority(ram, 0);
            o.set_width_pixels(ram, 0x20);
            o.set_mapping_frame(ram, 0xFF);
            giant_ring_flash_animate(ram, o);
        }
        2 => {
            if !giant_ring_flash_animate(ram, o) {
                return;
            }
            if out_of_range(ram, o.x(ram)) {
                o.clear(ram);
                return;
            }
            sprites::display_sprite(ram, o);
        }
        _ => o.clear(ram),
    }
}

fn giant_ring_flash_animate(ram: &mut Ram, o: Obj) -> bool {
    let d = o.anim_frame_duration(ram).wrapping_sub(1);
    o.set_anim_frame_duration(ram, d);
    if (d as i8) >= 0 {
        return true;
    }
    o.set_anim_frame_duration(ram, 1);
    o.set_mapping_frame(ram, o.mapping_frame(ram).wrapping_add(1));
    if o.mapping_frame(ram) >= 8 {
        o.set_routine(ram, o.routine(ram).wrapping_add(2));
        Obj::new(vars::MAIN_CHARACTER).set_word(ram, 0, 0);
        return false;
    }
    if o.mapping_frame(ram) == 3 {
        Obj::new(vars::MAIN_CHARACTER).set_anim(ram, 0x1C);
        ram.w8(vars::SPECIAL_STAGE_REQUESTED, 1);
        let sonic = Obj::new(vars::MAIN_CHARACTER);
        sonic.set_status_secondary(ram, sonic.status_secondary(ram) & !3);
    }
    true
}

// ------------------------------------------------------------------ Egg prison ($3E)

const EGG_PARTS: [(u16, u8, u8, u8, u8); 4] = [
    (0, 2, 0x20, 4, 0),
    (0x28, 4, 0x10, 5, 4),
    (0x18, 6, 8, 3, 5),
    (0, 8, 0x20, 4, 0),
];

pub fn egg_prison(ram: &mut Ram, sets: &CollisionSets, o: Obj) -> Result<(), Unported> {
    match o.routine(ram) {
        0 => create_egg_parts(ram, o),
        2 => run_egg_body(ram, sets, o)?,
        4 => run_egg_button(ram, sets, o)?,
        6 => {
            if o.routine_secondary(ram) != 0 {
                if !o.on_screen(ram) {
                    delete_object(ram, o);
                    return Ok(());
                }
                move_and_fall(ram, o);
            }
            display_or_mark_gone(ram, o);
        }
        8 => run_egg_emitter(ram, o),
        0x0A => run_egg_wait(ram, o)?,
        _ => {}
    }
    Ok(())
}

fn create_egg_parts(ram: &mut Ram, o: Obj) {
    let mut part = o;
    let mut slot_ref = o.address().wrapping_add(0x38);
    for (i, (dy, routine, width, priority, frame)) in EGG_PARTS.iter().enumerate() {
        if i > 0 {
            let Some(slot) = find_free_slot(ram) else {
                return;
            };
            ram.w16(slot_ref, slot.address());
            slot_ref = slot_ref.wrapping_add(2);
            part = slot;
        }
        part.set_id(ram, 0x3E);
        part.set_x(ram, o.x(ram));
        part.set_y(ram, o.y(ram).wrapping_sub(*dy));
        part.set_word(ram, 0x30, part.y(ram));
        part.set_mappings(ram, 0x43658);
        part.set_art_tile(ram, 0x2680);
        part.set_render_flags(ram, LEVEL_COORDS | 0x80);
        part.set_routine(ram, *routine);
        part.set_width_pixels(ram, *width);
        part.set_priority(ram, *priority);
        part.set_mapping_frame(ram, *frame);
    }
}

fn run_egg_body(ram: &mut Ram, sets: &CollisionSets, o: Obj) -> Result<(), Unported> {
    if o.routine_secondary(ram) == 0 {
        let button = Obj::new(o.word(ram, 0x38));
        if button.word(ram, 0x32) == 0 {
            return Ok(());
        }
        let lid = Obj::new(o.word(ram, 0x3A));
        if let Some(explosion) = find_free_slot(ram) {
            explosion.set_id(ram, 0x27);
            explosion.set_routine(ram, 2);
            explosion.set_x(ram, lid.x(ram));
            explosion.set_y(ram, lid.y(ram));
        }
        lid.set_y_vel(ram, -0x400);
        lid.set_x_vel(ram, 0x800);
        lid.set_routine_secondary(ram, lid.routine_secondary(ram).wrapping_add(2));
        o.set_word(ram, 0x34, 0x1D);
        o.set_routine_secondary(ram, 2);
        if ram.u16(vars::CURRENT_ZONE_AND_ACT) == 0x1800 {
            let x = ram.u16(vars::CAMERA_X_POS);
            for a in [
                vars::CAMERA_MIN_X_POS,
                vars::TAILS_MIN_X_POS,
                vars::CAMERA_MAX_X_POS,
                vars::TAILS_MAX_X_POS,
            ] {
                ram.w16(a, x);
            }
        }
    } else if o.routine_secondary(ram) == 2 {
        let timer = o.word(ram, 0x34).wrapping_sub(1);
        o.set_word(ram, 0x34, timer);
        if (timer as i16) < 0 {
            o.set_anim(ram, 1);
            let mut delay = 0x9Ai32;
            let mut dx = -0x1Ci32;
            for _ in 0..8 {
                let Some(animal) = find_free_slot(ram) else {
                    break;
                };
                spawn_prison_animal(ram, animal, o.x(ram) as i32 + dx, o.y(ram), delay);
                dx += 7;
                delay -= 8;
            }
            let emitter = Obj::new(o.word(ram, 0x3C));
            emitter.set_word(ram, 0x1E, 0xB4);
            emitter.set_routine_secondary(ram, emitter.routine_secondary(ram).wrapping_add(2));
            o.set_routine_secondary(ram, 4);
        }
    }
    crate::objects::solid::solid_object(ram, sets, o, 0x2B, 0x18, 0x18, o.x(ram) as i32)?;
    display_or_mark_gone(ram, o);
    Ok(())
}

fn run_egg_button(ram: &mut Ram, sets: &CollisionSets, o: Obj) -> Result<(), Unported> {
    crate::objects::solid::solid_object(ram, sets, o, 0x1B, 8, 8, o.x(ram) as i32)?;
    let base = o.word(ram, 0x30);
    o.set_y(ram, base);
    if o.status(ram) & 0x18 != 0 {
        o.set_y(ram, base.wrapping_add(8));
        ram.w8(vars::UPDATE_HUD_TIMER, 0);
        o.set_word(ram, 0x32, 1);
    }
    display_or_mark_gone(ram, o);
    Ok(())
}

fn spawn_prison_animal(ram: &mut Ram, a: Obj, x: i32, y: u16, delay: i32) {
    a.set_id(ram, 0x28);
    a.set_x(ram, x as u16);
    a.set_y(ram, y);
    a.set_byte(ram, 0x38, 1);
    a.set_word(ram, 0x36, delay as u16);
}

fn run_egg_emitter(ram: &mut Ram, o: Obj) {
    if o.routine_secondary(ram) == 0 {
        return;
    }
    if ram.u8(vars::VINT_RUNCOUNT_LOW) & 7 == 0 {
        if let Some(a) = find_free_slot(ram) {
            let r = crate::objects::boss::random_number(ram);
            let mut dx = (r & 0x1F) as i32 - 6;
            if (ram.u16(vars::RNG_SEED + 2) as i16) < 0 {
                dx = -dx;
            }
            spawn_prison_animal(ram, a, o.x(ram) as i32 + dx, o.y(ram), 0x0C);
        }
    }
    let timer = o.word(ram, 0x1E).wrapping_sub(1);
    o.set_word(ram, 0x1E, timer);
    if timer == 0 {
        o.set_routine(ram, 0x0A);
        o.set_word(ram, 0x1E, 0xB4);
    }
}

fn run_egg_wait(ram: &mut Ram, o: Obj) -> Result<(), Unported> {
    for slot in 0..0x70u16 {
        let a = vars::FIRST_DYNAMIC_SLOT + slot * 0x40;
        if ram.u8(a) == 0x28 {
            return Ok(());
        }
    }
    start_results(ram)?;
    delete_object(ram, o);
    Ok(())
}

// ------------------------------------------------------------------ Game over ($39)

pub fn game_over(ram: &mut Ram, o: Obj) {
    match o.routine(ram) {
        0 => {
            if ram.u32(vars::PLC_BUFFER) != 0 {
                return;
            }
            o.set_routine(ram, 2);
            o.set_x(
                ram,
                if o.mapping_frame(ram) & 1 == 0 {
                    0x50
                } else {
                    0x1F0
                },
            );
            o.set_word(ram, 0x0A, 0xF0);
            o.set_mappings(ram, 0x1820E);
            o.set_art_tile(ram, 0x84DE);
            o.set_render_flags(ram, 0);
            o.set_priority(ram, 0);
            game_over_slide(ram, o);
        }
        2 => game_over_slide(ram, o),
        _ => {
            if o.mapping_frame(ram) & 1 == 0 {
                game_over_wait(ram, o);
            } else {
                sprites::display_sprite(ram, o);
            }
        }
    }
}

fn game_over_slide(ram: &mut Ram, o: Obj) {
    if o.x(ram) == 0x120 {
        o.set_word(ram, 0x1E, 0x2D0);
        o.set_routine(ram, 4);
        return;
    }
    let x = o.x(ram) as i32
        + if (o.x(ram) as i32) < 0x120 {
            0x10
        } else {
            -0x10
        };
    o.set_x(ram, x as u16);
    sprites::display_sprite(ram, o);
}

fn game_over_wait(ram: &mut Ram, o: Obj) {
    let pressed = (ram.u8(vars::CTRL1_PRESS) | ram.u8(vars::CTRL2_PRESS)) & 0x70 != 0;
    let timer = o.word(ram, 0x1E);
    if !pressed && timer != 0 {
        o.set_word(ram, 0x1E, timer - 1);
        sprites::display_sprite(ram, o);
        return;
    }
    if ram.u8(vars::TIMED_MODE) != 0 {
        return;
    }
    if ram.u8(vars::TIME_OVER_FLAG) != 0 || ram.u8(vars::TIME_OVER_FLAG_P2) != 0 {
        ram.w32(vars::SAVED_TIMER, 0);
        ram.w16(vars::LEVEL_INACTIVE_FLAG, 1);
    } else if ram.u8(vars::CONTINUE_COUNT) != 0 {
        ram.w8(vars::GAME_MODE, 0x14);
    } else {
        ram.w8(vars::CURRENT_SPECIAL_ACT, 1);
        ram.w8(vars::GAME_MODE, 0);
    }
    sprites::display_sprite(ram, o);
}
