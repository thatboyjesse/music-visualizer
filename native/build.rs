//! Embeds the Windows resources (app icon, manifest, version info) into the exe.
//!
//! There's no resource compiler in the GNU toolchain, so this writes the COFF object that a
//! resource compiler would produce: one `.rsrc` section holding the resource directory tree,
//! with an ADDR32NB relocation on each data entry so the linker fills in the real addresses.

use std::path::PathBuf;

const RT_ICON: u32 = 3;
const RT_GROUP_ICON: u32 = 14;
const RT_VERSION: u32 = 16;
const RT_MANIFEST: u32 = 24;
const LANG_EN_US: u32 = 0x0409;

fn main() {
    let dir = PathBuf::from(std::env::var("CARGO_MANIFEST_DIR").unwrap());
    let icon_path = dir.join("..").join("Assets").join("app.ico");
    let manifest_path = dir.join("app.manifest");
    println!("cargo:rerun-if-changed={}", icon_path.display());
    println!("cargo:rerun-if-changed={}", manifest_path.display());
    println!("cargo:rerun-if-changed=build.rs");
    println!("cargo:rerun-if-env-changed=MV_VERSION");

    let version = std::env::var("MV_VERSION").unwrap_or_else(|_| std::env::var("CARGO_PKG_VERSION").unwrap());
    println!("cargo:rustc-env=MV_VERSION={version}");

    let mut resources: Vec<(u32, u32, Vec<u8>)> = Vec::new(); // (type, id, data)

    // Icon: each image of the .ico becomes an RT_ICON; the directory becomes RT_GROUP_ICON 1.
    let ico = std::fs::read(&icon_path).expect("Assets/app.ico");
    let count = u16::from_le_bytes([ico[4], ico[5]]) as usize;
    let mut group = Vec::new();
    group.extend_from_slice(&[0, 0, 1, 0]);
    group.extend_from_slice(&(count as u16).to_le_bytes());
    for i in 0..count {
        let e = &ico[6 + i * 16..6 + (i + 1) * 16];
        let size = u32::from_le_bytes([e[8], e[9], e[10], e[11]]) as usize;
        let offset = u32::from_le_bytes([e[12], e[13], e[14], e[15]]) as usize;
        let id = (i + 1) as u16;
        resources.push((RT_ICON, id as u32, ico[offset..offset + size].to_vec()));
        group.extend_from_slice(&e[0..12]);
        group.extend_from_slice(&id.to_le_bytes());
    }
    resources.push((RT_GROUP_ICON, 1, group));
    resources.push((RT_VERSION, 1, version_info(&version)));
    resources.push((RT_MANIFEST, 1, std::fs::read(&manifest_path).expect("app.manifest")));

    let obj = coff(&resources);
    let out = PathBuf::from(std::env::var("OUT_DIR").unwrap()).join("resources.o");
    std::fs::write(&out, obj).unwrap();
    println!("cargo:rustc-link-arg-bins={}", out.display());
}

/// The resource section: directory tree (type → id → language → data entry), then the data.
fn coff(resources: &[(u32, u32, Vec<u8>)]) -> Vec<u8> {
    let mut types: Vec<u32> = resources.iter().map(|r| r.0).collect();
    types.sort();
    types.dedup();

    let dir_size = |entries: usize| 16 + 8 * entries;
    // Offsets of every table, laid out level by level.
    let mut offset = dir_size(types.len());
    let mut type_dirs = Vec::new(); // (type, offset, ids)
    for &t in &types {
        let mut ids: Vec<usize> = (0..resources.len()).filter(|&i| resources[i].0 == t).collect();
        ids.sort_by_key(|&i| resources[i].1);
        type_dirs.push((t, offset, ids.clone()));
        offset += dir_size(ids.len());
    }
    let mut lang_dirs = vec![0usize; resources.len()];
    for (_, _, ids) in &type_dirs {
        for &i in ids {
            lang_dirs[i] = offset;
            offset += dir_size(1);
        }
    }
    let mut data_entries = vec![0usize; resources.len()];
    for (_, _, ids) in &type_dirs {
        for &i in ids {
            data_entries[i] = offset;
            offset += 16;
        }
    }
    let mut data_at = vec![0usize; resources.len()];
    for (_, _, ids) in &type_dirs {
        for &i in ids {
            offset = (offset + 7) & !7;
            data_at[i] = offset;
            offset += resources[i].2.len();
        }
    }
    let total = (offset + 7) & !7;

    let mut sec = vec![0u8; total];
    let put32 = |s: &mut Vec<u8>, at: usize, v: u32| s[at..at + 4].copy_from_slice(&v.to_le_bytes());
    let dir_header = |s: &mut Vec<u8>, at: usize, ids: usize| s[at + 14..at + 16].copy_from_slice(&(ids as u16).to_le_bytes());

    dir_header(&mut sec, 0, types.len());
    for (k, (t, off, ids)) in type_dirs.iter().enumerate() {
        put32(&mut sec, 16 + k * 8, *t);
        put32(&mut sec, 16 + k * 8 + 4, 0x8000_0000 | *off as u32);
        dir_header(&mut sec, *off, ids.len());
        for (j, &i) in ids.iter().enumerate() {
            put32(&mut sec, off + 16 + j * 8, resources[i].1);
            put32(&mut sec, off + 16 + j * 8 + 4, 0x8000_0000 | lang_dirs[i] as u32);
            dir_header(&mut sec, lang_dirs[i], 1);
            put32(&mut sec, lang_dirs[i] + 16, LANG_EN_US);
            put32(&mut sec, lang_dirs[i] + 20, data_entries[i] as u32);
            put32(&mut sec, data_entries[i], data_at[i] as u32); // relocated (ADDR32NB)
            put32(&mut sec, data_entries[i] + 4, resources[i].2.len() as u32);
            sec[data_at[i]..data_at[i] + resources[i].2.len()].copy_from_slice(&resources[i].2);
        }
    }

    // COFF: file header, one section header, raw data, relocations, symbol table, string table.
    let relocs: Vec<usize> = data_entries.clone();
    let raw_at = 20 + 40;
    let reloc_at = raw_at + sec.len();
    let sym_at = reloc_at + relocs.len() * 10;
    let mut o = Vec::new();
    o.extend_from_slice(&0x8664u16.to_le_bytes()); // AMD64
    o.extend_from_slice(&1u16.to_le_bytes());
    o.extend_from_slice(&0u32.to_le_bytes());
    o.extend_from_slice(&(sym_at as u32).to_le_bytes());
    o.extend_from_slice(&1u32.to_le_bytes());
    o.extend_from_slice(&0u16.to_le_bytes());
    o.extend_from_slice(&0u16.to_le_bytes());
    o.extend_from_slice(b".rsrc\0\0\0");
    o.extend_from_slice(&0u32.to_le_bytes());
    o.extend_from_slice(&0u32.to_le_bytes());
    o.extend_from_slice(&(sec.len() as u32).to_le_bytes());
    o.extend_from_slice(&(raw_at as u32).to_le_bytes());
    o.extend_from_slice(&(reloc_at as u32).to_le_bytes());
    o.extend_from_slice(&0u32.to_le_bytes());
    o.extend_from_slice(&(relocs.len() as u16).to_le_bytes());
    o.extend_from_slice(&0u16.to_le_bytes());
    o.extend_from_slice(&0x4030_0040u32.to_le_bytes()); // initialized data, read, 4-byte aligned
    o.extend_from_slice(&sec);
    for r in relocs {
        o.extend_from_slice(&(r as u32).to_le_bytes());
        o.extend_from_slice(&0u32.to_le_bytes()); // symbol 0: the section
        o.extend_from_slice(&3u16.to_le_bytes()); // IMAGE_REL_AMD64_ADDR32NB
    }
    o.extend_from_slice(b".rsrc\0\0\0");
    o.extend_from_slice(&0u32.to_le_bytes());
    o.extend_from_slice(&1i16.to_le_bytes());
    o.extend_from_slice(&0u16.to_le_bytes());
    o.push(3); // IMAGE_SYM_CLASS_STATIC
    o.push(0);
    o.extend_from_slice(&4u32.to_le_bytes()); // empty string table
    o
}

