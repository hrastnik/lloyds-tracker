use std::cell::Cell;
use std::rc::Rc;

use chrono::{DateTime, Local};
use gtk4::prelude::*;
use gtk4::gdk::Key;
use gtk4::glib::Propagation;
use gtk4::{Align, Application, Button, EventControllerKey, Window};

use crate::theme::Fmt;
use crate::ui;

/// Pop-up koji iskoči kod pokretanja aplikacije i u zadano vrijeme početka radnog dana
/// kao podsjetnik da se pokrene radni dan — inače se lako zaboravi startati tracking.
pub struct StartupReminder {
    window: Window,
}

impl StartupReminder {
    /// `start_options` (od početka radnog dana koji je već prošao do početka trenutnog
    /// intervala, u koracima intervala) dodaje strelice ‹ › kojima se pomiče početak.
    /// `on_start` dobije odabrano vrijeme ili None za start od sada.
    pub fn new(
        app: &Application,
        day_title: &str,
        start_options: Vec<DateTime<Local>>,
        on_start: impl Fn(Option<DateTime<Local>>) + 'static,
        on_dismiss: impl Fn() + 'static,
    ) -> Rc<Self> {
        let content = ui::vbox(16);
        content.append(&ui::brand_header(Some(day_title)));

        let intro = ui::vbox(6);
        intro.append(&ui::label("Novi radni dan?", &["big-title"]));
        intro.append(&ui::wrapped(&subtitle(start_options.first().copied()), &["muted"], 46));
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

        let buttons = ui::hbox(10);
        let start_options = Rc::new(start_options);
        // Indeks u `start_options`. Zadano je početak radnog dana — to je razlog zašto je
        // pop-up uopće iskočio u zadano vrijeme.
        let selection = Rc::new(Cell::new(0usize));

        // Vrijeme je u samom gumbu, a ‹ › ga pomiču za jedan interval — jedan klik za
        // zadano, bez zasebnog izbornika.
        let start_row = ui::hbox(2);
        start_row.set_hexpand(true);
        let start_button = {
            let win = window.clone();
            let cb = on_start.clone();
            let options = start_options.clone();
            let selection = selection.clone();
            ui::button(&start_title(&start_options, 0), &["yellow"], move || {
                win.close();
                cb(options.get(selection.get()).copied());
            })
        };
        start_button.set_hexpand(true);

        if start_options.is_empty() {
            start_row.append(&start_button);
        } else {
            let back = step_button("‹");
            let forward = step_button("›");
            let show_selection: Rc<dyn Fn()> = {
                let (start_button, back, forward) = (start_button.clone(), back.clone(), forward.clone());
                let (options, selection) = (start_options.clone(), selection.clone());
                Rc::new(move || {
                    let i = selection.get();
                    start_button.set_label(&start_title(&options, i));
                    back.set_sensitive(i > 0);
                    forward.set_sensitive(i + 1 < options.len());
                })
            };
            {
                let (selection, show) = (selection.clone(), show_selection.clone());
                back.connect_clicked(move |_| {
                    selection.set(selection.get().saturating_sub(1));
                    show();
                });
            }
            {
                let (selection, show) = (selection.clone(), show_selection.clone());
                let last = start_options.len() - 1;
                forward.connect_clicked(move |_| {
                    selection.set((selection.get() + 1).min(last));
                    show();
                });
            }
            show_selection();
            start_row.append(&back);
            start_row.append(&start_button);
            start_row.append(&forward);
        }
        buttons.append(&start_row);

        let later_button = {
            let win = window.clone();
            let cb = on_dismiss.clone();
            ui::button("Kasnije", &["outline"], move || {
                win.close();
                cb();
            })
        };
        buttons.append(&later_button);
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
        ui::PanelFade::appear(&window);
        Rc::new(StartupReminder { window })
    }

    pub fn close(&self) {
        self.window.close();
    }
}

fn start_title(options: &[DateTime<Local>], selected: usize) -> String {
    match options.get(selected) {
        Some(from) => format!("Start od {}", Fmt::hhmm(*from)),
        None => "Start — počni radni dan".to_string(),
    }
}

/// Strelica uz gumb Start — pomiče početak za jedan interval.
fn step_button(glyph: &str) -> Button {
    let b = Button::with_label(glyph);
    b.add_css_class("yellow");
    b.add_css_class("step");
    b
}

fn subtitle(backfill_from: Option<DateTime<Local>>) -> String {
    match backfill_from {
        None => "Tracking još nije pokrenut. Klikni Start da počneš bilježiti vrijeme.".to_string(),
        Some(from) => format!(
            "Radni dan počinje u {}, a tracking još nije pokrenut. Strelicama pomakni početak — \
             prvi prompt te onda pita i za propušteno vrijeme.",
            Fmt::hhmm(from)
        ),
    }
}
