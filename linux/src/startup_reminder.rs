use std::rc::Rc;

use chrono::{DateTime, Local};
use gtk4::prelude::*;
use gtk4::gdk::Key;
use gtk4::glib::Propagation;
use gtk4::{Align, Application, EventControllerKey, Window};

use crate::theme::Fmt;
use crate::ui;

/// Pop-up koji iskoči kod pokretanja aplikacije i u zadano vrijeme početka radnog dana
/// kao podsjetnik da se pokrene radni dan — inače se lako zaboravi startati tracking.
pub struct StartupReminder {
    window: Window,
}

impl StartupReminder {
    /// `backfill_from` (početak radnog dana koji je već prošao) dodaje izbor: start od tog
    /// vremena ili tek od sada. `on_start` dobije samo je li odabrana nadoknada — točno
    /// vrijeme računa engine u trenutku klika, jer prozor čeka odgovor i može prenoćiti,
    /// pa bi zapamćeni datum tada pomaknuo početak na jučer.
    pub fn new(
        app: &Application,
        day_title: &str,
        backfill_from: Option<DateTime<Local>>,
        on_start: impl Fn(bool) + 'static,
        on_dismiss: impl Fn() + 'static,
    ) -> Rc<Self> {
        let content = ui::vbox(16);
        content.append(&ui::brand_header(Some(day_title)));

        let intro = ui::vbox(6);
        intro.append(&ui::label("Novi radni dan?", &["big-title"]));
        intro.append(&ui::wrapped(&subtitle(backfill_from), &["muted"], 46));
        content.append(&intro);

        let window = Window::builder()
            .application(app)
            .title("Lloyds Tracker")
            .resizable(false)
            .decorated(false)
            .build();
        window.add_css_class("brand");

        let on_start = Rc::new(on_start);
        let on_dismiss = Rc::new(on_dismiss);

        let buttons = ui::vbox(10);
        let primary_row = ui::hbox(10);

        // Uz nadoknadu je primarni gumb start od početka radnog dana — to je razlog zašto
        // je pop-up uopće iskočio u zadano vrijeme.
        let primary_label = match backfill_from {
            Some(from) => format!("Start od {}", Fmt::hhmm(from)),
            None => "Start — počni radni dan".to_string(),
        };
        let use_backfill = backfill_from.is_some();
        let start_button = {
            let win = window.clone();
            let cb = on_start.clone();
            ui::button(&primary_label, &["yellow"], move || {
                win.close();
                cb(use_backfill);
            })
        };
        start_button.set_hexpand(true);
        primary_row.append(&start_button);

        let later_button = {
            let win = window.clone();
            let cb = on_dismiss.clone();
            ui::button("Kasnije", &["outline"], move || {
                win.close();
                cb();
            })
        };
        primary_row.append(&later_button);
        buttons.append(&primary_row);

        if backfill_from.is_some() {
            let win = window.clone();
            let cb = on_start.clone();
            let now_button = ui::button("Počni tek od sada", &["outline"], move || {
                win.close();
                cb(false);
            });
            now_button.set_hexpand(true);
            buttons.append(&now_button);
        }
        content.append(&buttons);

        content.add_css_class("brand-card");
        content.set_halign(Align::Center);
        content.set_valign(Align::Center);
        ui::pad(&content, 22);
        content.set_size_request(360, -1);

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
        window.present();
        Rc::new(StartupReminder { window })
    }

    pub fn close(&self) {
        self.window.close();
    }
}

fn subtitle(backfill_from: Option<DateTime<Local>>) -> String {
    match backfill_from {
        None => "Tracking još nije pokrenut. Klikni Start da počneš bilježiti vrijeme.".to_string(),
        Some(from) => format!(
            "Radni dan počinje u {}, a tracking još nije pokrenut. Mogu ga voditi od tada \
             (pa te prvi prompt pita i za jutro) ili tek od sada.",
            Fmt::hhmm(from)
        ),
    }
}
