//! Music Visualizer's engine: the part that runs all day. It puts the visualizer behind the
//! desktop icons, captures audio, analyses it and draws it with Direct2D, and owns the tray icon.
//! The settings window and the optional visualizer window are the C# app
//! (`MusicVisualizerSettings.exe`), started only while they're open; both sides share
//! `%AppData%\MusicVisualizer\settings.json`, which the engine reloads whenever it changes.
//!
//! Command line (same switches as the C# app before 2.0):
//!   (none)        run as the wallpaper. If already running: start the wallpaper, or open settings
//!                 when it's already on.
//!   --settings    open settings (the "Music Visualizer Settings" Start-menu shortcut)
//!   --background  restore the saved state quietly in the tray (used by "Start with Windows")
//!   --wallpaper   same as no arguments
//!   --uninstall   remove the app (handed to the settings app, which does the work); add --quiet for no prompts

#![windows_subsystem = "windows"]
#![allow(non_snake_case)]

mod analysis;
mod audio;
mod imaging;
mod nowplaying;
mod render;
mod settings;
mod sys;
mod tray;
mod wallpaper;

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::{channel, Receiver};
use std::time::{Duration, Instant};

use serde_json::Value;
use windows::core::{w, PCWSTR};
use windows::Win32::Foundation::*;
use windows::Win32::Storage::FileSystem::*;
use windows::Win32::System::Com::*;
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::System::Threading::*;
use windows::Win32::UI::WindowsAndMessaging::*;

use audio::{Capture, Source};
use imaging::Rgb;
use nowplaying::{NowPlaying, Track};
use render::{Card, Desk, Frame, Renderer, Visual};
use settings::{CycleBy, ListenMode, Loaded, Mode, TitlePosition};
use sys::*;
use tray::{Command, Tray};
use wallpaper::Rect;

#[derive(PartialEq, Clone, Copy)]
enum Launch {
    Wallpaper,
    Settings,
    Background,
}

fn main() {
    std::panic::set_hook(Box::new(|info| log_error(&format!("panic: {info}"))));
    let args: Vec<String> = std::env::args().skip(1).collect();
    let has = |a: &str| args.iter().any(|x| x.eq_ignore_ascii_case(a));

    if has("--uninstall") {
        // The settings app (C#) does the uninstall: messages, registry, shortcuts and files.
        let helper = exe_dir().join(HELPER_EXE);
        let rest: Vec<&str> = args.iter().map(|s| s.as_str()).collect();
        if !spawn(&helper, &rest) {
            message_box_blocking("The uninstaller (MusicVisualizerSettings.exe) is missing. Remove the program folder by hand.");
        }
        return;
    }
    let launch = if has("--settings") {
        Launch::Settings
    } else if has("--background") {
        Launch::Background
    } else {
        Launch::Wallpaper
    };

    unsafe {
        // One copy only. A second launch passes its request to the running copy.
        let name = wide(INSTANCE_MUTEX);
        let mutex = CreateMutexW(None, true, PCWSTR(name.as_ptr())).unwrap_or_default();
        if GetLastError() == ERROR_ALREADY_EXISTS {
            match launch {
                Launch::Settings => {
                    signal(SHOW_EVENT);
                }
                Launch::Wallpaper => {
                    signal(WALLPAPER_EVENT);
                }
                Launch::Background => {}
            }
            return;
        }
        let _ = CoInitializeEx(None, COINIT_MULTITHREADED);

        let mut app = App::new();
        app.start(launch);
        app.run();
        app.shutdown();
        drop(app);
        let _ = ReleaseMutex(mutex);
        let _ = CloseHandle(mutex);
    }
}

fn message_box_blocking(text: &str) {
    let t = wide(text);
    let c = wide(APP_NAME);
    unsafe {
        MessageBoxW(None, PCWSTR(t.as_ptr()), PCWSTR(c.as_ptr()), MB_OK | MB_ICONWARNING);
    }
}

// ------------------------------------------------------------------ the wallpaper window

static CLOSING: AtomicBool = AtomicBool::new(false);
static DESTROYED: AtomicBool = AtomicBool::new(false);

