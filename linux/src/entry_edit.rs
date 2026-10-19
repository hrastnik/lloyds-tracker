use std::cell::Cell;
use std::rc::Rc;

use chrono::{DateTime, Local};
use gtk4::gdk::Key;
use gtk4::glib::Propagation;
use gtk4::prelude::*;
use gtk4::{
    Align, Box as GtkBox, Button, DropDown, Entry, EventControllerFocus, EventControllerKey,
    Label, ToggleButton,
};

use crate::models::{ChronoRow, Entry as DayEntry, EntryKind, Summarize};
use crate::theme::Fmt;
use crate::ui;

/// Što se uređuje u kronološkom pregledu — postojeći red ili novi unos.
#[derive(Debug, Clone, PartialEq)]
pub enum EditTarget {
    Existing(ChronoRow),
    /// Novi unos; `gap_start` je rupa iz koje je pokrenut (None = gumb "Dodaj unos").
    New { gap_start: Option<DateTime<Local>> },
}

impl EditTarget {
    pub fn row(&self) -> Option<&ChronoRow> {
        match self {
            EditTarget::Existing(row) => Some(row),
            EditTarget::New { .. } => None,
        }
    }
}

/// Potvrđeno spremanje: opis, od, do, vrsta.
type SaveHandler = dyn Fn(String, DateTime<Local>, DateTime<Local>, EntryKind);

/// Uređivač unosa unutar kronološkog pregleda — otvara se na mjestu reda (ispravak), rupe
/// ili gumba "Dodaj unos" (novi unos). Spojeni red (`2×`) se ispravlja kao jedna cjelina:
/// promjena samo opisa/vrste zadržava blokove, a promjena vremena ih stopi u jedan unos
/// (novi raspon nema stare granice). Preklopljeni dio drugih unosa se reže.
pub struct EntryEditor {
    root: GtkBox,
    target: EditTarget,
    /// Dan pregleda — upisana vremena se tumače unutar njega.
    day: DateTime<Local>,
    entries: Vec<DayEntry>,
    text: Entry,
    start: Entry,
    end: Entry,
    duration: Label,
    notice: Label,
    work_toggle: ToggleButton,
    save_button: Button,
    on_save: Box<SaveHandler>,
    on_delete: Box<dyn Fn()>,
    on_cancel: Box<dyn Fn()>,
    /// Gumb je jednom pritisnut — drugi Enter/klik dok se pregled osvježava ne sprema dvaput.
    done: Cell<bool>,
}

