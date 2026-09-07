use std::rc::Rc;

use chrono::{DateTime, Local, Timelike};
use gtk4::gdk::Key;
use gtk4::glib::Propagation;
use gtk4::prelude::*;
use gtk4::{
    Align, Application, Box as GtkBox, Button, DropDown, Entry, EventControllerKey, Label,
    SpinButton, ToggleButton, Window,
};

use crate::models::{ChronoRow, EntryKind};
use crate::theme::Fmt;
use crate::ui;

/// Potvrđeni ispravak: opis, od, do, vrsta.
type SaveHandler = dyn Fn(String, DateTime<Local>, DateTime<Local>, EntryKind);

/// Ispravak postojećeg unosa iz kronološkog pregleda — opis, vrijeme i vrsta (rad/pauza).
/// Spojeni red (`2×`) se ispravlja kao jedna cjelina: promjena samo opisa/vrste zadržava
/// blokove, a promjena vremena ih stopi u jedan unos (novi raspon nema stare granice).
pub struct EntryEditWindow {
    window: Window,
    row: ChronoRow,
    text: Entry,
    start_hour: SpinButton,
    start_minute: SpinButton,
    end_hour: SpinButton,
    end_minute: SpinButton,
    duration: Label,
    warning: Label,
    work_toggle: ToggleButton,
    save_button: Button,
    on_save: Box<SaveHandler>,
}

impl EntryEditWindow {
    pub fn new(
        app: &Application,
        parent: &Window,
        row: ChronoRow,
        history: Vec<String>,
        on_save: impl Fn(String, DateTime<Local>, DateTime<Local>, EntryKind) + 'static,
    ) -> Rc<Self> {
        let window = Window::builder()
            .application(app)
            .title("Ispravi unos")
            .modal(true)
            .transient_for(parent)
            .resizable(false)
            .build();
        window.add_css_class("brand");

        let text = Entry::new();
        text.add_css_class("brand");
        text.set_hexpand(true);
        text.set_placeholder_text(Some("npr. Projekt X — opis zadatka"));
        text.set_text(&row.text);

        let work_toggle = ToggleButton::with_label("Rad");
        let pause_toggle = ToggleButton::with_label("Pauza");
        pause_toggle.set_group(Some(&work_toggle));
        work_toggle.add_css_class("outline");
        pause_toggle.add_css_class("outline");
        work_toggle.set_active(row.kind == EntryKind::Work);
        pause_toggle.set_active(row.kind == EntryKind::Pause);

        let me = Rc::new(EntryEditWindow {
            window: window.clone(),
            text: text.clone(),
            start_hour: ui::spin(0.0, 23.0),
            start_minute: ui::spin(0.0, 59.0),
            end_hour: ui::spin(0.0, 23.0),
            end_minute: ui::spin(0.0, 59.0),
            duration: ui::label("", &["mono", "accent"]),
            warning: ui::wrapped("", &["muted"], 46),
            work_toggle: work_toggle.clone(),
            save_button: Button::with_label("Spremi"),
            on_save: Box::new(on_save),
            row,
        });

        me.start_hour.set_value(me.row.start.hour() as f64);
        me.start_minute.set_value(me.row.start.minute() as f64);
        me.end_hour.set_value(me.row.end.hour() as f64);
        me.end_minute.set_value(me.row.end.minute() as f64);

        let content = ui::vbox(16);

        // MARK: Zaglavlje
        let header = ui::hbox(8);
        header.append(&ui::swatch(16, "brand-square"));
        header.append(&ui::label("ISPRAVI UNOS", &["heading"]));
        header.append(&ui::spacer());
        header.append(&ui::label(&Fmt::day_title(me.row.start), &["muted-dim"]));
        content.append(&header);

        // MARK: Opis
        let description = ui::vbox(6);
        description.append(&ui::label("OPIS", &["section-label"]));
        description.append(&text);
        if !history.is_empty() {
            let mut labels = vec!["Iz povijesti…".to_string()];
            labels.extend(history.iter().cloned());
            let picker = DropDown::from_strings(
                &labels.iter().map(String::as_str).collect::<Vec<_>>(),
            );
            picker.set_halign(Align::Start);
            {
                let text = text.clone();
                picker.connect_selected_notify(move |picker| {
                    let index = picker.selected() as usize;
                    if index == 0 {
                        return;
                    }
                    text.set_text(&labels[index]);
                    // Vraća se na naslov, pa je odabir istog opisa moguć i drugi put.
                    picker.set_selected(0);
                });
            }
            description.append(&picker);
        }
        content.append(&description);

        // MARK: Vrijeme
        let times = ui::hbox(14);
        times.append(&field("OD", &ui::time_box(&me.start_hour, &me.start_minute)));
        times.append(&field("DO", &ui::time_box(&me.end_hour, &me.end_minute)));
        times.append(&field("TRAJANJE", &me.duration));
        times.append(&ui::spacer());
        content.append(&times);

        // MARK: Vrsta
        let kinds = ui::hbox(6);
        kinds.append(&work_toggle);
        kinds.append(&pause_toggle);
        content.append(&field("VRSTA", &kinds));

        content.append(&me.warning);

        // MARK: Podnožje
        let footer = ui::hbox(10);
        footer.append(&ui::spacer());
        let cancel = {
            let win = window.clone();
            ui::button("Odustani", &["outline"], move || win.close())
        };
        footer.append(&cancel);
        me.save_button.add_css_class("yellow");
        {
            let save = me.save_button.clone();
            let me = me.clone();
            save.connect_clicked(move |_| me.save());
        }
        footer.append(&me.save_button);
        content.append(&footer);

        ui::pad(&content, 22);
        content.set_size_request(420, -1);
        window.set_child(Some(&content));

        // MARK: Vezanje kontrola
        {
            let me = me.clone();
            text.connect_changed(move |_| me.sync());
        }
        {
            let me = me.clone();
            text.connect_activate(move |_| me.save());
        }
        for spin in [&me.start_hour, &me.start_minute, &me.end_hour, &me.end_minute] {
            let me = me.clone();
            spin.connect_value_changed(move |_| me.sync());
        }
        for toggle in [&work_toggle, &pause_toggle] {
            let me = me.clone();
            toggle.connect_toggled(move |_| me.sync());
        }

        let key = EventControllerKey::new();
        {
            let win = window.clone();
            key.connect_key_pressed(move |_, keyval, _, _| {
                if keyval == Key::Escape {
                    win.close();
                    return Propagation::Stop;
                }
                Propagation::Proceed
            });
        }
        window.add_controller(key);

        me.sync();
        window.present();
        text.grab_focus();
        me
    }

