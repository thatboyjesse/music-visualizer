//! Audio capture, the native version of `Audio/AudioEngine.cs` and `Audio/ProcessAudioCapture.cs`.
//!
//! - All audio: WASAPI loopback of the default output device. If the default device changes
//!   (headphones plugged in) or the device goes away, capture moves to the new default.
//! - Selected apps: one "process loopback" capture per top-level process of each chosen app
//!   (Windows 10 build 20348 / Windows 11), re-checked every 2 seconds so apps that start later
//!   are picked up.
//!
//! Every capture is read on the main thread, once per frame (or 4× a second while nothing is
//! drawn), into its own ring of the latest 2048 mono samples. No extra threads, no locks. The
//! whole process is COM multithreaded, so the objects can be used from any thread.

use std::collections::HashMap;
use std::sync::atomic::{AtomicIsize, Ordering};
use std::time::{Duration, Instant};

use windows::core::{implement, Interface, Result, GUID, PCWSTR};
use windows::Win32::Foundation::*;
use windows::Win32::Media::Audio::*;
use windows::Win32::System::Com::*;
use windows::Win32::System::Diagnostics::ToolHelp::*;
use windows::Win32::System::Threading::*;

use crate::analysis::N;

/// Peak level below which a block counts as silence (about -66 dBFS), as in the C# app.
const AUDIBLE: f32 = 0.0005;
const BUFFER_HNS: i64 = 10_000_000; // 1 s, so nothing is lost between the 4-a-second reads while paused
const APP_RATE: u32 = 48000;

/// The latest N mono samples from one source.
pub struct Ring {
    data: Vec<f32>,
    pos: usize,
    last_write: Option<Instant>,
    last_sound: Option<Instant>,
}

impl Ring {
    fn new() -> Self {
        Self { data: vec![0.0; N], pos: 0, last_write: None, last_sound: None }
    }

    fn add(&mut self, frames: &[f32], channels: usize, silent: bool, now: Instant) {
        let mut peak = 0.0f32;
        for frame in frames.chunks_exact(channels) {
            let v = if silent { 0.0 } else { frame.iter().sum::<f32>() / channels as f32 };
            self.data[self.pos] = v;
            self.pos = (self.pos + 1) % N;
            peak = peak.max(v.abs());
        }
        self.last_write = Some(now);
        if peak > AUDIBLE {
            self.last_sound = Some(now);
        }
    }

    /// Adds this ring's samples (oldest first) unless it hasn't had audio for a moment (paused, silent app).
    fn add_to(&self, dest: &mut [f32], now: Instant) {
        match self.last_write {
            Some(t) if now.duration_since(t) <= Duration::from_millis(150) => {}
            _ => return,
        }
        for (i, d) in dest.iter_mut().enumerate() {
            *d += self.data[(self.pos + i) % N];
        }
    }

    fn since_sound(&self, now: Instant) -> f64 {
        self.last_sound.map(|t| now.duration_since(t).as_secs_f64()).unwrap_or(f64::MAX)
    }
}

enum Format {
    Float,
    Int16,
    Int32,
}

/// One running WASAPI capture (device loopback or one app's process loopback).
struct Stream {
    client: IAudioClient,
    capture: IAudioCaptureClient,
    channels: usize,
    format: Format,
    ring: Ring,
    scratch: Vec<f32>,
}

impl Stream {
    unsafe fn start(client: IAudioClient, format: *const WAVEFORMATEX, flags: u32) -> Result<Stream> {
        let f = &*format;
        let channels = f.nChannels.max(1) as usize;
        let tag = f.wFormatTag as u32;
        let is_float = tag == 3 /* WAVE_FORMAT_IEEE_FLOAT */
            || (tag == 0xFFFE && {
                let ext = &*(format as *const WAVEFORMATEXTENSIBLE);
                let sub = ext.SubFormat;
                sub == GUID::from_u128(0x00000003_0000_0010_8000_00aa00389b71)
            });
        let fmt = match (is_float, f.wBitsPerSample) {
            (true, _) => Format::Float,
            (false, 16) => Format::Int16,
            _ => Format::Int32,
        };
        client.Initialize(AUDCLNT_SHAREMODE_SHARED, flags, BUFFER_HNS, 0, format, None)?;
        let capture: IAudioCaptureClient = client.GetService()?;
        client.Start()?;
        Ok(Stream { client, capture, channels, format: fmt, ring: Ring::new(), scratch: Vec::new() })
    }

