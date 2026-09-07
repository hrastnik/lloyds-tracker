use std::rc::Rc;
use std::time::Duration;

use chrono::{DateTime, Local};
use gtk4::prelude::*;
use gtk4::gdk::Key;
use gtk4::glib::{self, Propagation};
use gtk4::{Align, Application, EventControllerKey, ProgressBar, Window};

use crate::theme::Fmt;
use crate::ui;

/// Upozorenje minutu prije automatskog zaustavljanja radnog dana. Daje priliku da se
/// dan produži (samo za danas); ako se ne reagira, engine zatvori dan sam — tako
/// tracking ne ostane pokrenut preko noći.
pub struct AutoStopWarning {
    window: Window,
}

/// Produženja se računaju od zakazanog vremena (16:00 + 30 min → 16:30).
const OPTIONS: [i64; 4] = [15, 30, 45, 60];

impl AutoStopWarning {
    pub fn new(
        app: &Application,
        stop_at: DateTime<Local>,
        lead: f64,
        on_extend: impl Fn(i64) + 'static,
        on_stop_now: impl Fn() + 'static,
        on_dismiss: impl Fn() + 'static,
    ) -> Rc<Self> {
        let window = Window::builder()
            .application(app)
            .title("Lloyds Tracker")
            .resizable(false)
            .decorated(false)
            .build();
        window.add_css_class("brand");

        let on_extend = Rc::new(on_extend);
        let on_stop_now = Rc::new(on_stop_now);
        let on_dismiss = Rc::new(on_dismiss);

        let content = ui::vbox(14);

        // Zaglavlje s odbrojavanjem.
        let header = ui::hbox(8);
        header.append(&ui::swatch(18, "brand-square"));
        header.append(&ui::label("LLOYDS", &["heading"]));
        header.append(&ui::label("TRACKER", &["heading-light"]));
        header.append(&ui::spacer());
        let countdown = ui::label("", &["mono", "accent"]);
        header.append(&countdown);
        content.append(&header);

        // Traka koja se prazni do zaustavljanja — odmah je vidljivo koliko vremena ostaje.
        let bar = ProgressBar::new();
        bar.add_css_class("accent");
        content.append(&bar);

        let intro = ui::vbox(6);
        intro.append(&ui::label("Zaustavljam tracking", &["big-title"]));
        intro.append(&ui::wrapped(
            &format!(
                "Radni dan se automatski zatvara u {}. Ako još radiš, produži — inače dobiješ \
                 pregled dana i tracking se zaustavlja.",
                Fmt::hhmm(stop_at)
            ),
            &["muted"],
            48,
        ));
        content.append(&intro);

        // Produženja.
        let extend_block = ui::vbox(8);
        extend_block.append(&ui::label("PRODUŽI — SAMO ZA DANAS", &["section-label"]));
        let row = ui::hbox(8);
        for minutes in OPTIONS {
            let until = stop_at + chrono::Duration::minutes(minutes);
            let label = if minutes >= 60 {
                format!("+1 h\n{}", Fmt::hhmm(until))
            } else {
                format!("+{minutes} min\n{}", Fmt::hhmm(until))
            };
            let win = window.clone();
            let cb = on_extend.clone();
            let button = ui::button(&label, &["pill"], move || {
                win.close();
                cb(minutes);
            });
            button.set_hexpand(true);
            button.set_tooltip_text(Some(&format!("Produži do {}", Fmt::hhmm(until))));
            row.append(&button);
        }
        extend_block.append(&row);
        content.append(&extend_block);

        // Period se i tako bilježi do zakazanog vremena, pa to piše na gumbu.
        let stop_button = {
            let win = window.clone();
            let cb = on_stop_now.clone();
            ui::button(
                &format!("Zaustavi sad — bilježi do {}", Fmt::hhmm(stop_at)),
                &["outline"],
                move || {
                    win.close();
                    cb();
                },
            )
        };
        stop_button.set_hexpand(true);
        content.append(&stop_button);

        content.add_css_class("brand-card");
        content.set_halign(Align::Center);
        content.set_valign(Align::Center);
        ui::pad(&content, 22);
        content.set_size_request(380, -1);

        let key = EventControllerKey::new();
        {
            let win = window.clone();
            let cb = on_dismiss.clone();
            key.connect_key_pressed(move |_, keyval, _, _| {
                if keyval == Key::Escape {
                    win.close();
                    cb();
                    return Propagation::Stop;
                }
                Propagation::Proceed
            });
        }
        window.add_controller(key);

        window.set_child(Some(&content));
        ui::PanelFade::appear(&window);

        // Odbrojavanje se osvježava 4×/s (kao macOS TimelineView) i sam se ugasi kad
        // prozor nestane, pa ne drži referencu na zatvoreni prozor.
        {
            let window = window.downgrade();
            glib::timeout_add_local(Duration::from_millis(250), move || {
                let Some(window) = window.upgrade() else {
                    return glib::ControlFlow::Break;
                };
                if !window.is_visible() {
                    return glib::ControlFlow::Break;
                }
                let remaining = (stop_at - Local::now()).num_milliseconds() as f64 / 1000.0;
                countdown.set_text(&Fmt::countdown(remaining));
                bar.set_fraction(if lead > 0.0 { (remaining / lead).clamp(0.0, 1.0) } else { 0.0 });
                glib::ControlFlow::Continue
            });
        }

        Rc::new(AutoStopWarning { window })
    }

    pub fn close(&self) {
        self.window.close();
    }
}