extern "system" fn wallpaper_proc(hwnd: HWND, msg: u32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
    unsafe {
        match msg {
            WM_ERASEBKGND => LRESULT(1),
            WM_PAINT => {
                let mut ps = windows::Win32::Graphics::Gdi::PAINTSTRUCT::default();
                windows::Win32::Graphics::Gdi::BeginPaint(hwnd, &mut ps);
                let _ = windows::Win32::Graphics::Gdi::EndPaint(hwnd, &ps);
                LRESULT(0)
            }
            WM_DESTROY => {
                // Also happens when Explorer restarts (our parent window is destroyed).
                if !CLOSING.load(Ordering::SeqCst) {
                    DESTROYED.store(true, Ordering::SeqCst);
                }
                LRESULT(0)
            }
            _ => DefWindowProcW(hwnd, msg, wparam, lparam),
        }
    }
}

struct Wall {
    hwnd: HWND,
    renderer: Renderer,
    screen: Rect,
}

/// A value animated linearly from `from` to `to`.
#[derive(Clone, Copy)]
struct Anim {
    from: f32,
    to: f32,
    start: Instant,
    secs: f32,
}

impl Anim {
    fn value(&self, now: Instant) -> f32 {
        let t = (now.duration_since(self.start).as_secs_f32() / self.secs).clamp(0.0, 1.0);
        self.from + (self.to - self.from) * t
    }
    fn done(&self, now: Instant) -> bool {
        now.duration_since(self.start).as_secs_f32() >= self.secs
    }
}

struct App {
    loaded: Loaded,
    capture: Capture,
    visual: Visual,
    wall: Option<Wall>,
    tray: Tray,
    commands: Receiver<Command>,
    now_playing: NowPlaying,
    track: Option<Track>,
    album: Option<(Rgb, Rgb)>,

    // Named events from other processes, plus our own wake-up event and the settings folder watch.
    ev_show: HANDLE,
    ev_wallpaper: HANDLE,
    ev_quit: HANDLE,
    ev_stop: HANDLE,
    ev_wake: HANDLE,
    dir_watch: HANDLE,
    timer: HANDLE,

    next_tick: Instant,
    ticks: u64,
    next_frame: Instant,
    last_frame: Instant,
    reload_at: Option<Instant>,
    reattach: Option<(Instant, u32)>,

    // Pause when covered, quiet-time fade.
    covered: bool,
    idle: bool,
    idle_hidden: bool,
    desk: Option<Anim>,
    attached_at: Instant,
    paused_since: Option<Instant>,
    trimmed: bool,

    // Style cycle.
    cycle_since: Instant,
    songs_since_switch: i64,
    cycle_song_key: Option<String>,
    rng: u64,

    // Song-title card "peek" while it's hidden.
    card_song_key: Option<String>,
    peek_start: Option<Instant>,

    audio_error_shown: bool,
    quit: bool,
    /// Microseconds spent in the occlusion check and in reading audio while paused (MV_DEBUG_LOG).
    debug_times: [u64; 2],
}

impl App {
    fn new() -> App {
        let ev_wake = unsafe { CreateEventW(None, false, false, PCWSTR::null()).unwrap_or_default() };
        let (tx, rx) = channel();
        let tray = Tray::start(tx, ev_wake);
        let now_playing = NowPlaying::start(ev_wake);
        let _ = std::fs::create_dir_all(settings::data_dir());
        let dir = wide(&settings::data_dir().to_string_lossy());
        let dir_watch = unsafe {
            FindFirstChangeNotificationW(PCWSTR(dir.as_ptr()), false, FILE_NOTIFY_CHANGE_LAST_WRITE | FILE_NOTIFY_CHANGE_FILE_NAME | FILE_NOTIFY_CHANGE_SIZE)
                .unwrap_or(INVALID_HANDLE_VALUE)
        };
        let timer = unsafe {
            CreateWaitableTimerExW(None, None, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS.0)
                .or_else(|_| CreateWaitableTimerExW(None, None, 0, TIMER_ALL_ACCESS.0))
                .unwrap_or_default()
        };
        let now = Instant::now();
        let seed = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map(|d| d.as_nanos() as u64).unwrap_or(1) | 1;
        App {
            loaded: settings::load(),
            capture: Capture::new(),
            visual: Visual::new(),
            wall: None,
            tray,
            commands: rx,
            now_playing,
            track: None,
            album: None,
            ev_show: create_event(SHOW_EVENT),
            ev_wallpaper: create_event(WALLPAPER_EVENT),
            ev_quit: create_event(QUIT_EVENT),
            ev_stop: create_event(STOP_EVENT),
            ev_wake,
            dir_watch,
            timer,
            next_tick: now,
            ticks: 0,
            next_frame: now,
            last_frame: now,
            reload_at: None,
            reattach: None,
            covered: false,
            idle: false,
            idle_hidden: false,
            desk: None,
            attached_at: now,
            paused_since: None,
            trimmed: false,
            cycle_since: now,
            songs_since_switch: 0,
            cycle_song_key: None,
            rng: seed,
            card_song_key: None,
            peek_start: None,
            audio_error_shown: false,
            quit: false,
            debug_times: [0; 2],
        }
    }