    /// Moves everything captured since the last read into the ring.
    unsafe fn read(&mut self, now: Instant) -> Result<()> {
        while self.capture.GetNextPacketSize()? > 0 {
            let mut data = std::ptr::null_mut();
            let mut frames = 0u32;
            let mut flags = 0u32;
            self.capture.GetBuffer(&mut data, &mut frames, &mut flags, None, None)?;
            let silent = flags & AUDCLNT_BUFFERFLAGS_SILENT.0 as u32 != 0 || data.is_null();
            let count = frames as usize * self.channels;
            if silent {
                self.scratch.clear();
                self.scratch.resize(count, 0.0);
            } else {
                match self.format {
                    Format::Float => {
                        self.scratch.clear();
                        self.scratch.extend_from_slice(std::slice::from_raw_parts(data as *const f32, count));
                    }
                    Format::Int16 => {
                        let s = std::slice::from_raw_parts(data as *const i16, count);
                        self.scratch.clear();
                        self.scratch.extend(s.iter().map(|&v| v as f32 / 32768.0));
                    }
                    Format::Int32 => {
                        let s = std::slice::from_raw_parts(data as *const i32, count);
                        self.scratch.clear();
                        self.scratch.extend(s.iter().map(|&v| v as f32 / 2147483648.0));
                    }
                }
            }
            self.capture.ReleaseBuffer(frames)?;
            if count > 0 {
                self.ring.add(&self.scratch, self.channels, silent, now);
            }
        }
        Ok(())
    }
}

impl Drop for Stream {
    fn drop(&mut self) {
        unsafe {
            let _ = self.client.Stop();
        }
    }
}

/// Signals an event when ActivateAudioInterfaceAsync finishes (it completes on another thread).
#[implement(IActivateAudioInterfaceCompletionHandler, IAgileObject)]
struct Completion {
    event: AtomicIsize,
}

impl IActivateAudioInterfaceCompletionHandler_Impl for Completion_Impl {
    fn ActivateCompleted(&self, _op: Option<&IActivateAudioInterfaceAsyncOperation>) -> Result<()> {
        unsafe {
            let _ = SetEvent(HANDLE(self.event.load(Ordering::SeqCst) as *mut _));
        }
        Ok(())
    }
}

impl IAgileObject_Impl for Completion_Impl {}

/// An app capture that's being set up.
struct Pending {
    op: IActivateAudioInterfaceAsyncOperation,
    event: HANDLE,
    _handler: IActivateAudioInterfaceCompletionHandler,
    _params: Box<AUDIOCLIENT_ACTIVATION_PARAMS>,
    _variant: Box<[u64; 3]>,
}

impl Drop for Pending {
    fn drop(&mut self) {
        unsafe {
            let _ = CloseHandle(self.event);
        }
    }
}

enum App {
    Starting(Pending),
    Running(Stream),
    Failed(Instant),
}

#[derive(PartialEq, Clone, Debug)]
pub enum Source {
    None,
    AllAudio,
    Apps(Vec<String>),
}

pub struct Capture {
    source: Source,
    loopback: Option<Stream>,
    loopback_device: String,
    loopback_rate: u32,
    loopback_retry: Option<Instant>,
    apps: HashMap<u32, App>,
    last_scan: Option<Instant>,
    last_device_check: Instant,
    /// Last error from starting an app capture (e.g. Windows too old).
    pub app_error: Option<String>,
}

impl Capture {
    pub fn new() -> Self {
        Self {
            source: Source::None,
            loopback: None,
            loopback_device: String::new(),
            loopback_rate: 48000,
            loopback_retry: None,
            apps: HashMap::new(),
            last_scan: None,
            last_device_check: Instant::now(),
            app_error: None,
        }
    }

    /// Starts capturing what's asked for (stopping whatever ran before). Errors are reported once.
    pub fn set_source(&mut self, source: Source) -> std::result::Result<(), String> {
        if source == self.source {
            return Ok(());
        }
        self.stop();
        self.source = source;
        match self.source {
            Source::AllAudio => self.start_loopback().map_err(|e| e.message()),
            _ => Ok(()),
        }
    }

    pub fn stop(&mut self) {
        self.loopback = None;
        self.loopback_retry = None;
        self.apps.clear();
        self.last_scan = None;
        self.source = Source::None;
    }

    pub fn sample_rate(&self) -> u32 {
        match self.source {
            Source::Apps(_) => APP_RATE,
            _ => self.loopback_rate,
        }
    }

