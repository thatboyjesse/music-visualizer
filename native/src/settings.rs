//! `%AppData%\MusicVisualizer\settings.json`, the same file and schema as `Settings/AppSettings.cs`
//! and `Settings/Appearance.cs`. The settings window (C#) owns most of it; the engine reads it
//! whenever it changes and writes back only the few values it changes itself (style cycle,
//! wallpaper on/off from the tray, the tray hint), patching them into the latest file so nothing
//! else the settings window saved is lost.

use std::path::PathBuf;

use serde::{Deserialize, Deserializer};
use serde_json::{Map, Value};

pub fn data_dir() -> PathBuf {
    let appdata = std::env::var_os("APPDATA").map(PathBuf::from).unwrap_or_else(|| PathBuf::from("."));
    appdata.join("MusicVisualizer")
}

pub fn file_path() -> PathBuf {
    data_dir().join("settings.json")
}

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum Mode {
    Bars,
    Radial,
    Wave,
    Mirror,
    Blocks,
    Spectrum,
    Ring,
}

impl Mode {
    pub const ALL: [Mode; 7] = [Mode::Bars, Mode::Radial, Mode::Wave, Mode::Mirror, Mode::Blocks, Mode::Spectrum, Mode::Ring];

    pub fn parse(s: &str) -> Option<Mode> {
        Mode::ALL.iter().copied().find(|m| m.name().eq_ignore_ascii_case(s.trim()))
    }

    pub fn name(self) -> &'static str {
        match self {
            Mode::Bars => "Bars",
            Mode::Radial => "Radial",
            Mode::Wave => "Wave",
            Mode::Mirror => "Mirror",
            Mode::Blocks => "Blocks",
            Mode::Spectrum => "Spectrum",
            Mode::Ring => "Ring",
        }
    }

    pub fn index(self) -> usize {
        Mode::ALL.iter().position(|&m| m == self).unwrap()
    }
}

/// C#'s JsonStringEnumConverter writes names and also accepts numbers; accept both.
fn enum_value<'de, D: Deserializer<'de>>(d: D, names: &[&str], default: usize) -> Result<usize, D::Error> {
    Ok(match Value::deserialize(d)? {
        Value::String(s) => names.iter().position(|n| n.eq_ignore_ascii_case(s.trim())).unwrap_or(default),
        Value::Number(n) => n.as_u64().map(|v| v as usize).filter(|&v| v < names.len()).unwrap_or(default),
        _ => default,
    })
}

macro_rules! string_enum {
    ($name:ident { $($variant:ident),* } default $default:ident) => {
        #[derive(Clone, Copy, PartialEq, Eq, Debug)]
        pub enum $name { $($variant),* }
        impl $name {
            const NAMES: &'static [&'static str] = &[$(stringify!($variant)),*];
            const VARIANTS: &'static [$name] = &[$($name::$variant),*];
        }
        impl Default for $name {
            fn default() -> Self { $name::$default }
        }
        impl<'de> Deserialize<'de> for $name {
            fn deserialize<D: Deserializer<'de>>(d: D) -> Result<Self, D::Error> {
                let default = Self::VARIANTS.iter().position(|v| *v == $name::$default).unwrap();
                Ok(Self::VARIANTS[enum_value(d, Self::NAMES, default)?])
            }
        }
    };
}

string_enum!(ColorMode { AlbumArt, Rainbow, Custom } default AlbumArt);
string_enum!(TitlePosition { BottomRight, BottomCenter, BottomLeft, TopRight, TopCenter, TopLeft, Hidden } default BottomRight);
string_enum!(CaptionPlace { Below, Above, Left, Right } default Below);
string_enum!(ListenMode { AllAudio, SelectedApps } default AllAudio);
string_enum!(CycleBy { Time, Songs } default Time);

