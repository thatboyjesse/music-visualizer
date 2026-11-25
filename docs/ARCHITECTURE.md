# Architecture

How Music Visualizer works, from sound to pixels. It's meant for anyone changing the code. User docs are in the [README](../README.md).

## Big picture

```
            ┌──────────────── audio ────────────────┐
 file ──► AudioFileReader ─► TapSampleProvider ─► WaveOutEvent ─► speakers
                                   │ copy
 speakers ─► WasapiLoopbackCapture ┤            (all audio)
 chosen apps ─► ProcessAudioCapture┤ (one per app, Windows process loopback)
                                   ▼
                    AudioEngine ring buffers (2048 mono samples each, summed)
                                   │ GetSamples() every frame
                                   ▼
                     SpectrumAnalyzer (window → FFT → 96 log bands → smoothing)
                                   │
                                   ▼
                     VisualizerCanvas.OnRender (Bars, Mirror, Blocks, Spectrum, Radial, Ring, Wave)
                          ▲                          ▲
     AlbumColors (album-art colours)        hosted in WallpaperWindow (behind desktop icons)
                          │                 and/or MainWindow (the optional visualizer window)
 NowPlayingService ───────┘
   └─ MediaSessionProvider  (Windows media session: Spotify desktop app, no sign-in)

 AppController owns all of the above, plus the TrayIcon and the windows.
```

Everything runs in one process. The UI thread owns rendering, now-playing polling and the windows. Audio arrives on capture threads and is handed over through lock-protected ring buffers.

## Code map

| File | Responsibility |
|---|---|
| `App.xaml(.cs)` | Startup: command-line switches, single instance, signals from other processes, crash log. Dark styles for buttons, toggles, check boxes and text boxes. `ShutdownMode=OnExplicitShutdown`. |
| `Core/AppController.cs` | Owns settings, audio, now-playing, the wallpaper, the tray icon and the windows. Decides when to capture audio. Re-attaches the wallpaper after Explorer restarts. |
| `Core/TrayIcon.cs` | The notification-area icon and its dark right-click menu (Windows Forms `NotifyIcon`). |
| `Core/StartWithWindows.cs` | The per-user `Run` registry entry. |
| `Core/Uninstaller.cs` | `--uninstall`: removes the app, shortcuts, registry entries and settings. |
| `Core/DarkTitleBar.cs` | Dark title bars through `DwmSetWindowAttribute`. |
| `SettingsWindow.xaml(.cs)` | Settings and first-run welcome: live preview, *Run as wallpaper*, screen, and Appearance / Audio / General tabs. |
| `MainWindow.xaml(.cs)` | The optional visualizer window: control bar, intro, now-playing card, file playback, shortcuts, fullscreen. |
| `Audio/AudioEngine.cs` | File playback, all-audio capture, per-app capture, the sample ring buffers. |
| `Audio/ProcessAudioCapture.cs` | Captures one app's audio with the Windows process-loopback API (raw WASAPI COM interop). |
| `Audio/AudioApps.cs` | Lists apps that have audio sessions; finds an app's top-level processes. |
| `Visualizer/SpectrumAnalyzer.cs` | FFT and band math. |
| `Visualizer/VisualizerCanvas.cs` | Per-frame drawing of the seven styles. |
| `NowPlaying/TrackInfo.cs` | `TrackInfo` record and the `INowPlayingProvider` interface. |
| `NowPlaying/MediaSessionProvider.cs` | Reads the Windows media session (GSMTC). |
| `NowPlaying/NowPlayingService.cs` | Polls the media session and computes the album-art hue. |
| `NowPlaying/ImageUtil.cs` | Image loading and dominant-hue extraction. |
| `Controls/NowPlayingCard.xaml(.cs)` | Album art, title, progress and transport buttons, reused by the windows and the preview. `Place()` puts it in a corner or edge, or hides it. |
| `Wallpaper/DesktopWallpaper.cs` | Win32 code that places a window behind the desktop icons. |
| `Wallpaper/Screens.cs` | Lists monitors (`EnumDisplayMonitors`/`GetMonitorInfo`) and picks the saved one. |
| `Wallpaper/WallpaperWindow.xaml(.cs)` | The borderless window used as the wallpaper; also fades to the normal wallpaper while nothing plays. |
| `Settings/AppSettings.cs` | JSON settings in `%AppData%\MusicVisualizer`. |
| `Settings/Appearance.cs` | The look: style options, bar count, colours, position, size, width, song-title position, intensity, per-frequency sensitivity, glow, background picture. |
| `Shared/InstallInfo.cs` | Names, paths, registry keys and switches shared by the app and the installer. Compiled into both, so it's plain C# 7.3. |
| `Setup/` | The installer: a separate .NET Framework 4.8 WPF project (`Setup.csproj`, `SetupWindow`, `Installer.cs`). |
| `app.manifest` | Declares Windows 8/10 support, which layered child windows (the wallpaper) need. |
| `build.ps1` | Publishes the exe, then builds the installer around it. |
| `scripts/make-icon.ps1` | Generates `Assets/app.ico`. |