    fn start_loopback(&mut self) -> Result<()> {
        self.loopback = None;
        unsafe {
            let enumerator: IMMDeviceEnumerator = CoCreateInstance(&MMDeviceEnumerator, None, CLSCTX_ALL)?;
            let device = enumerator.GetDefaultAudioEndpoint(eRender, eConsole)?;
            let id = device.GetId()?;
            self.loopback_device = id.to_string().unwrap_or_default();
            CoTaskMemFree(Some(id.0 as *const _));
            let client: IAudioClient = device.Activate(CLSCTX_ALL, None)?;
            let format = client.GetMixFormat()?;
            self.loopback_rate = (*format).nSamplesPerSec;
            let stream = Stream::start(client, format, AUDCLNT_STREAMFLAGS_LOOPBACK);
            CoTaskMemFree(Some(format as *const _));
            self.loopback = Some(stream?);
        }
        Ok(())
    }

    fn default_device_id() -> Option<String> {
        unsafe {
            let enumerator: IMMDeviceEnumerator = CoCreateInstance(&MMDeviceEnumerator, None, CLSCTX_ALL).ok()?;
            let device = enumerator.GetDefaultAudioEndpoint(eRender, eConsole).ok()?;
            let id = device.GetId().ok()?;
            let s = id.to_string().ok();
            CoTaskMemFree(Some(id.0 as *const _));
            s
        }
    }

    /// Reads all captures, follows device changes, and (for apps) starts and drops captures.
    pub fn poll(&mut self, now: Instant) {
        match self.source.clone() {
            Source::None => {}
            Source::AllAudio => self.poll_loopback(now),
            Source::Apps(names) => self.poll_apps(&names, now),
        }
    }

    fn poll_loopback(&mut self, now: Instant) {
        let failed = match &mut self.loopback {
            Some(s) => unsafe { s.read(now).is_err() },
            None => true,
        };
        // Every 2 s: has the default output device changed?
        let mut switch = false;
        if now.duration_since(self.last_device_check) >= Duration::from_secs(2) {
            self.last_device_check = now;
            if let Some(id) = Self::default_device_id() {
                switch = id != self.loopback_device;
            }
        }
        if failed || switch {
            if failed && self.loopback.is_some() {
                self.loopback = None;
            }
            let due = self.loopback_retry.map(|t| now >= t).unwrap_or(true);
            if due || switch {
                if self.start_loopback().is_err() {
                    self.loopback_retry = Some(now + Duration::from_secs(1));
                } else {
                    self.loopback_retry = None;
                }
            }
        }
    }

    fn poll_apps(&mut self, names: &[String], now: Instant) {
        // Re-check which processes to capture: every 2 s while none is captured, else every 10 s
        // (listing every process costs a few milliseconds).
        let running = self.apps.values().any(|a| matches!(a, App::Running(_)));
        let every = Duration::from_secs(if running { 10 } else { 2 });
        if self.last_scan.map(|t| now.duration_since(t) >= every).unwrap_or(true) {
            self.last_scan = Some(now);
            let wanted = root_process_ids(names);
            self.apps.retain(|pid, app| {
                wanted.contains(pid) && !matches!(app, App::Failed(t) if now.duration_since(*t) >= Duration::from_secs(2))
            });
            for pid in wanted {
                if !self.apps.contains_key(&pid) {
                    let app = match unsafe { activate(pid) } {
                        Ok(p) => App::Starting(p),
                        Err(e) => {
                            self.app_error = Some(e.message());
                            App::Failed(now)
                        }
                    };
                    self.apps.insert(pid, app);
                }
            }
        }
        for app in self.apps.values_mut() {
            match app {
                App::Starting(p) => unsafe {
                    if WaitForSingleObject(p.event, 0) == WAIT_OBJECT_0 {
                        *app = match finish_activation(p) {
                            Ok(s) => App::Running(s),
                            Err(e) => {
                                self.app_error = Some(e.message());
                                App::Failed(now)
                            }
                        };
                    }
                },
                App::Running(s) => unsafe {
                    if s.read(now).is_err() {
                        *app = App::Failed(now); // the app exited or the device changed; restarted on the next scan
                    }
                },
                App::Failed(_) => {}
            }
        }
    }

