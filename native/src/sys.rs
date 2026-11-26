//! Small Win32 helpers: wide strings, named events, registry, message boxes, error log.

use std::path::PathBuf;

use windows::core::PCWSTR;
use windows::Win32::Foundation::*;
use windows::Win32::System::Registry::*;
use windows::Win32::System::Threading::*;
use windows::Win32::UI::WindowsAndMessaging::*;

// Names shared with the C# app and Setup (`Shared/InstallInfo.cs`). Keep them stable.
pub const APP_NAME: &str = "Music Visualizer";
pub const INSTANCE_MUTEX: &str = r"Local\MusicVisualizer.Instance";
pub const SHOW_EVENT: &str = r"Local\MusicVisualizer.Show";
pub const QUIT_EVENT: &str = r"Local\MusicVisualizer.Quit";
pub const WALLPAPER_EVENT: &str = r"Local\MusicVisualizer.Wallpaper";
pub const STOP_EVENT: &str = r"Local\MusicVisualizer.Stop";
pub const HELPER_SETTINGS_EVENT: &str = r"Local\MusicVisualizer.Helper.Settings";
pub const HELPER_VISUALIZER_EVENT: &str = r"Local\MusicVisualizer.Helper.Visualizer";
pub const HELPER_QUIT_EVENT: &str = r"Local\MusicVisualizer.Helper.Quit";
pub const HELPER_EXE: &str = "MusicVisualizerSettings.exe";
pub const RUN_KEY: &str = r"Software\Microsoft\Windows\CurrentVersion\Run";
pub const RUN_VALUE: &str = "MusicVisualizer";

pub fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

pub fn from_wide(buf: &[u16]) -> String {
    let end = buf.iter().position(|&c| c == 0).unwrap_or(buf.len());
    String::from_utf16_lossy(&buf[..end])
}

/// An auto-reset named event (created, or opened if it already exists).
pub fn create_event(name: &str) -> HANDLE {
    let n = wide(name);
    unsafe { CreateEventW(None, false, false, PCWSTR(n.as_ptr())).unwrap_or_default() }
}

/// Sets a named event another process created. False if no one has created it.
pub fn signal(name: &str) -> bool {
    let n = wide(name);
    unsafe {
        match OpenEventW(EVENT_MODIFY_STATE, false, PCWSTR(n.as_ptr())) {
            Ok(h) => {
                let ok = SetEvent(h).is_ok();
                let _ = CloseHandle(h);
                ok
            }
            Err(_) => false,
        }
    }
}

pub fn exe_path() -> PathBuf {
    std::env::current_exe().unwrap_or_else(|_| PathBuf::from("MusicVisualizer.exe"))
}

pub fn exe_dir() -> PathBuf {
    exe_path().parent().map(PathBuf::from).unwrap_or_else(|| PathBuf::from("."))
}

/// Shows a message box on its own thread, so drawing and the tray keep running behind it.
pub fn message_box(text: String) {
    std::thread::spawn(move || {
        let t = wide(&text);
        let c = wide(APP_NAME);
        unsafe {
            MessageBoxW(None, PCWSTR(t.as_ptr()), PCWSTR(c.as_ptr()), MB_OK | MB_ICONWARNING | MB_SETFOREGROUND);
        }
    });
}

/// Appends to %AppData%\MusicVisualizer\error.log (the same file the C# app uses).
pub fn log_error(text: &str) {
    use std::io::Write;
    let dir = crate::settings::data_dir();
    let _ = std::fs::create_dir_all(&dir);
    if let Ok(mut f) = std::fs::OpenOptions::new().create(true).append(true).open(dir.join("error.log")) {
        let secs = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map(|d| d.as_secs()).unwrap_or(0);
        let _ = writeln!(f, "engine, unix time {secs}\n{text}\n");
    }
}

fn reg_read(key: HKEY, sub: &str, value: &str) -> Option<String> {
    let (s, v) = (wide(sub), wide(value));
    let mut buf = vec![0u16; 2048];
    let mut size = (buf.len() * 2) as u32;
    unsafe {
        RegGetValueW(key, PCWSTR(s.as_ptr()), PCWSTR(v.as_ptr()), RRF_RT_REG_SZ, None, Some(buf.as_mut_ptr() as *mut _), Some(&mut size)).ok().ok()?;
    }
    Some(from_wide(&buf))
}

pub fn reg_string(sub: &str, value: &str) -> Option<String> {
    reg_read(HKEY_CURRENT_USER, sub, value)
}

/// `StartWithWindows.Repair`: if the startup entry exists but points at another copy of the exe, fix it.
pub fn repair_startup_entry() {
    let Some(current) = reg_string(RUN_KEY, RUN_VALUE) else { return };
    let wanted = format!("\"{}\" --background", exe_path().display());
    if current == wanted {
        return;
    }
    let (k, v, data) = (wide(RUN_KEY), wide(RUN_VALUE), wide(&wanted));
    unsafe {
        let mut key = HKEY::default();
        if RegOpenKeyExW(HKEY_CURRENT_USER, PCWSTR(k.as_ptr()), 0, KEY_SET_VALUE, &mut key).is_ok() {
            let bytes = std::slice::from_raw_parts(data.as_ptr() as *const u8, data.len() * 2);
            let _ = RegSetValueExW(key, PCWSTR(v.as_ptr()), 0, REG_SZ, Some(bytes));
            let _ = RegCloseKey(key);
        }
    }
}

/// Hands memory the process isn't using right now back to Windows (pages come back on demand).
pub fn trim_memory() {
    unsafe {
        let _ = SetProcessWorkingSetSize(GetCurrentProcess(), usize::MAX, usize::MAX);
    }
}

/// Starts a program without waiting for it.
pub fn spawn(exe: &PathBuf, args: &[&str]) -> bool {
    std::process::Command::new(exe).args(args).current_dir(exe_dir()).spawn().is_ok()
}
