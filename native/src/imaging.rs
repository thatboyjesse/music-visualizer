//! Picture decoding with WIC (album covers, background picture, desktop wallpaper) and the
//! album-colour extraction from `NowPlaying/ImageUtil.cs`.

use windows::core::{Result, PCWSTR};
use windows::Win32::Graphics::Imaging::*;
use windows::Win32::System::Com::*;

/// Premultiplied BGRA pixels, ready for a Direct2D bitmap.
pub struct Pixels {
    pub width: u32,
    pub height: u32,
    pub data: Vec<u8>,
}

fn factory() -> Result<IWICImagingFactory> {
    unsafe { CoCreateInstance(&CLSID_WICImagingFactory, None, CLSCTX_INPROC_SERVER) }
}

/// Decodes the first frame, scaled down so it's at most `max_width` wide (never scaled up).
fn decode(wic: &IWICImagingFactory, decoder: &IWICBitmapDecoder, max_width: u32, exact_width: bool) -> Result<Pixels> {
    unsafe {
        let frame = decoder.GetFrame(0)?;
        let (mut w, mut h) = (0u32, 0u32);
        frame.GetSize(&mut w, &mut h)?;
        if w == 0 || h == 0 {
            return Err(windows::core::Error::from(windows::Win32::Foundation::E_FAIL));
        }
        let source: IWICBitmapSource = if (exact_width && w != max_width) || w > max_width {
            let nh = ((h as u64 * max_width as u64) / w as u64).max(1) as u32;
            let scaler = wic.CreateBitmapScaler()?;
            scaler.Initialize(&frame, max_width, nh, WICBitmapInterpolationModeFant)?;
            w = max_width;
            h = nh;
            scaler.into()
        } else {
            frame.into()
        };
        let conv = wic.CreateFormatConverter()?;
        conv.Initialize(&source, &GUID_WICPixelFormat32bppPBGRA, WICBitmapDitherTypeNone, None, 0.0, WICBitmapPaletteTypeCustom)?;
        let mut data = vec![0u8; (w * h * 4) as usize];
        conv.CopyPixels(std::ptr::null(), w * 4, &mut data)?;
        Ok(Pixels { width: w, height: h, data })
    }
}

/// A picture file scaled down to the smallest size that still covers w×h when filling ("Fill":
/// cover and crop), so a large photo doesn't cost more memory than the screen needs.
pub fn load_file_cover(path: &str, w: u32, h: u32) -> Option<Pixels> {
    let wic = factory().ok()?;
    let p = crate::sys::wide(path);
    unsafe {
        let decoder = wic.CreateDecoderFromFilename(PCWSTR(p.as_ptr()), None, GENERIC_READ_ACCESS, WICDecodeMetadataCacheOnDemand).ok()?;
        let (mut iw, mut ih) = (0u32, 0u32);
        decoder.GetFrame(0).ok()?.GetSize(&mut iw, &mut ih).ok()?;
        if iw == 0 || ih == 0 {
            return None;
        }
        let scale = (w as f64 / iw as f64).max(h as f64 / ih as f64).min(1.0);
        let width = ((iw as f64 * scale).ceil() as u32).clamp(1, iw);
        decode(&wic, &decoder, width, false).ok()
    }
}

/// A picture file scaled to exactly `width` wide (like WPF's DecodePixelWidth).
pub fn load_file_width(path: &str, width: u32) -> Option<Pixels> {
    let wic = factory().ok()?;
    let p = crate::sys::wide(path);
    unsafe {
        let decoder = wic.CreateDecoderFromFilename(PCWSTR(p.as_ptr()), None, GENERIC_READ_ACCESS, WICDecodeMetadataCacheOnDemand).ok()?;
        decode(&wic, &decoder, width, true).ok()
    }
}

const GENERIC_READ_ACCESS: windows::Win32::Foundation::GENERIC_ACCESS_RIGHTS = windows::Win32::Foundation::GENERIC_READ;

/// An encoded picture in memory (an album cover), scaled to `width` wide like the C# app (300).
pub fn load_bytes(bytes: &[u8], width: u32) -> Option<Pixels> {
    let wic = factory().ok()?;
    unsafe {
        let stream = wic.CreateStream().ok()?;
        stream.InitializeFromMemory(bytes).ok()?;
        let decoder = wic.CreateDecoderFromStream(&stream, std::ptr::null(), WICDecodeMetadataCacheOnDemand).ok()?;
        decode(&wic, &decoder, width, true).ok()
    }
}

/// Scales pixels to exactly w×h (for the colour analysis).
fn resize(p: &Pixels, w: u32, h: u32) -> Option<Vec<u8>> {
    let wic = factory().ok()?;
    unsafe {
        let bmp = wic.CreateBitmapFromMemory(p.width, p.height, &GUID_WICPixelFormat32bppPBGRA, p.width * 4, &p.data).ok()?;
        let scaler = wic.CreateBitmapScaler().ok()?;
        scaler.Initialize(&bmp, w, h, WICBitmapInterpolationModeLinear).ok()?;
        let mut out = vec![0u8; (w * h * 4) as usize];
        scaler.CopyPixels(std::ptr::null(), w * 4, &mut out).ok()?;
        Some(out)
    }
}

pub type Rgb = [u8; 3];

