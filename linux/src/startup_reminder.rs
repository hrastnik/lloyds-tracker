use std::cell::{Cell, RefCell};
use std::rc::Rc;

use chrono::{DateTime, Local};
use gtk4::prelude::*;
use gtk4::gdk::Key;
use gtk4::glib::Propagation;
use gtk4::{Align, Application, Button, EventControllerKey, Label, Window};

use crate::theme::Fmt;
use crate::ui;

/// Pop-up koji iskoči kod pokretanja aplikacije i u zadano vrijeme početka radnog dana
/// kao podsjetnik da se pokrene radni dan — inače se lako zaboravi startati tracking.
pub struct StartupReminder {
    window: Window,
    subtitle: Label,
    start_options: Rc<RefCell<Vec<DateTime<Local>>>>,
    selection: Rc<Cell<usize>>,
    /// Osvježi gumb Start i strelice — samo uz ponudu nadoknade.
    show_selection: Option<Rc<dyn Fn()>>,
}

impl StartupReminder {
    /// `start_options` (od početka radnog dana koji je već prošao — ili kraja zadnjeg današnjeg
    /// unosa — do početka trenutnog intervala, po mreži intervala) dodaje strelice ‹ › kojima
    /// se pomiče početak.
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
        let subtitle_label = ui::wrapped(&subtitle(start_options.first().copied()), &["muted"], 46);
        intro.append(&subtitle_label);
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
        let start_options = Rc::new(RefCell::new(start_options));
        // Indeks u `start_options`. Zadano je najraniji početak (početak radnog dana ili kraj
        // zadnjeg današnjeg unosa) — to je razlog zašto je pop-up uopće iskočio u zadano vrijeme.
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
            ui::button(&start_title(&start_options.borrow(), 0), &["yellow"], move || {
                win.destroy();
                let from = options.borrow().get(selection.get()).copied();
                cb(from);
            })
        };
        start_button.set_hexpand(true);

        let mut show_selection_fn = None;
        if start_options.borrow().is_empty() {
            start_row.append(&start_button);
        } else {
            let back = step_button("‹");
            let forward = step_button("›");
            let show_selection: Rc<dyn Fn()> = {
                let (start_button, back, forward) = (start_button.clone(), back.clone(), forward.clone());
                let (options, selection) = (start_options.clone(), selection.clone());
                Rc::new(move || {
                    let i = selection.get();
                    let options = options.borrow();
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
                let options = start_options.clone();
                forward.connect_clicked(move |_| {
                    let last = options.borrow().len().saturating_sub(1);
                    selection.set((selection.get() + 1).min(last));
                    show();
                });
            }
            show_selection();
            start_row.append(&back);
            start_row.append(&start_button);
            start_row.append(&forward);
            show_selection_fn = Some(show_selection);
        }
        buttons.append(&start_row);

        let later_button = {
            let win = window.clone();
            let cb = on_dismiss.clone();
            ui::button("Kasnije", &["outline"], move || {
                win.destroy();
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
                    win.destroy();
                    cb();
                    return Propagation::Stop;
                }
                Propagation::Proceed
            });
        }
        window.add_controller(key);

        // Alt+F4 / zatvaranje iz window managera = Kasnije — inače engine misli da je prozor
        // još otvoren. Programska zatvaranja idu preko `destroy()`, koji ovaj signal ne šalje.
        {
            let cb = on_dismiss.clone();
            window.connect_close_request(move |_| {
                cb();
                Propagation::Proceed
            });
        }

        window.set_child(Some(&content));
        ui::PanelFade::appear(&window);
        Rc::new(StartupReminder {
            window,
            subtitle: subtitle_label,
            start_options,
            selection,
            show_selection: show_selection_fn,
        })
    }

    /// Ponuda početaka vrijedi za interval u kojem je prozor prikazan — ako stoji satima,
    /// najkasniji početak zaostaje (prikazan u 8:40, u 11:00 i dalje nudi najviše 8:30).
    /// Engine mu na novom intervalu pošalje svježu ponudu; odabrano vrijeme ostaje odabrano
    /// ako je još u ponudi. Samo kad prozor već nudi nadoknadu i nova ponuda nije prazna —
    /// pojavu nadoknade i prenoćeni prozor rješava zamjena prozora u engineu.
    pub fn update_start_options(&self, options: Vec<DateTime<Local>>) {
        let Some(show) = &self.show_selection else { return };
        if options.is_empty() {
            return;
        }
        {
            let mut current = self.start_options.borrow_mut();
            let selected = current.get(self.selection.get()).copied();
            let index = selected
                .and_then(|from| options.iter().position(|t| *t == from))
                .unwrap_or_else(|| self.selection.get().min(options.len() - 1));
            self.selection.set(index);
            // Početak radnog dana se mijenja samo s postavkama — podnaslov ostaje iste visine.
            self.subtitle.set_label(&subtitle(options.first().copied()));
            *current = options;
        }
        show();
    }

    pub fn close(&self) {
        self.window.destroy();
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
        Some(_) => "Tracking još nije pokrenut. Strelicama odaberi od kad radiš — nakon Starta te \
                    prompt odmah pita za propušteno vrijeme."
            .to_string(),
    }
}