    fn s(&self) -> &settings::Settings {
        &self.loaded.settings
    }

    fn start(&mut self, launch: Launch) {
        repair_startup_entry();
        if launch == Launch::Wallpaper && !self.s().wallpaper_enabled {
            self.patch(|m| {
                m.insert("WallpaperEnabled".into(), Value::Bool(true));
            });
        }
        if self.s().wallpaper_enabled {
            if self.try_attach().is_err() {
                self.start_reattach(); // Explorer may not be ready yet right after sign-in
            }
        }
        match launch {
            Launch::Settings => self.show_helper(false),
            Launch::Wallpaper => self.tray_hint_once(),
            Launch::Background => {}
        }
        self.update_tray();
    }

    // ------------------------------------------------------------ main loop

    fn running(&self) -> bool {
        self.wall.is_some() && !self.covered && !self.idle_hidden
    }

    fn run(&mut self) {
        while !self.quit {
            let now = Instant::now();
            let mut deadline = self.next_tick;
            if self.running() {
                deadline = deadline.min(self.next_frame);
            }
            if let Some(t) = self.reload_at {
                deadline = deadline.min(t);
            }
            if let Some((t, _)) = self.reattach {
                deadline = deadline.min(t);
            }
            let wait = deadline.saturating_duration_since(now);
            if !wait.is_zero() {
                unsafe {
                    let due = -((wait.as_nanos() / 100) as i64).max(1);
                    let _ = SetWaitableTimer(self.timer, &due, 0, None, None, false);
                    let mut handles = vec![self.timer, self.ev_show, self.ev_wallpaper, self.ev_quit, self.ev_stop, self.ev_wake];
                    if self.dir_watch != INVALID_HANDLE_VALUE {
                        handles.push(self.dir_watch);
                    }
                    let r = MsgWaitForMultipleObjects(Some(&handles), false, INFINITE, QS_ALLINPUT).0 as usize;
                    match r {
                        1 => self.show_helper(false),
                        2 => self.run_as_wallpaper(true),
                        3 => self.quit = true,
                        4 => self.stop(),
                        6 => {
                            let _ = FindNextChangeNotification(self.dir_watch);
                            self.reload_at = Some(Instant::now() + Duration::from_millis(150));
                        }
                        _ => {}
                    }
                }
            }
            self.pump();
            self.handle_commands();
            self.take_now_playing();
            if DESTROYED.swap(false, Ordering::SeqCst) {
                self.on_wallpaper_destroyed();
            }

            let now = Instant::now();
            if self.reload_at.map(|t| now >= t).unwrap_or(false) {
                self.reload_at = None;
                self.reload();
            }
            if self.reattach.map(|(t, _)| now >= t).unwrap_or(false) {
                self.retry_attach();
            }
            if now >= self.next_tick {
                self.next_tick = now + Duration::from_millis(250);
                self.tick(now);
            }
            if self.running() && Instant::now() >= self.next_frame {
                self.frame();
            }
        }
    }

    fn pump(&mut self) {
        unsafe {
            let mut msg = MSG::default();
            while PeekMessageW(&mut msg, None, 0, 0, PM_REMOVE).as_bool() {
                if msg.message == WM_QUIT {
                    self.quit = true;
                }
                let _ = TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }
        }
    }