/// The two most prominent colours of an album cover, adjusted to stay visible on a dark
/// background (`ImageUtil.Palette`). The cover is scaled to 48×48 and every pixel votes into a
/// 6×6×6 RGB grid, weighted towards colourful pixels and away from dark ones. If the cover has
/// any real colour, only colourful cells can win. The winning cell's average is the first colour;
/// the best clearly different cell is the second (or a lighter shade of the first).
pub fn album_colors(p: &Pixels) -> Option<(Rgb, Rgb)> {
    let px = resize(p, 48, 48)?;
    const L: usize = 6;
    let mut score = vec![0.0f64; L * L * L];
    let mut sum = vec![[0.0f64; 3]; L * L * L];
    for c in px.chunks_exact(4) {
        let (b, g, r) = (c[0] as f64 / 255.0, c[1] as f64 / 255.0, c[2] as f64 / 255.0);
        let max = r.max(g).max(b);
        let min = r.min(g).min(b);
        let sat = if max == 0.0 { 0.0 } else { (max - min) / max };
        let weight = (0.25 + sat * 1.5) * if max < 0.1 { 0.1 } else if max < 0.25 { 0.4 } else { 1.0 };
        let q = |v: f64| (v * (L as f64 - 0.001)) as usize;
        let cell = q(r) * L * L + q(g) * L + q(b);
        score[cell] += weight;
        sum[cell][0] += r * weight;
        sum[cell][1] += g * weight;
        sum[cell][2] += b * weight;
    }
    let avg = |c: usize| -> Rgb {
        [
            (sum[c][0] / score[c] * 255.0) as u8,
            (sum[c][1] / score[c] * 255.0) as u8,
            (sum[c][2] / score[c] * 255.0) as u8,
        ]
    };
    let dist = |a: Rgb, b: Rgb| -> f64 {
        let d = |i: usize| a[i] as f64 - b[i] as f64;
        (d(0).powi(2) * 0.3 + d(1).powi(2) * 0.59 + d(2).powi(2) * 0.11).sqrt()
    };
    let colourful = |c: usize| -> bool {
        if score[c] <= 0.0 {
            return false;
        }
        let a = avg(c);
        let mx = *a.iter().max().unwrap() as f64 / 255.0;
        let mn = *a.iter().min().unwrap() as f64 / 255.0;
        mx > 0.2 && (mx - mn) / mx > 0.3
    };
    let total: f64 = score.iter().sum();
    let colourful_total: f64 = (0..score.len()).filter(|&c| colourful(c)).map(|c| score[c]).sum();
    let use_colour = colourful_total > total * 0.04;
    let cells: Vec<usize> = (0..score.len()).filter(|&c| score[c] > 0.0 && (!use_colour || colourful(c))).collect();
    if cells.is_empty() {
        return None;
    }
    // First with the highest score (ties: the lowest cell, as LINQ's stable OrderByDescending).
    let mut first = cells[0];
    for &c in &cells {
        if score[c] > score[first] {
            first = c;
        }
    }
    let c1 = avg(first);
    let mut second: Option<usize> = None;
    for &c in &cells {
        if c != first && score[c] > score[first] * 0.05 && dist(avg(c), c1) > 55.0 && second.map(|s| score[c] > score[s]).unwrap_or(true) {
            second = Some(c);
        }
    }
    let c1 = visible(c1);
    let mut c2 = match second {
        Some(s) => visible(avg(s)),
        None => shade(c1),
    };
    if dist(c1, c2) < 30.0 {
        c2 = shade(c1);
    }
    Some((c1, c2))
}

/// Brightens dark colours and lifts dull ones; greys stay grey.
fn visible(c: Rgb) -> Rgb {
    let (r, g, b) = (c[0] as f64 / 255.0, c[1] as f64 / 255.0, c[2] as f64 / 255.0);
    let max = r.max(g).max(b);
    let min = r.min(g).min(b);
    let mut sat = if max == 0.0 { 0.0 } else { (max - min) / max };
    let mut hue = if max == min {
        0.0
    } else if max == r {
        60.0 * (((g - b) / (max - min)) % 6.0)
    } else if max == g {
        60.0 * ((b - r) / (max - min) + 2.0)
    } else {
        60.0 * ((r - g) / (max - min) + 4.0)
    };
    if hue < 0.0 {
        hue += 360.0;
    }
    if sat > 0.15 {
        sat = sat.max(0.55);
    }
    hsv_bytes(hue, sat, max.max(0.85))
}

/// A lighter (or, for very light colours, deeper) version, for single-colour covers.
fn shade(c: Rgb) -> Rgb {
    let k = if (c[0] as f64 + c[1] as f64 + c[2] as f64) / 765.0 > 0.8 { 0.6 } else { 1.0 };
    let s = |v: u8| -> u8 {
        let v = v as f64;
        (if k < 1.0 { v * k } else { v + (255.0 - v) * 0.55 }).clamp(0.0, 255.0) as u8
    };
    [s(c[0]), s(c[1]), s(c[2])]
}

/// HSV (h in degrees) to bytes, truncating like the C# `(byte)` casts.
pub fn hsv_bytes(h: f64, s: f64, v: f64) -> Rgb {
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
    [((r + m) * 255.0) as u8, ((g + m) * 255.0) as u8, ((b + m) * 255.0) as u8]
}
