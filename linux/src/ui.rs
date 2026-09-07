//! Sitni graditelji za brand UI — pandan Windows `UiKit.cs`. Cilj je da prozori u
//! `prompt.rs`, `summary.rs` i ostalima ostanu čitljivi kao SwiftUI deklaracije.

use std::cell::Cell;
use std::rc::Rc;
use std::time::{Duration, Instant};

use gtk4::glib;
use gtk4::glib::Propagation;
use gtk4::prelude::*;
use gtk4::{
    Align, Box as GtkBox, Button, EventControllerKey, Label, Orientation, PropagationPhase,
    SpinButton, Widget, Window,
};

pub fn label(text: &str, classes: &[&str]) -> Label {
    let l = Label::new(Some(text));
    l.set_xalign(0.0);
    for c in classes {
        l.add_css_class(c);
    }
    l
}

/// Labela koja se lomi u više redova (opisi opcija, podnaslovi).
pub fn wrapped(text: &str, classes: &[&str], width_chars: i32) -> Label {
    let l = label(text, classes);
    l.set_wrap(true);
    l.set_max_width_chars(width_chars);
    l
}

pub fn button(text: &str, classes: &[&str], on_click: impl Fn() + 'static) -> Button {
    let b = Button::with_label(text);
    for c in classes {
        b.add_css_class(c);
    }
    b.connect_clicked(move |_| on_click());
    b
}

pub fn hbox(spacing: i32) -> GtkBox {
    GtkBox::new(Orientation::Horizontal, spacing)
}

pub fn vbox(spacing: i32) -> GtkBox {
    GtkBox::new(Orientation::Vertical, spacing)
}

/// Rastezljivi razmak — pandan SwiftUI `Spacer()`.
pub fn spacer() -> GtkBox {
    let b = GtkBox::new(Orientation::Horizontal, 0);
    b.set_hexpand(true);
    b
}

/// Mali obojani kvadratić/točka fiksne veličine (brand kvadrat, status točka).
pub fn swatch(size: i32, class: &str) -> GtkBox {
    let b = GtkBox::new(Orientation::Horizontal, 0);
    b.set_size_request(size, size);
    b.set_valign(Align::Center);
    b.add_css_class(class);
    b
}

/// Zaglavlje "▪ LLOYDS TRACKER … datum" — isto na promptu, podsjetniku i upozorenju.
pub fn brand_header(day_title: Option<&str>) -> GtkBox {
    let row = hbox(8);
    row.append(&swatch(18, "brand-square"));
    row.append(&label("LLOYDS", &["heading"]));
    row.append(&label("TRACKER", &["heading-light"]));
    row.append(&spacer());
    if let Some(title) = day_title {
        row.append(&label(title, &["muted-dim"]));
    }
    row
}

/// Tipkovnički savjet u podnožju prompta ("⏎ spremi").
pub fn hint(key: &str, text: &str) -> GtkBox {
    let row = hbox(4);
    row.append(&label(key, &["kbd"]));
    row.append(&label(text, &["muted-dim"]));
    row
}

/// Brojčano polje za sat/minutu (postavke, ispravak unosa).
pub fn spin(min: f64, max: f64) -> SpinButton {
    let s = SpinButton::with_range(min, max, 1.0);
    s.set_orientation(Orientation::Vertical);
    s.set_wrap(true);
    s.set_numeric(true);
    s.set_width_chars(2);
    // Uvijek dvoznamenkasto (08, ne 8) — isti oblik kao HH:mm drugdje u UI-u.
    s.connect_output(|s| {
        s.set_text(&format!("{:02}", s.value_as_int()));
        Propagation::Stop
    });
    s
}

/// "08 : 30" — par brojčanih polja s dvotočkom.
pub fn time_box(hour: &SpinButton, minute: &SpinButton) -> GtkBox {
    let b = hbox(4);
    b.append(hour);
    b.append(&label(":", &["muted"]));
    b.append(minute);
    b
}

/// Margine oko widgeta u jednom pozivu.
pub fn pad(widget: &impl IsA<Widget>, all: i32) {
    let w = widget.as_ref();
    w.set_margin_top(all);
    w.set_margin_bottom(all);
    w.set_margin_start(all);
    w.set_margin_end(all);
}

// MARK: - Pojavljivanje pop-upa

/// Blago pojavljivanje pop-upa: fade umjesto "upada" iz ničega.
///
/// Uz to, prozor **ne uzima tipkovnicu odmah**. Pop-up koji istog trenutka preuzme
/// tipkanje pokrade rečenicu u pola riječi — npr. iskoči dok pišeš u drugoj aplikaciji i
/// ostatak rečenice završi u polju prompta (pa i pregazi pre-fill). Zato prozor prvih
/// `KEY_DELAY_MS` u fazi hvatanja **guta tipke**, a tek se onda fokusira polje.
///
/// Namjerna razlika od macOS-a: tamo prozor jednostavno kasnije postane key, pa tipke u
/// međuvremenu i dalje idu prethodnoj aplikaciji. GTK4 nema pandan `makeKey`-u (ni
/// pozicioniranju prozora), pa se tipke iz tog međuvremena ovdje odbacuju, a pomaka
/// prema konačnoj poziciji nema — samo fade.
pub struct PanelFade;

impl PanelFade {
    pub const DURATION_MS: u64 = 280;
    /// Koliko pop-up čeka prije nego preuzme tipkovnicu.
    pub const KEY_DELAY_MS: u64 = 900;
    /// ~60 fps.
    const FRAME_MS: u64 = 15;

    pub fn appear(window: &Window) {
        Self::appear_with(window, || {});
    }

    /// `on_keyboard` se zove kad prozor preuzme tipkovnicu — tu ide fokus na polje.
    pub fn appear_with(window: &Window, on_keyboard: impl Fn() + 'static) {
        let armed = Rc::new(Cell::new(false));

        let key = EventControllerKey::new();
        key.set_propagation_phase(PropagationPhase::Capture);
        {
            let armed = armed.clone();
            key.connect_key_pressed(move |_, _, _, _| {
                if armed.get() {
                    Propagation::Proceed
                } else {
                    Propagation::Stop
                }
            });
        }
        window.add_controller(key);

        window.set_opacity(0.0);
        window.present();

        {
            let window = window.downgrade();
            let start = Instant::now();
            glib::timeout_add_local(Duration::from_millis(Self::FRAME_MS), move || {
                let Some(window) = window.upgrade() else {
                    return glib::ControlFlow::Break;
                };
                let t = (start.elapsed().as_millis() as f64 / Self::DURATION_MS as f64).min(1.0);
                // easeOut — isti osjećaj kao macOS `CAMediaTimingFunction(name: .easeOut)`.
                window.set_opacity(1.0 - (1.0 - t).powi(3));
                if t >= 1.0 {
                    glib::ControlFlow::Break
                } else {
                    glib::ControlFlow::Continue
                }
            });
        }

        {
            let window = window.downgrade();
            glib::timeout_add_local_once(Duration::from_millis(Self::KEY_DELAY_MS), move || {
                let Some(window) = window.upgrade() else {
                    return;
                };
                if !window.is_visible() {
                    return;
                }
                armed.set(true);
                on_keyboard();
            });
        }
    }
}
