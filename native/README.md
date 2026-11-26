# The engine (Rust + Direct2D)

`MusicVisualizer.exe`: the part of Music Visualizer that runs all day. It puts the visualizer behind the desktop icons, captures audio (everything, or chosen apps), analyses it, draws all seven styles with Direct2D, reads now-playing from the Windows media session, and owns the tray icon. The settings and visualizer windows are the C# app (`MusicVisualizerSettings.exe`), started only while they're open; both read and write `%AppData%\MusicVisualizer\settings.json`.

How it fits together, the file map and the measurements are in [docs/ARCHITECTURE.md](../docs/ARCHITECTURE.md#two-programs-the-engine-and-the-settings-app).

## Building and running

Requires Rust (`rustup`, stable, `x86_64-pc-windows-gnu` or MSVC).

```powershell
cargo build --release
```

`build.ps1` in the repo root builds this too and puts it in `publish\app\` next to the settings app; run `publish\app\MusicVisualizer.exe` from there so *Settings* in the tray can find `MusicVisualizerSettings.exe`. Quit the installed copy first (tray → Exit completely): only one copy runs at a time.

The icon, the manifest (Windows 8+ support, which layered child windows behind the icons need on Windows 11 24H2+, and per-monitor DPI awareness) and the version info are embedded by `build.rs`. Set `MV_VERSION` to stamp a version (build.ps1 does).

For testing, `MV_DEBUG_LOG=<file>` makes it append its state every 2 seconds.

## History: the prototype

This started as a prototype to answer one question: would rewriting the live wallpaper natively cut CPU and memory enough to be worth it? With only Bars and Wave, no glow, cover or card, it measured 3.4–5.1% of one core and 15–19 MB private / 38–43 MB working set, against 7.9–15.9% and 62–74 / 143–155 MB for the C# 1.6.0 app. The full engine, with every option, lands at about 5–8% and 19–29 / 44–57 MB (see the architecture doc for every style).
