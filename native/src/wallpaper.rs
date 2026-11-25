//! Puts a window behind the desktop icons. Same technique as `Wallpaper/DesktopWallpaper.cs`:
//! send 0x052C to Progman; on Windows 11 24H2+ parent the window to Progman just below
//! SHELLDLL_DefView and make it layered (Progman only draws layered children); on older
//! Windows parent it to the WorkerW after the one holding SHELLDLL_DefView.
//! Unlike WPF, a plain Win32 window keeps WS_EX_LAYERED without any tricks.

use windows::core::{w, Result, PCWSTR};
use windows::Win32::Foundation::*;
use windows::Win32::UI::WindowsAndMessaging::*;

pub unsafe fn attach(hwnd: HWND, width: i32, height: i32) -> Result<()> {
    let progman = FindWindowW(w!("Progman"), PCWSTR::null())?;
    let mut result = 0usize;
    SendMessageTimeoutW(progman, 0x052C, WPARAM(0xD), LPARAM(1), SMTO_NORMAL, 1000, Some(&mut result));
    SendMessageTimeoutW(progman, 0x052C, WPARAM(0), LPARAM(0), SMTO_NORMAL, 1000, Some(&mut result));

    let def_view = FindWindowExW(progman, None, w!("SHELLDLL_DefView"), PCWSTR::null()).unwrap_or_default();
    let (parent, insert_after) = if !def_view.is_invalid() {
        (progman, def_view) // 24H2+ layout
    } else {
        let mut worker = HWND::default();
        let _ = EnumWindows(Some(find_worker), LPARAM(&mut worker as *mut HWND as isize));
        (worker, HWND_TOP)
    };

    let style = GetWindowLongPtrW(hwnd, GWL_STYLE);
    let style = (style & !(WS_POPUP.0 as isize | WS_CAPTION.0 as isize | WS_THICKFRAME.0 as isize)) | WS_CHILD.0 as isize;
    SetWindowLongPtrW(hwnd, GWL_STYLE, style);
    SetParent(hwnd, parent)?;

    if parent == progman {
        let ex = GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
        SetWindowLongPtrW(hwnd, GWL_EXSTYLE, ex | WS_EX_LAYERED.0 as isize);
        SetLayeredWindowAttributes(hwnd, COLORREF(0), 255, LWA_ALPHA)?;
    }

    let mut pr = RECT::default();
    GetWindowRect(parent, &mut pr)?;
    SetWindowPos(hwnd, insert_after, -pr.left, -pr.top, width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW)?;
    Ok(())
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

/// Re-applies the normal wallpaper so no frozen frame is left behind.
pub unsafe fn restore() {
    let mut path = [0u16; 520];
    if SystemParametersInfoW(SPI_GETDESKWALLPAPER, path.len() as u32, Some(path.as_mut_ptr() as *mut _), SYSTEM_PARAMETERS_INFO_UPDATE_FLAGS(0)).is_ok() {
        let _ = SystemParametersInfoW(SPI_SETDESKWALLPAPER, 0, Some(path.as_mut_ptr() as *mut _), SPIF_SENDCHANGE);
    }
}
