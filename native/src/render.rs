//! Direct2D drawing: the native version of `Visualizer/VisualizerCanvas.cs` (all seven styles,
//! colours, glow, background picture, the album cover in the middle with its bounce and caption)
//! plus the now-playing card (`Controls/NowPlayingCard.xaml`) and the fade to the normal desktop
//! background while nothing plays.
//!
//! Coordinates are DIPs, like WPF: the render target's DPI is the monitor's, so sizes such as
//! "2 px rounded corners" or "15 pt title" match the C# app on scaled screens too.
//!
//! Brushes are created once and recoloured with SetColor when the palette changes; geometry for
//! the curved styles is rebuilt every frame (it changes every frame anyway).

use std::collections::HashMap;
use std::time::Instant;

use windows::core::{w, Result, PCWSTR};
use windows::Foundation::Numerics::Matrix3x2;
use windows::Win32::Foundation::*;
use windows::Win32::Graphics::Direct2D::Common::*;
use windows::Win32::Graphics::Direct2D::*;
use windows::Win32::Graphics::DirectWrite::*;
use windows::Win32::Graphics::Dxgi::Common::*;

use crate::analysis::Analyzer;
use crate::imaging::{Pixels, Rgb};
use crate::nowplaying::Track;
use crate::settings::{Appearance, CaptionPlace, ColorMode, Mode, TitlePosition};
use crate::wallpaper::Fit;

const BACKGROUND: Rgb = [0x0B, 0x0B, 0x12];

/// Everything that animates from frame to frame (`VisualizerCanvas.Tick`).
pub struct Visual {
    pub analyzer: Analyzer,
    hue_shift: f64,
    album_a: Option<Rgb>,
    album_b: Option<Rgb>,
    bounce: f64,
}

impl Visual {
    pub fn new() -> Self {
        Self { analyzer: Analyzer::new(96), hue_shift: 0.0, album_a: None, album_b: None, bounce: 0.0 }
    }

    /// Analyses the samples already copied into `analyzer.samples` and moves the animations on.
    pub fn tick(&mut self, look: &Appearance, rate: u32, dt: f64, album: Option<(Rgb, Rgb)>) {
        let bars = look.bar_count.clamp(8, 128) as usize;
        if bars != self.analyzer.band_count() {
            let samples = std::mem::take(&mut self.analyzer.samples);
            self.analyzer = Analyzer::new(bars);
            self.analyzer.samples = samples;
        }
        self.analyzer.gain = look.intensity;
        self.analyzer.set_eq([look.eq_bass, look.eq_low_mids, look.eq_high_mids, look.eq_treble]);
        self.analyzer.update(rate, dt as f32);
        let bass = self.analyzer.bass as f64;
        self.hue_shift = (self.hue_shift + dt * (6.0 + bass * 50.0)) % 360.0;

        // Album colours glide over about a second when the song changes.
        if let Some((t1, t2)) = album {
            match (self.album_a, self.album_b) {
                (Some(a), Some(b)) => {
                    let k = 1.0 - (-dt * 3.0).exp();
                    self.album_a = Some(step_towards(a, t1, k));
                    self.album_b = Some(step_towards(b, t2, k));
                }
                _ => {
                    self.album_a = Some(t1);
                    self.album_b = Some(t2);
                }
            }
        }

        // The cover's bounce: rises with the bass and falls back, shaped by the sharpness setting.
        let sharp = look.album_sharpness.clamp(0.0, 1.0);
        let target = bass.max(0.0).powf(1.0 + 3.0 * sharp);
        let rise = 1.0 - (-dt * (12.0 + 50.0 * sharp)).exp();
        let fall = 1.0 - (-dt * (3.0 + 22.0 * sharp)).exp();
        self.bounce += (target - self.bounce) * if target > self.bounce { rise } else { fall };
    }
}

/// `Lerp(..., snap: true)`: moves at least one step per channel so a glide always finishes.
fn step_towards(a: Rgb, b: Rgb, t: f64) -> Rgb {
    let s = |from: u8, to: u8| -> u8 {
        if from == to {
            return to;
        }
        let v = from as f64 + (to as f64 - from as f64) * t;
        let mut moved = v.round() as i32;
        if moved == from as i32 {
            moved += (to as i32 - from as i32).signum();
        }
        moved.clamp(0, 255) as u8
    };
    [s(a[0], b[0]), s(a[1], b[1]), s(a[2], b[2])]
}

/// What the now-playing card shows this frame.
pub struct Card<'a> {
    pub track: &'a Track,
    pub place: TitlePosition,
    pub opacity: f32,
}

pub struct Frame<'a> {
    pub mode: Mode,
    pub look: &'a Appearance,
    pub visual: &'a Visual,
    pub track: Option<&'a Track>,
    pub card: Option<Card<'a>>,
    /// 0..1: how much of the normal desktop background covers everything (quiet-time fade).
    pub desk: f32,
    pub now: Instant,
}

/// The normal desktop background, copied for the fade (`WallpaperWindow.LoadDesktopBackground`).
pub struct Desk {
    pub color: (f32, f32, f32),
    pub picture: Option<Pixels>,
    pub fit: Fit,
}

pub fn parse_color(hex: &str, fallback: Rgb) -> Rgb {
    let h = hex.trim().trim_start_matches('#');
    let v = |s: &str| u8::from_str_radix(s, 16).ok();
    let parsed = match h.len() {
        6 => (|| Some([v(&h[0..2])?, v(&h[2..4])?, v(&h[4..6])?]))(),
        8 => (|| Some([v(&h[2..4])?, v(&h[4..6])?, v(&h[6..8])?]))(),
        3 => (|| {
            let d = |i: usize| v(&h[i..i + 1]).map(|x| x * 17);
            Some([d(0)?, d(1)?, d(2)?])
        })(),
        _ => None,
    };
    parsed.unwrap_or(fallback)
}

fn color(c: Rgb, a: f32) -> D2D1_COLOR_F {
    D2D1_COLOR_F { r: c[0] as f32 / 255.0, g: c[1] as f32 / 255.0, b: c[2] as f32 / 255.0, a }
}

fn rect(x: f32, y: f32, w: f32, h: f32) -> D2D_RECT_F {
    D2D_RECT_F { left: x, top: y, right: x + w, bottom: y + h }
}

fn pt(x: f32, y: f32) -> D2D_POINT_2F {
    D2D_POINT_2F { x, y }
}

fn ident() -> Matrix3x2 {
    Matrix3x2 { M11: 1.0, M12: 0.0, M21: 0.0, M22: 1.0, M31: 0.0, M32: 0.0 }
}

