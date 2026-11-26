//! Puts a window behind the desktop icons, the same technique as `Wallpaper/DesktopWallpaper.cs`:
//! send 0x052C to Progman; on Windows 11 24H2+ parent the window to Progman just below
//! SHELLDLL_DefView and make it layered (Progman only draws layered children, and only for apps
//! whose manifest declares Windows 8+); on older Windows parent it to the WorkerW after the one
//! holding SHELLDLL_DefView. Unlike WPF, a plain Win32 window keeps WS_EX_LAYERED without tricks.
//!
//! Also: the list of screens (`Wallpaper/Screens.cs`), "is this screen covered by a maximized
//! window" (`Wallpaper/Occlusion.cs`), and what the normal desktop background looks like.

use windows::core::{w, PCWSTR};
use windows::Win32::Foundation::*;
use windows::Win32::Graphics::Dwm::*;
use windows::Win32::Graphics::Gdi::*;
use windows::Win32::UI::WindowsAndMessaging::*;

use crate::sys::from_wide;

#[derive(Clone, Copy, PartialEq, Eq, Debug, Default)]
pub struct Rect {
    pub x: i32,
    pub y: i32,
    pub w: i32,
    pub h: i32,
}

#[derive(Clone, Debug)]
pub struct Screen {
    pub device: String,
    pub primary: bool,
    pub bounds: Rect,
}

