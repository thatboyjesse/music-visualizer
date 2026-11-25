# Architecture

How Music Visualizer works, from sound to pixels. It's meant for anyone changing the code. User docs are in the [README](../README.md).

## Big picture

```
            ┌──────────────── audio ────────────────┐
 file ──► AudioFileReader ─► TapSampleProvider ─► WaveOutEvent ─► speakers
                                   │ copy
 speakers ─► WasapiLoopbackCapture ┤
                                   ▼
                       AudioEngine ring buffer (2048 mono samples)
                                   │ GetSamples() every frame
                                   ▼
                     SpectrumAnalyzer (window → FFT → 96 log bands → smoothing)
                                   │
                                   ▼
                     VisualizerCanvas.OnRender (Bars / Radial / Wave)
                          ▲                          ▲
         ThemeHue (album-art colour)        hosted in MainWindow
                          │                 and/or WallpaperWindow (behind desktop icons)
 NowPlayingService ───────┘
   └─ MediaSessionProvider  (Windows media session: Spotify desktop app, no sign-in)
```

Everything runs in one process. The UI thread owns rendering and now-playing polling. NAudio's threads deliver audio, which is handed over through a lock-protected ring buffer.

## Code map

| File | Responsibility |
|---|---|
| `App.xaml` | App-wide dark styles for buttons, toggles, text boxes. `ShutdownMode=OnMainWindowClose`. |
| `MainWindow.xaml(.cs)` | Main UI: control bar, intro screen, now-playing card, keyboard shortcuts, fullscreen, wallpaper toggle, `--wallpaper` startup flag. Coordinates everything else. |
| `Audio/AudioEngine.cs` | File playback, system-audio capture, the sample ring buffer. |
| `Visualizer/SpectrumAnalyzer.cs` | FFT and band math. |
| `Visualizer/VisualizerCanvas.cs` | Per-frame drawing of the three modes. |
| `NowPlaying/TrackInfo.cs` | `TrackInfo` record and the `INowPlayingProvider` interface. |
| `NowPlaying/MediaSessionProvider.cs` | Reads the Windows media session (GSMTC). |
| `NowPlaying/NowPlayingService.cs` | Polls the media session and computes the album-art hue. |
| `NowPlaying/ImageUtil.cs` | Image loading and dominant-hue extraction. |
| `Controls/NowPlayingCard.xaml(.cs)` | Album art, title, progress and transport buttons, reused by both windows. |
| `Wallpaper/DesktopWallpaper.cs` | Win32 code that places a window behind the desktop icons. |
| `Wallpaper/Screens.cs` | Lists monitors (`EnumDisplayMonitors`/`GetMonitorInfo`) and picks the saved one. |
| `Wallpaper/WallpaperWindow.xaml(.cs)` | The borderless window used as the wallpaper. |
| `Settings/AppSettings.cs` | JSON settings in `%AppData%\MusicVisualizer`. |
| `app.manifest` | Declares Windows 8/10 support, which layered child windows (the wallpaper) need. |
| `build.ps1`, `installer/MusicVisualizer.iss` | Release exe and Inno Setup installer. |
| `scripts/make-icon.ps1` | Generates `Assets/app.ico`. |

## Audio: `AudioEngine`

Two sources, one at a time (`AudioSource.None | File | Loopback`):

- **File:** NAudio's `AudioFileReader` decodes the file to 32-bit float samples, using Media Foundation for mp3, m4a and flac. It's wrapped in `TapSampleProvider`, a pass-through `ISampleProvider` that copies every block it hands to the output (`WaveOutEvent`) into the engine. So the visualizer sees exactly what's being played.
- **Loopback:** `WasapiLoopbackCapture` records what the default output device is playing. That's how Spotify, browsers and games get visualized. Spotify doesn't expose raw audio, so this is the only way to visualize it. Float (32-bit) and 16-bit PCM capture formats are handled.

Both paths call `AddSamples`, which down-mixes to mono and writes into a **ring buffer of 2048 samples** under a lock. `GetSamples` copies it out oldest-first for the analyzer.

If no audio has arrived for 150 ms (paused, or WASAPI sends nothing during silence), `GetSamples` returns zeros. That way the bars fall instead of freezing on the last frame.

## Analysis: `SpectrumAnalyzer`

Each frame:

1. **Window:** multiply the 2048 samples by a Hann window to reduce spectral leakage.
2. **FFT:** NAudio's `FastFourierTransform.FFT` (radix-2, `m = 11`). Bin `k` covers about `k × sampleRate / 2048` Hz, roughly 23 Hz per bin at 48 kHz.
3. **Log bands:** 96 bands spaced logarithmically from 40 Hz to 16 kHz, the way we hear pitch. Each band takes the **max** power of its bins. Low bands can share a bin, so they look stepped. That's expected.
4. **dB + tilt:** convert to dB, then add +3 dB per octave above 100 Hz. Music energy falls off with frequency, so without this the treble bars would barely move. Map −72…−12 dB to 0…1.
5. **Smoothing:** fast attack (`1 − e^(−35·dt)`), slow release (`1 − e^(−7·dt)`), frame-rate independent.
6. **Peaks:** each band's peak holds for 0.35 s, then falls at 0.5 per second.
7. **Bass:** the average of bands under 150 Hz drives the pulsing glow, the radial ring size and the hue speed.

To tune how the visuals feel, change `MinDb`/`MaxDb` (sensitivity), the tilt, or the two smoothing rates.

## Rendering: `VisualizerCanvas`

A `FrameworkElement` hooked to `CompositionTarget.Rendering`, which fires once per display frame. Each tick it runs the analyzer, then calls `InvalidateVisual()`, and WPF calls `OnRender`, where everything is drawn with `DrawingContext`.

- **Brushes and pens are cached:** 360 frozen hue brushes, 36 glow gradients, and radial pens rebuilt only when the size changes. This keeps garbage-collection pressure low at 60 fps.
- **Colour:** `BaseHue` is either a rainbow that drifts faster with the bass, or, when `ThemeHue` is set from album art, that hue swaying ±12°. `HueSpread` narrows the spread across bars in themed mode, so the colours stay on-palette.
- **Wave mode** starts drawing at a rising zero-crossing. This works like an oscilloscope trigger and keeps the waveform from sliding sideways.
- `IsPaused` skips analysis and drawing (window minimized, or the wallpaper is showing instead).

## Now playing

`NowPlayingService` polls every 2 s on the UI thread (`DispatcherTimer`), so its `Changed` event and `Current` are always UI-safe.

The only source is the Windows media session, so there is no sign-in and nothing leaves the PC. The transport buttons go to the same session. `INowPlayingProvider` is kept as the seam for adding another source later.

Each poll extrapolates the track position with `TrackInfo.PositionAt(now)`. The card updates its progress bar every 250 ms without extra API calls.

### `MediaSessionProvider`

This uses WinRT's `GlobalSystemMediaTransportControlsSessionManager`, the same source as the Windows volume flyout. That's why the project targets `net8.0-windows10.0.19041.0`. It prefers a session whose app ID contains "Spotify", otherwise Windows' current session. Album art comes from `props.Thumbnail`, and play/next/previous use `TryTogglePlayPauseAsync` and related calls.

### Album-art colour (`ImageUtil.DominantHue`)

The art is scaled to 32×32 and converted to BGRA. Each pixel with saturation ≥ 0.25 and value ≥ 0.2 votes into a 36-bin hue histogram, weighted by saturation × value. The bins are smoothed with their neighbours and the winner is returned. Mostly grey covers return `null`, so the visuals keep the rainbow. `MainWindow.ApplyTheme` applies the hue only in system-audio mode, since a local file has nothing to do with Spotify's cover.

## Live wallpaper

Windows draws the desktop in Explorer's `Progman` window. Sending it the undocumented message **`0x052C`** makes Explorer create a `WorkerW` window behind the icon layer (`SHELLDLL_DefView`). A window placed between the two shows up behind the icons. `DesktopWallpaper.Attach` handles two layouts:

| Windows version | Layout | What we do |
|---|---|---|
| 10, 11 before 24H2 | Top-level `WorkerW` holds `SHELLDLL_DefView`; the **next** top-level `WorkerW` is empty | `SetParent` into that empty `WorkerW` |
| 11 24H2 and later | `SHELLDLL_DefView` and `WorkerW` are both **children of Progman** | `SetParent` into Progman, z-order just below `SHELLDLL_DefView`, **and make the window layered** |