/// How the visualizer looks (`Settings/Appearance.cs`). Defaults match the C# ones.
#[derive(Deserialize, Clone, Debug)]
#[serde(rename_all = "PascalCase", default)]
pub struct Appearance {
    pub peak_caps: bool,
    pub reflection: bool,
    pub bar_width: f64,
    pub bar_count: i32,
    #[serde(rename = "PositionX")]
    pub position_x: f64,
    #[serde(rename = "PositionY")]
    pub position_y: f64,
    pub size: f64,
    pub width: f64,
    pub title: TitlePosition,
    pub peek_card: bool,
    pub album_art: bool,
    pub album_size: f64,
    pub album_caption: bool,
    pub caption_place: CaptionPlace,
    pub album_bounce: f64,
    pub album_sharpness: f64,
    pub intensity: f64,
    pub eq_bass: f64,
    pub eq_low_mids: f64,
    pub eq_high_mids: f64,
    pub eq_treble: f64,
    pub frame_rate: i32,
    pub colors: ColorMode,
    pub color1: String,
    pub color2: String,
    pub glow: bool,
    pub glow_strength: f64,
    pub background_image: Option<String>,
    pub background_dim: f64,
}

impl Default for Appearance {
    fn default() -> Self {
        Self {
            peak_caps: true,
            reflection: true,
            bar_width: 0.72,
            bar_count: 96,
            position_x: 0.5,
            position_y: 0.5,
            size: 1.0,
            width: 1.0,
            title: TitlePosition::BottomRight,
            peek_card: true,
            album_art: false,
            album_size: 0.3,
            album_caption: true,
            caption_place: CaptionPlace::Below,
            album_bounce: 1.0,
            album_sharpness: 0.35,
            intensity: 1.0,
            eq_bass: 0.0,
            eq_low_mids: 0.0,
            eq_high_mids: 0.0,
            eq_treble: 0.0,
            frame_rate: 30,
            colors: ColorMode::AlbumArt,
            color1: "#7A5CFF".into(),
            color2: "#3FD0C9".into(),
            glow: true,
            glow_strength: 1.0,
            background_image: None,
            background_dim: 0.35,
        }
    }
}

/// The parts of `Settings/AppSettings.cs` the engine uses.
#[derive(Deserialize, Clone, Debug)]
#[serde(rename_all = "PascalCase", default)]
pub struct Settings {
    pub mode: String,
    pub look: Appearance,
    pub cycle_enabled: bool,
    pub cycle_modes: Vec<String>,
    pub cycle_by: CycleBy,
    pub cycle_seconds: i64,
    pub cycle_songs: i64,
    pub cycle_shuffle: bool,
    pub wallpaper_enabled: bool,
    pub wallpaper_screen: Option<String>,
    pub listen: ListenMode,
    pub apps: Vec<String>,
    pub pause_when_covered: bool,
    pub hide_when_silent: bool,
    pub silent_seconds: f64,
    pub tray_hint_shown: bool,
}

impl Default for Settings {
    fn default() -> Self {
        Self {
            mode: "Bars".into(),
            look: Appearance::default(),
            cycle_enabled: false,
            cycle_modes: vec![],
            cycle_by: CycleBy::Time,
            cycle_seconds: 300,
            cycle_songs: 1,
            cycle_shuffle: false,
            wallpaper_enabled: false,
            wallpaper_screen: None,
            listen: ListenMode::AllAudio,
            apps: vec![],
            pause_when_covered: true,
            hide_when_silent: true,
            silent_seconds: 5.0,
            tray_hint_shown: false,
        }
    }
}

impl Settings {
    pub fn mode(&self) -> Mode {
        Mode::parse(&self.mode).unwrap_or(Mode::Bars)
    }

    /// The styles to cycle through, in the order they're listed in settings (enum order), no repeats.
    pub fn cycle_list(&self) -> Vec<Mode> {
        let mut list: Vec<Mode> = self.cycle_modes.iter().filter_map(|n| Mode::parse(n)).collect();
        list.sort_by_key(|m| m.index());
        list.dedup();
        list
    }
}

/// The file as it was last read: the raw text (to tell real changes from our own writes) and the parsed settings.
pub struct Loaded {
    pub text: String,
    pub settings: Settings,
}

/// Reads settings.json, falling back to the backup the C# side keeps (settings.json.bak), then defaults.
pub fn load() -> Loaded {
    for path in [file_path(), backup_path()] {
        if let Ok(text) = std::fs::read_to_string(&path) {
            if let Ok(settings) = serde_json::from_str::<Settings>(strip_bom(&text)) {
                return Loaded { text, settings };
            }
        }
    }
    Loaded { text: String::new(), settings: Settings::default() }
}