pub unsafe fn attach(hwnd: HWND, screen: Rect) -> std::result::Result<(), String> {
    let progman = FindWindowW(w!("Progman"), PCWSTR::null()).map_err(|_| "Couldn't find the Windows desktop (is Explorer running?).".to_string())?;
    let mut result = 0usize;
    SendMessageTimeoutW(progman, 0x052C, WPARAM(0xD), LPARAM(1), SMTO_NORMAL, 1000, Some(&mut result));
    SendMessageTimeoutW(progman, 0x052C, WPARAM(0), LPARAM(0), SMTO_NORMAL, 1000, Some(&mut result));

    let def_view = FindWindowExW(progman, None, w!("SHELLDLL_DefView"), PCWSTR::null()).unwrap_or_default();
    let (parent, insert_after) = if !def_view.is_invalid() {
        (progman, def_view) // 24H2+ layout
    } else {
        let mut worker = HWND::default();
        let _ = EnumWindows(Some(find_worker), LPARAM(&mut worker as *mut HWND as isize));
        if worker.is_invalid() {
            return Err("Couldn't find the desktop wallpaper layer.".into());
        }
        (worker, HWND_TOP)
    };

    let style = GetWindowLongPtrW(hwnd, GWL_STYLE);
    let style = (style & !(WS_POPUP.0 as isize | WS_CAPTION.0 as isize | WS_THICKFRAME.0 as isize)) | WS_CHILD.0 as isize;
    SetWindowLongPtrW(hwnd, GWL_STYLE, style);
    SetParent(hwnd, parent).map_err(|e| e.message())?;

    if parent == progman {
        let ex = GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
        SetWindowLongPtrW(hwnd, GWL_EXSTYLE, ex | WS_EX_LAYERED.0 as isize);
        SetLayeredWindowAttributes(hwnd, COLORREF(0), 255, LWA_ALPHA).map_err(|e| e.message())?;
        if GetWindowLongPtrW(hwnd, GWL_EXSTYLE) & WS_EX_LAYERED.0 as isize == 0 {
            return Err("Windows refused to make the wallpaper window layered.".into());
        }
    }
    place(hwnd, parent, insert_after, screen, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    Ok(())
}

/// Moves an attached window to cover a screen, re-asserting its place just below the icons
/// (on 24H2+ Explorer can raise its WorkerW above us, e.g. after a display change).
pub unsafe fn move_to(hwnd: HWND, screen: Rect) {
    let Ok(parent) = GetParent(hwnd) else { return };
    let def_view = FindWindowExW(parent, None, w!("SHELLDLL_DefView"), PCWSTR::null()).unwrap_or_default();
    let after = if def_view.is_invalid() { HWND_TOP } else { def_view };
    place(hwnd, parent, after, screen, SWP_NOACTIVATE);
}

unsafe fn place(hwnd: HWND, parent: HWND, after: HWND, s: Rect, flags: SET_WINDOW_POS_FLAGS) {
    // Child coordinates are relative to the parent, which spans the whole virtual screen.
    let mut pr = RECT::default();
    let _ = GetWindowRect(parent, &mut pr);
    let _ = SetWindowPos(hwnd, after, s.x - pr.left, s.y - pr.top, s.w, s.h, flags);
}

unsafe extern "system" fn find_worker(top: HWND, out: LPARAM) -> BOOL {
    if FindWindowExW(top, None, w!("SHELLDLL_DefView"), PCWSTR::null()).is_ok() {
        if let Ok(worker) = FindWindowExW(None, top, w!("WorkerW"), PCWSTR::null()) {
            *(out.0 as *mut HWND) = worker;
        }
        return FALSE;
    }
    TRUE
}

/// The current desktop wallpaper file, if it's a picture that exists.
pub fn desktop_wallpaper_path() -> Option<String> {
    let mut path = [0u16; 520];
    unsafe {
        SystemParametersInfoW(SPI_GETDESKWALLPAPER, path.len() as u32, Some(path.as_mut_ptr() as *mut _), SYSTEM_PARAMETERS_INFO_UPDATE_FLAGS(0)).ok()?;
    }
    let p = from_wide(&path);
    if !p.is_empty() && std::path::Path::new(&p).exists() {
        Some(p)
    } else {
        None
    }
}

/// Re-applies the normal wallpaper so no frozen frame is left behind.
/// Never call this while our wallpaper runs: Explorer raises its WorkerW above our window.
pub fn restore() {
    let mut path = [0u16; 520];
    unsafe {
        if SystemParametersInfoW(SPI_GETDESKWALLPAPER, path.len() as u32, Some(path.as_mut_ptr() as *mut _), SYSTEM_PARAMETERS_INFO_UPDATE_FLAGS(0)).is_ok() {
            let _ = SystemParametersInfoW(SPI_SETDESKWALLPAPER, 0, Some(path.as_mut_ptr() as *mut _), SPIF_SENDCHANGE);
        }
    }
}

/// How Windows fits the wallpaper picture (HKCU\Control Panel\Desktop).
#[derive(Clone, Copy, PartialEq)]
pub enum Fit {
    Fill,
    Fit,
    Stretch,
    Center,
}

pub fn desktop_fit() -> Fit {
    let style = crate::sys::reg_string(r"Control Panel\Desktop", "WallpaperStyle").unwrap_or_else(|| "10".into());
    let tile = crate::sys::reg_string(r"Control Panel\Desktop", "TileWallpaper").as_deref() == Some("1");
    match style.as_str() {
        "6" => Fit::Fit,
        "2" => Fit::Stretch,
        "0" if !tile => Fit::Center,
        _ => Fit::Fill,
    }
}

/// The desktop's solid background colour, as (r, g, b) 0..1.
pub fn desktop_color() -> (f32, f32, f32) {
    let c = unsafe { GetSysColor(COLOR_DESKTOP) };
    ((c & 0xFF) as f32 / 255.0, ((c >> 8) & 0xFF) as f32 / 255.0, ((c >> 16) & 0xFF) as f32 / 255.0)
}

// ------------------------------------------------------------------ screens

/// All monitors, left to right (then top to bottom), as `Screens.All()`.
pub fn screens() -> Vec<Screen> {
    let mut found: Vec<Screen> = Vec::new();
    unsafe {
        let _ = EnumDisplayMonitors(None, None, Some(monitor_proc), LPARAM(&mut found as *mut _ as isize));
    }
    found.sort_by_key(|s| (s.bounds.x, s.bounds.y));
    found
}

unsafe extern "system" fn monitor_proc(monitor: HMONITOR, _: HDC, _: *mut RECT, data: LPARAM) -> BOOL {
    let list = &mut *(data.0 as *mut Vec<Screen>);
    let mut info = MONITORINFOEXW::default();
    info.monitorInfo.cbSize = std::mem::size_of::<MONITORINFOEXW>() as u32;
    if GetMonitorInfoW(monitor, &mut info as *mut _ as *mut MONITORINFO).as_bool() {
        let r = info.monitorInfo.rcMonitor;
        list.push(Screen {
            device: from_wide(&info.szDevice),
            primary: info.monitorInfo.dwFlags & 1 != 0,
            bounds: Rect { x: r.left, y: r.top, w: r.right - r.left, h: r.bottom - r.top },
        });
    }
    TRUE
}

/// The screen with this device name, else the main screen.
pub fn find_screen(device: Option<&str>) -> Screen {
    let all = screens();
    all.iter()
        .find(|s| Some(s.device.as_str()) == device)
        .or_else(|| all.iter().find(|s| s.primary))
        .or(all.first())
        .cloned()
        .unwrap_or_else(|| unsafe {
            Screen {
                device: String::new(),
                primary: true,
                bounds: Rect { x: 0, y: 0, w: GetSystemMetrics(SM_CXSCREEN), h: GetSystemMetrics(SM_CYSCREEN) },
            }
        })
}

/// DPI of the monitor containing this rectangle (96 = 100%).
pub fn dpi_for(r: Rect) -> f32 {
    use windows::Win32::UI::HiDpi::{GetDpiForMonitor, MDT_EFFECTIVE_DPI};
    unsafe {
        let m = MonitorFromPoint(POINT { x: r.x + r.w / 2, y: r.y + r.h / 2 }, MONITOR_DEFAULTTOPRIMARY);
        let (mut x, mut y) = (96u32, 96u32);
        if GetDpiForMonitor(m, MDT_EFFECTIVE_DPI, &mut x, &mut y).is_ok() {
            x as f32
        } else {
            96.0
        }
    }
}

// ------------------------------------------------------------------ occlusion

struct OcclusionQuery {
    screen: Rect,
    self_pid: u32,
    covered: bool,
}

/// True if a visible, normal app window covers at least 90% of the screen. Ignores our own
/// windows (and the settings window), the shell, minimized and cloaked windows, and tool or
/// click-through overlays.
pub fn is_covered(screen: Rect) -> bool {
    if screen.w <= 0 || screen.h <= 0 {
        return false;
    }
    let mut q = OcclusionQuery { screen, self_pid: std::process::id(), covered: false };
    unsafe {
        let _ = EnumWindows(Some(occlusion_proc), LPARAM(&mut q as *mut _ as isize));
    }
    q.covered
}

unsafe extern "system" fn occlusion_proc(hwnd: HWND, data: LPARAM) -> BOOL {
    let q = &mut *(data.0 as *mut OcclusionQuery);
    if !IsWindowVisible(hwnd).as_bool() || IsIconic(hwnd).as_bool() {
        return TRUE;
    }
    let mut pid = 0u32;
    GetWindowThreadProcessId(hwnd, Some(&mut pid));
    if pid == q.self_pid {
        return TRUE;
    }
    let mut cloaked = 0i32;
    if DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, &mut cloaked as *mut _ as *mut _, 4).is_ok() && cloaked != 0 {
        return TRUE;
    }
    let ex = GetWindowLongPtrW(hwnd, GWL_EXSTYLE) as u32;
    if ex & (WS_EX_TOOLWINDOW.0 | WS_EX_TRANSPARENT.0 | WS_EX_NOACTIVATE.0) != 0 {
        return TRUE;
    }
    let mut cls = [0u16; 256];
    let n = GetClassNameW(hwnd, &mut cls) as usize;
    let class = String::from_utf16_lossy(&cls[..n]);
    const SHELL: [&str; 6] = ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "SHELLDLL_DefView", "Windows.UI.Core.CoreWindow"];
    if SHELL.iter().any(|s| s.eq_ignore_ascii_case(&class)) {
        return TRUE;
    }
    // The settings window is a separate process (WPF): its class is "HwndWrapper[MusicVisualizerSettings;...]".
    if class.starts_with("HwndWrapper[MusicVisualizerSettings") {
        return TRUE;
    }
    let mut r = RECT::default();
    if DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, &mut r as *mut _ as *mut _, std::mem::size_of::<RECT>() as u32).is_err()
        && GetWindowRect(hwnd, &mut r).is_err()
    {
        return TRUE;
    }
    let s = q.screen;
    let w = (r.right.min(s.x + s.w) - r.left.max(s.x)) as f64;
    let h = (r.bottom.min(s.y + s.h) - r.top.max(s.y)) as f64;
    if w > 0.0 && h > 0.0 && w * h >= s.w as f64 * s.h as f64 * 0.9 {
        q.covered = true;
        return FALSE;
    }
    TRUE
}
