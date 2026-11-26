//! The notification-area icon (`Core/TrayIcon.cs`), on its own thread with a hidden top-level
//! window, so the menu's modal loop never stalls drawing. That window also receives the
//! "TaskbarCreated" broadcast (Explorer restarted) and display changes, which are passed on.
//! Left-click opens settings; right-click shows Settings / Open visualizer / Wallpaper / Stop / Exit completely.

use std::sync::atomic::{AtomicBool, AtomicIsize, Ordering};
use std::sync::mpsc::Sender;
use std::sync::Mutex;

use windows::core::{w, PCWSTR};
use windows::Win32::Foundation::*;
use windows::Win32::System::LibraryLoader::*;
use windows::Win32::System::Threading::SetEvent;
use windows::Win32::UI::Shell::*;
use windows::Win32::UI::WindowsAndMessaging::*;

use crate::sys::wide;

#[derive(Debug, Clone, Copy)]
pub enum Command {
    Settings,
    Visualizer,
    ToggleWallpaper,
    Stop,
    Exit,
    ExplorerRestarted,
    DisplayChanged,
}

const WM_TRAY: u32 = WM_APP + 1;
const WM_SET_TIP: u32 = WM_APP + 2;
const ID_SETTINGS: usize = 1;
const ID_VISUALIZER: usize = 2;
const ID_WALLPAPER: usize = 3;
const ID_STOP: usize = 4;
const ID_EXIT: usize = 5;

static HWND_RAW: AtomicIsize = AtomicIsize::new(0);
static WALLPAPER_ON: AtomicBool = AtomicBool::new(false);
static TIP: Mutex<String> = Mutex::new(String::new());
static SINK: Mutex<Option<(Sender<Command>, isize)>> = Mutex::new(None);
static TASKBAR_CREATED: AtomicIsize = AtomicIsize::new(0);

pub struct Tray;

impl Tray {
    /// Starts the tray thread. Commands go to `sender`, and `wake` is set so the main loop notices.
    pub fn start(sender: Sender<Command>, wake: HANDLE) -> Tray {
        *SINK.lock().unwrap() = Some((sender, wake.0 as isize));
        *TIP.lock().unwrap() = "Music Visualizer".into();
        std::thread::Builder::new().name("tray".into()).stack_size(256 * 1024).spawn(run).ok();
        Tray
    }

    pub fn set_wallpaper_checked(&self, on: bool) {
        WALLPAPER_ON.store(on, Ordering::SeqCst);
    }

    pub fn set_tooltip(&self, text: &str) {
        *TIP.lock().unwrap() = text.chars().take(127).collect();
        let h = HWND_RAW.load(Ordering::SeqCst);
        if h != 0 {
            unsafe {
                let _ = PostMessageW(HWND(h as *mut _), WM_SET_TIP, WPARAM(0), LPARAM(0));
            }
        }
    }

    /// A notification balloon (`NotifyIcon.ShowBalloonTip`, no icon).
    pub fn balloon(&self, title: &str, text: &str) {
        let h = HWND_RAW.load(Ordering::SeqCst);
        if h == 0 {
            return;
        }
        let mut nid = base(HWND(h as *mut _));
        nid.uFlags = NIF_INFO;
        copy(&mut nid.szInfoTitle, title);
        copy(&mut nid.szInfo, text);
        nid.dwInfoFlags = NIIF_NONE;
        unsafe {
            let _ = Shell_NotifyIconW(NIM_MODIFY, &nid);
        }
    }
}

impl Drop for Tray {
    fn drop(&mut self) {
        let h = HWND_RAW.swap(0, Ordering::SeqCst);
        if h != 0 {
            unsafe {
                let nid = base(HWND(h as *mut _));
                let _ = Shell_NotifyIconW(NIM_DELETE, &nid);
                let _ = PostMessageW(HWND(h as *mut _), WM_CLOSE, WPARAM(0), LPARAM(0));
            }
        }
    }
}

fn copy<const N: usize>(dest: &mut [u16; N], s: &str) {
    let v: Vec<u16> = s.encode_utf16().take(N - 1).collect();
    dest[..v.len()].copy_from_slice(&v);
    dest[v.len()] = 0;
}

fn base(hwnd: HWND) -> NOTIFYICONDATAW {
    NOTIFYICONDATAW { cbSize: std::mem::size_of::<NOTIFYICONDATAW>() as u32, hWnd: hwnd, uID: 1, ..Default::default() }
}

fn send(c: Command) {
    if let Some((tx, wake)) = SINK.lock().unwrap().as_ref() {
        let _ = tx.send(c);
        unsafe {
            let _ = SetEvent(HANDLE(*wake as *mut _));
        }
    }
}

/// Dark menus like the rest of the app (uxtheme's undocumented SetPreferredAppMode, ordinal 135;
/// harmless where it doesn't exist).
fn dark_menus() {
    unsafe {
        if let Ok(ux) = LoadLibraryW(w!("uxtheme.dll")) {
            if let Some(f) = GetProcAddress(ux, windows::core::PCSTR(135 as *const u8)) {
                let set: extern "system" fn(i32) -> i32 = std::mem::transmute(f);
                set(2); // ForceDark
            }
            if let Some(f) = GetProcAddress(ux, windows::core::PCSTR(136 as *const u8)) {
                let flush: extern "system" fn() = std::mem::transmute(f);
                flush();
            }
        }
    }
}

