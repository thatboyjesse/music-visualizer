# Music Visualizer

A Windows music visualizer (C# / WPF) that lives in your **system tray** and runs as a **live desktop wallpaper**. It reacts to everything your PC plays, or only to the apps you pick (for example just Spotify), and shows what's playing in Spotify.

![Bars mode running as the desktop wallpaper](docs/wallpaper.png)

## Features

- **Runs in the background:** a tray icon instead of a window. Click it for settings; right-click to quit. It can start with Windows.
- **Live wallpaper** behind your desktop icons, on the screen you choose.
- **Listen to the apps you choose:** everything on the PC, or only selected apps such as Spotify or a browser. Other sounds (game audio, notifications, calls) are ignored.
- **Three visual modes:** Bars (with peak caps and reflection), Radial and Wave.
- **Spotify now-playing:** album art, title, artist and progress, read from the Spotify desktop app through Windows. No sign-in, free accounts included.
- **Album-art colors:** the visuals take on the main color of the current album cover.
- **Visualizer window** (optional): fullscreen, keyboard shortcuts, play/pause/skip, and audio files (mp3, wav, flac, m4a, aac, wma, aiff).

## Install

Run `MusicVisualizer-Setup-<version>.exe` (built into `dist\`). It installs for your Windows user only, so there's no admin prompt. You can choose:

- where to install (default `%LocalAppData%\Programs\Music Visualizer`),
- **Start with Windows**: on by default. It starts quietly in the tray when you sign in.
- a desktop shortcut, and
- whether to open Music Visualizer when setup finishes.

Running a newer setup over an existing install updates it in place and keeps your settings. Setup also replaces version 1.0.0, which used a different installer.

Uninstall from *Settings → Apps → Installed apps → Music Visualizer*. This removes the app, its shortcuts, the startup entry and your settings.

Windows may show a SmartScreen warning the first time, because the installer isn't code-signed. Choose *More info → Run anyway*.

## Using it

After setup, the **settings window** opens with a short welcome. Close it whenever you're done: the app keeps running in the **system tray** (bottom-right of the taskbar; it may be under the **^** arrow).

| Tray icon | What it does |
|---|---|
| Click | Open settings |
| Right-click | Menu: Settings, Open visualizer, Wallpaper on/off, Quit |

Starting Music Visualizer again from the Start menu while it's running also opens settings.

### Settings

| Setting | What it does |
|---|---|
| **Live wallpaper** On/Off | Draws the visualizer behind your desktop icons. Your normal wallpaper comes back when you turn it off or quit. |
| **Screen** | Which monitor the wallpaper covers (shown with two or more monitors). |
| **Style** | Bars, Radial or Wave. |
| **Listen to** | *Everything on this PC*, or *Only the apps I choose*. |
| App list | Apps that have played sound. Tick the ones to visualize. Ticked apps that aren't running are remembered and picked up when they start. Click **Refresh** after starting a new app. |
| **Start with Windows** | Start in the tray when you sign in. |
| **Open visualizer window** | A normal window with the visualizer, now-playing card and controls. |
| **Quit Music Visualizer** | Stops everything and restores your wallpaper. |

### Visualizer window

| Control | What it does |
|---|---|
| **Open** / drag a file onto the window | Play and visualize an audio file |
| **Live audio** | Visualize what's playing on the PC, using the *Listen to* setting |
| **Bars / Radial / Wave** | Switch visual mode |
| **Screen 1 / Screen 2…** | Move the wallpaper to the next monitor |
| **Wallpaper** | Turn the live wallpaper on or off |
| ⚙ | Open settings |
| Now-playing card ⏮ ⏯ ⏭ | Control Spotify (or whichever media app is playing) |

| Key | Action |
|---|---|
| `Space` | Play/pause the opened file |
| `1` `2` `3` | Bars / Radial / Wave |
| `F` or `F11` | Fullscreen (`Esc` to leave) |
| `W` | Toggle live wallpaper |
| `S` | Move the wallpaper to the next screen |
| `H` | Hide/show the control bar |

Closing the visualizer window doesn't quit the app; it keeps running in the tray. The window's own view pauses while the wallpaper runs, so it isn't drawn twice.

### Spotify

Play something in the Spotify desktop app and the card shows it automatically. There's nothing to set up. Spotify never shares raw audio with apps, so the visuals come from your PC's sound output (or from the Spotify app itself if you choose it under *Listen to*). Spotify playing on another device, such as your phone or a speaker, isn't shown.

### Command line

| Command | What it does |
|---|---|
| `MusicVisualizer.exe` | Start (opens settings), or open settings if it's already running |
| `MusicVisualizer.exe --background` | Start in the tray with no window (used by *Start with Windows*) |
| `MusicVisualizer.exe --wallpaper` | Like `--background`, and turns the wallpaper on |
| `MusicVisualizer.exe --uninstall [--quiet]` | Remove the app (what *Settings → Apps → Uninstall* runs) |

## Building from source

Requirements: Windows 10/11 and the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). The installer targets .NET Framework 4.8, which is part of Windows, and builds with the same SDK; nothing else is needed.

```powershell
winget install Microsoft.DotNet.SDK.8
```

```powershell
dotnet run                       # run in development
.\build.ps1                      # publish\MusicVisualizer.exe + dist\MusicVisualizer-Setup-<version>.exe
.\build.ps1 -Version 1.2.0       # set the version
.\build.ps1 -SkipInstaller       # exe only
```

The version comes from `<Version>` in `MusicVisualizer.csproj`. The icon is generated by `scripts\make-icon.ps1`; rerun it if you change the design.

`publish\MusicVisualizer.exe` is a single self-contained file. It also runs without installing (Windows 10/11 x64, no .NET needed), but then *Start with Windows* points at wherever that file is.

## Where things are stored

| Path | Contents |
|---|---|
| `%LocalAppData%\Programs\Music Visualizer\` | The app (default install folder) |
| `%AppData%\MusicVisualizer\settings.json` | Wallpaper on/off and screen, style, listen mode and chosen apps |
| `%AppData%\MusicVisualizer\error.log` | Only created if something goes wrong |
| `HKCU\...\CurrentVersion\Run` → `MusicVisualizer` | *Start with Windows* |
| `HKCU\...\CurrentVersion\Uninstall\MusicVisualizer` | The *Installed apps* entry |

## Limitations

- The wallpaper covers one screen at a time.
- *Only the apps I choose* needs Windows 11, or Windows 10 build 20348 or later.
- An app shows in the list only after it has played sound since it started. Its child processes are included (browsers and Spotify play audio from a helper process).
- Now-playing only covers apps on this PC (the Spotify desktop app, or any app that reports to Windows' media controls).
- Monitors with different display scaling are untested.

## How it works

See **[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)** for how the tray app, the audio capture (including per-app capture), the spectrum analysis, rendering, now-playing, the wallpaper trick and the installer work, and a map of the code.