struct Gradient {
    key: Vec<Rgb>,
    brush: ID2D1LinearGradientBrush,
}

struct Text {
    key: String,
    layout: IDWriteTextLayout,
    width: f32,
    height: f32,
}

/// Device resources: recreated if the GPU device is lost or the window changes.
struct Target {
    rt: ID2D1HwndRenderTarget,
    solid: Vec<Option<(ID2D1SolidColorBrush, Rgb)>>,
    faded: HashMap<(u16, u8), (ID2D1SolidColorBrush, Rgb)>,
    misc: ID2D1SolidColorBrush,
    round: ID2D1StrokeStyle,
    bevel: ID2D1StrokeStyle,
    glow: Option<(Rgb, ID2D1RadialGradientBrush)>,
    gradients: [Option<Gradient>; 3],
    background: Option<(String, ID2D1Bitmap)>,
    art: Option<(u64, ID2D1Bitmap, ID2D1BitmapBrush)>,
    desk: Option<ID2D1Bitmap>,
}

pub struct Renderer {
    factory: ID2D1Factory,
    dwrite: IDWriteFactory,
    hwnd: HWND,
    px: (u32, u32),
    dpi: f32,
    target: Option<Target>,
    palette: Vec<Rgb>,
    palette_key: Option<(bool, Rgb, Rgb)>,
    texts: HashMap<&'static str, Text>,
    /// The picture last loaded (or tried) for the current device resources.
    background_path: Option<String>,
    pub desk: Option<Desk>,
    desk_dirty: bool,
}

