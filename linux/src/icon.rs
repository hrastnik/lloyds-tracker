use gtk4::cairo::{Context, Format, ImageSurface};

use crate::engine::TrayState;
use crate::theme::{BLACK_RGB, YELLOW_RGB};

/// Crta ikone u traci po stanju — Linux pandan macOS `MenuBarIcon` i Windows
/// `TrayIconFactory`: žuta zaobljena pločica s crnim znakom (sat / pauza / mjesec).
/// Pločica drži ikonu čitljivom i na svijetloj i na tamnoj traci, za razliku od golog
/// bijelog znaka koji na svijetloj nestane.
///
/// Geometrija je pisana na mreži od 32 px (ista brojka kao na Windows portu), a crta se
/// skalirano na `SIDE`, pa ostaje oštra.
const GRID: f64 = 32.0;
const SIDE: i32 = 64;

/// StatusNotifierItem traži ARGB32 u mrežnom (big-endian) poretku bajtova, a cairo daje
/// premultiplicirani ARGB32 u poretku hosta. Zato se ovdje i preslaguje i dijeli s alfom.
pub fn tray_icon(state: TrayState) -> ksni::Icon {
    let surface = ImageSurface::create(Format::ARgb32, SIDE, SIDE).expect("cairo surface");
    {
        let cr = Context::new(&surface).expect("cairo context");
        cr.scale(SIDE as f64 / GRID, SIDE as f64 / GRID);
        draw(&cr, state);
    }

    let width = surface.width();
    let height = surface.height();
    let stride = surface.stride() as usize;
    let data = surface.take_data().expect("cairo data");

    let mut out = Vec::with_capacity((width * height * 4) as usize);
    for y in 0..height as usize {
        let row = &data[y * stride..y * stride + width as usize * 4];
        for px in row.chunks_exact(4) {
            // Poredak bajtova u memoriji na little-endian hostu je B, G, R, A.
            let (b, g, r, a) = (px[0], px[1], px[2], px[3]);
            let unpremultiply = |c: u8| -> u8 {
                if a == 0 {
                    0
                } else {
                    ((c as u16 * 255) / a as u16).min(255) as u8
                }
            };
            out.extend_from_slice(&[a, unpremultiply(r), unpremultiply(g), unpremultiply(b)]);
        }
    }

    ksni::Icon { width, height, data: out }
}

fn draw(cr: &Context, state: TrayState) {
    // Brand pločica: žuti zaobljeni kvadrat. Clip na nju da nijedan znak (npr. zagriz
    // mjeseca) ne iscuri preko zaobljenih uglova.
    rounded_rect(cr, 1.0, 1.0, GRID - 2.0, GRID - 2.0, 7.0);
    set_rgb(cr, YELLOW_RGB, 1.0);
    let _ = cr.fill_preserve();
    cr.clip();

    match state {
        TrayState::Paused => draw_pause(cr),
        TrayState::AwaitingReturn => draw_moon(cr),
        TrayState::Tracking => draw_clock(cr, true),
        TrayState::Idle => draw_clock(cr, false),
    }
}

fn draw_clock(cr: &Context, filled: bool) {
    if filled {
        ellipse(cr, 4.0, 4.0, 24.0, 24.0);
        set_rgb(cr, BLACK_RGB, 0.22);
        let _ = cr.fill();
    }
    ellipse(cr, 4.0, 4.0, 24.0, 24.0);
    set_rgb(cr, BLACK_RGB, 1.0);
    cr.set_line_width(2.6);
    let _ = cr.stroke();

    cr.set_line_width(2.4);
    cr.set_line_cap(gtk4::cairo::LineCap::Round);
    cr.move_to(16.0, 16.0);
    cr.line_to(16.0, 8.0); // minutna → 12
    let _ = cr.stroke();
    cr.move_to(16.0, 16.0);
    cr.line_to(22.0, 18.0); // satna → ~4
    let _ = cr.stroke();
}

fn draw_pause(cr: &Context) {
    set_rgb(cr, BLACK_RGB, 1.0);
    rounded_rect(cr, 9.0, 6.0, 5.5, 20.0, 2.0);
    let _ = cr.fill();
    rounded_rect(cr, 17.5, 6.0, 5.5, 20.0, 2.0);
    let _ = cr.fill();
}

fn draw_moon(cr: &Context) {
    ellipse(cr, 5.0, 5.0, 22.0, 22.0);
    set_rgb(cr, BLACK_RGB, 1.0);
    let _ = cr.fill();
    // Polumjesec se izrezuje prebojavanjem bojom pločice — neprozirno, a ne prozirnom
    // rupom, da rub ostane antialiasan i da se ne probije do trake ispod.
    ellipse(cr, 12.0, 2.0, 22.0, 22.0);
    set_rgb(cr, YELLOW_RGB, 1.0);
    let _ = cr.fill();
}

// MARK: - Cairo pomoćnici

fn set_rgb(cr: &Context, rgb: (f64, f64, f64), alpha: f64) {
    cr.set_source_rgba(rgb.0, rgb.1, rgb.2, alpha);
}

fn rounded_rect(cr: &Context, x: f64, y: f64, w: f64, h: f64, r: f64) {
    use std::f64::consts::PI;
    let r = r.min(w / 2.0).min(h / 2.0);
    cr.new_sub_path();
    cr.arc(x + w - r, y + r, r, -PI / 2.0, 0.0);
    cr.arc(x + w - r, y + h - r, r, 0.0, PI / 2.0);
    cr.arc(x + r, y + h - r, r, PI / 2.0, PI);
    cr.arc(x + r, y + r, r, PI, 1.5 * PI);
    cr.close_path();
}

fn ellipse(cr: &Context, x: f64, y: f64, w: f64, h: f64) {
    use std::f64::consts::PI;
    cr.save().ok();
    cr.translate(x + w / 2.0, y + h / 2.0);
    cr.scale(w / 2.0, h / 2.0);
    cr.new_sub_path();
    cr.arc(0.0, 0.0, 1.0, 0.0, 2.0 * PI);
    cr.restore().ok();
}
