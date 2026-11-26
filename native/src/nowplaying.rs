//! Now playing, from the Windows media session (`NowPlaying/MediaSessionProvider.cs` and
//! `NowPlayingService.cs`): prefers Spotify, else whichever app Windows considers current.
//! Polled every 2 seconds on its own thread (the WinRT calls block), which also decodes the
//! cover and works out its colours; the main thread is woken with an event.

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Instant;

use windows::Foundation::TimeSpan;
use windows::Media::Control::*;
use windows::Storage::Streams::*;
use windows::Win32::Foundation::HANDLE;
use windows::Win32::System::Com::*;
use windows::Win32::System::Threading::{SetEvent, WaitForSingleObject};

use crate::imaging::{Pixels, Rgb};

#[derive(Clone)]
pub struct Track {
    pub title: String,
    pub artist: String,
    pub playing: bool,
    /// Position when fetched, and the song's length, in seconds.
    pub position: f64,
    pub duration: f64,
    pub fetched: Instant,
    pub source: String,
    pub art: Option<Arc<Pixels>>,
    /// Changes whenever the cover changes (so the drawing side knows to rebuild its bitmap).
    pub art_id: u64,
}

impl Track {
    pub fn position_at(&self, now: Instant) -> f64 {
        let mut p = if self.playing { self.position + now.duration_since(self.fetched).as_secs_f64() } else { self.position };
        if self.duration > 0.0 && p > self.duration {
            p = self.duration;
        }
        p.max(0.0)
    }

    pub fn key(&self) -> String {
        format!("{}|{}", self.title, self.artist)
    }
}

#[derive(Default)]
pub struct Shared {
    pub track: Option<Track>,
    /// Colours of the latest cover; kept when playback stops or a song has no cover.
    pub colors: Option<(Rgb, Rgb)>,
    pub changed: bool,
}

pub struct NowPlaying {
    pub shared: Arc<Mutex<Shared>>,
    stop: HANDLE,
    slow: Arc<AtomicBool>,
}

impl NowPlaying {
    /// Starts polling. `wake` is set after every poll.
    pub fn start(wake: HANDLE) -> NowPlaying {
        let shared = Arc::new(Mutex::new(Shared::default()));
        let stop = unsafe { windows::Win32::System::Threading::CreateEventW(None, true, false, None).unwrap_or_default() };
        let s = shared.clone();
        let slow = Arc::new(AtomicBool::new(false));
        let sl = slow.clone();
        let (wake_raw, stop_raw) = (wake.0 as isize, stop.0 as isize);
        std::thread::Builder::new()
            .name("now playing".into())
            .stack_size(256 * 1024)
            .spawn(move || run(s, sl, HANDLE(wake_raw as *mut _), HANDLE(stop_raw as *mut _)))
            .ok();
        NowPlaying { shared, stop, slow }
    }

    /// While nothing is drawn, poll every 6 s instead of every 2 s.
    pub fn set_slow(&self, slow: bool) {
        self.slow.store(slow, Ordering::Relaxed);
    }
}

impl Drop for NowPlaying {
    fn drop(&mut self) {
        unsafe {
            let _ = SetEvent(self.stop);
        }
    }
}

fn run(shared: Arc<Mutex<Shared>>, slow: Arc<AtomicBool>, wake: HANDLE, stop: HANDLE) {
    unsafe {
        let _ = CoInitializeEx(None, COINIT_MULTITHREADED);
    }
    let mut manager: Option<GlobalSystemMediaTransportControlsSessionManager> = None;
    let mut art_key = String::new();
    let mut art: Option<Arc<Pixels>> = None;
    let mut art_id = 0u64;
    loop {
        let track = poll(&mut manager, &mut art_key, &mut art, &mut art_id);
        {
            let mut s = shared.lock().unwrap();
            let art_changed = s.track.as_ref().map(|t| t.art_id) != track.as_ref().map(|t| t.art_id);
            if art_changed {
                if let Some(a) = track.as_ref().and_then(|t| t.art.as_ref()) {
                    if let Some(c) = crate::imaging::album_colors(a) {
                        s.colors = Some(c);
                    }
                }
            }
            s.track = track;
            s.changed = true;
        }
        unsafe {
            let _ = SetEvent(wake);
            let wait = if slow.load(Ordering::Relaxed) { 6000 } else { 2000 };
            if WaitForSingleObject(stop, wait) == windows::Win32::Foundation::WAIT_OBJECT_0 {
                return;
            }
        }
    }
}

