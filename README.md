# Music Visualizer

A Windows music visualizer (C# / WPF) that lives in your **system tray** and runs as a **live desktop wallpaper**. It reacts to everything your PC plays, or only to the apps you pick (for example just Spotify), and shows what's playing in Spotify.

![Bars mode running as the desktop wallpaper](docs/wallpaper.png)

## Features

- **Live wallpaper first:** start it and it runs behind your desktop icons straight away, with no window. It lives in the system tray; click the icon to customize it. It can start with Windows.
- **Album cover in the middle** of the screen, bouncing with the beat, with the song and artist next to it wherever you like.
- **Make it yours:** style, colors (song-art colors, rainbow or your own), position, size, intensity, background glow and a picture behind the visualizer, with a live preview.
- **Listen to the apps you choose:** everything on the PC, or only selected apps such as Spotify or a browser. Other sounds (game audio, notifications, calls) are ignored.
- **Seven styles:** Bars, Mirror, Blocks, Spectrum, Radial, Ring and Wave, with as many or as few bars as you like.
- **Back to your normal wallpaper when it's quiet:** when nothing is playing, your usual desktop background fades back in, and the visualizer returns with the music.
- **Spotify now-playing:** album art, title, artist and progress, read from the Spotify desktop app through Windows. No sign-in, free accounts included.
- **Visualizer window** (optional): fullscreen, keyboard shortcuts, play/pause/skip, and audio files (mp3, wav, flac, m4a, aac, wma, aiff).

## Install

Run `MusicVisualizer-Setup-<version>.exe` (built into `dist\`). It installs for your Windows user only, so there's no admin prompt. You can choose:

- where to install (default `%LocalAppData%\Programs\Music Visualizer`),
- **Start with Windows**: on by default. It starts quietly in the tray when you sign in.
- a desktop shortcut, and
- whether to start the wallpaper when setup finishes.

It adds two Start-menu entries: **Music Visualizer** (runs the wallpaper) and **Music Visualizer Settings**.

Running a newer setup over an existing install updates it in place and keeps your settings. Setup also replaces version 1.0.0, which used a different installer.

To uninstall, run the setup again and click **Uninstall**, or use *Settings → Apps → Installed apps → Music Visualizer*. Both remove the app, its shortcuts, the startup entry and your settings.

Windows may show a SmartScreen warning the first time, because the installer isn't code-signed. Choose *More info → Run anyway*.

## Using it

**Start Music Visualizer and it becomes your wallpaper.** No window opens: the visualizer appears behind your desktop icons, and a notification points you to the tray icon the first time. It keeps running in the **system tray** (bottom-right of the taskbar; it may be under the **^** arrow).

| To… | Do this |
|---|---|
| Start the wallpaper | Start **Music Visualizer** (Start menu, desktop shortcut), or click **▶ Run as wallpaper** in settings, or right-click the tray icon → **Wallpaper** |
| Change how it looks | Click the tray icon, or start **Music Visualizer Settings** |
| Stop the wallpaper | **■ Stop wallpaper** in settings, or right-click the tray icon → **Wallpaper** |
| Quit | Right-click the tray icon → **Quit** (your normal wallpaper comes back) |

Starting **Music Visualizer** again while the wallpaper is already running opens settings.

### Settings

The left side shows a **live preview** shaped like your wallpaper screen, the **▶ Run as wallpaper** / **■ Stop wallpaper** button, and which **screen** the wallpaper covers (with two or more monitors). *Run as wallpaper* starts it and closes the settings window. Changes apply to the running wallpaper immediately and are saved automatically.

**Appearance tab**

| Setting | What it does |
|---|---|
| **Look** | The style (see below). Peak caps and reflection on/off. **Number of bars** (16 to 128; fewer looks bolder and simpler) and bar thickness. |
| **Colors** | *Song art colors* (the default): a gradient between the two main colors of the album cover that's playing, for every song. Black-and-white covers give greys and white, and when the music stops it keeps the last song's colors. It never switches to the rainbow, and colors blend over about a second when the song changes. *Rainbow*: cycles colors with the beat. *My colors*: a gradient between two colors; pick from the swatches or type a hex code like `#FF4D6D`. |
| **Position and size** | Move it left/right and up/down, scale it (**Size**), and set how much of the screen's **Width** it spans. **Center it** puts it back in the middle. |
| **Song title** | Where the song card (album art, song and artist) appears on the wallpaper: any corner, top or bottom center, or hidden. When it's hidden, *show it in the bottom right for a few seconds when a new song starts* (on by default) still announces each new song. |
| **Album art** | *Show the album cover in the middle*. The cover always stays in the center of the screen; the position sliders move only the visualizer. Radial and Ring size it to fit their circle; the other styles show it behind the visualizer, bouncing with the beat and the glow. When it's on you also get **Cover size**, where to write the **song and artist** (below, above, left or right of the cover, or hidden), and *Hide the song title card*. |
| **Intensity** | From *Calm* to *Wild*: how strongly it reacts to the music. Turn it up for quiet music. |
| **Sensitivity by frequency** | Four sliders that make parts of the sound move it more or less (see below). **Even out** resets them. |
| **Background** | The glow that pulses with the bass (on/off and strength), and a **picture behind the visualizer**: choose a file, or **Use my current wallpaper**. **Darken picture** helps the visualizer stand out. The picture is copied into the app's settings folder, so moving the original doesn't break it. |
| **Reset appearance** | Back to the defaults (keeps your picture). |

**Audio tab**

| Setting | What it does |
|---|---|
| **Listen to** | *Everything on this PC*, or *Only the apps I choose*. |
| App list | Apps that have played sound. Tick the ones to visualize. Ticked apps that aren't running are remembered and picked up when they start. Click **Refresh** after starting a new app. |
| **When nothing is playing** | *Show my normal wallpaper until music plays again* (on by default), after it has been quiet for 2 to 60 seconds (5 by default). It listens to what you chose above, so with only Spotify chosen, other sounds don't bring the visualizer back. |

**Styles**

| Style | Looks like |
|---|---|
| Bars | Classic spectrum bars with peak caps and a reflection |
| Mirror | Bars growing up and down from a centre line |
| Blocks | LED-meter style stacked blocks |
| Spectrum | A smooth filled curve, like a studio analyser |
| Radial | Bars around a circle that pulses with the bass |
| Ring | The sound wave bent into a circle |
| Wave | The raw sound wave, like an oscilloscope |

**Sensitivity by frequency**

| Slider | Range | What's usually there |
|---|---|---|
| Bass | 40–250 Hz | Kick drums, bass lines, 808s: the thump you feel |
| Low mids | 250 Hz–2 kHz | The body of vocals, guitars, piano and snare: where most melody lives |
| High mids | 2–6 kHz | Vocal clarity, snare crack, plucks and claps: the attack of each sound |
| Treble | 6–16 kHz | Hi-hats, cymbals, shakers and breath: the sparkle and air |

They affect the bar-based styles and Spectrum. Ring and Wave draw the raw sound wave, so only *Intensity* changes them.

**General tab:** **Start with Windows** starts it in the tray when you sign in, and brings the wallpaper back if it was on.

The bottom-left buttons open the **visualizer window** or **quit**.

### Visualizer window

| Control | What it does |
|---|---|
| **Open** / drag a file onto the window | Play and visualize an audio file |
| **Live audio** | Visualize what's playing on the PC, using the *Listen to* setting |
| **Style** | Switch to the next style |
| **Screen 1 / Screen 2…** | Move the wallpaper to the next monitor |
| **Wallpaper** | Turn the live wallpaper on or off |
| ⚙ | Open settings |
| Now-playing card ⏮ ⏯ ⏭ | Control Spotify (or whichever media app is playing) |

| Key | Action |
|---|---|
| `Space` | Play/pause the opened file |
| `1` to `7` | Bars, Radial, Wave, Mirror, Blocks, Spectrum, Ring |
| `F` or `F11` | Fullscreen (`Esc` to leave) |
| `W` | Toggle live wallpaper |
| `S` | Move the wallpaper to the next screen |
| `H` | Hide/show the control bar |

The visualizer window uses the same appearance settings. Closing it doesn't quit the app; it keeps running in the tray. The window's own view pauses while the wallpaper runs, so it isn't drawn twice.

### Spotify

Play something in the Spotify desktop app and the card shows it automatically. There's nothing to set up. Spotify never shares raw audio with apps, so the visuals come from your PC's sound output (or from the Spotify app itself if you choose it under *Listen to*). Spotify playing on another device, such as your phone or a speaker, isn't shown.

### Command line

| Command | What it does |
|---|---|
| `MusicVisualizer.exe` | Run as the wallpaper with no window. If it's already running: start the wallpaper, or open settings if the wallpaper is already on. |
| `MusicVisualizer.exe --settings` | Open settings (the *Music Visualizer Settings* shortcut) |
| `MusicVisualizer.exe --background` | Start quietly in the tray and restore the last state (used by *Start with Windows*) |
| `MusicVisualizer.exe --wallpaper` | Same as no arguments (kept for older shortcuts) |
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
| `%AppData%\MusicVisualizer\settings.json` | Wallpaper on/off and screen, style, appearance, listen mode and chosen apps |
| `%AppData%\MusicVisualizer\background-*.jpg/png` | Copy of the picture behind the visualizer, if you chose one |
| `%AppData%\MusicVisualizer\error.log` | Only created if something goes wrong |
| `HKCU\...\CurrentVersion\Run` → `MusicVisualizer` | *Start with Windows* |
| `HKCU\...\CurrentVersion\Uninstall\MusicVisualizer` | The *Installed apps* entry |

## Limitations

- The wallpaper covers one screen at a time.
- *Only the apps I choose* needs Windows 11, or Windows 10 build 20348 or later.
- An app shows in the list only after it has played sound since it started. Its child processes are included (browsers and Spotify play audio from a helper process).
- Now-playing only covers apps on this PC (the Spotify desktop app, or any app that reports to Windows' media controls).
- Monitors with different display scaling are untested.
- The fade back to your normal wallpaper uses Windows' main wallpaper picture and fit. With a different picture per monitor, or a slideshow mid-change, the fade can briefly show the wrong picture before the real one appears.

## How it works

See **[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)** for how the tray app, the audio capture (including per-app capture), the spectrum analysis, rendering, now-playing, the wallpaper trick and the installer work, and a map of the code.