fn add_icon(hwnd: HWND) {
    unsafe {
        let instance = GetModuleHandleW(None).unwrap_or_default();
        let (cx, cy) = (GetSystemMetrics(SM_CXSMICON), GetSystemMetrics(SM_CYSMICON));
        let icon = LoadImageW(instance, PCWSTR(1 as *const u16), IMAGE_ICON, cx, cy, LR_DEFAULTCOLOR)
            .map(|h| HICON(h.0))
            .unwrap_or_default();
        let mut nid = base(hwnd);
        nid.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        nid.uCallbackMessage = WM_TRAY;
        nid.hIcon = icon;
        copy(&mut nid.szTip, &TIP.lock().unwrap());
        let _ = Shell_NotifyIconW(NIM_ADD, &nid);
    }
}

fn run() {
    unsafe {
        dark_menus();
        let instance = GetModuleHandleW(None).unwrap_or_default();
        let class = w!("MusicVisualizerTray");
        let wc = WNDCLASSW { lpfnWndProc: Some(wndproc), hInstance: instance.into(), lpszClassName: class, ..Default::default() };
        RegisterClassW(&wc);
        TASKBAR_CREATED.store(RegisterWindowMessageW(w!("TaskbarCreated")) as isize, Ordering::SeqCst);
        // A hidden top-level window (not message-only: those don't get broadcasts).
        let Ok(hwnd) = CreateWindowExW(WINDOW_EX_STYLE(0), class, w!("Music Visualizer"), WS_POPUP, 0, 0, 0, 0, None, None, instance, None) else {
            return;
        };
        HWND_RAW.store(hwnd.0 as isize, Ordering::SeqCst);
        add_icon(hwnd);
        let mut msg = MSG::default();
        while GetMessageW(&mut msg, None, 0, 0).as_bool() {
            let _ = TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }
}

fn show_menu(hwnd: HWND) {
    unsafe {
        let Ok(menu) = CreatePopupMenu() else { return };
        let item = |id: usize, text: &str, flags: MENU_ITEM_FLAGS| {
            let t = wide(text);
            let _ = AppendMenuW(menu, flags, id, PCWSTR(t.as_ptr()));
        };
        item(ID_SETTINGS, "Settings", MF_STRING);
        item(ID_VISUALIZER, "Open visualizer", MF_STRING);
        item(ID_WALLPAPER, "Wallpaper", if WALLPAPER_ON.load(Ordering::SeqCst) { MF_STRING | MF_CHECKED } else { MF_STRING });
        let _ = AppendMenuW(menu, MF_SEPARATOR, 0, PCWSTR::null());
        // "Stop" turns everything off but keeps the icon here; "Exit completely" closes the app.
        item(ID_STOP, "Stop", MF_STRING);
        item(ID_EXIT, "Exit completely", MF_STRING);
        let mut p = POINT::default();
        let _ = GetCursorPos(&mut p);
        let _ = SetForegroundWindow(hwnd); // so the menu closes when clicking elsewhere
        let id = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN, p.x, p.y, 0, hwnd, None).0 as usize;
        let _ = PostMessageW(hwnd, WM_NULL, WPARAM(0), LPARAM(0));
        let _ = DestroyMenu(menu);
        match id {
            ID_SETTINGS => send(Command::Settings),
            ID_VISUALIZER => send(Command::Visualizer),
            ID_WALLPAPER => send(Command::ToggleWallpaper),
            ID_STOP => send(Command::Stop),
            ID_EXIT => send(Command::Exit),
            _ => {}
        }
    }
}

extern "system" fn wndproc(hwnd: HWND, msg: u32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
    unsafe {
        if msg == TASKBAR_CREATED.load(Ordering::SeqCst) as u32 && msg != 0 {
            add_icon(hwnd); // Explorer restarted: the icon has to be added again
            send(Command::ExplorerRestarted);
            return LRESULT(0);
        }
        match msg {
            WM_TRAY => {
                match (lparam.0 & 0xFFFF) as u32 {
                    WM_LBUTTONUP => send(Command::Settings),
                    WM_RBUTTONUP | WM_CONTEXTMENU => show_menu(hwnd),
                    _ => {}
                }
                LRESULT(0)
            }
            WM_SET_TIP => {
                let mut nid = base(hwnd);
                nid.uFlags = NIF_TIP;
                copy(&mut nid.szTip, &TIP.lock().unwrap());
                let _ = Shell_NotifyIconW(NIM_MODIFY, &nid);
                LRESULT(0)
            }
            WM_DISPLAYCHANGE => {
                send(Command::DisplayChanged);
                DefWindowProcW(hwnd, msg, wparam, lparam)
            }
            WM_CLOSE => {
                let _ = DestroyWindow(hwnd);
                LRESULT(0)
            }
            WM_DESTROY => {
                PostQuitMessage(0);
                LRESULT(0)
            }
            _ => DefWindowProcW(hwnd, msg, wparam, lparam),
        }
    }
}
