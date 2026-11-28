//! Now playing, from the Windows media session (`NowPlaying/MediaSessionProvider.cs` and
//! `NowPlayingService.cs`). Shows the app the visualizer listens to, one app at a time: with
//! "Only the apps I choose", the chosen app that made sound most recently (see `pick_session`);
//! with "Everything on this PC", a playing session (Spotify first), else Windows' current one.
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
use windows::Win32::System::Threading::{SetEvent, WaitForMultipleObjects};

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
    /// Set to poll again right away (the app to show changed).
    poke: HANDLE,
    slow: Arc<AtomicBool>,
    prefer: Arc<Mutex<Option<Vec<String>>>>,
}

impl NowPlaying {
    /// Starts polling. `wake` is set after every poll.
    pub fn start(wake: HANDLE) -> NowPlaying {
        let shared = Arc::new(Mutex::new(Shared::default()));
        let stop = unsafe { windows::Win32::System::Threading::CreateEventW(None, true, false, None).unwrap_or_default() };
        let poke = unsafe { windows::Win32::System::Threading::CreateEventW(None, false, false, None).unwrap_or_default() };
        let s = shared.clone();
        let slow = Arc::new(AtomicBool::new(false));
        let sl = slow.clone();
        let prefer = Arc::new(Mutex::new(None));
        let pr = prefer.clone();
        let (wake_raw, stop_raw, poke_raw) = (wake.0 as isize, stop.0 as isize, poke.0 as isize);
        std::thread::Builder::new()
            .name("now playing".into())
            .stack_size(256 * 1024)
            .spawn(move || run(s, sl, pr, HANDLE(wake_raw as *mut _), HANDLE(stop_raw as *mut _), HANDLE(poke_raw as *mut _)))
            .ok();
        NowPlaying { shared, stop, poke, slow, prefer }
    }

    /// Which apps to show, in order of preference (lowercase process names without ".exe"),
    /// or `None` for "everything on this PC". When the first choice changes, polls again at once.
    pub fn set_prefer(&self, prefer: Option<Vec<String>>) {
        let mut p = self.prefer.lock().unwrap();
        if *p != prefer {
            let first_changed = p.as_ref().and_then(|v| v.first().cloned()) != prefer.as_ref().and_then(|v| v.first().cloned())
                || p.is_some() != prefer.is_some();
            *p = prefer;
            if first_changed {
                unsafe {
                    let _ = SetEvent(self.poke);
                }
            }
        }
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

fn run(shared: Arc<Mutex<Shared>>, slow: Arc<AtomicBool>, prefer: Arc<Mutex<Option<Vec<String>>>>, wake: HANDLE, stop: HANDLE, poke: HANDLE) {
    unsafe {
        let _ = CoInitializeEx(None, COINIT_MULTITHREADED);
    }
    let mut manager: Option<GlobalSystemMediaTransportControlsSessionManager> = None;
    let mut art_key = String::new();
    let mut art: Option<Arc<Pixels>> = None;
    let mut art_id = 0u64;
    loop {
        let wanted = prefer.lock().unwrap().clone();
        let track = poll(&mut manager, &wanted, &mut art_key, &mut art, &mut art_id);
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
            if WaitForMultipleObjects(&[stop, poke], false, wait) == windows::Win32::Foundation::WAIT_OBJECT_0 {
                return; // stop; a poke (index 1) or the timeout just polls again
            }
        }
    }
}

fn poll(
    manager: &mut Option<GlobalSystemMediaTransportControlsSessionManager>,
    prefer: &Option<Vec<String>>,
    art_key: &mut String,
    art: &mut Option<Arc<Pixels>>,
    art_id: &mut u64,
) -> Option<Track> {
    let result = (|| -> windows::core::Result<Option<Track>> {
        if manager.is_none() {
            *manager = Some(GlobalSystemMediaTransportControlsSessionManager::RequestAsync()?.get()?);
        }
        let m = manager.as_ref().unwrap();
        let Some(session) = pick_session(m, prefer)? else { return Ok(None) };
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

/// The session to show, one app at a time.
///
/// - Chosen apps (`prefer` is `Some`): the first app in `prefer` (the one that made sound most
///   recently comes first) that has a media session, preferring one of its sessions that's playing.
///   If none of the chosen apps has a session, nothing is shown: the card never shows an app the
///   visualizer isn't listening to.
/// - Everything (`None`): a playing Spotify, else Windows' current session if it's playing, else any
///   playing session, else Spotify, else the current session.
fn pick_session(
    m: &GlobalSystemMediaTransportControlsSessionManager,
    prefer: &Option<Vec<String>>,
) -> windows::core::Result<Option<GlobalSystemMediaTransportControlsSession>> {
    let sessions: Vec<_> = m.GetSessions()?.into_iter().collect();
    let aumid = |s: &GlobalSystemMediaTransportControlsSession| s.SourceAppUserModelId().map(|a| a.to_string()).unwrap_or_default();
    let playing = |s: &GlobalSystemMediaTransportControlsSession| {
        s.GetPlaybackInfo().and_then(|i| i.PlaybackStatus()).map(|p| p == GlobalSystemMediaTransportControlsSessionPlaybackStatus::Playing).unwrap_or(false)
    };
    if let Some(names) = prefer {
        for name in names {
            let mine: Vec<_> = sessions.iter().filter(|s| matches_app(&aumid(s), name)).collect();
            if let Some(s) = mine.iter().find(|s| playing(s)).or(mine.first()) {
                return Ok(Some((*s).clone()));
            }
        }
        return Ok(None);
    }
    let current = m.GetCurrentSession().ok();
    let spotify = sessions.iter().find(|s| is_spotify(&aumid(s)));
    if let Some(s) = spotify.filter(|s| playing(s)) {
        return Ok(Some(s.clone()));
    }
    if let Some(c) = current.as_ref().filter(|c| playing(c)) {
        return Ok(Some(c.clone()));
    }
    if let Some(s) = sessions.iter().find(|s| playing(s)) {
        return Ok(Some(s.clone()));
    }
    Ok(spotify.cloned().or(current))
}

/// Whether a media session (by its AppUserModelID) belongs to the app with this process name
/// (lowercase, no ".exe"). Desktop apps' IDs are their exe name (`Spotify.exe`, `vlc.exe`) or a
/// name of their own (`Chrome`, `MSEdge`, sometimes with a `.suffix`); Store apps' IDs are
/// `Publisher.App_hash!App`. Firefox uses a hash of its install path, so it can't be matched.
pub fn matches_app(aumid: &str, name: &str) -> bool {
    let a = aumid.to_lowercase();
    let a = a.split('!').next().unwrap_or("");
    if name == "spotify" && a.contains("spotify") {
        return true;
    }
    let base = a.strip_suffix(".exe").unwrap_or(a);
    base == name || base.split(|c| c == '.' || c == '_').next() == Some(name)
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
