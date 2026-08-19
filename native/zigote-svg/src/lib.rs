//! A C ABI over resvg: parse an SVG once, rasterize it at any pixel size.
//!
//! Three calls carry the whole feature — parse, render, free — plus `compile`, which runs the
//! expensive half of parsing (CSS cascade, text shaping, gradient/marker/use resolution) ahead of
//! time and writes usvg's simplified SVG back out. A compiled document is still SVG, so it goes
//! through the same `parse` at runtime, only with nothing left to resolve.
//!
//! Pixels come out STRAIGHT (non-premultiplied) RGBA8, which is what the engine's image shader
//! samples; tiny-skia works premultiplied, so `render` demultiplies on the way out.

use std::sync::{Arc, OnceLock};

use resvg::tiny_skia;
use resvg::usvg;

/// A parsed document. Opaque to the caller — only ever seen as a pointer.
pub struct SvgTree(usvg::Tree);

/// The font database usvg resolves `<text>` against. Enumerating the system fonts is tens of
/// milliseconds of disk walking — measured, it is essentially the entire cost of the first parse
/// in a process — and usvg wants a database handed over before it can tell whether the document
/// needs one. So look for a text element in the bytes first, and hand over an empty database when
/// there is none.
///
/// A document with no `<text` cannot use a font: `<tspan>` and `<textPath>` only appear inside a
/// text element, and a `font-family` with nothing to apply it to changes no pixel. Icons — and
/// every compiled document, whose text is already paths — therefore never pay the enumeration.
fn fontdb(data: &[u8]) -> Arc<usvg::fontdb::Database> {
    static EMPTY: OnceLock<Arc<usvg::fontdb::Database>> = OnceLock::new();
    static SYSTEM: OnceLock<Arc<usvg::fontdb::Database>> = OnceLock::new();

    if !data.windows(5).any(|w| w == b"<text") {
        return EMPTY
            .get_or_init(|| Arc::new(usvg::fontdb::Database::new()))
            .clone();
    }
    SYSTEM
        .get_or_init(|| {
            let mut db = usvg::fontdb::Database::new();
            db.load_system_fonts();
            Arc::new(db)
        })
        .clone()
}

/// Resolve a document and write it back out as usvg's simplified SVG — the "compiled" form.
/// Shared by [`zgsvg_compile`] and the `zigote-svgc` binary.
pub fn compile_svg(data: &[u8]) -> Option<String> {
    Some(parse(data)?.to_string(&usvg::WriteOptions::default()))
}

fn parse(data: &[u8]) -> Option<usvg::Tree> {
    let opt = usvg::Options {
        fontdb: fontdb(data),
        ..Default::default()
    };
    usvg::Tree::from_data(data, &opt).ok()
}

/// Parse `data` (plain or gzipped SVG). Writes the document's intrinsic size in CSS pixels to
/// `out_w`/`out_h`. Returns null if the bytes are not a valid SVG; free the result with
/// [`zgsvg_free`].
///
/// # Safety
/// `data` must point to `len` readable bytes; `out_w`/`out_h` must be writable.
#[no_mangle]
pub unsafe extern "C" fn zgsvg_parse(
    data: *const u8,
    len: usize,
    out_w: *mut f32,
    out_h: *mut f32,
) -> *mut SvgTree {
    if data.is_null() || len == 0 || out_w.is_null() || out_h.is_null() {
        return std::ptr::null_mut();
    }
    let Some(tree) = parse(std::slice::from_raw_parts(data, len)) else {
        return std::ptr::null_mut();
    };
    *out_w = tree.size().width();
    *out_h = tree.size().height();
    Box::into_raw(Box::new(SvgTree(tree)))
}

/// Rasterize `tree` into a `width × height` straight-alpha RGBA8 buffer. The document is scaled
/// to fill exactly that box (the caller picks the box, so it picks the aspect); `out_len` must be
/// at least `width * height * 4`. Returns false on a bad argument or an allocation failure.
///
/// # Safety
/// `tree` must come from [`zgsvg_parse`]; `out` must point to `out_len` writable bytes.
#[no_mangle]
pub unsafe extern "C" fn zgsvg_render(
    tree: *const SvgTree,
    width: u32,
    height: u32,
    out: *mut u8,
    out_len: usize,
) -> bool {
    if tree.is_null() || out.is_null() || width == 0 || height == 0 {
        return false;
    }
    let needed = width as usize * height as usize * 4;
    if out_len < needed {
        return false;
    }
    let tree = &(*tree).0;
    let Some(mut pixmap) = tiny_skia::Pixmap::new(width, height) else {
        return false;
    };
    let scale = tiny_skia::Transform::from_scale(
        width as f32 / tree.size().width(),
        height as f32 / tree.size().height(),
    );
    resvg::render(tree, scale, &mut pixmap.as_mut());

    let dst = std::slice::from_raw_parts_mut(out, needed);
    for (texel, px) in dst.chunks_exact_mut(4).zip(pixmap.pixels()) {
        let c = px.demultiply();
        texel[0] = c.red();
        texel[1] = c.green();
        texel[2] = c.blue();
        texel[3] = c.alpha();
    }
    true
}

/// # Safety
/// `tree` must come from [`zgsvg_parse`] and not have been freed.
#[no_mangle]
pub unsafe extern "C" fn zgsvg_free(tree: *mut SvgTree) {
    if !tree.is_null() {
        drop(Box::from_raw(tree));
    }
}

/// Resolve `data` into usvg's simplified SVG and return it as UTF-8 bytes — the "compiled" form.
/// Returns null if the bytes are not a valid SVG; free the result with [`zgsvg_bytes_free`].
///
/// # Safety
/// `data` must point to `len` readable bytes; `out_len` must be writable.
#[no_mangle]
pub unsafe extern "C" fn zgsvg_compile(
    data: *const u8,
    len: usize,
    out_len: *mut usize,
) -> *mut u8 {
    if data.is_null() || len == 0 || out_len.is_null() {
        return std::ptr::null_mut();
    }
    let Some(xml) = compile_svg(std::slice::from_raw_parts(data, len)) else {
        return std::ptr::null_mut();
    };
    let mut bytes = xml.into_bytes();
    bytes.shrink_to_fit();
    *out_len = bytes.len();
    let ptr = bytes.as_mut_ptr();
    std::mem::forget(bytes);
    ptr
}

/// # Safety
/// `ptr`/`len` must be exactly what [`zgsvg_compile`] returned.
#[no_mangle]
pub unsafe extern "C" fn zgsvg_bytes_free(ptr: *mut u8, len: usize) {
    if !ptr.is_null() {
        drop(Vec::from_raw_parts(ptr, len, len));
    }
}