    fn handle_commands(&mut self) {
        while let Ok(c) = self.commands.try_recv() {
            match c {
                Command::Settings => self.show_helper(false),
                Command::Visualizer => self.show_helper(true),
                Command::ToggleWallpaper => {
                    let on = self.wall.is_none();
                    self.set_wallpaper(on, true);
                }
                Command::Stop => self.stop(),
                Command::Exit => self.quit = true,
                Command::ExplorerRestarted => {
                    if self.s().wallpaper_enabled {
                        // Our old window went with Explorer (or is orphaned); attach a fresh one.
                        self.close_wall();
                        self.start_reattach();
                    }
                }
                Command::DisplayChanged => self.move_wall(),
            }
        }
    }

    fn frame(&mut self) {
        let now = Instant::now();
        let fps = self.s().look.frame_rate.clamp(15, 60) as f64;
        let period = Duration::from_secs_f64(1.0 / fps);
        self.next_frame += period;
        if self.next_frame < now {
            self.next_frame = now + period;
        }
        let dt = now.duration_since(self.last_frame).as_secs_f64().clamp(0.0, 0.1);
        self.last_frame = now;

        self.capture.poll(now);
        self.capture.samples(&mut self.visual.analyzer.samples, now);
        let rate = self.capture.sample_rate();
        let look = self.loaded.settings.look.clone();
        self.visual.tick(&look, rate, dt, self.album);
        self.update_desk(now);

        let card_state = self.card_state(now);
        let card = card_state.and_then(|(place, opacity)| self.track.as_ref().map(|track| Card { track, place, opacity }));
        let desk = self.desk.map(|a| a.value(now)).unwrap_or(0.0);
        let mode = self.loaded.settings.mode();
        let Some(wall) = self.wall.as_mut() else { return };
        let frame = Frame { mode, look: &look, visual: &self.visual, track: self.track.as_ref(), card, desk, now };
        if let Err(e) = wall.renderer.draw(&frame) {
            log_error(&format!("draw failed: {}", e.message()));
            wall.renderer.release();
        }
    }

    /// What the now-playing card shows this frame (`NowPlayingCard.Place` / peek).
    fn card_state(&mut self, now: Instant) -> Option<(TitlePosition, f32)> {
        self.track.as_ref()?;
        let look = &self.loaded.settings.look;
        if look.title != TitlePosition::Hidden {
            let place = look.title;
            self.peek_start = None;
            return Some((place, 1.0));
        }
        let start = self.peek_start?;
        let t = now.duration_since(start).as_secs_f32();
        let opacity = if t < 0.4 {
            t / 0.4
        } else if t < 6.0 {
            1.0
        } else if t < 6.6 {
            1.0 - (t - 6.0) / 0.6
        } else {
            self.peek_start = None;
            return None;
        };
        Some((TitlePosition::BottomRight, opacity))
    }

    fn tick(&mut self, now: Instant) {
        self.ticks += 1;
        self.debug_log(now);
        self.now_playing.set_slow(!self.running());
        // Now playing follows what's being listened to: with chosen apps, the one that made sound
        // most recently first, then the others in the order they were chosen.
        let prefer = if self.s().listen == ListenMode::SelectedApps {
            let mut order: Vec<String> = self.capture.app_sound_times(now).into_iter()
                .filter(|(_, t)| *t < f64::MAX)
                .map(|(n, _)| n)
                .collect();
            for a in &self.s().apps {
                let a = a.to_lowercase();
                if !order.contains(&a) {
                    order.push(a);
                }
            }
            Some(order)
        } else {
            None
        };
        self.now_playing.set_prefer(prefer);
        if self.wall.is_none() {
            return;
        }
        if !self.running() {
            let t0 = Instant::now();
            self.capture.poll(now); // keep listening, to notice when the music comes back
            self.debug_times[1] += t0.elapsed().as_micros() as u64;
        }

        // Once a second: is the wallpaper's screen hidden behind a maximized / full-screen window?
        if self.ticks % 4 == 0 {
            let screen = self.wall.as_ref().unwrap().screen;
            let t0 = Instant::now();
            let covered = self.s().pause_when_covered && wallpaper::is_covered(screen);
            self.debug_times[0] += t0.elapsed().as_micros() as u64;
            if covered != self.covered {
                self.covered = covered;
                self.resume_clock();
            }
        }

        // After 30 s of not drawing, hand memory and GPU resources back (once per pause).
        if !self.running() {
            let since = *self.paused_since.get_or_insert(now);
            if !self.trimmed && now.duration_since(since) > Duration::from_secs(30) {
                if let Some(w) = self.wall.as_mut() {
                    w.renderer.release();
                }
                trim_memory();
                self.trimmed = true;
            }
        } else {
            self.paused_since = None;
            self.trimmed = false;
        }

        // Quiet-time fade. Give capture a moment after attaching, so it doesn't fade out before the first sound.
        let quiet = self.capture.seconds_since_sound(now).min(now.duration_since(self.attached_at).as_secs_f64());
        let (hide, secs) = (self.s().hide_when_silent, self.s().silent_seconds.clamp(1.0, 120.0));
        if !hide {
            self.set_idle(false);
        } else if !self.idle && quiet >= secs {
            self.set_idle(true);
        } else if self.idle && quiet < 0.3 {
            self.set_idle(false);
        }
        self.update_desk(now);

        // Style cycle by time.
        let s = self.s();
        if s.cycle_enabled && s.cycle_by == CycleBy::Time && s.cycle_list().len() >= 2 {
            if now.duration_since(self.cycle_since).as_secs_f64() >= s.cycle_seconds.clamp(5, 24 * 3600) as f64 {
                self.next_cycle_style();
            }
        }
    }