impl Renderer {
    pub fn new(hwnd: HWND, px: (u32, u32), dpi: f32) -> Result<Self> {
        unsafe {
            let factory: ID2D1Factory = D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, None)?;
            let dwrite: IDWriteFactory = DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED)?;
            Ok(Self {
                factory,
                dwrite,
                hwnd,
                px,
                dpi,
                target: None,
                palette: vec![[0; 3]; 360],
                palette_key: None,
                texts: HashMap::new(),
                background_path: None,
                desk: None,
                desk_dirty: false,
            })
        }
    }

    /// The window was moved to another screen or resized.
    pub fn resize(&mut self, px: (u32, u32), dpi: f32) {
        self.px = px;
        if (dpi - self.dpi).abs() > 0.1 {
            self.dpi = dpi;
            self.target = None;
            return;
        }
        if let Some(t) = &self.target {
            unsafe {
                if t.rt.Resize(&D2D_SIZE_U { width: px.0, height: px.1 }).is_err() {
                    self.target = None;
                }
            }
        }
    }

    pub fn set_desk(&mut self, desk: Option<Desk>) {
        self.desk = desk;
        self.desk_dirty = true;
    }

    /// Frees the GPU resources (while nothing is drawn for a long time). They come back on the next frame.
    pub fn release(&mut self) {
        self.target = None;
        self.texts.clear();
    }

    fn size(&self) -> (f32, f32) {
        (self.px.0 as f32 * 96.0 / self.dpi, self.px.1 as f32 * 96.0 / self.dpi)
    }

    fn ensure_target(&mut self) -> Result<()> {
        if self.target.is_some() {
            return Ok(());
        }
        unsafe {
            let props = D2D1_RENDER_TARGET_PROPERTIES {
                r#type: D2D1_RENDER_TARGET_TYPE_DEFAULT,
                pixelFormat: D2D1_PIXEL_FORMAT { format: DXGI_FORMAT_B8G8R8A8_UNORM, alphaMode: D2D1_ALPHA_MODE_IGNORE },
                dpiX: self.dpi,
                dpiY: self.dpi,
                ..Default::default()
            };
            let rt = self.factory.CreateHwndRenderTarget(
                &props,
                &D2D1_HWND_RENDER_TARGET_PROPERTIES {
                    hwnd: self.hwnd,
                    pixelSize: D2D_SIZE_U { width: self.px.0, height: self.px.1 },
                    presentOptions: D2D1_PRESENT_OPTIONS_NONE,
                },
            )?;
            rt.SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE);
            let misc = rt.CreateSolidColorBrush(&color([255; 3], 1.0), None)?;
            let stroke = |cap: D2D1_CAP_STYLE, join: D2D1_LINE_JOIN| {
                self.factory.CreateStrokeStyle(
                    &D2D1_STROKE_STYLE_PROPERTIES { startCap: cap, endCap: cap, dashCap: cap, lineJoin: join, miterLimit: 10.0, ..Default::default() },
                    None,
                )
            };
            let round = stroke(D2D1_CAP_STYLE_ROUND, D2D1_LINE_JOIN_ROUND)?;
            let bevel = stroke(D2D1_CAP_STYLE_FLAT, D2D1_LINE_JOIN_BEVEL)?;
            self.target = Some(Target {
                rt,
                solid: (0..360).map(|_| None).collect(),
                faded: HashMap::new(),
                misc,
                round,
                bevel,
                glow: None,
                gradients: [None, None, None],
                background: None,
                art: None,
                desk: None,
            });
            self.desk_dirty = true;
            self.background_path = None; // upload the picture again
        }
        Ok(())
    }

    // ------------------------------------------------------------ colours

    fn ensure_palette(&mut self, look: &Appearance, v: &Visual) {
        let rainbow = look.colors == ColorMode::Rainbow;
        let (c1, c2) = gradient_colors(look, v);
        let key = (rainbow, c1, c2);
        if self.palette_key == Some(key) {
            return;
        }
        self.palette_key = Some(key);
        for k in 0..360 {
            self.palette[k] = if rainbow {
                crate::imaging::hsv_bytes(k as f64, 0.75, 1.0)
            } else {
                let t = k as f64 / 359.0;
                let l = |a: u8, b: u8| (a as f64 + (b as f64 - a as f64) * t) as u8;
                [l(c1[0], c2[0]), l(c1[1], c2[1]), l(c1[2], c2[2])]
            };
        }
    }

    pub fn draw(&mut self, f: &Frame) -> Result<()> {
        self.ensure_target()?;
        self.ensure_palette(f.look, f.visual);
        self.ensure_images(f);
        let (w, h) = self.size();
        let t = self.target.as_ref().unwrap();
        unsafe {
            t.rt.BeginDraw();
            t.rt.SetTransform(&ident());
        }
        let result = unsafe { self.draw_scene(f, w, h) };
        let t = self.target.as_ref().unwrap();
        let end = unsafe { t.rt.EndDraw(None, None) };
        if let Err(e) = &end {
            if e.code() == D2DERR_RECREATE_TARGET {
                self.target = None;
                return Ok(());
            }
        }
        result.and(end)
    }

    /// Bitmaps for the background picture, the cover and the desktop copy, made when they change.
    fn ensure_images(&mut self, f: &Frame) {
        let Some(t) = self.target.as_mut() else { return };
        let have = t.background.as_ref().map(|b| b.0.clone());
        if f.look.background_image != have && f.look.background_image != self.background_path {
            // Decoded only as large as the screen needs, uploaded, and the decoded copy dropped.
            self.background_path = f.look.background_image.clone();
            t.background = None;
            if let Some(path) = &self.background_path {
                if let Some(p) = crate::imaging::load_file_cover(path, self.px.0, self.px.1) {
                    if let Ok(b) = unsafe { make_bitmap(&t.rt, &p) } {
                        t.background = Some((path.clone(), b));
                    }
                }
            }
        }
        unsafe {
            let art = f.track.and_then(|tr| tr.art.as_ref().map(|a| (tr.art_id, a.clone())));
            match (&art, &t.art) {
                (Some((id, _)), Some((have, _, _))) if id == have => {}
                (Some((id, pixels)), _) => {
                    t.art = make_bitmap(&t.rt, pixels).ok().and_then(|b| {
                        let props = D2D1_BITMAP_BRUSH_PROPERTIES {
                            extendModeX: D2D1_EXTEND_MODE_CLAMP,
                            extendModeY: D2D1_EXTEND_MODE_CLAMP,
                            interpolationMode: D2D1_BITMAP_INTERPOLATION_MODE_LINEAR,
                        };
                        t.rt.CreateBitmapBrush(&b, Some(&props), None).ok().map(|brush| (*id, b, brush))
                    });
                }
                (None, Some(_)) => t.art = None,
                (None, None) => {}
            }
            if self.desk_dirty {
                self.desk_dirty = false;
                t.desk = self.desk.as_ref().and_then(|d| d.picture.as_ref()).and_then(|p| make_bitmap(&t.rt, p).ok());
            }
        }
    }

    unsafe fn draw_scene(&mut self, f: &Frame, w: f32, h: f32) -> Result<()> {
        let look = f.look;
        let v = f.visual;
        let a = &v.analyzer;
        {
            let t = self.target.as_ref().unwrap();
            t.rt.Clear(Some(&color(BACKGROUND, 1.0)));
            // Background picture: fill the screen and crop the overflow, like Windows' "Fill".
            if let Some((_, b)) = &t.background {
                let s = b.GetSize();
                let scale = (w / s.width).max(h / s.height);
                let (iw, ih) = (s.width * scale, s.height * scale);
                t.rt.DrawBitmap(b, Some(&rect((w - iw) / 2.0, (h - ih) / 2.0, iw, ih)), 1.0, D2D1_BITMAP_INTERPOLATION_MODE_LINEAR, None);
                if look.background_dim > 0.0 {
                    t.misc.SetColor(&color([0; 3], look.background_dim.clamp(0.0, 0.95) as f32));
                    t.rt.FillRectangle(&rect(0.0, 0.0, w, h), &t.misc);
                }
            }
        }

        let cx = (look.position_x * w as f64) as f32;
        let cy = (look.position_y * h as f64) as f32;
        if look.glow && look.glow_strength > 0.0 {
            let pulse = (a.bass * a.bass) as f64;
            let gr = (w.max(h) as f64 * (0.45 + pulse * 0.25) * look.size.max(0.5)) as f32;
            let idx = self.color_index(look, v, 0.3, 0.0) / 10 * 10;
            let c = self.palette[idx];
            let opacity = ((0.25 + pulse * 1.5) * look.glow_strength).clamp(0.0, 1.0) as f32;
            let t = self.target.as_mut().unwrap();
            if t.glow.as_ref().map(|g| g.0) != Some(c) {
                let stops = [
                    D2D1_GRADIENT_STOP { position: 0.0, color: color(c, 80.0 / 255.0) },
                    D2D1_GRADIENT_STOP { position: 1.0, color: color(c, 0.0) },
                ];
                let coll = t.rt.CreateGradientStopCollection(&stops, D2D1_GAMMA_2_2, D2D1_EXTEND_MODE_CLAMP)?;
                let props = D2D1_RADIAL_GRADIENT_BRUSH_PROPERTIES { center: pt(0.0, 0.0), gradientOriginOffset: pt(0.0, 0.0), radiusX: 1.0, radiusY: 1.0 };
                t.glow = Some((c, t.rt.CreateRadialGradientBrush(&props, None, &coll)?));
            }
            let g = &t.glow.as_ref().unwrap().1;
            g.SetCenter(pt(cx, cy));
            g.SetRadiusX(gr);
            g.SetRadiusY(gr);
            g.SetOpacity(opacity);
            t.rt.FillEllipse(&D2D1_ELLIPSE { point: pt(cx, cy), radiusX: gr, radiusY: gr }, g);
        }

        // Album art: always at the centre of the screen. Round styles size it to their circle;
        // the others draw it behind the visualizer, bouncing with the bass.
        let round = matches!(f.mode, Mode::Radial | Mode::Ring);
        let has_art = look.album_art && f.track.map(|t| t.art.is_some()).unwrap_or(false) && self.target.as_ref().unwrap().art.is_some();
        let (mx, my) = (w / 2.0, h / 2.0);
        let bounce_amount = look.album_bounce.clamp(0.0, 3.0);
        let (mut cover_size, mut caption_distance) = (0.0f64, 0.0f64);
        if has_art && !round {
            let base = look.album_size.clamp(0.08, 0.8) * h as f64;
            let side = (base * (1.0 + v.bounce * 0.12 * bounce_amount)) as f32;
            let r = rect(mx - side / 2.0, my - side / 2.0, side, side);
            self.fill_art(r, side * 0.06, false);
            cover_size = base;
            caption_distance = base / 2.0 * (1.0 + 0.12 * bounce_amount) + caption_gap(base);
        }

        match f.mode {
            Mode::Bars => self.draw_bars(look, v, w, h, cx, cy),
            Mode::Radial => self.draw_radial(look, v, w, h, cx, cy),
            Mode::Wave => self.draw_wave(look, v, w, h, cx, cy)?,
            Mode::Mirror => self.draw_mirror(look, v, w, h, cx, cy),
            Mode::Blocks => self.draw_blocks(look, v, w, h, cx, cy),
            Mode::Spectrum => self.draw_spectrum(look, v, w, h, cx, cy)?,
            Mode::Ring => self.draw_ring(look, v, w, h, cx, cy)?,
        }

        if has_art && round {
            let size = w.min(h) as f64 * look.size;
            let base_r = if f.mode == Mode::Radial { size * 0.17 - 12.0 } else { size * 0.22 * 0.72 }.max(4.0);
            let radius = (base_r * (1.0 + v.bounce * 0.3 * bounce_amount)) as f32;
            self.fill_art(rect(mx - radius, my - radius, radius * 2.0, radius * 2.0), radius, true);
            cover_size = base_r * 2.0;
            let clear = base_r * (1.0 + 0.3 * bounce_amount) + caption_gap(base_r * 2.0);
            let around = ((cx - mx).powi(2) + (cy - my).powi(2)).sqrt() < base_r as f32;
            caption_distance = if around { clear.max(size * 0.38) } else { clear };
        }

        if has_art && look.album_caption {
            if let Some(track) = f.track {
                self.draw_caption(look, track, mx, my, caption_distance as f32, cover_size as f32)?;
            }
        }

        if let Some(card) = &f.card {
            self.draw_card(card, w, h, f.now)?;
        }

        if f.desk > 0.0 {
            self.draw_desk(f.desk, w, h);
        }
        Ok(())
    }

    // ------------------------------------------------------------ helpers

    fn color_index(&self, look: &Appearance, v: &Visual, t: f64, spread: f64) -> usize {
        if look.colors != ColorMode::Rainbow {
            return (t * 359.0).clamp(0.0, 359.0) as usize;
        }
        ((((v.hue_shift + t * spread) as i64) % 360 + 360) % 360) as usize
    }

    /// The palette brush for an index, recoloured if the palette changed.
    fn solid(&mut self, idx: usize) -> ID2D1SolidColorBrush {
        let c = self.palette[idx];
        let t = self.target.as_mut().unwrap();
        match &mut t.solid[idx] {
            Some((b, have)) => {
                if *have != c {
                    unsafe { b.SetColor(&color(c, 1.0)) };
                    *have = c;
                }
                b.clone()
            }
            slot @ None => {
                let b = unsafe { t.rt.CreateSolidColorBrush(&color(c, 1.0), None).unwrap() };
                *slot = Some((b.clone(), c));
                b
            }
        }
    }

    /// A see-through copy of a palette colour (instead of an opacity layer).
    fn faded(&mut self, idx: usize, opacity: f64) -> ID2D1SolidColorBrush {
        let pct = (opacity.clamp(0.0, 1.0) * 100.0).round() as u8;
        let c = self.palette[idx];
        let t = self.target.as_mut().unwrap();
        let alpha = (255 * pct as u32 / 100) as f32 / 255.0;
        let entry = t.faded.entry((idx as u16, pct)).or_insert_with(|| unsafe {
            (t.rt.CreateSolidColorBrush(&color(c, alpha), None).unwrap(), c)
        });
        if entry.1 != c {
            unsafe { entry.0.SetColor(&color(c, alpha)) };
            entry.1 = c;
        }
        entry.0.clone()
    }

    /// A left-to-right gradient through the current colours (`SpectrumBrush`), cached by colours.
    unsafe fn gradient(&mut self, slot: usize, colors: Vec<Rgb>, x0: f32, x1: f32) -> Result<ID2D1LinearGradientBrush> {
        let t = self.target.as_mut().unwrap();
        let rebuild = t.gradients[slot].as_ref().map(|g| g.key != colors).unwrap_or(true);
        if rebuild {
            let n = colors.len();
            let stops: Vec<D2D1_GRADIENT_STOP> =
                colors.iter().enumerate().map(|(i, c)| D2D1_GRADIENT_STOP { position: i as f32 / (n - 1) as f32, color: color(*c, 1.0) }).collect();
            let coll = t.rt.CreateGradientStopCollection(&stops, D2D1_GAMMA_2_2, D2D1_EXTEND_MODE_CLAMP)?;
            let props = D2D1_LINEAR_GRADIENT_BRUSH_PROPERTIES { startPoint: pt(0.0, 0.0), endPoint: pt(1.0, 0.0) };
            let brush = t.rt.CreateLinearGradientBrush(&props, None, &coll)?;
            t.gradients[slot] = Some(Gradient { key: colors, brush });
        }
        let b = t.gradients[slot].as_ref().unwrap().brush.clone();
        b.SetStartPoint(pt(x0, 0.0));
        b.SetEndPoint(pt(x1.max(x0 + 1.0), 0.0));
        b.SetOpacity(1.0);
        Ok(b)
    }

    fn spectrum_colors(&self, look: &Appearance, v: &Visual) -> Vec<Rgb> {
        (0..=4).map(|k| self.palette[self.color_index(look, v, k as f64 / 4.0, 200.0)]).collect()
    }

    fn fill_art(&mut self, r: D2D_RECT_F, radius: f32, ellipse: bool) {
        let t = self.target.as_ref().unwrap();
        let Some((_, bitmap, brush)) = &t.art else { return };
        unsafe {
            let s = bitmap.GetSize();
            let (side_w, side_h) = (r.right - r.left, r.bottom - r.top);
            // UniformToFill: cover the area, centred.
            let scale = (side_w / s.width).max(side_h / s.height);
            let tx = r.left + (side_w - s.width * scale) / 2.0;
            let ty = r.top + (side_h - s.height * scale) / 2.0;
            brush.SetTransform(&Matrix3x2 { M11: scale, M12: 0.0, M21: 0.0, M22: scale, M31: tx, M32: ty });
            brush.SetOpacity(1.0);
            if ellipse {
                let c = pt((r.left + r.right) / 2.0, (r.top + r.bottom) / 2.0);
                t.rt.FillEllipse(&D2D1_ELLIPSE { point: c, radiusX: radius, radiusY: radius }, brush);
            } else {
                t.rt.FillRoundedRectangle(&D2D1_ROUNDED_RECT { rect: r, radiusX: radius, radiusY: radius }, brush);
            }
        }
    }

    fn bars_width(look: &Appearance, w: f32) -> f32 {
        ((w - 48.0) * look.width.clamp(0.1, 1.0) as f32).max(40.0)
    }

    // ------------------------------------------------------------ styles

    fn draw_bars(&mut self, look: &Appearance, v: &Visual, w: f32, h: f32, cx: f32, cy: f32) {
        let (bands, peaks) = (&v.analyzer.bands, &v.analyzer.peaks);
        let n = bands.len();
        let width = Self::bars_width(look, w);
        let max_h = h * 0.62 * look.size as f32;
        let group_h = max_h * if look.reflection { 1.3 } else { 1.0 };
        let baseline = cy - group_h / 2.0 + max_h;
        let left = cx - width / 2.0;
        let slot = width / n as f32;
        let bar_w = (slot * look.bar_width.clamp(0.1, 1.0) as f32).max(1.0);
        for i in 0..n {
            let x = left + i as f32 * slot + (slot - bar_w) / 2.0;
            let bh = (bands[i] * max_h).max(2.0);
            let brush = self.solid(self.color_index(look, v, i as f64 / n as f64, 200.0));
            let rt = &self.target.as_ref().unwrap().rt;
            unsafe {
                rt.FillRoundedRectangle(&D2D1_ROUNDED_RECT { rect: rect(x, baseline - bh, bar_w, bh), radiusX: 2.0, radiusY: 2.0 }, &brush);
                if look.peak_caps {
                    let py = baseline - peaks[i] * max_h - 6.0;
                    rt.FillRectangle(&rect(x, py, bar_w, 2.5), &brush);
                }
            }
        }
        if !look.reflection {
            return;
        }
        for i in 0..n {
            let x = left + i as f32 * slot + (slot - bar_w) / 2.0;
            let rh = bands[i] * max_h * 0.3;
            if rh < 1.0 {
                continue;
            }
            let brush = self.faded(self.color_index(look, v, i as f64 / n as f64, 200.0), 0.16);
            unsafe { self.target.as_ref().unwrap().rt.FillRectangle(&rect(x, baseline + 4.0, bar_w, rh), &brush) };
        }
    }

    fn draw_radial(&mut self, look: &Appearance, v: &Visual, w: f32, h: f32, cx: f32, cy: f32) {
        let bands = &v.analyzer.bands;
        let n = bands.len();
        let size = w.min(h) as f64 * look.size;
        let r0 = size * 0.17;
        let bass = v.analyzer.bass as f64;
        let r = r0 * (1.0 + bass * bass * 0.35);
        let max_len = size * 0.28;
        let pen_w = (2.0 * std::f64::consts::PI * r0 / (2.0 * n as f64) * look.bar_width.clamp(0.1, 1.0) / 0.72 * 0.6).max(1.5) as f32;
        for i in 0..n {
            let len = 3.0 + bands[i] as f64 * max_len;
            let brush = self.solid(self.color_index(look, v, i as f64 / n as f64, 240.0));
            let t = (i as f64 + 0.5) / n as f64 * std::f64::consts::PI;
            let (cos, sin) = ((-std::f64::consts::FRAC_PI_2 + t).cos(), (-std::f64::consts::FRAC_PI_2 + t).sin());
            let (x0, y0) = (cos * r, sin * r);
            let (x1, y1) = (cos * (r + len), sin * (r + len));
            let tg = self.target.as_ref().unwrap();
            unsafe {
                tg.rt.DrawLine(pt(cx + x0 as f32, cy + y0 as f32), pt(cx + x1 as f32, cy + y1 as f32), &brush, pen_w, &tg.round);
                tg.rt.DrawLine(pt(cx - x0 as f32, cy + y0 as f32), pt(cx - x1 as f32, cy + y1 as f32), &brush, pen_w, &tg.round);
            }
        }
        let brush = self.solid(self.color_index(look, v, 0.0, 0.0));
        let ring = ((r - 8.0).max(1.0)) as f32;
        unsafe {
            self.target.as_ref().unwrap().rt.DrawEllipse(&D2D1_ELLIPSE { point: pt(cx, cy), radiusX: ring, radiusY: ring }, &brush, 2.0, None);
        }
    }

    unsafe fn draw_wave(&mut self, look: &Appearance, v: &Visual, w: f32, h: f32, cx: f32, cy: f32) -> Result<()> {
        let s = &v.analyzer.samples;
        let n = s.len();
        // Start at a rising zero crossing so the wave doesn't jitter sideways.
        let mut start = 0;
        for i in 1..n / 2 {
            if s[i - 1] < 0.0 && s[i] >= 0.0 {
                start = i;
                break;
            }
        }
        const POINTS: usize = 256;
        let span = n / 2;
        let width = w * look.width.clamp(0.1, 1.0) as f32;
        let left = cx - width / 2.0;
        let amp = h as f64 * 0.38 * 1.6 * look.size * look.intensity;
        let limit = h as f64 * 0.45 * look.size;
        let pts: Vec<D2D_POINT_2F> = (0..POINTS)
            .map(|p| {
                let val = s[start + p * span / POINTS] as f64;
                pt(left + p as f32 * width / (POINTS - 1) as f32, cy - (val * amp).clamp(-limit, limit) as f32)
            })
            .collect();
        let geo = self.factory.CreatePathGeometry()?;
        let sink = geo.Open()?;
        sink.BeginFigure(pts[0], D2D1_FIGURE_BEGIN_HOLLOW);
        sink.AddLines(&pts[1..]);
        sink.EndFigure(D2D1_FIGURE_END_OPEN);
        sink.Close()?;

        // Custom and album colours: a gradient along the line. Rainbow: one moving hue.
        let brush: ID2D1Brush = if look.colors != ColorMode::Rainbow {
            let colors = vec![self.palette[0], self.palette[359]];
            self.gradient(0, colors, left, left + width)?.into()
        } else {
            let b = self.solid(self.color_index(look, v, 0.0, 0.0));
            windows::core::Interface::cast(&b)?
        };
        let t = self.target.as_ref().unwrap();
        brush.SetOpacity(0.18);
        t.rt.DrawGeometry(&geo, &brush, 8.0, &t.bevel);
        brush.SetOpacity(1.0);
        t.rt.DrawGeometry(&geo, &brush, 2.5, &t.bevel);
        Ok(())
    }

    fn draw_mirror(&mut self, look: &Appearance, v: &Visual, w: f32, h: f32, cx: f32, cy: f32) {
        let (bands, peaks) = (&v.analyzer.bands, &v.analyzer.peaks);
        let n = bands.len();
        let width = Self::bars_width(look, w);
        let half = h * 0.36 * look.size as f32;
        let left = cx - width / 2.0;
        let slot = width / n as f32;
        let bar_w = (slot * look.bar_width.clamp(0.1, 1.0) as f32).max(1.0);
        for i in 0..n {
            let x = left + i as f32 * slot + (slot - bar_w) / 2.0;
            let bh = (bands[i] * half).max(1.5);
            let brush = self.solid(self.color_index(look, v, i as f64 / n as f64, 200.0));
            let rt = &self.target.as_ref().unwrap().rt;
            unsafe {
                rt.FillRoundedRectangle(&D2D1_ROUNDED_RECT { rect: rect(x, cy - bh, bar_w, bh * 2.0), radiusX: 2.0, radiusY: 2.0 }, &brush);
                if look.peak_caps {
                    let p = peaks[i] * half + 5.0;
                    rt.FillRectangle(&rect(x, cy - p - 2.5, bar_w, 2.5), &brush);
                    rt.FillRectangle(&rect(x, cy + p, bar_w, 2.5), &brush);
                }
            }
        }
    }

    fn draw_blocks(&mut self, look: &Appearance, v: &Visual, w: f32, h: f32, cx: f32, cy: f32) {
        let (bands, peaks) = (&v.analyzer.bands, &v.analyzer.peaks);
        let n = bands.len();
        const ROWS: i32 = 20;
        let width = Self::bars_width(look, w);
        let max_h = h * 0.62 * look.size as f32;
        let group_h = max_h * if look.reflection { 1.3 } else { 1.0 };
        let baseline = cy - group_h / 2.0 + max_h;
        let left = cx - width / 2.0;
        let slot = width / n as f32;
        let bar_w = (slot * look.bar_width.clamp(0.1, 1.0) as f32).max(1.0);
        let cell = max_h / ROWS as f32;
        let block_h = (cell * 0.72).max(1.0);
        for i in 0..n {
            let x = left + i as f32 * slot + (slot - bar_w) / 2.0;
            let idx = self.color_index(look, v, i as f64 / n as f64, 200.0);
            let brush = self.solid(idx);
            let lit = (bands[i] as f64 * ROWS as f64).round() as i32;
            let rt = self.target.as_ref().unwrap().rt.clone();
            unsafe {
                for r in 0..lit {
                    rt.FillRectangle(&rect(x, baseline - (r + 1) as f32 * cell, bar_w, block_h), &brush);
                }
                if look.peak_caps {
                    let pr = (ROWS - 1).min((peaks[i] as f64 * ROWS as f64).round() as i32);
                    if pr >= lit {
                        let b = self.faded(idx, 0.6);
                        rt.FillRectangle(&rect(x, baseline - (pr + 1) as f32 * cell, bar_w, block_h), &b);
                    }
                }
                if look.reflection && lit > 0 {
                    let b = self.faded(idx, 0.14);
                    for r in 0..lit.min(6) {
                        rt.FillRectangle(&rect(x, baseline + 4.0 + r as f32 * cell, bar_w, block_h), &b);
                    }
                }
            }
        }
    }

    unsafe fn draw_spectrum(&mut self, look: &Appearance, v: &Visual, w: f32, h: f32, cx: f32, cy: f32) -> Result<()> {
        let bands = &v.analyzer.bands;
        let n = bands.len();
        let width = Self::bars_width(look, w);
        let max_h = h * 0.62 * look.size as f32;
        let group_h = max_h * if look.reflection { 1.3 } else { 1.0 };
        let baseline = cy - group_h / 2.0 + max_h;
        let left = cx - width / 2.0;
        let p = |i: usize| pt(left + (i as f32 + 0.5) * width / n as f32, baseline - (bands[i] * max_h).max(1.0));

        // Quadratic curves through the midpoints between neighbours give a smooth line.
        let build = |closed: bool| -> Result<ID2D1PathGeometry> {
            let geo = self.factory.CreatePathGeometry()?;
            let sink = geo.Open()?;
            let first = p(0);
            if closed {
                sink.BeginFigure(pt(left, baseline), D2D1_FIGURE_BEGIN_FILLED);
                sink.AddLine(pt(left, first.y));
                sink.AddLine(first);
            } else {
                sink.BeginFigure(first, D2D1_FIGURE_BEGIN_HOLLOW);
            }
            for i in 1..n {
                let (a, b) = (p(i - 1), p(i));
                sink.AddQuadraticBezier(&D2D1_QUADRATIC_BEZIER_SEGMENT { point1: a, point2: pt((a.x + b.x) / 2.0, (a.y + b.y) / 2.0) });
            }
            let last = p(n - 1);
            sink.AddLine(last);
            if closed {
                sink.AddLine(pt(left + width, last.y));
                sink.AddLine(pt(left + width, baseline));
                sink.EndFigure(D2D1_FIGURE_END_CLOSED);
            } else {
                sink.EndFigure(D2D1_FIGURE_END_OPEN);
            }
            sink.Close()?;
            Ok(geo)
        };
        let line = build(false)?;
        let fill = build(true)?;
        let colors = self.spectrum_colors(look, v);
        let fill_brush = self.gradient(1, colors.clone(), left, left + width)?;
        let t = self.target.as_ref().unwrap();
        fill_brush.SetOpacity(0.28);
        t.rt.FillGeometry(&fill, &fill_brush, None);
        if look.reflection {
            fill_brush.SetOpacity(0.12);
            t.rt.SetTransform(&Matrix3x2 { M11: 1.0, M12: 0.0, M21: 0.0, M22: -0.3, M31: 0.0, M32: baseline * 1.3 + 4.0 });
            t.rt.FillGeometry(&fill, &fill_brush, None);
            t.rt.SetTransform(&ident());
        }
        let (x0, x1) = (p(0).x, p(n - 1).x);
        let line_brush = self.gradient(2, colors, x0, x1)?;
        let t = self.target.as_ref().unwrap();
        t.rt.DrawGeometry(&line, &line_brush, 3.0, &t.bevel);
        Ok(())
    }

    unsafe fn draw_ring(&mut self, look: &Appearance, v: &Visual, w: f32, h: f32, cx: f32, cy: f32) -> Result<()> {
        let s = &v.analyzer.samples;
        let n = s.len();
        const POINTS: usize = 200;
        let size = w.min(h) as f64 * look.size;
        let bass = v.analyzer.bass as f64;
        let r0 = size * 0.22 * (1.0 + bass * bass * 0.25);
        let amp = size * 0.18 * look.intensity;
        let mut pts = Vec::with_capacity(POINTS);
        let (mut minx, mut maxx) = (f32::MAX, f32::MIN);
        for p in 0..POINTS {
            // Blend the end into the start so the ring closes without a jump.
            let t = p as f64 / POINTS as f64;
            let i = p * (n / 2) / POINTS;
            let val = s[i] as f64 * (1.0 - t) + s[(i + n / 2) % n] as f64 * t;
            let r = r0 + (val * amp).clamp(-r0 * 0.6, size * 0.3);
            let a = -std::f64::consts::FRAC_PI_2 + t * 2.0 * std::f64::consts::PI;
            let q = pt(cx + (a.cos() * r) as f32, cy + (a.sin() * r) as f32);
            minx = minx.min(q.x);
            maxx = maxx.max(q.x);
            pts.push(q);
        }
        let geo = self.factory.CreatePathGeometry()?;
        let sink = geo.Open()?;
        sink.BeginFigure(pts[0], D2D1_FIGURE_BEGIN_FILLED);
        sink.AddLines(&pts[1..]);
        sink.EndFigure(D2D1_FIGURE_END_CLOSED);
        sink.Close()?;
        let colors = self.spectrum_colors(look, v);
        let brush = self.gradient(1, colors, minx, maxx)?;
        let t = self.target.as_ref().unwrap();
        brush.SetOpacity(0.12);
        t.rt.FillGeometry(&geo, &brush, None);
        brush.SetOpacity(0.2);
        t.rt.DrawGeometry(&geo, &brush, 8.0, &t.bevel);
        brush.SetOpacity(1.0);
        t.rt.DrawGeometry(&geo, &brush, 2.5, &t.bevel);
        Ok(())
    }

    // ------------------------------------------------------------ text

    /// A single-line text layout trimmed with "…", cached until the text, font or width changes.
    unsafe fn text(&mut self, slot: &'static str, s: &str, font: PCWSTR, size: f32, weight: DWRITE_FONT_WEIGHT, max_w: f32) -> Result<(IDWriteTextLayout, f32, f32)> {
        let key = format!("{s}|{size:.2}|{}|{max_w:.1}", weight.0);
        if let Some(t) = self.texts.get(slot) {
            if t.key == key {
                return Ok((t.layout.clone(), t.width, t.height));
            }
        }
        let format = self.dwrite.CreateTextFormat(font, None, weight, DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL, size, w!("en-us"))?;
        format.SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP)?;
        let sign = self.dwrite.CreateEllipsisTrimmingSign(&format)?;
        let trimming = DWRITE_TRIMMING { granularity: DWRITE_TRIMMING_GRANULARITY_CHARACTER, delimiter: 0, delimiterCount: 0 };
        format.SetTrimming(&trimming, &sign)?;
        let text: Vec<u16> = s.encode_utf16().collect();
        let layout = self.dwrite.CreateTextLayout(&text, &format, max_w, 1000.0)?;
        let mut m = DWRITE_TEXT_METRICS::default();
        layout.GetMetrics(&mut m)?;
        let (width, height) = (m.width.min(max_w), m.height);
        self.texts.insert(slot, Text { key, layout: layout.clone(), width, height });
        Ok((layout, width, height))
    }

    unsafe fn draw_text(&self, layout: &IDWriteTextLayout, x: f32, y: f32, c: Rgb, alpha: f32) {
        let t = self.target.as_ref().unwrap();
        t.misc.SetColor(&color(c, alpha));
        t.misc.SetOpacity(1.0);
        t.rt.DrawTextLayout(pt(x, y), layout, &t.misc, D2D1_DRAW_TEXT_OPTIONS_NONE);
    }

    /// Song title and artist next to the centred cover, with a soft shadow (`DrawCaption`).
    unsafe fn draw_caption(&mut self, look: &Appearance, track: &Track, mx: f32, my: f32, distance: f32, cover: f32) -> Result<()> {
        if track.title.is_empty() {
            return Ok(());
        }
        let size = (cover * 0.085).clamp(12.0, 30.0).round();
        let max_w = (cover * 2.0).max(200.0);
        let (title, tw, th) = self.text("caption-title", &track.title, w!("Segoe UI"), size, DWRITE_FONT_WEIGHT_SEMI_BOLD, max_w)?;
        let artist = if track.artist.is_empty() {
            None
        } else {
            Some(self.text("caption-artist", &track.artist, w!("Segoe UI"), size * 0.72, DWRITE_FONT_WEIGHT_NORMAL, max_w)?)
        };
        let gap = 2.0;
        let block_h = th + artist.as_ref().map(|a| gap + a.2).unwrap_or(0.0);
        let (x, top, align) = match look.caption_place {
            CaptionPlace::Above => (mx, my - distance - block_h, 0.5),
            CaptionPlace::Left => (mx - distance - 8.0, my - block_h / 2.0, 1.0),
            CaptionPlace::Right => (mx + distance + 8.0, my - block_h / 2.0, 0.0),
            CaptionPlace::Below => (mx, my + distance, 0.5),
        };
        // The layout is max_w wide and left-aligned; place it so the text's own width is aligned.
        let tx = x - tw * align;
        self.draw_text(&title, tx + 1.5, top + 1.5, [0; 3], 140.0 / 255.0);
        self.draw_text(&title, tx, top, [255; 3], 1.0);
        if let Some((layout, aw, _)) = artist {
            let ax = x - aw * align;
            let ay = top + th + gap;
            self.draw_text(&layout, ax + 1.5, ay + 1.5, [0; 3], 105.0 / 255.0);
            self.draw_text(&layout, ax, ay, [255; 3], 191.0 / 255.0);
        }
        Ok(())
    }

    /// The now-playing card: cover, title, artist, progress line and source, scaled 1.25× as on the C# wallpaper.
    unsafe fn draw_card(&mut self, card: &Card, w: f32, h: f32, now: Instant) -> Result<()> {
        const SCALE: f32 = 1.25;
        const FG: Rgb = [0xE8, 0xE8, 0xF0];
        let (cw, ch) = (344.0f32, 92.0f32);
        let (sw, sh) = (cw * SCALE, ch * SCALE);
        let name = match card.place {
            TitlePosition::Hidden => TitlePosition::BottomRight,
            p => p,
        };
        let (top, left, centre) = match name {
            TitlePosition::TopLeft => (true, true, false),
            TitlePosition::TopCenter => (true, false, true),
            TitlePosition::TopRight => (true, false, false),
            TitlePosition::BottomLeft => (false, true, false),
            TitlePosition::BottomCenter => (false, false, true),
            _ => (false, false, false),
        };
        let x = if centre { (w - sw) / 2.0 } else if left { 56.0 } else { w - 56.0 - sw };
        let y = if top { 56.0 } else { h - 88.0 - sh };
        let o = card.opacity.clamp(0.0, 1.0);
        let tr = card.track;

        let (title, _, title_h) = self.text("card-title", &tr.title, w!("Segoe UI"), 15.0, DWRITE_FONT_WEIGHT_SEMI_BOLD, 240.0)?;
        let (artist, _, artist_h) = self.text("card-artist", if tr.artist.is_empty() { " " } else { &tr.artist }, w!("Segoe UI"), 13.0, DWRITE_FONT_WEIGHT_NORMAL, 240.0)?;
        let source_text = format!("via {}", tr.source);
        let (source, _, source_h) = self.text("card-source", &source_text, w!("Segoe UI"), 11.0, DWRITE_FONT_WEIGHT_NORMAL, 240.0)?;

        let t = self.target.as_ref().unwrap();
        t.rt.SetTransform(&Matrix3x2 { M11: SCALE, M12: 0.0, M21: 0.0, M22: SCALE, M31: x, M32: y });
        t.misc.SetOpacity(1.0);
        t.misc.SetColor(&D2D1_COLOR_F { r: 0x15 as f32 / 255.0, g: 0x15 as f32 / 255.0, b: 0x1F as f32 / 255.0, a: 0xB3 as f32 / 255.0 * o });
        t.rt.FillRoundedRectangle(&D2D1_ROUNDED_RECT { rect: rect(0.0, 0.0, cw, ch), radiusX: 12.0, radiusY: 12.0 }, &t.misc);

        // Cover (or a placeholder note).
        let art_rect = rect(10.0, 10.0, 72.0, 72.0);
        let has_art = tr.art.is_some() && t.art.is_some();
        if has_art {
            let (_, bitmap, brush) = t.art.as_ref().unwrap();
            let s = bitmap.GetSize();
            let scale = (72.0 / s.width).max(72.0 / s.height);
            let tx = 10.0 + (72.0 - s.width * scale) / 2.0;
            let ty = 10.0 + (72.0 - s.height * scale) / 2.0;
            brush.SetTransform(&Matrix3x2 { M11: scale, M12: 0.0, M21: 0.0, M22: scale, M31: tx, M32: ty });
            brush.SetOpacity(o);
            t.rt.FillRoundedRectangle(&D2D1_ROUNDED_RECT { rect: art_rect, radiusX: 8.0, radiusY: 8.0 }, brush);
            brush.SetOpacity(1.0);
        } else {
            t.misc.SetColor(&color([0x26, 0x26, 0x36], o));
            t.rt.FillRoundedRectangle(&D2D1_ROUNDED_RECT { rect: art_rect, radiusX: 8.0, radiusY: 8.0 }, &t.misc);
        }
        if !has_art {
            let (glyph, gw, gh) = self.text("card-glyph", "\u{E8D6}", w!("Segoe MDL2 Assets"), 26.0, DWRITE_FONT_WEIGHT_NORMAL, 72.0)?;
            self.draw_text(&glyph, 10.0 + (72.0 - gw) / 2.0, 10.0 + (72.0 - gh) / 2.0, FG, 0.5 * o);
        }

        // Text column: 240 wide, 12 right of the cover, centred vertically.
        let stack_h = title_h + artist_h + 8.0 + 3.0 + 6.0 + source_h;
        let tx = 10.0 + 72.0 + 12.0;
        let mut ty = 10.0 + (72.0f32 - stack_h).max(0.0) / 2.0;
        self.draw_text(&title, tx, ty, FG, o);
        ty += title_h;
        self.draw_text(&artist, tx, ty, FG, 0.75 * o);
        ty += artist_h + 8.0;
        let t = self.target.as_ref().unwrap();
        t.misc.SetColor(&color([255; 3], 0x33 as f32 / 255.0 * o));
        t.rt.FillRoundedRectangle(&D2D1_ROUNDED_RECT { rect: rect(tx, ty, 240.0, 3.0), radiusX: 1.5, radiusY: 1.5 }, &t.misc);
        if tr.duration > 0.0 {
            let frac = (tr.position_at(now) / tr.duration).clamp(0.0, 1.0) as f32;
            if frac > 0.0 {
                t.misc.SetColor(&color(FG, o));
                t.rt.FillRoundedRectangle(&D2D1_ROUNDED_RECT { rect: rect(tx, ty, 240.0 * frac, 3.0), radiusX: 1.5, radiusY: 1.5 }, &t.misc);
            }
        }
        ty += 3.0 + 6.0;
        self.draw_text(&source, tx, ty, FG, 0.5 * o);
        self.target.as_ref().unwrap().rt.SetTransform(&ident());
        Ok(())
    }

    /// The copy of the normal desktop background, faded over everything while going quiet.
    unsafe fn draw_desk(&self, opacity: f32, w: f32, h: f32) {
        let Some(desk) = &self.desk else { return };
        let t = self.target.as_ref().unwrap();
        let (r, g, b) = desk.color;
        t.misc.SetOpacity(1.0);
        t.misc.SetColor(&D2D1_COLOR_F { r, g, b, a: opacity });
        t.rt.FillRectangle(&rect(0.0, 0.0, w, h), &t.misc);
        if let Some(bmp) = &t.desk {
            let s = bmp.GetSize();
            let dest = match desk.fit {
                Fit::Stretch => rect(0.0, 0.0, w, h),
                Fit::Fit => {
                    let k = (w / s.width).min(h / s.height);
                    rect((w - s.width * k) / 2.0, (h - s.height * k) / 2.0, s.width * k, s.height * k)
                }
                Fit::Center => rect((w - s.width) / 2.0, (h - s.height) / 2.0, s.width, s.height),
                Fit::Fill => {
                    let k = (w / s.width).max(h / s.height);
                    rect((w - s.width * k) / 2.0, (h - s.height * k) / 2.0, s.width * k, s.height * k)
                }
            };
            t.rt.DrawBitmap(bmp, Some(&dest), opacity, D2D1_BITMAP_INTERPOLATION_MODE_LINEAR, None);
        }
    }
}