/// VS_VERSIONINFO, so Explorer and Task Manager show "Music Visualizer" and the version.
fn version_info(version: &str) -> Vec<u8> {
    let mut parts = version.split('.').map(|p| p.parse::<u16>().unwrap_or(0)).chain(std::iter::repeat(0));
    let (a, b, c, d) = (parts.next().unwrap(), parts.next().unwrap(), parts.next().unwrap(), parts.next().unwrap());
    let ms = ((a as u32) << 16) | b as u32;
    let ls = ((c as u32) << 16) | d as u32;
    let mut fixed = Vec::new();
    for v in [0xFEEF04BDu32, 0x0001_0000, ms, ls, ms, ls, 0x3F, 0, 0x0004_0004, 1, 0, 0, 0] {
        fixed.extend_from_slice(&v.to_le_bytes());
    }

    let strings = [
        ("CompanyName", "Music Visualizer"),
        ("FileDescription", "Music Visualizer"),
        ("FileVersion", version),
        ("InternalName", "MusicVisualizer"),
        ("OriginalFilename", "MusicVisualizer.exe"),
        ("ProductName", "Music Visualizer"),
        ("ProductVersion", version),
    ];
    let table: Vec<Vec<u8>> = strings
        .iter()
        .map(|(k, v)| {
            let mut value: Vec<u8> = v.encode_utf16().chain([0]).flat_map(|u| u.to_le_bytes()).collect();
            let len = value.len() / 2;
            node(k, &mut value, len as u16, 1, vec![])
        })
        .collect();
    let string_table = node("040904B0", &mut vec![], 0, 1, table);
    let string_info = node("StringFileInfo", &mut vec![], 0, 1, vec![string_table]);
    let mut translation = 0x04B0_0409u32.to_le_bytes().to_vec();
    let var = node("Translation", &mut translation, 4, 0, vec![]);
    let var_info = node("VarFileInfo", &mut vec![], 0, 1, vec![var]);
    node("VS_VERSION_INFO", &mut fixed, 52, 0, vec![string_info, var_info])
}

/// One version-info block: wLength, wValueLength, wType, key, padding, value, padding, children.
fn node(key: &str, value: &mut Vec<u8>, value_len: u16, kind: u16, children: Vec<Vec<u8>>) -> Vec<u8> {
    let mut n = vec![0u8; 6];
    n[2..4].copy_from_slice(&value_len.to_le_bytes());
    n[4..6].copy_from_slice(&kind.to_le_bytes());
    for u in key.encode_utf16().chain([0]) {
        n.extend_from_slice(&u.to_le_bytes());
    }
    let pad = |n: &mut Vec<u8>| {
        while n.len() % 4 != 0 {
            n.push(0)
        }
    };
    pad(&mut n);
    n.append(value);
    for c in children {
        pad(&mut n);
        n.extend_from_slice(&c);
    }
    let len = n.len() as u16;
    n[0..2].copy_from_slice(&len.to_le_bytes());
    n
}
