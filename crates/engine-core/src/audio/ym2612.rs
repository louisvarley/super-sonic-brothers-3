//! YM2612 FM synthesis, a direct port of `legacy/src/SonicBros3/Audio/Ym2612.cs`.
//!
//! Phase and envelope generators follow the hardware as documented by Nemesis and modelled by MAME's fm2612.c:
//! 20-bit phase accumulators with the detune table, the 3-sample envelope clock with its rate/shift/increment
//! tables, and log-sine + exponent tables for the operator output. The LFO is not emulated (the driver never enables
//! it: register $22 is not written), nor is SSG-EG or channel 3's special mode ($27 is only ever written as 0).
//! The DAC (registers $2A/$2B) replaces channel 6's output.

pub const CLOCK_NTSC: f64 = 7_670_453.0;

pub fn sample_rate() -> f64 {
    CLOCK_NTSC / 144.0
}

const SIN_BITS: usize = 10;
const SIN_LEN: usize = 1 << SIN_BITS;
const SIN_MASK: usize = SIN_LEN - 1;
const TL_RES_LEN: usize = 256;
const TL_TAB_LEN: usize = 13 * 2 * TL_RES_LEN;
const ENV_QUIET: usize = TL_TAB_LEN >> 3;
const MAX_ATT: i32 = 1023;

#[derive(Clone, Copy, PartialEq, Eq)]
enum EgPhase {
    Off,
    Release,
    Sustain,
    Decay,
    Attack,
}

#[derive(Clone, Copy)]
struct Operator {
    dt: i32,
    mul: i32,
    tl: i32,
    ks: i32,
    ar: i32,
    d1r: i32,
    d2r: i32,
    sl: i32,
    rr: i32,
    key_on: bool,
    phase: u32,
    volume: i32,
    state: EgPhase,
    ksr: i32,
    increment: i32,
    ar_rate: i32,
    d1_rate: i32,
    d2_rate: i32,
    rr_rate: i32,
}

impl Default for Operator {
    fn default() -> Self {
        Self {
            dt: 0,
            mul: 1,
            tl: 0,
            ks: 0,
            ar: 0,
            d1r: 0,
            d2r: 0,
            sl: 0,
            rr: 0,
            key_on: false,
            phase: 0,
            volume: MAX_ATT,
            state: EgPhase::Off,
            ksr: 0,
            increment: 0,
            ar_rate: 0,
            d1_rate: 0,
            d2_rate: 0,
            rr_rate: 0,
        }
    }
}

#[derive(Clone, Copy)]
struct Channel {
    /// Register order: op1, op3, op2, op4.
    ops: [Operator; 4],
    fnum: i32,
    block: i32,
    algorithm: i32,
    feedback: i32,
    left: bool,
    right: bool,
    op1_out0: i32,
    op1_out1: i32,
    key_code: i32,
}

impl Default for Channel {
    fn default() -> Self {
        Self {
            ops: [Operator::default(); 4],
            fnum: 0,
            block: 0,
            algorithm: 0,
            feedback: 0,
            left: true,
            right: true,
            op1_out0: 0,
            op1_out1: 0,
            key_code: 0,
        }
    }
}

const DETUNE_TABLE: [i32; 128] = [
    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 3, 3, 3, 4, 4, 4, 5, 5, 6, 6, 7, 8, 8, 8, 8,
    1, 1, 1, 1, 2, 2, 2, 2, 2, 3, 3, 3, 4, 4, 4, 5, 5, 6, 6, 7, 8, 8, 9, 10, 11, 12, 13, 14, 16,
    16, 16, 16, 2, 2, 2, 2, 2, 3, 3, 3, 4, 4, 4, 5, 5, 6, 6, 7, 8, 8, 9, 10, 11, 12, 13, 14, 16,
    17, 19, 20, 22, 22, 22, 22,
];

const EG_INC: [i32; 152] = [
    0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 1, 1, 0, 1, 0, 1, 1, 1, 0, 1, 1, 1, 0, 1, 1, 1, 1, 1, 1, 1,
    1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2, 1, 1, 1, 2, 1, 2, 1, 2, 1, 2, 1, 2, 1, 2, 2, 2, 1, 2, 2, 2,
    2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 4, 2, 2, 2, 4, 2, 4, 2, 4, 2, 4, 2, 4, 2, 4, 4, 4, 2, 4, 4, 4,
    4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 8, 4, 4, 4, 8, 4, 8, 4, 8, 4, 8, 4, 8, 4, 8, 8, 8, 4, 8, 8, 8,
    8, 8, 8, 8, 8, 8, 8, 8, 16, 16, 16, 16, 16, 16, 16, 16, 0, 0, 0, 0, 0, 0, 0, 0,
];