    /// For testing: with MV_DEBUG_LOG=<file>, writes the engine's state every 2 seconds.
    fn debug_log(&mut self, now: Instant) {
        if self.ticks % 8 != 0 {
            return;
        }
        let Some(path) = std::env::var_os("MV_DEBUG_LOG") else { return };
        let times = std::mem::take(&mut self.debug_times);
        use std::io::Write;
        if let Ok(mut f) = std::fs::OpenOptions::new().create(true).append(true).open(path) {
            let _ = writeln!(
                f,
                "cover_us={} poll_us={} t={:.0} wall={} running={} covered={} idle={} hidden={} sound={:.1}s mode={} bass={:.2} track={:?} art={}",
                times[0],
                times[1],
                now.duration_since(self.attached_at).as_secs_f64(),
                self.wall.is_some(),
                self.running(),
                self.covered,
                self.idle,
                self.idle_hidden,
                self.capture.seconds_since_sound(now).min(9999.0),
                self.s().mode,
                self.visual.analyzer.bass,
                self.track.as_ref().map(|t| t.title.clone()),
                self.track.as_ref().map(|t| t.art.is_some()).unwrap_or(false),
            );
        }
    }

    fn resume_clock(&mut self) {
        let now = Instant::now();
        self.last_frame = now; // no catch-up jump after a pause
        self.next_frame = now;
    }

    // ------------------------------------------------------------ quiet time

    /// Going idle fades a copy of the normal desktop background in over 1.2 s and then hides the
    /// window, so the real wallpaper takes over with no visible jump. Waking shows the window with
    /// that copy on top and fades it out over 0.4 s.
    fn set_idle(&mut self, idle: bool) {
        if idle == self.idle {
            return;
        }
        self.idle = idle;
        let now = Instant::now();
        let Some(wall) = self.wall.as_mut() else { return };
        if idle {
            let screen_w = wall.screen.w.max(640) as u32;
            let picture = wallpaper::desktop_wallpaper_path().and_then(|p| imaging::load_file_width(&p, screen_w));
            wall.renderer.set_desk(Some(Desk { color: wallpaper::desktop_color(), picture, fit: wallpaper::desktop_fit() }));
            let from = self.desk.map(|a| a.value(now)).unwrap_or(0.0);
            self.desk = Some(Anim { from, to: 1.0, start: now, secs: 1.2 });
        } else {
            self.idle_hidden = false;
            unsafe {
                let _ = ShowWindow(wall.hwnd, SW_SHOWNOACTIVATE);
                // Back under the icons, above Explorer's own wallpaper layer.
                wallpaper::move_to(wall.hwnd, wall.screen);
            }
            self.desk = Some(Anim { from: 1.0, to: 0.0, start: now, secs: 0.4 });
            self.resume_clock();
        }
    }