fn caption_gap(cover: f64) -> f64 {
    18.0 + cover * 0.08
}

/// The two gradient colours: the album's (once known) in song-art mode, else the user's own.
fn gradient_colors(look: &Appearance, v: &Visual) -> (Rgb, Rgb) {
    let c1 = parse_color(&look.color1, [0x7A, 0x5C, 0xFF]);
    let c2 = parse_color(&look.color2, [0x3F, 0xD0, 0xC9]);
    if look.colors == ColorMode::AlbumArt {
        if let (Some(a), Some(b)) = (v.album_a, v.album_b) {
            return (a, b);
        }
    }
    (c1, c2)
}

unsafe fn make_bitmap(rt: &ID2D1HwndRenderTarget, p: &Pixels) -> Result<ID2D1Bitmap> {
    let props = D2D1_BITMAP_PROPERTIES {
        pixelFormat: D2D1_PIXEL_FORMAT { format: DXGI_FORMAT_B8G8R8A8_UNORM, alphaMode: D2D1_ALPHA_MODE_PREMULTIPLIED },
        dpiX: 96.0,
        dpiY: 96.0,
    };
    rt.CreateBitmap(D2D_SIZE_U { width: p.width, height: p.height }, Some(p.data.as_ptr() as *const _), p.width * 4, &props)
}