impl EntryEditor {
    #[allow(clippy::too_many_arguments)]
    pub fn new(
        target: EditTarget,
        initial: (DateTime<Local>, DateTime<Local>),
        day: DateTime<Local>,
        entries: Vec<DayEntry>,
        history: Vec<String>,
        on_save: impl Fn(String, DateTime<Local>, DateTime<Local>, EntryKind) + 'static,
        on_delete: impl Fn() + 'static,
        on_cancel: impl Fn() + 'static,
    ) -> Rc<Self> {
        let (text_value, kind) = match &target {
            EditTarget::Existing(row) => (row.text.clone(), row.kind),
            EditTarget::New { .. } => (String::new(), EntryKind::Work),
        };

        let text = Entry::new();
        text.add_css_class("brand");
        text.add_css_class("compact");
        text.set_hexpand(true);
        text.set_placeholder_text(Some("Opis — npr. Projekt X — opis zadatka"));
        text.set_text(&text_value);

        let work_toggle = ToggleButton::with_label("Rad");
        let pause_toggle = ToggleButton::with_label("Pauza");
        pause_toggle.set_group(Some(&work_toggle));
        work_toggle.add_css_class("outline");
        pause_toggle.add_css_class("outline");
        work_toggle.set_active(kind == EntryKind::Work);
        pause_toggle.set_active(kind == EntryKind::Pause);

        let save_label = if target.row().is_some() { "Spremi" } else { "Dodaj" };
        let me = Rc::new(EntryEditor {
            root: ui::vbox(10),
            day,
            entries,
            text: text.clone(),
            start: time_entry(&Fmt::hhmm(initial.0)),
            end: time_entry(&Fmt::hhmm(initial.1)),
            duration: ui::label("", &["mono", "accent"]),
            notice: ui::wrapped("", &["muted"], 60),
            work_toggle: work_toggle.clone(),
            save_button: Button::with_label(save_label),
            on_save: Box::new(on_save),
            on_delete: Box::new(on_delete),
            on_cancel: Box::new(on_cancel),
            done: Cell::new(false),
            target,
        });
        me.root.add_css_class("editor-card");
        ui::pad(&me.root, 0);

        // MARK: Vrijeme i vrsta
        let times = ui::hbox(8);
        times.append(&ui::label("OD", &["section-label"]));
        times.append(&me.start);
        times.append(&ui::label("DO", &["section-label"]));
        times.append(&me.end);
        me.duration.set_width_chars(8);
        times.append(&me.duration);
        times.append(&ui::spacer());
        let kinds = ui::hbox(4);
        kinds.append(&work_toggle);
        kinds.append(&pause_toggle);
        times.append(&kinds);
        me.root.append(&times);

        // MARK: Opis
        let description = ui::hbox(8);
        description.append(&text);
        if !history.is_empty() {
            let mut labels = vec!["Povijest…".to_string()];
            labels.extend(history.iter().cloned());
            let picker =
                DropDown::from_strings(&labels.iter().map(String::as_str).collect::<Vec<_>>());
            picker.set_valign(Align::Center);
            picker.set_tooltip_text(Some("Opis iz povijesti"));
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
        me.root.append(&description);

        me.root.append(&me.notice);

        // MARK: Gumbi
        let footer = ui::hbox(8);
        if let Some(row) = me.target.row() {
            let label = if row.is_merged() {
                format!("Obriši ({} bloka)", row.ids.len())
            } else {
                "Obriši".to_string()
            };
            let weak = Rc::downgrade(&me);
            footer.append(&ui::button(&label, &["danger"], move || {
                if let Some(me) = weak.upgrade() {
                    if !me.done.replace(true) {
                        (me.on_delete)();
                    }
                }
            }));
        }
        footer.append(&ui::spacer());
        {
            let weak = Rc::downgrade(&me);
            footer.append(&ui::button("Odustani", &["outline"], move || {
                if let Some(me) = weak.upgrade() {
                    me.cancel();
                }
            }));
        }
        me.save_button.add_css_class("yellow");
        {
            let weak = Rc::downgrade(&me);
            me.save_button.connect_clicked(move |_| {
                if let Some(me) = weak.upgrade() {
                    me.save();
                }
            });
        }
        footer.append(&me.save_button);
        me.root.append(&footer);

        // MARK: Vezanje kontrola
        for entry in [&me.text, &me.start, &me.end] {
            let weak = Rc::downgrade(&me);
            entry.connect_changed(move |_| {
                if let Some(me) = weak.upgrade() {
                    me.sync();
                }
            });
            let weak = Rc::downgrade(&me);
            entry.connect_activate(move |_| {
                if let Some(me) = weak.upgrade() {
                    me.save();
                }
            });
        }
        for field in [&me.start, &me.end] {
            // Strelice ↑/↓ pomiču vrijeme za 5 minuta.
            let key = EventControllerKey::new();
            let target = field.clone();
            key.connect_key_pressed(move |_, keyval, _, _| match keyval {
                Key::Up => {
                    step(&target, 5);
                    Propagation::Stop
                }
                Key::Down => {
                    step(&target, -5);
                    Propagation::Stop
                }
                _ => Propagation::Proceed,
            });
            field.add_controller(key);

            // Napušteno polje se uredi u HH:mm ("930" → "09:30").
            let focus = EventControllerFocus::new();
            let target = field.clone();
            focus.connect_leave(move |_| {
                if let Some((h, m)) = Fmt::parse_time(&target.text()) {
                    let formatted = format!("{h:02}:{m:02}");
                    if target.text() != formatted {
                        target.set_text(&formatted);
                    }
                }
            });
            field.add_controller(focus);
        }
        for toggle in [&work_toggle, &pause_toggle] {
            let weak = Rc::downgrade(&me);
            toggle.connect_toggled(move |_| {
                if let Some(me) = weak.upgrade() {
                    me.sync();
                }
            });
        }

        let key = EventControllerKey::new();
        {
            let weak = Rc::downgrade(&me);
            key.connect_key_pressed(move |_, keyval, _, _| {
                if keyval == Key::Escape {
                    if let Some(me) = weak.upgrade() {
                        me.cancel();
                    }
                    return Propagation::Stop;
                }
                Propagation::Proceed
            });
        }
        me.root.add_controller(key);

        me.sync();
        me
    }

    pub fn widget(&self) -> &GtkBox {
        &self.root
    }

    pub fn target(&self) -> &EditTarget {
        &self.target
    }

    pub fn focus_text(&self) {
        self.text.grab_focus();
    }

    // MARK: - Stanje

    fn kind(&self) -> EntryKind {
        if self.work_toggle.is_active() {
            EntryKind::Work
        } else {
            EntryKind::Pause
        }
    }

    fn parse(&self, field: &Entry) -> Option<DateTime<Local>> {
        Fmt::parse_time(&field.text()).map(|(h, m)| Fmt::at(self.day, h, m))
    }

    fn range(&self) -> Option<(DateTime<Local>, DateTime<Local>)> {
        let (start, end) = (self.parse(&self.start)?, self.parse(&self.end)?);
        (end > start).then_some((start, end))
    }

    fn trimmed(&self) -> String {
        self.text.text().trim().to_string()
    }

    fn is_valid(&self) -> bool {
        !self.trimmed().is_empty() && self.range().is_some()
    }

    fn times_changed(&self) -> bool {
        match (self.target.row(), self.parse(&self.start), self.parse(&self.end)) {
            (Some(row), Some(start), Some(end)) => {
                (start - row.start).num_seconds().abs() > 1 || (end - row.end).num_seconds().abs() > 1
            }
            _ => true,
        }
    }

    /// Unosi koje će spremanje skratiti ili maknuti.
    fn overlap_count(&self) -> usize {
        match self.range() {
            Some((start, end)) if self.times_changed() => {
                let excluding = self.target.row().map(|r| r.ids.clone()).unwrap_or_default();
                Summarize::overlapping(&self.entries, start, end, &excluding).len()
            }
            _ => 0,
        }
    }

    /// Trajanje, upozorenja i stanje gumba nakon svake promjene — trajanje nakon izmjene je
    /// odmah vidljivo.
    fn sync(&self) {
        let (start, end) = (self.parse(&self.start), self.parse(&self.end));
        set_class(&self.start, "invalid", start.is_none());
        set_class(&self.end, "invalid", end.is_none());

        let ok = self.range().is_some();
        self.duration.set_text(&match (start, end) {
            (Some(s), Some(e)) => Fmt::dur(((e - s).num_milliseconds() as f64 / 1000.0).max(0.0)),
            _ => "—".to_string(),
        });
        set_class(&self.duration, "accent", ok);
        set_class(&self.duration, "warn", !ok);

        let (notice, warn) = if start.is_none() || end.is_none() {
            ("Vrijeme upiši kao 9:30 ili 0930.".to_string(), true)
        } else if !ok {
            ("Kraj mora biti nakon početka.".to_string(), true)
        } else {
            let mut parts = Vec::new();
            if let Some(row) = self.target.row() {
                if row.is_merged() && self.times_changed() {
                    parts.push(format!("Promjena vremena stapa {} bloka u jedan unos.", row.ids.len()));
                }
            }
            let n = self.overlap_count();
            if n > 0 {
                let noun = if n == 1 { "unosom" } else { "unosa" };
                parts.push(format!(
                    "Preklapa se s {n} {noun} — ovaj unos zamjenjuje preklopljeni dio."
                ));
            }
            (parts.join(" "), false)
        };
        self.notice.set_text(&notice);
        self.notice.set_visible(!notice.is_empty());
        set_class(&self.notice, "warn", warn);
        set_class(&self.notice, "accent", !warn);

        self.save_button.set_sensitive(self.is_valid());
    }

    fn save(&self) {
        let Some((start, end)) = self.range() else {
            return;
        };
        if !self.is_valid() || self.done.replace(true) {
            return;
        }
        (self.on_save)(self.trimmed(), start, end, self.kind());
    }

    fn cancel(&self) {
        if !self.done.replace(true) {
            (self.on_cancel)();
        }
    }
}

/// Polje za vrijeme: upisuje se rukom ("930", "9:30"), a strelice ↑/↓ pomiču za 5 min.
fn time_entry(value: &str) -> Entry {
    let entry = Entry::new();
    entry.add_css_class("brand");
    entry.add_css_class("time");
    entry.set_width_chars(5);
    entry.set_max_width_chars(5);
    EditableExt::set_alignment(&entry, 0.5);
    entry.set_placeholder_text(Some("hh:mm"));
    entry.set_tooltip_text(Some("Upiši npr. 9:30 ili 0930 — strelice ↑/↓ pomiču za 5 minuta"));
    entry.set_text(value);
    entry
}

fn step(field: &Entry, minutes: i32) {
    let Some((h, m)) = Fmt::parse_time(&field.text()) else {
        return;
    };
    let total = (h as i32 * 60 + m as i32 + minutes).clamp(0, 23 * 60 + 59);
    field.set_text(&format!("{:02}:{:02}", total / 60, total % 60));
    field.set_position(-1);
}

fn set_class(widget: &impl IsA<gtk4::Widget>, class: &str, on: bool) {
    if on {
        widget.as_ref().add_css_class(class);
    } else {
        widget.as_ref().remove_css_class(class);
    }
}