pub struct Ym2612 {
    ch: [Channel; 6],
    fnum_latch: [i32; 6],
    eg_counter: i32,
    eg_timer: i32,
    dac: i32,
    dac_enabled: bool,
    tl_tab: [i32; TL_TAB_LEN],
    sin_tab: [i32; SIN_LEN],
    rate_select: [i32; 128],
    rate_shift: [i32; 128],
}

impl Default for Ym2612 {
    fn default() -> Self {
        Self::new()
    }
}

impl Ym2612 {
    pub fn new() -> Self {
        let mut tl_tab = [0; TL_TAB_LEN];
        for x in 0..TL_RES_LEN {
            let m = ((1u32 << 16) as f64 / 2f64.powf((x + 1) as f64 * (1.0 / 32.0) / 8.0)).floor();
            let mut n = (m as i32) >> 4;
            n = (n >> 1) + (n & 1);
            n <<= 2;
            for i in 0..13 {
                tl_tab[x * 2 + i * 2 * TL_RES_LEN] = n >> i;
                tl_tab[x * 2 + 1 + i * 2 * TL_RES_LEN] = -(n >> i);
            }
        }

        let mut sin_tab = [0; SIN_LEN];
        for (i, slot) in sin_tab.iter_mut().enumerate() {
            let m = ((i * 2 + 1) as f64 * std::f64::consts::PI / SIN_LEN as f64).sin();
            let mut o = 8.0 * (1.0 / m.abs()).ln() / std::f64::consts::LN_2;
            o /= 1.0 / 32.0;
            let mut n = (2.0 * o) as i32;
            n = (n >> 1) + (n & 1);
            *slot = n * 2 + if m >= 0.0 { 0 } else { 1 };
        }

        let mut rate_select = [0; 128];
        let mut rate_shift = [0; 128];
        for r in 0..128 {
            let rate = r as i32 - 32;
            let (row, shift) = if rate < 0 {
                (18, 0)
            } else if rate < 48 {
                let whole = rate >> 2;
                let frac = rate & 3;
                let row = if whole == 0 {
                    if frac < 2 {
                        18
                    } else {
                        0
                    }
                } else if whole == 1 {
                    if frac < 2 {
                        0
                    } else {
                        2
                    }
                } else {
                    frac
                };
                (row, 11 - whole)
            } else if rate < 60 {
                (4 + (rate - 48), 0)
            } else {
                (16, 0)
            };
            rate_select[r] = row * 8;
            rate_shift[r] = shift;
        }

        Self {
            ch: [Channel::default(); 6],
            fnum_latch: [0; 6],
            eg_counter: 0,
            eg_timer: 0,
            dac: 0x80,
            dac_enabled: false,
            tl_tab,
            sin_tab,
            rate_select,
            rate_shift,
        }
    }

    pub fn dac(&self) -> i32 {
        self.dac
    }

    pub fn set_dac(&mut self, value: i32) {
        self.dac = value & 0xFF;
    }

    pub fn write(&mut self, part: i32, reg: i32, value: i32) {
        let value = value & 0xFF;
        if part == 0 && reg < 0x30 {
            match reg {
                0x28 => {
                    let mut c = value & 3;
                    if c == 3 {
                        return;
                    }
                    if (value & 4) != 0 {
                        c += 3;
                    }
                    let ch = &mut self.ch[c as usize];
                    Self::set_key(&mut ch.ops[0], (value & 0x10) != 0);
                    Self::set_key(&mut ch.ops[2], (value & 0x20) != 0);
                    Self::set_key(&mut ch.ops[1], (value & 0x40) != 0);
                    Self::set_key(&mut ch.ops[3], (value & 0x80) != 0);
                }
                0x2A => self.dac = value,
                0x2B => self.dac_enabled = (value & 0x80) != 0,
                _ => {}
            }
            return;
        }
        let mut ch_index = reg & 3;
        if ch_index == 3 {
            return;
        }
        ch_index += part * 3;
        let chan = &mut self.ch[ch_index as usize];
        if reg < 0xA0 {
            let op = &mut chan.ops[((reg >> 2) & 3) as usize];
            match reg & 0xF0 {
                0x30 => {
                    op.dt = (value >> 4) & 7;
                    op.mul = value & 15;
                }
                0x40 => op.tl = value & 0x7F,
                0x50 => {
                    op.ks = value >> 6;
                    op.ar = value & 0x1F;
                }
                0x60 => op.d1r = value & 0x1F,
                0x70 => op.d2r = value & 0x1F,
                0x80 => {
                    let sl = value >> 4;
                    op.sl = if sl == 15 { 0x3E0 } else { sl << 5 };
                    op.rr = value & 15;
                }
                _ => return,
            }
            Self::update_channel(chan);
            return;
        }
        match reg & 0xFC {
            0xA0 => {
                chan.fnum = ((self.fnum_latch[ch_index as usize] & 7) << 8) | value;
                chan.block = (self.fnum_latch[ch_index as usize] >> 3) & 7;
                Self::update_channel(chan);
            }
            0xA4 => self.fnum_latch[ch_index as usize] = value & 0x3F,
            0xB0 => {
                chan.algorithm = value & 7;
                chan.feedback = (value >> 3) & 7;
            }
            0xB4 => {
                chan.left = (value & 0x80) != 0;
                chan.right = (value & 0x40) != 0;
            }
            _ => {}
        }
    }

