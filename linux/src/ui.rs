//! Sitni graditelji za brand UI — pandan Windows `UiKit.cs`. Cilj je da prozori u
//! `prompt.rs`, `summary.rs` i ostalima ostanu čitljivi kao SwiftUI deklaracije.

use gtk4::prelude::*;
use gtk4::{Align, Box as GtkBox, Button, Label, Orientation, Widget};

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

/// Margine oko widgeta u jednom pozivu.
pub fn pad(widget: &impl IsA<Widget>, all: i32) {
    let w = widget.as_ref();
    w.set_margin_top(all);
    w.set_margin_bottom(all);
    w.set_margin_start(all);
    w.set_margin_end(all);
}