    /// The latest N samples, oldest first, summing all sources.
    pub fn samples(&self, dest: &mut [f32], now: Instant) {
        dest.fill(0.0);
        if let Some(s) = &self.loopback {
            s.ring.add_to(dest, now);
        }
        for app in self.apps.values() {
            if let App::Running(s) = app {
                s.ring.add_to(dest, now);
            }
        }
    }

    /// Seconds since any source delivered audible sound; huge when nothing is captured.
    pub fn seconds_since_sound(&self, now: Instant) -> f64 {
        let mut best = self.loopback.as_ref().map(|s| s.ring.since_sound(now)).unwrap_or(f64::MAX);
        for app in self.apps.values() {
            if let App::Running(s) = app {
                best = best.min(s.ring.since_sound(now));
            }
        }
        best
    }
}

unsafe fn activate(pid: u32) -> Result<Pending> {
    let params = Box::new(AUDIOCLIENT_ACTIVATION_PARAMS {
        ActivationType: AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK,
        Anonymous: AUDIOCLIENT_ACTIVATION_PARAMS_0 {
            ProcessLoopbackParams: AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS {
                TargetProcessId: pid,
                ProcessLoopbackMode: PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE,
            },
        },
    });
    // A PROPVARIANT holding a VT_BLOB that points at the parameters: vt at 0, cbSize at 8, pBlobData at 16.
    let mut variant = Box::new([0u64; 3]);
    variant[0] = 65; // VT_BLOB
    variant[1] = std::mem::size_of::<AUDIOCLIENT_ACTIVATION_PARAMS>() as u64;
    variant[2] = &*params as *const _ as u64;

    let event = CreateEventW(None, true, false, PCWSTR::null())?;
    let handler: IActivateAudioInterfaceCompletionHandler = Completion { event: AtomicIsize::new(event.0 as isize) }.into();
    let op = ActivateAudioInterfaceAsync(
        VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK,
        &IAudioClient::IID,
        Some(&*variant as *const _ as *const windows::core::PROPVARIANT),
        &handler,
    );
    match op {
        Ok(op) => Ok(Pending { op, event, _handler: handler, _params: params, _variant: variant }),
        Err(e) => {
            let _ = CloseHandle(event);
            Err(e)
        }
    }
}

unsafe fn finish_activation(p: &Pending) -> Result<Stream> {
    let mut hr = windows::core::HRESULT(0);
    let mut iface: Option<windows::core::IUnknown> = None;
    p.op.GetActivateResult(&mut hr, &mut iface)?;
    hr.ok()?;
    let client: IAudioClient = iface.ok_or_else(|| windows::core::Error::from(E_NOINTERFACE))?.cast()?;
    // Process loopback has no mix format to ask for: always 48 kHz stereo float.
    let format = WAVEFORMATEX {
        wFormatTag: 3,
        nChannels: 2,
        nSamplesPerSec: APP_RATE,
        nAvgBytesPerSec: APP_RATE * 8,
        nBlockAlign: 8,
        wBitsPerSample: 32,
        cbSize: 0,
    };
    Stream::start(
        client,
        &format,
        AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY,
    )
}

/// `AudioApps.RootProcessIds`: processes with one of these names (no ".exe") whose parent has a
/// different name. Capturing those includes their children (browsers and Spotify play from a child).
fn root_process_ids(names: &[String]) -> Vec<u32> {
    let mut procs: Vec<(u32, u32, String)> = Vec::new(); // (pid, parent, lowercase name without .exe)
    unsafe {
        let Ok(snap) = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0) else { return vec![] };
        let mut e = PROCESSENTRY32W { dwSize: std::mem::size_of::<PROCESSENTRY32W>() as u32, ..Default::default() };
        if Process32FirstW(snap, &mut e).is_ok() {
            loop {
                let name = crate::sys::from_wide(&e.szExeFile).to_lowercase();
                let name = name.strip_suffix(".exe").map(str::to_string).unwrap_or(name);
                procs.push((e.th32ProcessID, e.th32ParentProcessID, name));
                if Process32NextW(snap, &mut e).is_err() {
                    break;
                }
            }
        }
        let _ = CloseHandle(snap);
    }
    let wanted: Vec<String> = names.iter().map(|n| n.to_lowercase()).collect();
    let mut result = Vec::new();
    for (pid, parent, name) in &procs {
        if !wanted.contains(name) {
            continue;
        }
        let parent_same = procs.iter().any(|(p, _, n)| p == parent && n == name);
        if !parent_same {
            result.push(*pid);
        }
    }
    result
}