    fn update_desk(&mut self, now: Instant) {
        let Some(a) = self.desk else { return };
        if !a.done(now) {
            return;
        }
        if a.to >= 1.0 && self.idle && !self.idle_hidden {
            // Faded in: draw that last frame and hide.
            if let Some(wall) = self.wall.as_mut() {
                self.idle_hidden = true;
                unsafe {
                    let _ = ShowWindow(wall.hwnd, SW_HIDE);
                }
            }
        } else if a.to <= 0.0 && !self.idle {
            self.desk = None;
            if let Some(wall) = self.wall.as_mut() {
                wall.renderer.set_desk(None); // free the decoded picture
            }
        }
    }

    // ------------------------------------------------------------ now playing

    fn take_now_playing(&mut self) {
        let (track, colors) = {
            let mut s = self.now_playing.shared.lock().unwrap();
            if !s.changed {
                return;
            }
            s.changed = false;
            (s.track.clone(), s.colors)
        };
        self.track = track;
        if colors.is_some() {
            self.album = colors;
        }
        let Some(t) = &self.track else { return };
        let key = t.key();

        // A different song (not the first one seen) while the card is hidden: peek.
        if self.card_song_key.as_deref() != Some(&key) {
            let look = &self.loaded.settings.look;
            if self.card_song_key.is_some() && look.title == TitlePosition::Hidden && look.peek_card {
                self.peek_start = Some(Instant::now());
            }
            self.card_song_key = Some(key.clone());
        }

        // Style cycle by songs.
        if self.cycle_song_key.as_deref() != Some(&key) {
            let first = self.cycle_song_key.is_none();
            self.cycle_song_key = Some(key);
            let (enabled, by, songs) = (self.s().cycle_enabled, self.s().cycle_by, self.s().cycle_songs);
            if !first && enabled && by == CycleBy::Songs {
                self.songs_since_switch += 1;
                if self.songs_since_switch >= songs.clamp(1, 100) {
                    self.next_cycle_style();
                }
            }
        }
    }

    // ------------------------------------------------------------ style cycle

    fn reset_cycle(&mut self) {
        self.cycle_since = Instant::now();
        self.songs_since_switch = 0;
    }

    /// Moves to the next chosen style (in order, or at random without repeating the current one).
    fn next_cycle_style(&mut self) {
        let list = self.s().cycle_list();
        self.reset_cycle();
        if list.len() < 2 {
            return;
        }
        let current = self.s().mode();
        let next = if self.s().cycle_shuffle {
            let others: Vec<Mode> = list.iter().copied().filter(|&m| m != current).collect();
            self.rng ^= self.rng << 13;
            self.rng ^= self.rng >> 7;
            self.rng ^= self.rng << 17;
            others[(self.rng % others.len() as u64) as usize]
        } else {
            match list.iter().position(|&m| m == current) {
                Some(i) => list[(i + 1) % list.len()],
                None => list[0],
            }
        };
        self.patch(|m| settings::switch_mode(m, next));
    }

    // ------------------------------------------------------------ settings

    /// Saves changes to some values; the rest of the file is left exactly as the settings window wrote it.
    fn patch(&mut self, edit: impl FnOnce(&mut serde_json::Map<String, Value>)) {
        if let Some(l) = settings::patch(edit) {
            self.loaded = l;
        }
    }

    /// settings.json changed: apply whatever the settings window changed.
    fn reload(&mut self) {
        let Some(new) = settings::read_current() else {
            // Missing or mid-write: try again shortly.
            if std::path::Path::new(&settings::file_path()).exists() {
                self.reload_at = Some(Instant::now() + Duration::from_millis(300));
            }
            return;
        };
        if new.text == self.loaded.text {
            return; // our own write, or no real change
        }
        let old = std::mem::replace(&mut self.loaded, new);
        let (o, n) = (&old.settings, &self.loaded.settings);

        let cycle_changed = o.cycle_enabled != n.cycle_enabled
            || o.cycle_modes != n.cycle_modes
            || o.cycle_by != n.cycle_by
            || o.cycle_seconds != n.cycle_seconds
            || o.cycle_songs != n.cycle_songs
            || o.cycle_shuffle != n.cycle_shuffle;
        let mode_changed = o.mode() != n.mode();
        let title_shown = n.look.title != TitlePosition::Hidden;
        let listen_changed = o.listen != n.listen || o.apps != n.apps;
        let screen_changed = o.wallpaper_screen != n.wallpaper_screen;
        let wallpaper = n.wallpaper_enabled;
        if cycle_changed || mode_changed {
            self.reset_cycle(); // a style picked by hand gets a full turn
        }
        if title_shown {
            self.peek_start = None;
        }

        if wallpaper && self.wall.is_none() && self.reattach.is_none() {
            if let Err(e) = self.try_attach() {
                self.patch(|m| {
                    m.insert("WallpaperEnabled".into(), Value::Bool(false));
                });
                message_box(format!("Couldn't attach to the desktop:\n\n{e}"));
            } else {
                self.tray_hint_once();
            }
        } else if !wallpaper && (self.wall.is_some() || self.reattach.is_some()) {
            self.reattach = None;
            if self.wall.is_some() {
                self.close_wall();
                wallpaper::restore();
            }
        } else {
            if screen_changed {
                self.move_wall();
            }
            if listen_changed {
                self.update_audio(true);
            }
        }
        self.update_tray();
    }

