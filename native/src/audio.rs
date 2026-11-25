//! WASAPI loopback capture of the default output device (everything the PC plays),
//! read once per frame on the render thread: no extra threads, no locks.

use windows::core::Result;
use windows::Win32::Media::Audio::*;
use windows::Win32::System::Com::*;

use crate::analysis::Analyzer;

pub struct Loopback {
    _client: IAudioClient,
    capture: IAudioCaptureClient,
    channels: usize,
    pub sample_rate: u32,
}

impl Loopback {
    pub fn start() -> Result<Self> {
        unsafe {
            let enumerator: IMMDeviceEnumerator = CoCreateInstance(&MMDeviceEnumerator, None, CLSCTX_ALL)?;
            let device = enumerator.GetDefaultAudioEndpoint(eRender, eConsole)?;
            let client: IAudioClient = device.Activate(CLSCTX_ALL, None)?;
            let format = client.GetMixFormat()?;
            // The shared-mode mix format is 32-bit float on every current Windows version.
            let channels = (*format).nChannels as usize;
            let sample_rate = (*format).nSamplesPerSec;
            client.Initialize(AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_LOOPBACK, 2_000_000, 0, format, None)?;
            CoTaskMemFree(Some(format as *const _));
            let capture: IAudioCaptureClient = client.GetService()?;
            client.Start()?;
            Ok(Self { _client: client, capture, channels, sample_rate })
        }
    }

    /// Moves everything captured since the last frame into the analyzer (down-mixed to mono).
    pub fn read(&mut self, analyzer: &mut Analyzer) -> Result<()> {
        unsafe {
            while self.capture.GetNextPacketSize()? > 0 {
                let mut data = std::ptr::null_mut();
                let mut frames = 0u32;
                let mut flags = 0u32;
                self.capture.GetBuffer(&mut data, &mut frames, &mut flags, None, None)?;
                let silent = flags & AUDCLNT_BUFFERFLAGS_SILENT.0 as u32 != 0;
                let samples = std::slice::from_raw_parts(data as *const f32, frames as usize * self.channels);
                for frame in samples.chunks_exact(self.channels) {
                    let mono = if silent { 0.0 } else { frame.iter().sum::<f32>() / self.channels as f32 };
                    analyzer.push(mono);
                }
                self.capture.ReleaseBuffer(frames)?;
            }
        }
        Ok(())
    }
}
