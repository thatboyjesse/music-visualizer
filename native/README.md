# Native prototype (Rust + Direct2D)

An experiment to answer one question: **would rewriting the live wallpaper natively cut CPU and memory enough to be worth it?** It isn't part of the app or the installer.

`mv-native.exe` does the core of what the wallpaper does, natively:

- puts a window behind the desktop icons (the same Progman / WorkerW technique as `Wallpaper/DesktopWallpaper.cs`, including the Windows 11 24H2+ layered child window);
- captures everything the PC plays with WASAPI loopback, read once per frame on the render thread;
- runs the same analysis as `Visualizer/SpectrumAnalyzer.cs` (Hann window, 2048-point FFT with `rustfft`, 96 log bands from 40 Hz to 16 kHz, tilt, dB mapping, smoothing, peak caps);
- draws **Bars** (with peak caps and reflection) or **Wave** with Direct2D at 30 fps, paced by a high-resolution waitable timer.

There's no tray, settings, album art, now-playing, per-app capture or other styles.

## Results

Measured on the development PC (Intel i7-10510U, Windows 11 build 26200, 1366×768 main screen), 30 fps, music playing, wallpaper visible. The C# app (1.6.0, installed) used the same style with 96 bars, no glow and no album cover. CPU is the share of one core averaged over 12 s; memory as Task Manager shows it.

| | Native Rust | C# / WPF 1.6.0 | Native uses |
|---|---|---|---|
| **Bars** CPU | 3.4% | 7.9% | ~2.3× less |
| **Bars** private memory | 15 MB | 62 MB | ~4× less |
| **Bars** working set | 38 MB | 143 MB | ~3.8× less |
| **Wave** CPU | 5.1% | 15.9% | ~3.1× less |
| **Wave** private memory | 19 MB | 74 MB | ~3.9× less |
| **Wave** working set | 43 MB | 155 MB | ~3.6× less |
| Threads | 5 | 25–26 | |
| On disk | 1.3 MB exe | 185 MB folder | |

Why native is cheaper: there's no .NET runtime, JIT or GC, and no WPF (its render thread, retained visual tree and per-frame CPU tessellation of changing geometry). Direct2D draws straight to the window on the GPU.

## What a full port would involve

| Piece | Effort | Notes |
|---|---|---|
| Wallpaper attach, loopback capture, analysis, Bars/Wave | Done here | |
| Other five styles, glow, colours, all appearance options | Medium | Mostly drawing code; Direct2D has everything needed (gradients, paths, bitmaps). |
| Album cover, album colours, caption text | Medium | WIC to decode the cover; DirectWrite for text. |
| Now playing (Spotify / media session) | Medium | The same WinRT API (`GlobalSystemMediaTransportControlsSessionManager`) is available from Rust via the `windows` crate. |
| Per-app capture (process loopback) | Medium | Same COM calls as `Audio/ProcessAudioCapture.cs`. |
| Tray icon and menu, single instance, startup, signals | Small | `Shell_NotifyIcon`, a named mutex and events. |
| Quiet-time fade, pause when covered, multi-monitor, Explorer restarts | Small–medium | Ports of existing C# logic. |
| **Settings window** | **Large** if rewritten natively | Native Rust UI is the hard part. |
| Installer | Small | Setup stays as it is; only the payload changes. |

## Recommendation

A **hybrid** gets almost all of the gain for far less work than a full rewrite:

- **Native engine (Rust):** the always-running part, meaning wallpaper, audio, analysis, drawing, tray and now-playing. This is where the CPU and memory go all day.
- **Settings window (the existing C# WPF):** started as a separate process only while the user changes settings, then closed. Both read and write the same `settings.json`; the engine reloads it when it changes. Settings stays exactly as it looks today.

Expected result: roughly 3–6% of one core and 20–40 MB while the wallpaper runs (instead of 8–25% and 140–180 MB), and nothing extra when settings are closed. The cost is maintaining two languages, plus a few days of focused work to port the engine piece by piece, checking each style against the C# version.

## Building and running

Requires Rust (`rustup`, stable `x86_64-pc-windows-gnu` or MSVC toolchain).

```powershell
cd native
cargo build --release
copy mv-native.exe.manifest target\release\
target\release\mv-native.exe bars 30     # or: wave 30   (style, seconds to run)
```

Quit Music Visualizer first (tray → Exit completely) so the two don't both attach to the desktop. The prototype removes itself and re-applies the normal wallpaper when the time is up.

`mv-native.exe.manifest` must sit next to the exe: Windows only allows layered child windows, which the 24H2+ desktop needs, for apps that declare Windows 8 or later.
