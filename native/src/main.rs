//! Prototype: the Music Visualizer live wallpaper, rendered natively.
//!
//! Same pipeline as the C# app, minus everything but the essentials:
//!   WASAPI loopback capture -> 2048-sample ring -> Hann + FFT -> 96 log bands (40 Hz..16 kHz,
//!   +3 dB/oct tilt, -72..-12 dB, attack/release smoothing) -> Direct2D drawing at 30 fps
//!   into a window placed behind the desktop icons.
//!
//! Usage:  mv-native.exe [bars|wave] [seconds]
//! It runs for `seconds` (default 30) and then removes itself and re-applies the normal wallpaper.

#![windows_subsystem = "windows"]

mod analysis;
mod audio;
mod wallpaper;

use std::time::{Duration, Instant};

use windows::core::{w, Result};
use windows::Win32::Foundation::*;
use windows::Win32::Graphics::Direct2D::Common::*;
use windows::Win32::Graphics::Direct2D::*;
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::System::Threading::*;
use windows::Win32::UI::HiDpi::*;
use windows::Win32::UI::WindowsAndMessaging::*;

const FPS: u32 = 30;

#[derive(Clone, Copy, PartialEq)]
enum Style {
    Bars,
    Wave,
}

fn main() -> Result<()> {
    let args: Vec<String> = std::env::args().collect();
    let style = if args.iter().any(|a| a == "wave") { Style::Wave } else { Style::Bars };
    let seconds: u64 = args.iter().filter_map(|a| a.parse().ok()).next().unwrap_or(30);

    unsafe {
        let _ = SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        windows::Win32::System::Com::CoInitializeEx(None, windows::Win32::System::Com::COINIT_MULTITHREADED).ok()?;

        // ---- window behind the desktop icons
        let instance = GetModuleHandleW(None)?;
        let class = w!("MusicVisualizerNative");
        let wc = WNDCLASSW {
            lpfnWndProc: Some(wndproc),
            hInstance: instance.into(),
            lpszClassName: class,
            ..Default::default()
        };
        RegisterClassW(&wc);
        let (width, height) = (GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN));
        let hwnd = CreateWindowExW(
            WINDOW_EX_STYLE(0), class, w!("Music Visualizer (native prototype)"), WS_POPUP,
            -20000, -20000, width, height, None, None, instance, None)?;
        wallpaper::attach(hwnd, width, height)?;

        // ---- Direct2D
        let factory: ID2D1Factory = D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, None)?;
        let target = factory.CreateHwndRenderTarget(
            &D2D1_RENDER_TARGET_PROPERTIES::default(),
            &D2D1_HWND_RENDER_TARGET_PROPERTIES {
                hwnd,
                pixelSize: D2D_SIZE_U { width: width as u32, height: height as u32 },
                presentOptions: D2D1_PRESENT_OPTIONS_NONE,
            },
        )?;
        let palette: Vec<ID2D1SolidColorBrush> = (0..analysis::BANDS)
            .map(|i| {
                let (r, g, b) = hsv(270.0 - 200.0 * i as f32 / analysis::BANDS as f32, 0.75, 1.0);
                target.CreateSolidColorBrush(&D2D1_COLOR_F { r, g, b, a: 1.0 }, None).unwrap()
            })
            .collect();
        let faded: Vec<ID2D1SolidColorBrush> = (0..analysis::BANDS)
            .map(|i| {
                let (r, g, b) = hsv(270.0 - 200.0 * i as f32 / analysis::BANDS as f32, 0.75, 1.0);
                target.CreateSolidColorBrush(&D2D1_COLOR_F { r, g, b, a: 0.16 }, None).unwrap()
            })
            .collect();

        // ---- audio
        let mut capture = audio::Loopback::start()?;
        let mut analyzer = analysis::Analyzer::new();

        // ---- 30 fps loop: a high-resolution waitable timer, so we sleep precisely between
        // frames without raising the system timer resolution, while still pumping messages.
        let timer = CreateWaitableTimerExW(None, None, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS.0)?;
        let period = Duration::from_secs_f64(1.0 / FPS as f64);
        let start = Instant::now();
        let mut last = Instant::now();
        let mut next = Instant::now();

        while start.elapsed() < Duration::from_secs(seconds) {
            // Pump window messages.
            let mut msg = MSG::default();
            while PeekMessageW(&mut msg, None, 0, 0, PM_REMOVE).as_bool() {
                let _ = TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }

            let now = Instant::now();
            let dt = (now - last).as_secs_f32().clamp(0.0, 0.1);
            last = now;

            capture.read(&mut analyzer)?;
            analyzer.update(capture.sample_rate, dt);
            draw(&target, &factory, style, &analyzer, &palette, &faded, width as f32, height as f32)?;

            // Sleep until the next frame.
            next += period;
            let wait = next.saturating_duration_since(Instant::now());
            if wait.is_zero() {
                next = Instant::now();
                continue;
            }
            let due = -(wait.as_nanos() as i64 / 100); // relative, in 100 ns units
            SetWaitableTimer(timer, &due, 0, None, None, false)?;
            let _ = MsgWaitForMultipleObjects(Some(&[timer]), false, INFINITE, QS_ALLINPUT);
        }

        let _ = CloseHandle(timer);
        let _ = DestroyWindow(hwnd);
        wallpaper::restore();
    }
    Ok(())
}