In both cases the window becomes `WS_CHILD` and covers one monitor. Child coordinates are relative to the parent, which spans the whole virtual screen and can start left of or above the main monitor (for example a second screen placed higher gives the parent a negative top). So the position is the monitor's bounds minus the parent's origin (`DesktopWallpaper.Place`).

### Choosing the screen

`Screens.All()` lists monitors in screen pixels, numbered left to right. The choice is saved as the monitor's device name (`\\.\DISPLAY2`) in `AppSettings.WallpaperScreen`; `Screens.Find` falls back to the main screen if that device is gone. The **Screen** button (or `S`) cycles monitors. While the wallpaper runs, `DesktopWallpaper.MoveTo` just repositions the window, and `SystemEvents.DisplaySettingsChanged` (monitor plugged in or out, resolution change) refreshes the button and re-places the window.

`MoveTo` also re-asserts the z-order below `SHELLDLL_DefView`. Don't call `RestoreStaticWallpaper()` while the wallpaper is running: re-applying the wallpaper makes Explorer raise its `WorkerW` above our window on 24H2+, which hides the visualizer. The screen that was left repaints by itself.

The app is system-DPI aware (WPF's default). Monitors with different scaling may get a slightly blurry or mis-sized wallpaper; this is untested.

### Why 24H2+ needed three extra fixes

On 24H2+, Progman composes its children with DirectComposition and **only draws layered children**. The icon layer itself is `WS_EX_LAYERED`. Making our WPF window layered took three changes:

1. **`app.manifest` declares Windows 8/10 support.** Windows only allows layered *child* windows for apps that declare Windows 8 or newer. .NET doesn't add that declaration by default.
2. **Attach after `Show()`.** WPF applies its window styles while showing, so `WallpaperWindow.ShowAsWallpaper()` calls `Show()` first, with the window parked off-screen at −20000,−20000 so it doesn't flash, and then attaches.
3. **Hook `WM_STYLECHANGING`.** WPF rewrites the requested style and drops `WS_EX_LAYERED` on windows it didn't create as layered. `KeepLayeredHook` forces that bit into the new style and marks the message handled. The layered flag is then set with `SetLayeredWindowAttributes(alpha 255)`. `Attach` reads the style back and throws a clear error if it still didn't stick.

`AllowsTransparency=True` (WPF's own layered mode) is **not** used. It renders through `UpdateLayeredWindow`, which costs much more per frame at full-screen size and still didn't draw inside Progman.

**Teardown:** closing the wallpaper window destroys it. `RestoreStaticWallpaper()` then re-applies the user's wallpaper through `SystemParametersInfo(SPI_SETDESKWALLPAPER)`, so no frozen frame is left behind.

### Debugging the wallpaper

The quickest checks:

- List Progman's children with `EnumChildWindows`. `Music Visualizer Wallpaper` should sit between `SHELLDLL_DefView` and `WorkerW`, with ex-style `0x80000` on 24H2+.
- Take a screenshot with every window minimized (`Shell.Application.MinimizeAll()`).

If the window sits in the right place but the screen shows only the plain wallpaper, the layered flag is missing.

## Build and release

- `build.ps1` runs `dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true`. The result is one `MusicVisualizer.exe` of about 75 MB, with the .NET runtime, WPF and native libraries included, so users don't need .NET installed.
- Inno Setup (`installer/MusicVisualizer.iss`) packs it into `dist/MusicVisualizer-Setup-<version>.exe`. The installer is **per-user by default** (`PrivilegesRequired=lowest`, installs to `%LocalAppData%\Programs`). It offers a desktop icon and an optional `HKCU\...\Run` entry `"MusicVisualizer.exe" --wallpaper`. Uninstalling removes `%AppData%\MusicVisualizer`. `AppId` must stay the same across versions so upgrades replace the existing install.

## Extending

- **New visual mode:** add a value to `VisualMode`, a `DrawXxx` method in `VisualizerCanvas`, a `RadioButton` in `MainWindow.xaml`, and a key in `OnKeyDown`/`OnModeChecked`/`SetMode`.
- **New now-playing source:** implement `INowPlayingProvider` and add it to the order in `NowPlayingService.PollAsync`.
- **Wallpaper on all screens at once:** create one `WallpaperWindow` per entry in `Screens.All()` and attach each with its own bounds.