    fn set_key(op: &mut Operator, on: bool) {
        if on && !op.key_on {
            op.phase = 0;
            op.state = EgPhase::Attack;
            if op.ar_rate >= 32 + 62 {
                op.volume = 0;
                op.state = EgPhase::Decay;
            }
        } else if !on && op.key_on && op.state != EgPhase::Off {
            op.state = EgPhase::Release;
        }
        op.key_on = on;
    }

    fn update_channel(ch: &mut Channel) {
        let f = ch.fnum;
        let n4 = (f >> 10) & 1;
        let n3 = ((f >> 10) & 1) & (((f >> 9) | (f >> 8) | (f >> 7)) & 1)
            | (!(f >> 10) & 1) & ((f >> 9) & (f >> 8) & (f >> 7) & 1);
        ch.key_code = (ch.block << 2) | (n4 << 1) | n3;
        let fc = (f << ch.block) >> 1;
        for op in &mut ch.ops {
            let mut dt = DETUNE_TABLE[((op.dt & 3) * 32 + ch.key_code) as usize];
            if (op.dt & 4) != 0 {
                dt = -dt;
            }
            let inc = (fc + dt) & 0x1FFFF;
            op.increment = if op.mul == 0 {
                inc >> 1
            } else {
                (inc * op.mul) & 0xFFFFF
            };
            op.ksr = ch.key_code >> (3 - op.ks);
            op.ar_rate = if op.ar == 0 {
                0
            } else {
                32 + (op.ar << 1) + op.ksr
            };
            op.d1_rate = if op.d1r == 0 {
                0
            } else {
                32 + (op.d1r << 1) + op.ksr
            };
            op.d2_rate = if op.d2r == 0 {
                0
            } else {
                32 + (op.d2r << 1) + op.ksr
            };
            op.rr_rate = 32 + (op.rr << 2) + 2 + op.ksr;
            op.ar_rate = op.ar_rate.min(127);
            op.d1_rate = op.d1_rate.min(127);
            op.d2_rate = op.d2_rate.min(127);
            op.rr_rate = op.rr_rate.min(127);
        }
    }

    fn clock_envelopes(&mut self) {
        self.eg_counter += 1;
        for ch in &mut self.ch {
            for op in &mut ch.ops {
                match op.state {
                    EgPhase::Attack => {
                        let r = op.ar_rate;
                        if (self.eg_counter & ((1 << self.rate_shift[r as usize]) - 1)) != 0 {
                            continue;
                        }
                        let inc = EG_INC[(self.rate_select[r as usize]
                            + ((self.eg_counter >> self.rate_shift[r as usize]) & 7))
                            as usize];
                        op.volume += (!op.volume * inc) >> 4;
                        if op.volume <= 0 {
                            op.volume = 0;
                            op.state = EgPhase::Decay;
                        }
                    }
                    EgPhase::Decay => {
                        let r = op.d1_rate;
                        if (self.eg_counter & ((1 << self.rate_shift[r as usize]) - 1)) == 0 {
                            op.volume += EG_INC[(self.rate_select[r as usize]
                                + ((self.eg_counter >> self.rate_shift[r as usize]) & 7))
                                as usize];
                        }
                        if op.volume >= op.sl {
                            op.state = EgPhase::Sustain;
                        }
                    }
                    EgPhase::Sustain => {
                        let r = op.d2_rate;
                        if (self.eg_counter & ((1 << self.rate_shift[r as usize]) - 1)) != 0 {
                            continue;
                        }
                        op.volume += EG_INC[(self.rate_select[r as usize]
                            + ((self.eg_counter >> self.rate_shift[r as usize]) & 7))
                            as usize];
                        if op.volume >= MAX_ATT {
                            op.volume = MAX_ATT;
                        }
                    }
                    EgPhase::Release => {
                        let r = op.rr_rate;
                        if (self.eg_counter & ((1 << self.rate_shift[r as usize]) - 1)) != 0 {
                            continue;
                        }
                        op.volume += EG_INC[(self.rate_select[r as usize]
                            + ((self.eg_counter >> self.rate_shift[r as usize]) & 7))
                            as usize];
                        if op.volume >= MAX_ATT {
                            op.volume = MAX_ATT;
                            op.state = EgPhase::Off;
                        }
                    }
                    EgPhase::Off => {}
                }
            }
        }
    }

