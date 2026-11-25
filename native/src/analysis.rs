//! The same analysis as `Visualizer/SpectrumAnalyzer.cs`: Hann window, 2048-point FFT,
//! 96 log bands from 40 Hz to 16 kHz (max power per band), +3 dB/octave tilt above 100 Hz,
//! -72..-12 dB mapped to 0..1, fast attack / slow release, and falling peak caps.

use std::sync::Arc;

use rustfft::{num_complex::Complex32, Fft, FftPlanner};

pub const N: usize = 2048;
pub const BANDS: usize = 96;
const MIN_FREQ: f32 = 40.0;
const MAX_FREQ: f32 = 16000.0;
const MIN_DB: f32 = -72.0;
const MAX_DB: f32 = -12.0;

pub struct Analyzer {
    ring: Vec<f32>,
    pos: usize,
    pub samples: Vec<f32>,
    pub bands: Vec<f32>,
    pub peaks: Vec<f32>,
    hold: Vec<f32>,
    window: Vec<f32>,
    fft: Arc<dyn Fft<f32>>,
    buf: Vec<Complex32>,
    lo: Vec<usize>,
    hi: Vec<usize>,
    tilt: Vec<f32>,
    built_for: u32,
}

impl Analyzer {
    pub fn new() -> Self {
        let window = (0..N).map(|i| 0.5 * (1.0 - (2.0 * std::f32::consts::PI * i as f32 / (N - 1) as f32).cos())).collect();
        Self {
            ring: vec![0.0; N],
            pos: 0,
            samples: vec![0.0; N],
            bands: vec![0.0; BANDS],
            peaks: vec![0.0; BANDS],
            hold: vec![0.0; BANDS],
            window,
            fft: FftPlanner::new().plan_fft_forward(N),
            buf: vec![Complex32::default(); N],
            lo: vec![0; BANDS],
            hi: vec![0; BANDS],
            tilt: vec![0.0; BANDS],
            built_for: 0,
        }
    }

    pub fn push(&mut self, sample: f32) {
        self.ring[self.pos] = sample;
        self.pos = (self.pos + 1) % N;
    }

    fn build(&mut self, rate: u32) {
        self.built_for = rate;
        let ratio = MAX_FREQ / MIN_FREQ;
        for b in 0..BANDS {
            let f0 = MIN_FREQ * ratio.powf(b as f32 / BANDS as f32);
            let f1 = MIN_FREQ * ratio.powf((b + 1) as f32 / BANDS as f32);
            let lo = ((f0 * N as f32 / rate as f32).floor() as usize).clamp(1, N / 2 - 1);
            let hi = ((f1 * N as f32 / rate as f32).ceil() as usize).clamp(lo + 1, N / 2);
            self.lo[b] = lo;
            self.hi[b] = hi;
            let centre = (f0 * f1).sqrt();
            self.tilt[b] = 3.0 * (centre.max(100.0) / 100.0).log2();
        }
    }

    pub fn update(&mut self, rate: u32, dt: f32) {
        if rate != self.built_for {
            self.build(rate);
        }
        for i in 0..N {
            let s = self.ring[(self.pos + i) % N];
            self.samples[i] = s;
            self.buf[i] = Complex32::new(s * self.window[i], 0.0);
        }
        self.fft.process(&mut self.buf);

        let rise = 1.0 - (-dt * 35.0).exp();
        let fall = 1.0 - (-dt * 7.0).exp();
        // rustfft is unnormalised; NAudio's FFT divides by N. Match it so the dB range lines up.
        let scale = 1.0 / N as f32;
        for b in 0..BANDS {
            let mut max = 0.0f32;
            for k in self.lo[b]..self.hi[b] {
                let c = self.buf[k] * scale;
                max = max.max(c.re * c.re + c.im * c.im);
            }
            let db = 10.0 * (max + 1e-12).log10() + self.tilt[b];
            let target = ((db - MIN_DB) / (MAX_DB - MIN_DB)).clamp(0.0, 1.0);
            let cur = self.bands[b];
            let cur = cur + (target - cur) * if target > cur { rise } else { fall };
            self.bands[b] = cur;
            if cur >= self.peaks[b] {
                self.peaks[b] = cur;
                self.hold[b] = 0.35;
            } else {
                self.hold[b] -= dt;
                if self.hold[b] <= 0.0 {
                    self.peaks[b] = cur.max(self.peaks[b] - dt * 0.5);
                }
            }
        }
    }
}