/// Reads the main file only; None while it's missing or mid-write.
pub fn read_current() -> Option<Loaded> {
    let text = std::fs::read_to_string(file_path()).ok()?;
    let settings = serde_json::from_str::<Settings>(strip_bom(&text)).ok()?;
    Some(Loaded { text, settings })
}

fn strip_bom(s: &str) -> &str {
    s.strip_prefix('\u{feff}').unwrap_or(s)
}

fn backup_path() -> PathBuf {
    data_dir().join("settings.json.bak")
}

/// Changes some values in the latest settings file, keeping everything else exactly as it is,
/// and saves it the same way the C# side does (temp file, then an atomic replace that keeps a
/// backup). Returns the new file contents, or None if it couldn't be written.
pub fn patch(edit: impl FnOnce(&mut Map<String, Value>)) -> Option<Loaded> {
    let current = std::fs::read_to_string(file_path())
        .ok()
        .and_then(|t| serde_json::from_str::<Value>(strip_bom(&t)).ok())
        .or_else(|| std::fs::read_to_string(backup_path()).ok().and_then(|t| serde_json::from_str::<Value>(strip_bom(&t)).ok()));
    let mut root = match current {
        Some(Value::Object(m)) => m,
        _ => Map::new(),
    };
    edit(&mut root);
    let text = serde_json::to_string_pretty(&Value::Object(root)).ok()?;
    write_atomic(&text).ok()?;
    let settings = serde_json::from_str::<Settings>(&text).ok()?;
    Some(Loaded { text, settings })
}

fn write_atomic(text: &str) -> std::io::Result<()> {
    use windows::core::PCWSTR;
    use windows::Win32::Storage::FileSystem::{MoveFileExW, ReplaceFileW, MOVEFILE_REPLACE_EXISTING, REPLACE_FILE_FLAGS};

    std::fs::create_dir_all(data_dir())?;
    let path = file_path();
    let temp = data_dir().join("settings.json.tmp");
    std::fs::write(&temp, text)?;
    let w = |p: &PathBuf| crate::sys::wide(&p.to_string_lossy());
    let (target, source, backup) = (w(&path), w(&temp), w(&backup_path()));
    unsafe {
        if path.exists() {
            ReplaceFileW(PCWSTR(target.as_ptr()), PCWSTR(source.as_ptr()), PCWSTR(backup.as_ptr()), REPLACE_FILE_FLAGS(2) /* ignore merge errors */, None, None)
                .map_err(|e| std::io::Error::other(e.message()))?;
        } else {
            MoveFileExW(PCWSTR(source.as_ptr()), PCWSTR(target.as_ptr()), MOVEFILE_REPLACE_EXISTING).map_err(|e| std::io::Error::other(e.message()))?;
        }
    }
    Ok(())
}

/// The settings that belong to the wallpaper rather than to a style (`Appearance.CopySharedFrom`).
pub const SHARED_LOOK_KEYS: [&str; 5] = ["FrameRate", "BackgroundImage", "BackgroundDim", "Title", "PeekCard"];

/// `AppController.SetMode`: saves the current look under its style, switches to the new style's own
/// look (or a copy of the current one the first time), keeping the shared wallpaper settings.
pub fn switch_mode(root: &mut Map<String, Value>, to: Mode) {
    let current = root.get("Mode").and_then(|v| v.as_str()).and_then(Mode::parse).unwrap_or(Mode::Bars);
    let look = root.get("Look").cloned().unwrap_or_else(|| Value::Object(Map::new()));
    let looks = root.entry("Looks").or_insert_with(|| Value::Object(Map::new()));
    if !looks.is_object() {
        *looks = Value::Object(Map::new());
    }
    let looks = looks.as_object_mut().unwrap();
    looks.insert(current.name().into(), look.clone());
    if to != current {
        let mut next = looks.get(to.name()).cloned().filter(|v| v.is_object()).unwrap_or_else(|| look.clone());
        if let (Some(n), Some(l)) = (next.as_object_mut(), look.as_object()) {
            for key in SHARED_LOOK_KEYS {
                match l.get(key) {
                    Some(v) => {
                        n.insert(key.into(), v.clone());
                    }
                    None => {
                        n.remove(key);
                    }
                }
            }
        }
        root.insert("Look".into(), next);
    }
    root.insert("Mode".into(), Value::String(to.name().into()));
}
