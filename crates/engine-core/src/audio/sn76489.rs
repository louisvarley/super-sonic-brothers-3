//! SN76489-compatible PSG, a direct port of `legacy/src/SonicBros3/Audio/Sn76489.cs`.

pub const CLOCK_NTSC: f64 = 3_579_545.0;

pub struct Sn76489 {
    tone: [i32; 3],
    volume: [i32; 4],
    counter: [i32; 4],
    output: [bool; 4],
    noise_control: i32,
    latch: i32,
    lfsr: i32,
    clock_accumulator: f64,
    ticks_per_sample: f64,
    volume_table: [i32; 16],
}

impl Sn76489 {
    pub fn new(sample_rate: f64) -> Self {
        let mut volume_table = [0; 16];
        for (i, slot) in volume_table.iter_mut().take(15).enumerate() {
            *slot = (2800.0 * 10f64.powf(-2.0 * i as f64 / 20.0)) as i32;
        }
        Self {
            tone: [0; 3],
            volume: [15; 4],
            counter: [0; 4],
            output: [false; 4],
            noise_control: 0,
            latch: 0,
            lfsr: 0x8000,
            clock_accumulator: 0.0,
            ticks_per_sample: CLOCK_NTSC / 16.0 / sample_rate,
            volume_table,
        }
    }

    pub fn write(&mut self, value: i32) {
        let value = value & 0xFF;
        if (value & 0x80) != 0 {
            self.latch = (value >> 4) & 7;
            let data = value & 0x0F;
            let ch = (self.latch >> 1) as usize;
            if (self.latch & 1) != 0 {
                self.volume[ch] = data;
            } else if ch < 3 {
                self.tone[ch] = (self.tone[ch] & 0x3F0) | data;
            } else {
                self.noise_control = data & 7;
                self.lfsr = 0x8000;
            }
        } else {
            let ch = (self.latch >> 1) as usize;
            if (self.latch & 1) != 0 {
                self.volume[ch] = value & 0x0F;
            } else if ch < 3 {
                self.tone[ch] = (self.tone[ch] & 0x0F) | ((value & 0x3F) << 4);
            } else {
                self.noise_control = value & 7;
                self.lfsr = 0x8000;
            }
        }
    }

    /// One output sample (mono). Each channel's level is averaged over the PSG ticks in this sample's period.
    pub fn clock(&mut self) -> i32 {
        self.clock_accumulator += self.ticks_per_sample;
        let ticks = self.clock_accumulator as i32;
        self.clock_accumulator -= ticks as f64;
        if ticks == 0 {
            return self.level();
        }
        let mut total = 0;
        for _ in 0..ticks {
            for c in 0..3 {
                self.counter[c] -= 1;
                if self.counter[c] > 0 {
                    continue;
                }
                self.counter[c] = if self.tone[c] == 0 { 1 } else { self.tone[c] };
                self.output[c] = !self.output[c];
            }
            self.counter[3] -= 1;
            if self.counter[3] <= 0 {
                let rate = self.noise_control & 3;
                self.counter[3] = if rate == 3 {
                    self.tone[2].max(1) * 2
                } else {
                    0x20 << rate
                };
                let white = (self.noise_control & 4) != 0;
                let bit = if white {
                    (self.lfsr & 1) ^ ((self.lfsr >> 3) & 1)
                } else {
                    self.lfsr & 1
                };
                self.lfsr = (self.lfsr >> 1) | (bit << 15);
                self.output[3] = (self.lfsr & 1) != 0;
            }
            total += self.level();
        }
        total / ticks
    }

    fn level(&self) -> i32 {
        let mut sum = 0;
        for c in 0..3 {
            if self.tone[c] > 1 {
                sum += if self.output[c] {
                    self.volume_table[self.volume[c] as usize]
                } else {
                    -self.volume_table[self.volume[c] as usize]
                };
            }
        }
        sum += if self.output[3] {
            self.volume_table[self.volume[3] as usize]
        } else {
            -self.volume_table[self.volume[3] as usize]
        };
        sum
    }
}