    // ------------------------------------------------------------ wallpaper

    fn try_attach(&mut self) -> Result<(), String> {
        unsafe {
            let instance = GetModuleHandleW(None).map_err(|e| e.message())?;
            let class = w!("MusicVisualizerWallpaper");
            let wc = WNDCLASSW { lpfnWndProc: Some(wallpaper_proc), hInstance: instance.into(), lpszClassName: class, ..Default::default() };
            RegisterClassW(&wc); // fails harmlessly when already registered
            let screen = wallpaper::find_screen(self.s().wallpaper_screen.as_deref()).bounds;
            let hwnd = CreateWindowExW(
                WS_EX_NOACTIVATE,
                class,
                w!("Music Visualizer Wallpaper"),
                WS_POPUP,
                -20000,
                -20000,
                screen.w,
                screen.h,
                None,
                None,
                instance,
                None,
            )
            .map_err(|e| e.message())?;
            if let Err(e) = wallpaper::attach(hwnd, screen) {
                CLOSING.store(true, Ordering::SeqCst);
                let _ = DestroyWindow(hwnd);
                CLOSING.store(false, Ordering::SeqCst);
                return Err(e);
            }
            let renderer = Renderer::new(hwnd, (screen.w as u32, screen.h as u32), wallpaper::dpi_for(screen)).map_err(|e| e.message())?;
            self.wall = Some(Wall { hwnd, renderer, screen });
        }
        let now = Instant::now();
        self.attached_at = now;
        self.idle = false;
        self.idle_hidden = false;
        self.covered = false;
        self.desk = None;
        self.reattach = None;
        self.resume_clock();
        self.update_audio(false);
        self.update_tray();
        Ok(())
    }

    fn close_wall(&mut self) {
        if let Some(w) = self.wall.take() {
            drop(w.renderer);
            CLOSING.store(true, Ordering::SeqCst);
            unsafe {
                let _ = DestroyWindow(w.hwnd);
            }
            CLOSING.store(false, Ordering::SeqCst);
        }
        self.update_audio(false);
        self.update_tray();
    }

    fn on_wallpaper_destroyed(&mut self) {
        if let Some(w) = self.wall.take() {
            drop(w.renderer);
        }
        self.update_audio(false);
        self.update_tray();
        if self.s().wallpaper_enabled {
            self.start_reattach();
        }
    }

    fn start_reattach(&mut self) {
        self.reattach = Some((Instant::now() + Duration::from_secs(3), 0));
    }

    fn retry_attach(&mut self) {
        let Some((_, attempts)) = self.reattach else { return };
        if !self.s().wallpaper_enabled || self.wall.is_some() || attempts >= 40 {
            self.reattach = None;
            return;
        }
        if self.try_attach().is_ok() {
            self.reattach = None;
        } else {
            self.reattach = Some((Instant::now() + Duration::from_secs(3), attempts + 1));
        }
    }

    /// Monitors plugged in or out, resolution or scaling changed, or another screen chosen.
    fn move_wall(&mut self) {
        let screen = wallpaper::find_screen(self.s().wallpaper_screen.as_deref()).bounds;
        if let Some(w) = self.wall.as_mut() {
            w.screen = screen;
            unsafe { wallpaper::move_to(w.hwnd, screen) };
            w.renderer.resize((screen.w as u32, screen.h as u32), wallpaper::dpi_for(screen));
        }
    }