## App lifecycle: tray, single instance, background

The app has no main window. `App.OnStartup` reads the command line, then:

- **`--uninstall`** runs `Uninstaller` and exits (see [Installer](#installer)).
- **Launch mode** (`LaunchMode`): no arguments = **Wallpaper**, the main workflow: turn the wallpaper on, open no window, and show a one-time tray notification. `--settings` = **Settings** (the *Music Visualizer Settings* shortcut). `--background` = **Background** (*Start with Windows*): restore the saved state quietly, so the wallpaper comes back only if it was on. `--wallpaper` is treated like no arguments.
- **Single instance:** a named mutex, `Local\MusicVisualizer.Instance`. A second launch passes its request on through a named event and exits: `Local\MusicVisualizer.Wallpaper` for a plain launch (the running copy calls `RunAsWallpaper(openSettingsIfRunning: true)`: start the wallpaper, or open settings if it's already on, so the click always does something visible), `Local\MusicVisualizer.Show` for `--settings`.
- **Quit signal:** the named event `Local\MusicVisualizer.Quit` makes the running copy shut down cleanly. The installer and uninstaller use it so the desktop wallpaper is restored before files are replaced. A background thread waits on all three events and hands them to the UI thread.
- The welcome banner shows the first time settings open (`SetupDone` false).
- `ShutdownMode=OnExplicitShutdown`: closing windows never exits. Only *Quit* (tray menu or settings), the quit signal, or signing out does. `OnExit` and `OnSessionEnding` dispose `AppController`, which restores the static wallpaper and removes the tray icon.
- Unhandled UI exceptions are logged to `%AppData%\MusicVisualizer\error.log` and shown once; the app keeps running.

`AppController` is the hub. Windows talk to it (`RunAsWallpaper`, `SetWallpaper`, `SetMode`, `SetAppearance`, `SetBackgroundImage`, `SetScreen`, `SetListen`, `ShowSettings`, `ShowVisualizer`, `Quit`) and listen to its `Changed` event to refresh. Appearance changes raise a separate `AppearanceChanged` event, because sliders fire it many times a second and the settings window shouldn't rebuild itself each time. Settings are saved on every change (appearance changes are debounced by 600 ms), since the app is rarely closed.

The settings preview is an exact replica: a `Grid` the size of the wallpaper screen holding a `VisualizerCanvas` and a `NowPlayingCard`, inside a `Viewbox` that scales it to fit. So positions, sizes and the song-title card look the same as on the desktop.

`RunAsWallpaper` is the main action: it turns the wallpaper on and closes the settings window, so nothing covers it.

**When audio is captured:** only while something shows it: the wallpaper, the visualizer window, or the settings window's preview (`AppController.UpdateAudio`). With neither open the app just sits in the tray, polling now-playing every 2 s. A file played in the visualizer window stops when that window closes.

**Tray icon:** WPF has no tray API, so `TrayIcon` uses Windows Forms' `NotifyIcon` (`UseWindowsForms` in the csproj). Its global `using`s are removed in the csproj so `Application`, `MessageBox`, `Color` and so on keep meaning the WPF types. Left-click opens settings; the right-click `ContextMenuStrip` uses a custom dark `ProfessionalColorTable`. A one-time balloon tells the user the app is still running after they close its first window.

**Start with Windows** is the value `MusicVisualizer` under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, set to `"<exe>" --background`. The settings window reads and writes the registry directly, so it always shows the real state. On startup `StartWithWindows.Repair()` updates the path if the exe moved.

## Audio: `AudioEngine`

One source at a time (`AudioSource.None | File | Loopback | Apps`):

- **File:** NAudio's `AudioFileReader` decodes the file to 32-bit float samples, using Media Foundation for mp3, m4a and flac. It's wrapped in `TapSampleProvider`, a pass-through `ISampleProvider` that copies every block it hands to the output (`WaveOutEvent`) into the engine. So the visualizer sees exactly what's being played.
- **Loopback** (*Everything on this PC*): `WasapiLoopbackCapture` records what the default output device is playing. Spotify doesn't expose raw audio, so capturing output is the only way to visualize it. Float (32-bit) and 16-bit PCM capture formats are handled.
- **Apps** (*Only the apps I choose*): one `ProcessAudioCapture` per running top-level process of each chosen app, described below.

Each source writes into its own `SampleRing`: it down-mixes to mono and keeps the latest **2048 samples** under a lock. `GetSamples` sums all rings, oldest sample first, for the analyzer. A ring that hasn't received audio for 150 ms (paused, silent app, or WASAPI sending nothing during silence) contributes zeros, so the bars fall instead of freezing on the last frame.

### Per-app capture: `ProcessAudioCapture`

Windows 10 build 20348 and Windows 11 can capture the audio of one process tree ("process loopback"). NAudio 2.2 contains the types for it but doesn't expose them, so this class calls WASAPI through its own COM interop:

1. `ActivateAudioInterfaceAsync("VAD\Process_Loopback", IID_IAudioClient, params)`, where `params` is a `PROPVARIANT` blob holding `AUDIOCLIENT_ACTIVATION_PARAMS { PROCESS_LOOPBACK, pid, INCLUDE_TARGET_PROCESS_TREE }`. The result arrives on a completion handler, which must also implement `IAgileObject`. The COM types are public because COM can only call into interfaces it can see.
2. `IAudioClient.Initialize(shared, LOOPBACK | AUTOCONVERTPCM, 200 ms, …)` with a format we choose: **48 kHz stereo float**. Process loopback has no mix format to ask for.
3. A background thread polls `IAudioCaptureClient` every 10 ms and passes the samples to the app's ring.

The COM objects are created on a thread-pool (MTA) thread, and calling them from the UI thread fails with `E_NOINTERFACE`. So the capture thread itself stops and releases them when `Dispose` asks it to finish.

**Which processes:** settings store process names (`Spotify`, `chrome`). Every 2 s a timer resolves each name to its *top-level* processes: those whose parent has a different name (parent ID from `NtQueryInformationProcess`). Capturing a top-level process includes its children. That matters because browsers and Spotify play audio from a helper process, and capturing every process of an app would count the same audio several times. Captures for exited processes are dropped, new processes are picked up, and a capture that ended (for example when the audio device changed) is restarted.

**Listing apps:** `AudioApps.WithAudioSessions` enumerates the audio sessions on every active output device (`AudioSessionManager`) and names each by its exe description. An app appears once it has opened an audio session, which normally means it has played sound.

## Analysis: `SpectrumAnalyzer`

Each frame:

1. **Window:** multiply the 2048 samples by a Hann window to reduce spectral leakage.
2. **FFT:** NAudio's `FastFourierTransform.FFT` (radix-2, `m = 11`). Bin `k` covers about `k × sampleRate / 2048` Hz, roughly 23 Hz per bin at 48 kHz.
3. **Log bands:** 96 bands spaced logarithmically from 40 Hz to 16 kHz, the way we hear pitch. Each band takes the **max** power of its bins. Low bands can share a bin, so they look stepped. That's expected.
4. **dB + tilt:** convert to dB, then add +3 dB per octave above 100 Hz. Music energy falls off with frequency, so without this the treble bars would barely move. Map −72…−12 dB to 0…1.
5. **Smoothing:** fast attack (`1 − e^(−35·dt)`), slow release (`1 − e^(−7·dt)`), frame-rate independent.
6. **Peaks:** each band's peak holds for 0.35 s, then falls at 0.5 per second.
7. **Bass:** the average of bands under 150 Hz drives the pulsing glow, the radial ring size and the hue speed.

8. **Per-frequency sensitivity** (`SetEq`): the four *Sensitivity by frequency* sliders are dB offsets anchored at 100 Hz (bass), 700 Hz (low mids), 3.5 kHz (high mids) and 10 kHz (treble). Each band gets a value interpolated linearly in log-frequency between the two nearest anchors (flat beyond the ends), so there are no steps between neighbouring bars. It's added to the band's dB before the mapping, together with the overall *Intensity* gain.

The number of bands is the *Number of bars* setting (16 to 128); the canvas creates a new analyzer when it changes.

To tune how the visuals feel, change `MinDb`/`MaxDb` (sensitivity), the tilt, or the two smoothing rates.

## Rendering: `VisualizerCanvas`

A `FrameworkElement` hooked to `CompositionTarget.Rendering`, which fires once per display frame. Each tick it runs the analyzer, then calls `InvalidateVisual()`, and WPF calls `OnRender`, where everything is drawn with `DrawingContext`.

- **Brushes and pens are cached:** a palette of 360 frozen brushes, 36 glow gradients, and radial pens created on demand (reset when the spoke width changes). This keeps garbage-collection pressure low at 60 fps.
- **Colour** (`Appearance.Colors`): the palette is 360 frozen brushes. For *Rainbow* it is the hue wheel and `ColorIndex(t)` returns `BaseHue + t × spread`, where `BaseHue` drifts faster with the bass. For *Song art colors* and *My colors* it is 360 steps of a two-colour gradient and `ColorIndex(t)` maps the position along the spectrum (0..1) straight onto it. Song-art mode uses `AlbumColors` and never falls back to the rainbow: before the first cover is known it uses the user's two colours. When the song changes, the drawn colours glide towards the new ones (exponential, about a second), and the palette is rebuilt only when the colours actually change.
- **Appearance** (`VisualizerCanvas.Look`): positions and sizes are fractions of the surface, so the settings preview (sized to the wallpaper screen's aspect ratio) matches the wallpaper. `PositionX/Y` is the visualizer's centre: the bars group (bars plus reflection) is centred on it, as are the radial ring and the wave's midline. `Size` scales bar height and width (width capped at the screen), the ring, and the wave. `Intensity` is a gain on the analyzer (`SpectrumAnalyzer.Gain`, added in dB before the −72…−12 dB mapping) and on the wave amplitude; the slider is logarithmic, ¼× to 4×. Glow strength multiplies the glow's opacity.
- **Background picture:** decoded once per path (at most 3840 px wide, `OnLoad` so the file isn't locked) and drawn like Windows' *Fill*: scaled to cover, centred, cropped. A black layer at `BackgroundDim` opacity darkens it. `AppController.SetBackgroundImage` copies the chosen file to `%AppData%\MusicVisualizer\background-<ticks>.<ext>` and deletes older copies; the new name each time makes every view reload it.
- **Styles:** *Bars*, *Mirror* (bars up and down from the centre line), *Blocks* (each bar drawn as up to 20 separate cells), *Spectrum* (a curve through the bar tops made of quadratic Béziers through the midpoints between neighbours, filled at low opacity; its reflection is the same geometry under a flipping `MatrixTransform`), *Radial* (spokes around a circle), *Ring* (the waveform as the radius of a closed circle; the last samples are blended into the first so it closes without a jump) and *Wave*. Line and area styles use `SpectrumBrush()`, a left-to-right gradient through five points of the current palette.
- **Width** sets how much of the screen the bar-based styles and the wave span (`BarsWidth`); *Size* sets height and the size of the round styles.
- **Wave mode** starts drawing at a rising zero-crossing. This works like an oscilloscope trigger and keeps the waveform from sliding sideways.
- `IsPaused` skips analysis and drawing (window minimized, or the wallpaper is showing instead).

## Now playing

`NowPlayingService` polls every 2 s on the UI thread (`DispatcherTimer`), so its `Changed` event and `Current` are always UI-safe.

The only source is the Windows media session, so there is no sign-in and nothing leaves the PC. The transport buttons go to the same session. `INowPlayingProvider` is kept as the seam for adding another source later.

Each poll extrapolates the track position with `TrackInfo.PositionAt(now)`. The card updates its progress bar every 250 ms without extra API calls.

### `MediaSessionProvider`

This uses WinRT's `GlobalSystemMediaTransportControlsSessionManager`, the same source as the Windows volume flyout. That's why the project targets `net8.0-windows10.0.19041.0`. It prefers a session whose app ID contains "Spotify", otherwise Windows' current session. Album art comes from `props.Thumbnail`, and play/next/previous use `TryTogglePlayPauseAsync` and related calls.

### Album-art colours (`ImageUtil.Palette`)

The cover is scaled to 48×48 and each pixel votes into a 6×6×6 RGB grid. Votes are weighted towards colourful pixels (`0.25 + saturation × 1.5`), so a small red logo can beat a large grey background, and near-black pixels count for little. If colourful cells (at least 30% saturated) hold more than about 4% of the vote, only colourful cells can win, so a red frame on a dark grey cover gives red rather than grey. The winning cell's average colour is the first colour. The second is the strongest remaining cell that is clearly different (perceptual distance above 55); if there is none, it's a lighter shade of the first. Both are then made visible on the dark background: brightness at least 85%, and colourful ones at least 55% saturated, while greys stay grey (so black-and-white covers give greys and white).

`NowPlayingService.AlbumColors` keeps the last cover's colours when playback stops or a track has no cover. The colours are used whenever *Song art colors* is selected, whatever the audio source.

## Live wallpaper

Windows draws the desktop in Explorer's `Progman` window. Sending it the undocumented message **`0x052C`** makes Explorer create a `WorkerW` window behind the icon layer (`SHELLDLL_DefView`). A window placed between the two shows up behind the icons. `DesktopWallpaper.Attach` handles two layouts:

| Windows version | Layout | What we do |
|---|---|---|
| 10, 11 before 24H2 | Top-level `WorkerW` holds `SHELLDLL_DefView`; the **next** top-level `WorkerW` is empty | `SetParent` into that empty `WorkerW` |
| 11 24H2 and later | `SHELLDLL_DefView` and `WorkerW` are both **children of Progman** | `SetParent` into Progman, z-order just below `SHELLDLL_DefView`, **and make the window layered** |

In both cases the window becomes `WS_CHILD` and covers one monitor. Child coordinates are relative to the parent, which spans the whole virtual screen and can start left of or above the main monitor (for example a second screen placed higher gives the parent a negative top). So the position is the monitor's bounds minus the parent's origin (`DesktopWallpaper.Place`).

### Back to the normal wallpaper while nothing plays

Each `SampleRing` remembers when it last received an audible block (peak above about −66 dBFS), and `AudioEngine.SecondsSinceSound` is the most recent of those across all sources. It's very large when nothing is captured at all, for example when only certain apps are chosen and none of them is running.

`AppController.CheckIdle` runs every 250 ms. After `SilentSeconds` of quiet (also counted from when the wallpaper appeared, so it doesn't fade out before the first sound arrives) it calls `WallpaperWindow.SetIdle(true)`; as soon as sound returns, `SetIdle(false)`.

The switch is meant to be invisible. Going idle, the window fades in `Desk`, a copy of the desktop background: the desktop colour (`GetSysColor(COLOR_DESKTOP)`) plus the wallpaper picture (`SPI_GETDESKWALLPAPER`), stretched to match the user's fit mode (`WallpaperStyle`/`TileWallpaper` in `HKCU\Control Panel\Desktop`: Fill, Fit, Stretch, Center). When the fade ends, the window is hidden with `ShowWindow(SW_HIDE)` so Explorer's real wallpaper shows, and rendering pauses. Waking shows the window again with the copy fully opaque, re-asserts the z-order (`DesktopWallpaper.MoveTo`), and fades the copy out. Capture keeps running while idle, so it notices when sound returns.

### Song title

`NowPlayingCard.Place(TitlePosition)` sets the card's alignment and margin (bottom positions leave 88 px for the taskbar), or hides it.

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

**Teardown:** closing the wallpaper window destroys it. `RestoreStaticWallpaper()` then re-applies the user's wallpaper through `SystemParametersInfo(SPI_SETDESKWALLPAPER)`, so no frozen frame is left behind. This also happens on quit and sign-out.

**Staying attached:** the wallpaper setting is saved, so it comes back whenever the app starts. Two situations need a retry, both handled in `AppController`:

- **Right after sign-in**, Explorer may not have built the desktop yet, so `Attach` fails. The controller retries every 3 s, up to 40 times.
- **Explorer restarts** destroy Progman, and our child window with it. A hidden top-level `HwndSource` listens for the `TaskbarCreated` broadcast that a new Explorer sends (message-only windows don't receive broadcasts), and the window's unexpected `Closed` event also triggers re-attaching with the same retry.

### Debugging the wallpaper

The quickest checks:

- List Progman's children with `EnumChildWindows`. `Music Visualizer Wallpaper` should sit between `SHELLDLL_DefView` and `WorkerW`, with ex-style `0x80000` on 24H2+.
- Take a screenshot with every window minimized (`Shell.Application.MinimizeAll()`).

If the window sits in the right place but the screen shows only the plain wallpaper, the layered flag is missing.

## Build and release

`build.ps1` does two steps:

1. `dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true`. The result is one `MusicVisualizer.exe` of about 75 MB, with the .NET runtime, WPF and native libraries included, so users don't need .NET installed.
2. `dotnet build Setup\Setup.csproj -p:PayloadPath=<that exe>`, which embeds the exe as the resource `payload.exe`, then copies the result to `dist\MusicVisualizer-Setup-<version>.exe`.

## Installer

`Setup/` is a separate WPF project with the same dark look as the app. It targets **.NET Framework 4.8**, which ships with Windows 10 and 11, so Setup.exe needs no runtime of its own and is about the size of the app it carries. The main project's csproj excludes `Setup\**` from its own build.

- **Window:** borderless with its own title bar (drag to move), a decorative spectrum panel, and three pages: options → progress → done (or error).
- **Manifest:** `Setup/app.manifest` requests `asInvoker`. Without it, Windows' installer detection sees "Setup" in the file name and asks for admin rights.
- **Per user, no admin:** default folder `%LocalAppData%\Programs\Music Visualizer`.
- **Steps (`Installer.Install`):**
  1. Ask a running copy to quit through the quit event, wait for its mutex to disappear, and kill it if it's still running from the install folder.
  2. If the 1.0.0 Inno Setup install is present (its `{8F3C…}_is1` uninstall key), run its uninstaller silently.
  3. Stream `payload.exe` to `MusicVisualizer.exe.new`, then swap it in, retrying while the old file is still locked.
  4. Start-menu shortcuts **Music Visualizer** (no arguments: runs the wallpaper) and **Music Visualizer Settings** (`--settings`), and optionally a desktop shortcut (`WScript.Shell`).
  5. The *Installed apps* entry under `HKCU\…\Uninstall\MusicVisualizer`, with `UninstallString = "<exe>" --uninstall`.
  6. The `Run` entry if *Start with Windows* is ticked (on by default), otherwise remove it.
- **Updates:** an existing install is detected from the uninstall key. The folder is kept (so shortcuts and the startup entry stay valid) and settings are untouched.
- **Uninstall from Setup:** when an install exists, the options page shows **Uninstall**. `Installer.Uninstall` closes the running copy and removes everything itself (registry entries, the three shortcuts, settings, and the install folder if it really contains `MusicVisualizer.exe`), so it also cleans up a broken install. For the 1.0.0 Inno Setup install it runs that uninstaller instead.
- **Uninstall** from *Settings → Apps* is done by the app itself (`Core/Uninstaller.cs`), so no separate uninstaller is left in the folder. It quits the running copy, removes the registry entries, shortcuts and `%AppData%\MusicVisualizer`, then starts a hidden `cmd` that waits two seconds and deletes the install folder, since a running exe can't delete itself. It only deletes the folder registered as `InstallLocation`, and only when the exe is running from there, so running `--uninstall` from a dev build can't remove the wrong folder.

## Extending

- **New visual style:** add a value to the end of `VisualMode`, a `DrawXxx` method and a `case` in `VisualizerCanvas.OnRender`, and an entry in `SettingsWindow.Modes` (with a tooltip). The visualizer window's *Style* button and number keys pick it up from the enum.
- **New now-playing source:** implement `INowPlayingProvider` and add it to the order in `NowPlayingService.PollAsync`.
- **Wallpaper on all screens at once:** create one `WallpaperWindow` per entry in `Screens.All()` and attach each with its own bounds.
- **New setting:** add a property to `AppSettings`, a setter on `AppController` that saves and raises `Changed`, and a control in `SettingsWindow` (update it in `Sync()`).
- **New appearance option:** add it to `Appearance`, use it in `VisualizerCanvas`, and add a control on the Appearance tab (read in `SyncLook()`, written in the matching handler, which calls `SetAppearance`).