fn poll(
    manager: &mut Option<GlobalSystemMediaTransportControlsSessionManager>,
    art_key: &mut String,
    art: &mut Option<Arc<Pixels>>,
    art_id: &mut u64,
) -> Option<Track> {
    let result = (|| -> windows::core::Result<Option<Track>> {
        if manager.is_none() {
            *manager = Some(GlobalSystemMediaTransportControlsSessionManager::RequestAsync()?.get()?);
        }
        let m = manager.as_ref().unwrap();
        let sessions = m.GetSessions()?;
        let mut session = None;
        for s in sessions {
            if s.SourceAppUserModelId().map(|a| is_spotify(&a.to_string())).unwrap_or(false) {
                session = Some(s);
                break;
            }
        }
        let session = match session {
            Some(s) => s,
            None => match m.GetCurrentSession() {
                Ok(s) => s,
                Err(_) => return Ok(None),
            },
        };
        let props = session.TryGetMediaPropertiesAsync()?.get()?;
        let title = props.Title()?.to_string();
        if title.trim().is_empty() {
            return Ok(None);
        }
        let artist = props.Artist().map(|s| s.to_string()).unwrap_or_default();
        let album = props.AlbumTitle().map(|s| s.to_string()).unwrap_or_default();
        let playing = session.GetPlaybackInfo()?.PlaybackStatus()? == GlobalSystemMediaTransportControlsSessionPlaybackStatus::Playing;
        let timeline = session.GetTimelineProperties()?;

        let key = format!("{title}|{artist}|{album}");
        if key != *art_key {
            *art_key = key;
            *art = props.Thumbnail().ok().and_then(|t| load_art(&t)).map(Arc::new);
            *art_id += 1;
        }

        // The timeline position is only updated now and then; bring it up to "now".
        let secs = |t: TimeSpan| t.Duration as f64 / 1e7;
        let mut position = secs(timeline.Position()?);
        let updated = timeline.LastUpdatedTime()?.UniversalTime;
        // 100 ns ticks since 1601; year 2000 is about 1.26e17.
        if playing && updated > 125_911_584_000_000_000 {
            let now = unsafe { windows::Win32::System::SystemInformation::GetSystemTimePreciseAsFileTime() };
            let now = ((now.dwHighDateTime as i64) << 32) | now.dwLowDateTime as i64;
            position += (now - updated) as f64 / 1e7;
        }
        let duration = secs(timeline.EndTime()?) - secs(timeline.StartTime()?);
        let source = friendly_name(&session.SourceAppUserModelId().map(|s| s.to_string()).unwrap_or_default());
        Ok(Some(Track {
            title,
            artist,
            playing,
            position,
            duration,
            fetched: Instant::now(),
            source,
            art: art.clone(),
            art_id: *art_id,
        }))
    })();
    match result {
        Ok(t) => t,
        Err(_) => {
            *manager = None; // media session service restarted, etc.
            None
        }
    }
}

fn load_art(reference: &IRandomAccessStreamReference) -> Option<Pixels> {
    let stream = reference.OpenReadAsync().ok()?.get().ok()?;
    let size = stream.Size().ok()?.min(32 * 1024 * 1024) as u32;
    if size == 0 {
        return None;
    }
    let input: IInputStream = windows::core::Interface::cast(&stream).ok()?;
    let reader = DataReader::CreateDataReader(&input).ok()?;
    let loaded = reader.LoadAsync(size).ok()?.get().ok()?;
    let mut bytes = vec![0u8; loaded as usize];
    reader.ReadBytes(&mut bytes).ok()?;
    crate::imaging::load_bytes(&bytes, 300)
}

fn is_spotify(aumid: &str) -> bool {
    aumid.to_lowercase().contains("spotify")
}

/// "Spotify", "Chrome", "Edge"... from the app's AppUserModelID.
fn friendly_name(aumid: &str) -> String {
    if is_spotify(aumid) {
        return "Spotify".into();
    }
    let mut name = aumid.split('!').next().unwrap_or("").to_string();
    if name.to_lowercase().ends_with(".exe") {
        name.truncate(name.len() - 4);
    }
    if let Some(u) = name.find('_') {
        if u > 0 {
            name.truncate(u);
        }
    }
    if let Some(d) = name.rfind('.') {
        if d < name.len() - 1 {
            name = name[d + 1..].to_string();
        }
    }
    match name.to_lowercase().as_str() {
        "chrome" => "Chrome".into(),
        "msedge" => "Edge".into(),
        "firefox" => "Firefox".into(),
        "zunemusic" => "Media Player".into(),
        _ => name,
    }
}
