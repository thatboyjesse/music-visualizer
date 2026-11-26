//! The same analysis as `Visualizer/SpectrumAnalyzer.cs`: Hann window, 2048-point FFT,
//! log bands from 40 Hz to 16 kHz (max power per band), +3 dB/octave tilt above 100 Hz,
//! per-range sensitivity (EQ) and intensity gain, -72..-12 dB mapped to 0..1,
//! fast attack / slow release, falling peak caps, and a smoothed bass level.

use std::sync::Arc;

use rustfft::{num_complex::Complex32, Fft, FftPlanner};

pub const N: usize = 2048;
const MIN_FREQ: f64 = 40.0;
const MAX_FREQ: f64 = 16000.0;
const MIN_DB: f32 = -72.0;
const MAX_DB: f32 = -12.0;

pub struct Analyzer {
    pub samples: Vec<f32>,
    pub bands: Vec<f32>,
    pub peaks: Vec<f32>,
    pub bass: f32,
    hold: Vec<f32>,
    window: Vec<f32>,
    fft: Arc<dyn Fft<f32>>,
    buf: Vec<Complex32>,
    scratch: Vec<Complex32>,
    lo: Vec<usize>,
    hi: Vec<usize>,
    freq: Vec<f32>,
    tilt: Vec<f32>,
    eq_gain: Vec<f32>,
    eq: [f64; 4],
    built_for: u32,
    pub gain: f64,
}

impl Analyzer {
    pub fn new(bands: usize) -> Self {
        let window = (0..N).map(|i| 0.5 * (1.0 - (2.0 * std::f32::consts::PI * i as f32 / (N - 1) as f32).cos())).collect();
        let fft = FftPlanner::new().plan_fft_forward(N);
        let scratch = vec![Complex32::default(); fft.get_inplace_scratch_len()];
        Self {
            samples: vec![0.0; N],
            bands: vec![0.0; bands],
            peaks: vec![0.0; bands],
            bass: 0.0,
            hold: vec![0.0; bands],
            window,
            fft,
            buf: vec![Complex32::default(); N],
            scratch,
            lo: vec![0; bands],
            hi: vec![0; bands],
            freq: vec![0.0; bands],
            tilt: vec![0.0; bands],
            eq_gain: vec![0.0; bands],
            eq: [0.0; 4],
            built_for: 0,
            gain: 1.0,
        }
    }

    pub fn band_count(&self) -> usize {
        self.bands.len()
    }

    fn build(&mut self, rate: u32) {
        self.built_for = rate;
        let count = self.bands.len();
        let ratio = MAX_FREQ / MIN_FREQ;
        for b in 0..count {
            let f0 = MIN_FREQ * ratio.powf(b as f64 / count as f64);
            let f1 = MIN_FREQ * ratio.powf((b + 1) as f64 / count as f64);
            let lo = ((f0 * N as f64 / rate as f64).floor() as usize).clamp(1, N / 2 - 1);
            let hi = ((f1 * N as f64 / rate as f64).ceil() as usize).clamp(lo + 1, N / 2);
            self.lo[b] = lo;
            self.hi[b] = hi;
            let centre = (f0 * f1).sqrt();
            self.freq[b] = centre as f32;
            self.tilt[b] = (3.0 * (centre.max(100.0) / 100.0).log2()) as f32;
        }
        self.apply_eq();
    }

    /// Extra sensitivity per range in dB, at 100 Hz / 700 Hz / 3.5 kHz / 10 kHz, blended linearly in log-frequency.
    pub fn set_eq(&mut self, eq: [f64; 4]) {
        if eq != self.eq {
            self.eq = eq;
            self.apply_eq();
        }
    }

    fn apply_eq(&mut self) {
        let f = [100.0f64, 700.0, 3500.0, 10000.0];
        let g = self.eq;
        for b in 0..self.bands.len() {
            let x = (self.freq[b].max(1.0) as f64).ln();
            let gain = if x <= f[0].ln() {
                g[0]
            } else if x >= f[3].ln() {
                g[3]
            } else {
                let mut i = 0;
                while x > f[i + 1].ln() {
                    i += 1;
                }
                let t = (x - f[i].ln()) / (f[i + 1].ln() - f[i].ln());
                g[i] + (g[i + 1] - g[i]) * t
            };
            self.eq_gain[b] = gain as f32;
        }
    }

    /// Analyses `self.samples` (the latest N samples, oldest first), which the caller fills.
    pub fn update(&mut self, rate: u32, dt: f32) {
        if rate != self.built_for {
            self.build(rate);
        }
        for i in 0..N {
            self.buf[i] = Complex32::new(self.samples[i] * self.window[i], 0.0);
        }
        self.fft.process_with_scratch(&mut self.buf, &mut self.scratch);

        let gain_db = (20.0 * self.gain.clamp(0.05, 20.0).log10()) as f32;
        let rise = 1.0 - (-dt * 35.0).exp();
        let fall = 1.0 - (-dt * 7.0).exp();
        // rustfft is unnormalised; NAudio's FFT divides by N. Match it so the dB range lines up.
        let scale = 1.0 / (N as f32 * N as f32);
        let mut bass_sum = 0.0;
        let mut bass_count = 0;
        for b in 0..self.bands.len() {
            let mut max = 0.0f32;
            for k in self.lo[b]..self.hi[b] {
                let c = self.buf[k];
                max = max.max(c.re * c.re + c.im * c.im);
            }
            let db = 10.0 * (max * scale + 1e-12).log10() + self.tilt[b] + self.eq_gain[b] + gain_db;
            let target = ((db - MIN_DB) / (MAX_DB - MIN_DB)).clamp(0.0, 1.0);
            let mut cur = self.bands[b];
            cur += (target - cur) * if target > cur { rise } else { fall };
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
            if self.freq[b] < 150.0 {
                bass_sum += cur;
                bass_count += 1;
            }
        }
        let bass_target = if bass_count > 0 { bass_sum / bass_count as f32 } else { 0.0 };
        self.bass += (bass_target - self.bass) * if bass_target > self.bass { rise } else { fall };
    }
}