    // MARK: - Stanje

    fn kind(&self) -> EntryKind {
        if self.work_toggle.is_active() {
            EntryKind::Work
        } else {
            EntryKind::Pause
        }
    }

    fn start(&self) -> DateTime<Local> {
        at(self.row.start, self.start_hour.value_as_int(), self.start_minute.value_as_int())
    }

    fn end(&self) -> DateTime<Local> {
        at(self.row.end, self.end_hour.value_as_int(), self.end_minute.value_as_int())
    }

    fn trimmed(&self) -> String {
        self.text.text().trim().to_string()
    }

    fn is_valid(&self) -> bool {
        !self.trimmed().is_empty() && self.end() > self.start()
    }

    fn times_changed(&self) -> bool {
        (self.start() - self.row.start).num_seconds().abs() > 1
            || (self.end() - self.row.end).num_seconds().abs() > 1
    }

    /// Trajanje nakon ispravka — odmah je vidljivo koliko će unos nositi.
    fn sync(&self) {
        let (start, end) = (self.start(), self.end());
        let ok = end > start;
        self.duration
            .set_text(&Fmt::dur(((end - start).num_milliseconds() as f64 / 1000.0).max(0.0)));
        set_class(&self.duration, "accent", ok);
        set_class(&self.duration, "warn", !ok);

        if !ok {
            self.warning.set_text("Kraj mora biti nakon početka.");
            set_class(&self.warning, "warn", true);
            set_class(&self.warning, "accent", false);
        } else if self.row.is_merged() && self.times_changed() {
            self.warning.set_text(&format!(
                "Promjena vremena stapa {} bloka u jedan unos.",
                self.row.ids.len()
            ));
            set_class(&self.warning, "warn", false);
            set_class(&self.warning, "accent", true);
        } else {
            self.warning.set_text("");
        }
        self.warning.set_visible(!self.warning.text().is_empty());

        self.save_button.set_sensitive(self.is_valid());
    }

    fn save(&self) {
        if !self.is_valid() {
            return;
        }
        let (text, start, end, kind) = (self.trimmed(), self.start(), self.end(), self.kind());
        self.window.close();
        (self.on_save)(text, start, end, kind);
    }
}

/// Naslov sekcije nad kontrolom ("OD", "TRAJANJE", "VRSTA").
fn field(title: &str, control: &impl IsA<gtk4::Widget>) -> GtkBox {
    let b = ui::vbox(6);
    b.append(&ui::label(title, &["section-label"]));
    b.append(control);
    b
}

fn set_class(widget: &impl IsA<gtk4::Widget>, class: &str, on: bool) {
    if on {
        widget.as_ref().add_css_class(class);
    } else {
        widget.as_ref().remove_css_class(class);
    }
}

/// Isti dan kao `day`, ali u zadani sat i minutu (sekunde na nulu, kao macOS DatePicker).
fn at(day: DateTime<Local>, hour: i32, minute: i32) -> DateTime<Local> {
    day.with_hour(hour as u32)
        .and_then(|d| d.with_minute(minute as u32))
        .and_then(|d| d.with_second(0))
        .and_then(|d| d.with_nanosecond(0))
        .unwrap_or(day)
}