    fn op_calc(
        tl_tab: &[i32; TL_TAB_LEN],
        sin_tab: &[i32; SIN_LEN],
        op: &Operator,
        modulation: i32,
    ) -> i32 {
        let env = op.volume + (op.tl << 3);
        if env >= ENV_QUIET as i32 {
            return 0;
        }
        let p = (env << 3)
            + sin_tab[(((op.phase >> 10) as i32 + modulation) & SIN_MASK as i32) as usize];
        if p >= TL_TAB_LEN as i32 {
            0
        } else {
            tl_tab[p as usize]
        }
    }

    pub fn clock(&mut self) -> (i32, i32) {
        self.eg_timer += 1;
        if self.eg_timer >= 3 {
            self.eg_timer = 0;
            self.clock_envelopes();
        }
        let mut left = 0;
        let mut right = 0;
        let tl_tab = &self.tl_tab;
        let sin_tab = &self.sin_tab;
        for c in 0..6 {
            let ch = &mut self.ch[c];
            let output = if c == 5 && self.dac_enabled {
                (self.dac - 0x80) << 6
            } else {
                Self::channel_output(tl_tab, sin_tab, ch)
            };
            for op in &mut ch.ops {
                op.phase = (op.phase.wrapping_add(op.increment as u32)) & 0xFFFFF;
            }
            let output = output.clamp(-8192, 8191);
            if ch.left {
                left += output;
            }
            if ch.right {
                right += output;
            }
        }
        (left, right)
    }

    fn channel_output(
        tl_tab: &[i32; TL_TAB_LEN],
        sin_tab: &[i32; SIN_LEN],
        ch: &mut Channel,
    ) -> i32 {
        let fb_input = if ch.feedback != 0 {
            (ch.op1_out0 + ch.op1_out1) >> (10 - ch.feedback)
        } else {
            0
        };
        let op1 = Self::op_calc(tl_tab, sin_tab, &ch.ops[0], fb_input);
        ch.op1_out0 = ch.op1_out1;
        ch.op1_out1 = op1;
        let m = |v: i32| v >> 1;
        match ch.algorithm {
            0 => {
                let op2 = Self::op_calc(tl_tab, sin_tab, &ch.ops[2], m(op1));
                let op3 = Self::op_calc(tl_tab, sin_tab, &ch.ops[1], m(op2));
                Self::op_calc(tl_tab, sin_tab, &ch.ops[3], m(op3))
            }
            1 => {
                let op2 = Self::op_calc(tl_tab, sin_tab, &ch.ops[2], 0);
                let op3 = Self::op_calc(tl_tab, sin_tab, &ch.ops[1], m(op1 + op2));
                Self::op_calc(tl_tab, sin_tab, &ch.ops[3], m(op3))
            }
            2 => {
                let op2 = Self::op_calc(tl_tab, sin_tab, &ch.ops[2], 0);
                let op3 = Self::op_calc(tl_tab, sin_tab, &ch.ops[1], m(op2));
                Self::op_calc(tl_tab, sin_tab, &ch.ops[3], m(op1 + op3))
            }
            3 => {
                let op2 = Self::op_calc(tl_tab, sin_tab, &ch.ops[2], m(op1));
                let op3 = Self::op_calc(tl_tab, sin_tab, &ch.ops[1], 0);
                Self::op_calc(tl_tab, sin_tab, &ch.ops[3], m(op2 + op3))
            }
            4 => {
                let op2 = Self::op_calc(tl_tab, sin_tab, &ch.ops[2], m(op1));
                let op3 = Self::op_calc(tl_tab, sin_tab, &ch.ops[1], 0);
                op2 + Self::op_calc(tl_tab, sin_tab, &ch.ops[3], m(op3))
            }
            5 => {
                Self::op_calc(tl_tab, sin_tab, &ch.ops[2], m(op1))
                    + Self::op_calc(tl_tab, sin_tab, &ch.ops[1], m(op1))
                    + Self::op_calc(tl_tab, sin_tab, &ch.ops[3], m(op1))
            }
            6 => {
                Self::op_calc(tl_tab, sin_tab, &ch.ops[2], m(op1))
                    + Self::op_calc(tl_tab, sin_tab, &ch.ops[1], 0)
                    + Self::op_calc(tl_tab, sin_tab, &ch.ops[3], 0)
            }
            _ => {
                op1 + Self::op_calc(tl_tab, sin_tab, &ch.ops[2], 0)
                    + Self::op_calc(tl_tab, sin_tab, &ch.ops[1], 0)
                    + Self::op_calc(tl_tab, sin_tab, &ch.ops[3], 0)
            }
        }
    }
}