    fn set_wallpaper(&mut self, on: bool, show_errors: bool) {
        self.patch(|m| {
            m.insert("WallpaperEnabled".into(), Value::Bool(on));
        });
        self.reattach = None;
        if on && self.wall.is_none() {
            if let Err(e) = self.try_attach() {
                self.patch(|m| {
                    m.insert("WallpaperEnabled".into(), Value::Bool(false));
                });
                if show_errors {
                    message_box(format!("Couldn't attach to the desktop:\n\n{e}"));
                }
            }
        } else if !on && self.wall.is_some() {
            self.close_wall();
            wallpaper::restore();
        }
        self.update_tray();
    }

    /// The main action: run as the wallpaper. If it's already on, open settings instead, so
    /// starting the app again always does something visible.
    fn run_as_wallpaper(&mut self, open_settings_if_running: bool) {
        if self.wall.is_some() {
            if open_settings_if_running {
                self.show_helper(false);
            }
            return;
        }
        self.set_wallpaper(true, true);
        if self.wall.is_some() {
            self.tray_hint_once();
        }
    }

    // ------------------------------------------------------------ audio

    /// Live audio runs only while the wallpaper exists. `restart` re-creates it (listen settings changed).
    fn update_audio(&mut self, restart: bool) {
        let wanted = if self.wall.is_none() {
            Source::None
        } else if self.s().listen == ListenMode::SelectedApps {
            if self.s().apps.is_empty() {
                Source::None
            } else {
                Source::Apps(self.s().apps.clone())
            }
        } else {
            Source::AllAudio
        };
        if restart {
            self.capture.stop();
        }
        if let Err(e) = self.capture.set_source(wanted) {
            if !self.audio_error_shown {
                self.audio_error_shown = true;
                message_box(format!("Couldn't capture audio:\n\n{e}"));
            }
        }
    }

    // ------------------------------------------------------------ helper app, tray

    /// Opens the settings window (or the visualizer window): the C# app, started on demand.
    fn show_helper(&mut self, visualizer: bool) {
        let event = if visualizer { HELPER_VISUALIZER_EVENT } else { HELPER_SETTINGS_EVENT };
        if signal(event) {
            return; // already open: it comes to the front
        }
        let helper = exe_dir().join(HELPER_EXE);
        let arg = if visualizer { "--visualizer" } else { "--settings" };
        if !helper.exists() || !spawn(&helper, &[arg]) {
            message_box(format!("Couldn't open {}: {} is missing. Reinstall Music Visualizer.", if visualizer { "the visualizer" } else { "settings" }, HELPER_EXE));
        }
    }

    fn tray_hint_once(&mut self) {
        if self.s().tray_hint_shown {
            return;
        }
        self.patch(|m| {
            m.insert("TrayHintShown".into(), Value::Bool(true));
        });
        let title = if self.wall.is_some() { "Music Visualizer is your wallpaper now" } else { "Music Visualizer is still running" };
        self.tray.balloon(title, "It lives in the system tray. Click the icon to change how it looks, or right-click to quit.");
    }

    fn update_tray(&mut self) {
        let on = self.wall.is_some();
        self.tray.set_wallpaper_checked(on);
        self.tray.set_tooltip(if on { "Music Visualizer: wallpaper on" } else { "Music Visualizer" });
    }

    /// Turns everything off (wallpaper, windows, audio) but stays in the tray, so it can start
    /// again with one click.
    fn stop(&mut self) {
        self.set_wallpaper(false, false);
        signal(HELPER_QUIT_EVENT);
        self.capture.stop();
        self.tray.set_tooltip("Music Visualizer: stopped (click to start again)");
        self.tray.balloon("Music Visualizer stopped", "It's still in the tray. Click the icon to start it again, or right-click → Exit completely to close it.");
        trim_memory();
    }

    /// Exit: put the normal wallpaper back and close the settings window if it's open.
    fn shutdown(&mut self) {
        signal(HELPER_QUIT_EVENT);
        if self.wall.is_some() {
            self.close_wall();
            wallpaper::restore();
        }
        self.capture.stop();
    }
}