#[allow(clippy::too_many_arguments)]
unsafe fn draw(
    target: &ID2D1HwndRenderTarget,
    factory: &ID2D1Factory,
    style: Style,
    a: &analysis::Analyzer,
    palette: &[ID2D1SolidColorBrush],
    faded: &[ID2D1SolidColorBrush],
    w: f32,
    h: f32,
) -> Result<()> {
    target.BeginDraw();
    target.Clear(Some(&D2D1_COLOR_F { r: 0.043, g: 0.043, b: 0.071, a: 1.0 }));

    match style {
        Style::Bars => {
            let n = a.bands.len();
            let width = w - 48.0;
            let slot = width / n as f32;
            let bar = (slot * 0.72).max(1.0);
            let max_h = h * 0.62;
            let baseline = h * 0.5 - max_h * 1.3 / 2.0 + max_h;
            for i in 0..n {
                let x = 24.0 + i as f32 * slot + (slot - bar) / 2.0;
                let bh = (a.bands[i] * max_h).max(2.0);
                target.FillRectangle(&D2D_RECT_F { left: x, top: baseline - bh, right: x + bar, bottom: baseline }, &palette[i]);
                let py = baseline - a.peaks[i] * max_h - 6.0;
                target.FillRectangle(&D2D_RECT_F { left: x, top: py, right: x + bar, bottom: py + 2.5 }, &palette[i]);
                let rh = a.bands[i] * max_h * 0.3;
                if rh >= 1.0 {
                    target.FillRectangle(&D2D_RECT_F { left: x, top: baseline + 4.0, right: x + bar, bottom: baseline + 4.0 + rh }, &faded[i]);
                }
            }
        }
        Style::Wave => {
            // Same as the C# Wave: start at a rising zero crossing, 256 points across the screen.
            let s = &a.samples;
            let n = s.len();
            let mut start = 0;
            for i in 1..n / 2 {
                if s[i - 1] < 0.0 && s[i] >= 0.0 {
                    start = i;
                    break;
                }
            }
            const POINTS: usize = 256;
            let span = n / 2;
            let mid = h / 2.0;
            let amp = h * 0.38 * 1.6;
            let limit = h * 0.45;
            let pts: Vec<D2D_POINT_2F> = (0..POINTS)
                .map(|p| {
                    let v = s[start + p * span / POINTS];
                    D2D_POINT_2F { x: p as f32 * w / (POINTS - 1) as f32, y: mid - (v * amp).clamp(-limit, limit) }
                })
                .collect();
            let geo = factory.CreatePathGeometry()?;
            let sink = geo.Open()?;
            sink.BeginFigure(pts[0], D2D1_FIGURE_BEGIN_HOLLOW);
            sink.AddLines(&pts[1..]);
            sink.EndFigure(D2D1_FIGURE_END_OPEN);
            sink.Close()?;
            target.DrawGeometry(&geo, &faded[40], 8.0, None);
            target.DrawGeometry(&geo, &palette[40], 2.5, None);
        }
    }
    target.EndDraw(None, None)
}

fn hsv(h: f32, s: f32, v: f32) -> (f32, f32, f32) {
    let h = h.rem_euclid(360.0);
    let c = v * s;
    let x = c * (1.0 - ((h / 60.0) % 2.0 - 1.0).abs());
    let m = v - c;
    let (r, g, b) = match (h / 60.0) as i32 {
        0 => (c, x, 0.0),
        1 => (x, c, 0.0),
        2 => (0.0, c, x),
        3 => (0.0, x, c),
        4 => (x, 0.0, c),
        _ => (c, 0.0, x),
    };
    (r + m, g + m, b + m)
}

extern "system" fn wndproc(hwnd: HWND, msg: u32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
    unsafe { DefWindowProcW(hwnd, msg, wparam, lparam) }
}
