//! `cpal` output for the native front-end: 48 kHz stereo, fed one video frame at a time.
//!
//! The engine renders exactly 800 stereo frames per 60 Hz tick. The audio callback drains a small bounded queue and
//! inserts silence on underrun, so a slow frame never blocks the game loop.

use cpal::traits::{DeviceTrait, HostTrait, StreamTrait};
use cpal::{FromSample, SampleFormat, SizedSample, Stream, StreamConfig, I24, U24};
use std::sync::mpsc::{sync_channel, Receiver, SyncSender};

const OUTPUT_RATE: u32 = 48_000;
const QUEUE_FRAMES: usize = 8;

pub struct AudioOutput {
    _stream: Stream,
    sender: SyncSender<Vec<i16>>,
}

impl AudioOutput {
    /// Opens the default output device. Returns `None` (with a diagnostic) when no device or format is available.
    pub fn new() -> Option<Self> {
        let host = cpal::default_host();
        let device = match host.default_output_device() {
            Some(d) => d,
            None => {
                eprintln!("audio: no output device; running silent");
                return None;
            }
        };
        let sample_format = device
            .default_output_config()
            .map(|c| c.sample_format())
            .unwrap_or(SampleFormat::F32);
        let config = StreamConfig {
            channels: 2,
            sample_rate: OUTPUT_RATE,
            buffer_size: cpal::BufferSize::Default,
        };
        let (sender, receiver) = sync_channel::<Vec<i16>>(QUEUE_FRAMES);
        let stream = match sample_format {
            SampleFormat::I8 => build::<i8>(&device, &config, receiver),
            SampleFormat::U8 => build::<u8>(&device, &config, receiver),
            SampleFormat::I16 => build::<i16>(&device, &config, receiver),
            SampleFormat::U16 => build::<u16>(&device, &config, receiver),
            SampleFormat::I24 => build::<I24>(&device, &config, receiver),
            SampleFormat::U24 => build::<U24>(&device, &config, receiver),
            SampleFormat::I32 => build::<i32>(&device, &config, receiver),
            SampleFormat::U32 => build::<u32>(&device, &config, receiver),
            SampleFormat::I64 => build::<i64>(&device, &config, receiver),
            SampleFormat::U64 => build::<u64>(&device, &config, receiver),
            SampleFormat::F32 => build::<f32>(&device, &config, receiver),
            SampleFormat::F64 => build::<f64>(&device, &config, receiver),
            other => {
                eprintln!("audio: unsupported sample format {other:?}; running silent");
                return None;
            }
        };
        let stream = match stream {
            Ok(s) => s,
            Err(e) => {
                eprintln!("audio: output stream: {e}; running silent");
                return None;
            }
        };
        if let Err(e) = stream.play() {
            eprintln!("audio: play: {e}; running silent");
            return None;
        }
        Some(Self {
            _stream: stream,
            sender,
        })
    }

    /// Queues one frame (800 stereo samples). Dropped when the callback is behind rather than stalling gameplay.
    pub fn submit(&mut self, samples: &[i16]) {
        let _ = self.sender.try_send(samples.to_vec());
    }
}

fn build<T>(
    device: &cpal::Device,
    config: &StreamConfig,
    receiver: Receiver<Vec<i16>>,
) -> Result<Stream, cpal::Error>
where
    T: SizedSample + FromSample<f32>,
{
    let channels = config.channels as usize;
    let mut current: Vec<i16> = Vec::new();
    let mut index = 0usize;
    device.build_output_stream(
        *config,
        move |data: &mut [T], _: &cpal::OutputCallbackInfo| {
            for frame in data.chunks_mut(channels) {
                if index + 1 >= current.len() {
                    current = receiver.try_recv().unwrap_or_default();
                    index = 0;
                }
                let l = current.get(index).copied().unwrap_or(0) as f32 / 32768.0;
                let r = current.get(index + 1).copied().unwrap_or(0) as f32 / 32768.0;
                index += 2;
                if let Some(sample) = frame.first_mut() {
                    *sample = T::from_sample(l);
                }
                if channels > 1 {
                    if let Some(sample) = frame.get_mut(1) {
                        *sample = T::from_sample(r);
                    }
                }
                for sample in frame.iter_mut().skip(2) {
                    *sample = T::from_sample(0.0f32);
                }
            }
        },
        |e| eprintln!("audio: stream error: {e}"),
        None,
    )
}
