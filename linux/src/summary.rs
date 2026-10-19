use std::cell::RefCell;
use std::rc::Rc;

use chrono::{DateTime, Duration, Local};
use gtk4::prelude::*;
use gtk4::{
    Align, Box as GtkBox, Button, CheckButton, PolicyType, ScrolledWindow, ToggleButton, Window,
};

use crate::entry_edit::{EditTarget, EntryEditor};
use crate::models::{ChronoRow, Entry, EntryKind, Summarize, TimelineItem};
use crate::store::Store;
use crate::theme::Fmt;
use crate::ui;
use crate::App;

#[derive(Clone, Copy, PartialEq)]
enum Mode {
    Grouped,
    Chronological,
}

/// Ono što je zadnje nacrtano u sadržaju. Pregled se osvježava svake sekunde (tick ide
/// kroz `mutate`), a ponovno slaganje istih redova bi gubilo klikove i hover — zato se
/// sadržaj slaže samo kad se ovo promijeni.
#[derive(PartialEq)]
struct Rendered {
    day_key: String,
    mode: Mode,
    merging: bool,
    entries: Vec<Entry>,
}

/// Pregled dana — grupirano po opisu ili kronološki, s kopiranjem i CSV exportom.
pub struct SummaryWindow {
    window: Window,
    app: Rc<App>,
    date: RefCell<DateTime<Local>>,
    mode: RefCell<Mode>,
    title: gtk4::Label,
    work_badge: gtk4::Label,
    pause_badge: gtk4::Label,
    merge_row: GtkBox,
    merge_check: CheckButton,
    content: GtkBox,
    next_button: Button,
    chrono_toggle: ToggleButton,
    /// Otvoren uređivač u kronološkom prikazu — dok je otvoren, sadržaj se ne slaže
    /// ponovno (tick bi ga inače uništio usred tipkanja).
    editor: RefCell<Option<Rc<EntryEditor>>>,
    rendered: RefCell<Option<Rendered>>,
}

impl SummaryWindow {
    pub fn new(app: &Rc<App>) -> Rc<Self> {
        let window = Window::builder()
            .application(&app.gtk)
            .title("Pregled dana")
            .default_width(560)
            .default_height(520)
            .icon_name("lloyds-tracker")
            .build();
        window.add_css_class("brand");

        let root = ui::vbox(0);

        // MARK: Zaglavlje — navigacija po danima
        let header = ui::vbox(10);
        let nav = ui::hbox(8);
        let title = ui::label("", &["big-title"]);
        title.set_halign(Align::Center);
        title.set_hexpand(true);
        title.set_xalign(0.5);
        let prev_button = day_button("‹", "Prethodni dan");
        let next_button = day_button("›", "Sljedeći dan");
        nav.append(&prev_button);
        nav.append(&title);
        nav.append(&next_button);
        header.append(&nav);

        let badges = ui::hbox(16);
        let work_badge = ui::label("", &["mono", "accent"]);
        let pause_badge = ui::label("", &["mono", "muted"]);
        badges.append(&badge_box("RAD", &work_badge));
        badges.append(&badge_box("PAUZE", &pause_badge));
        badges.append(&ui::spacer());

        let grouped_toggle = ToggleButton::with_label("Grupirano");
        let chrono_toggle = ToggleButton::with_label("Kronološki");
        chrono_toggle.set_group(Some(&grouped_toggle));
        grouped_toggle.set_active(true);
        grouped_toggle.add_css_class("outline");
        chrono_toggle.add_css_class("outline");
        badges.append(&grouped_toggle);
        badges.append(&chrono_toggle);
        header.append(&badges);

        // Spajanje susjednih unosa je postavka (pamti se), ali se toggle-a ovdje jer
        // vrijedi samo za kronološki prikaz.
        let merge_row = ui::hbox(0);
        let merge_check = CheckButton::with_label("Spoji susjedne unose istog naziva");
        merge_check.set_tooltip_text(Some(
            "14:45–15:00 + 15:00–15:15 istog naziva prikazuje se kao 14:45–15:15",
        ));
        merge_check.set_active(app.engine.borrow().settings.merge_adjacent_entries);
        merge_row.append(&merge_check);
        merge_row.set_visible(false);
        header.append(&merge_row);

        ui::pad(&header, 16);
        root.append(&header);
        root.append(&gtk4::Separator::new(gtk4::Orientation::Horizontal));

        // MARK: Sadržaj
        let content = ui::vbox(8);
        ui::pad(&content, 16);
        let scroll = ScrolledWindow::builder()
            .hscrollbar_policy(PolicyType::Never)
            .vexpand(true)
            .child(&content)
            .build();
        root.append(&scroll);
        root.append(&gtk4::Separator::new(gtk4::Orientation::Horizontal));

        let summary = Rc::new(SummaryWindow {
            window: window.clone(),
            app: app.clone(),
            date: RefCell::new(Local::now()),
            mode: RefCell::new(Mode::Grouped),
            title,
            work_badge,
            pause_badge,
            merge_row,
            merge_check: merge_check.clone(),
            content,
            next_button: next_button.clone(),
            chrono_toggle: chrono_toggle.clone(),
            editor: RefCell::new(None),
            rendered: RefCell::new(None),
        });

        // MARK: Podnožje
        let footer = ui::hbox(8);
        let copy_button = {
            let me = summary.clone();
            ui::button("Kopiraj pregled", &["pill"], move || me.copy())
        };
        let export_button = {
            let me = summary.clone();
            ui::button("Export CSV…", &["pill"], move || me.export_csv())
        };
        footer.append(&copy_button);
        footer.append(&export_button);
        footer.append(&ui::spacer());
        footer.append(&ui::button("Otvori folder s podacima", &["link"], || {
            open_data_folder();
        }));
        ui::pad(&footer, 12);
        root.append(&footer);

        window.set_child(Some(&root));

        // MARK: Vezanje kontrola
        {
            let me = summary.clone();
            prev_button.connect_clicked(move |_| me.shift_day(-1));
        }
        {
            let me = summary.clone();
            next_button.connect_clicked(move |_| me.shift_day(1));
        }
        {
            let me = summary.clone();
            grouped_toggle.connect_toggled(move |b| {
                if b.is_active() {
                    me.close_editor(false);
                    *me.mode.borrow_mut() = Mode::Grouped;
                    me.refresh();
                }
            });
        }
        {
            let me = summary.clone();
            chrono_toggle.connect_toggled(move |b| {
                if b.is_active() {
                    me.close_editor(false);
                    *me.mode.borrow_mut() = Mode::Chronological;
                    me.refresh();
                }
            });
        }
        {
            let me = summary.clone();
            merge_check.connect_toggled(move |b| {
                let on = b.is_active();
                if on == me.app.engine.borrow().settings.merge_adjacent_entries {
                    return;
                }
                me.close_editor(false);
                let settings = {
                    let mut s = me.app.engine.borrow().settings.clone();
                    s.merge_adjacent_entries = on;
                    s
                };
                me.app.clone().mutate(|engine| engine.update_settings(settings));
                me.refresh();
            });
        }

        summary.refresh();
        window.present();
        summary
    }

    fn day_key(&self) -> String {
        Store::day_key(*self.date.borrow())
    }

    /// Današnji dan čitamo iz enginea (uživo), starije s diska.
    fn entries(&self) -> Vec<Entry> {
        let key = self.day_key();
        let engine = self.app.engine.borrow();
        if key == engine.current_day_key {
            engine.entries.clone()
        } else {
            Store::load_day(&key)
        }
    }

    fn shift_day(self: &Rc<Self>, days: i64) {
        self.close_editor(false);
        let new = *self.date.borrow() + Duration::days(days);
        *self.date.borrow_mut() = new;
        self.refresh();
    }

    pub fn refresh(self: &Rc<Self>) {
        let date = *self.date.borrow();
        let entries = self.entries();
        let mode = *self.mode.borrow();

        self.title.set_text(&Fmt::day_title(date));
        self.work_badge.set_text(&Fmt::dur(Summarize::work_total(&entries)));
        self.pause_badge.set_text(&Fmt::dur(Summarize::pause_total(&entries)));
        self.merge_row.set_visible(mode == Mode::Chronological);
        // Naprijed se ne ide dalje od danas.
        self.next_button
            .set_sensitive(date.date_naive() < Local::now().date_naive());

        let merge = self.app.engine.borrow().settings.merge_adjacent_entries;
        if self.merge_check.is_active() != merge {
            self.merge_check.set_active(merge);
        }

        if self.editor.borrow().is_some() {
            return;
        }
        let rendered = Rendered { day_key: self.day_key(), mode, merging: merge, entries };
        if self.rendered.borrow().as_ref() == Some(&rendered) {
            return;
        }
        self.render(&rendered);
        *self.rendered.borrow_mut() = Some(rendered);
    }

    fn render(self: &Rc<Self>, state: &Rendered) {
        while let Some(child) = self.content.first_child() {
            self.content.remove(&child);
        }
        let entries = &state.entries;

        if entries.is_empty() && self.editor.borrow().is_none() {
            let empty = ui::vbox(12);
            empty.set_valign(Align::Center);
            empty.set_vexpand(true);
            let text = ui::label("Nema unosa za ovaj dan.", &["muted"]);
            text.set_halign(Align::Center);
            empty.append(&text);
            let add = self.add_button();
            add.set_halign(Align::Center);
            add.set_size_request(200, -1);
            empty.append(&add);
            self.content.append(&empty);
            return;
        }

        match state.mode {
            Mode::Grouped => {
                for group in Summarize::groups(entries) {
                    let row = ui::vbox(4);
                    let head = ui::hbox(8);
                    let total = ui::label(&Fmt::dur(group.total), &["mono", "accent"]);
                    total.set_width_chars(8);
                    head.append(&total);
                    head.append(&ui::label(&group.text, &[]));
                    head.append(&ui::spacer());
                    row.append(&head);

                    let ranges: Vec<String> = group
                        .ranges
                        .iter()
                        .map(|(s, e)| format!("{}–{}", Fmt::hhmm(*s), Fmt::hhmm(*e)))
                        .collect();
                    let ranges_label = ui::label(&ranges.join(" · "), &["mono", "muted-dim"]);
                    ranges_label.set_margin_start(64);
                    row.append(&ranges_label);

                    row.add_css_class("row-card");
                    ui::pad(&row, 10);
                    self.content.append(&row);
                }
            }
            Mode::Chronological => {
                let editor = self.editor.borrow().clone();
                let target = editor.as_ref().map(|e| e.target().clone());
                let rows = Summarize::chronology(entries, state.merging);
                for item in Summarize::timeline(&rows) {
                    match item {
                        TimelineItem::Row(row) => match &editor {
                            Some(e) if target.as_ref().and_then(|t| t.row()) == Some(&row) => {
                                self.content.append(e.widget());
                            }
                            _ => self.content.append(&self.chrono_row(&row)),
                        },
                        TimelineItem::Gap(start, end) => match &editor {
                            Some(e) if target == Some(EditTarget::New { gap_start: Some(start) }) => {
                                self.content.append(e.widget());
                            }
                            _ => self.content.append(&self.gap_row(start, end)),
                        },
                    }
                }
                match &editor {
                    Some(e) if target == Some(EditTarget::New { gap_start: None }) => {
                        self.content.append(e.widget());
                    }
                    _ => {
                        let add = self.add_button();
                        add.set_margin_top(4);
                        self.content.append(&add);
                    }
                }
            }
        }
    }

    /// Cijeli red je gumb — klik otvara uređivač na mjestu reda.
    fn chrono_row(self: &Rc<Self>, chrono_row: &ChronoRow) -> Button {
        let row = ui::hbox(10);
        row.append(&ui::label(
            &format!("{}–{}", Fmt::hhmm(chrono_row.start), Fmt::hhmm(chrono_row.end)),
            &["mono", "muted"],
        ));
        let text = ui::label(&chrono_row.text, &[]);
        text.set_ellipsize(gtk4::pango::EllipsizeMode::End);
        if chrono_row.kind == EntryKind::Pause {
            text.add_css_class("pause-text");
        }
        row.append(&text);
        row.append(&ui::spacer());
        if chrono_row.is_merged() {
            row.append(&ui::label(&format!("{}×", chrono_row.ids.len()), &["mono", "accent"]));
        }
        row.append(&ui::label(&Fmt::dur(chrono_row.duration()), &["mono", "muted-dim"]));
        row.append(&ui::label("✎", &["edit-hint"]));

        let button = Button::new();
        button.set_child(Some(&row));
        button.add_css_class("row");
        button.set_tooltip_text(Some(&if chrono_row.is_merged() {
            format!("Uredi unos ({} bloka)", chrono_row.ids.len())
        } else {
            "Uredi unos".to_string()
        }));
        let me = self.clone();
        let edited = chrono_row.clone();
        button.connect_clicked(move |_| {
            me.open_editor(EditTarget::Existing(edited.clone()), (edited.start, edited.end));
        });
        button
    }

    /// Nezabilježeno vrijeme između dva unosa — klik ga popuni novim unosom.
    fn gap_row(self: &Rc<Self>, start: DateTime<Local>, end: DateTime<Local>) -> Button {
        let row = ui::hbox(10);
        row.append(&ui::label("+", &["gap-text"]));
        row.append(&ui::label(
            &format!("{}–{}", Fmt::hhmm(start), Fmt::hhmm(end)),
            &["mono", "gap-text"],
        ));
        let seconds = (end - start).num_milliseconds() as f64 / 1000.0;
        row.append(&ui::label(&format!("nezabilježeno · {}", Fmt::dur(seconds)), &["gap-text"]));
        row.append(&ui::spacer());
        row.append(&ui::label("Dodaj unos", &["gap-text", "gap-hint"]));

        let button = Button::new();
        button.set_child(Some(&row));
        button.add_css_class("gap");
        button.set_tooltip_text(Some(&format!(
            "Dodaj unos za {}–{}",
            Fmt::hhmm(start),
            Fmt::hhmm(end)
        )));
        let me = self.clone();
        button.connect_clicked(move |_| {
            me.open_editor(EditTarget::New { gap_start: Some(start) }, (start, end));
        });
        button
    }

    /// Novi unos na kraju dana (od kraja zadnjeg unosa) — i iz praznog dana.
    fn add_button(self: &Rc<Self>) -> Button {
        let button = Button::with_label("+  Dodaj unos");
        button.add_css_class("add");
        let me = self.clone();
        button.connect_clicked(move |_| {
            let range = Summarize::suggested_range(
                &me.entries(),
                *me.date.borrow(),
                &me.app.engine.borrow().settings,
            );
            // Iz praznog grupiranog prikaza se prelazi u kronološki (toggle zatvara uređivač
            // i osvježava, pa se uređivač otvara tek nakon toga).
            me.chrono_toggle.set_active(true);
            me.open_editor(EditTarget::New { gap_start: None }, range);
        });
        button
    }

    // MARK: - Uređivač

    /// Uređivač na mjestu reda, rupe ili gumba "Dodaj unos". Što se dogodi s blokovima
    /// spojenog reda odlučuje engine: promjena samo opisa/vrste ih zadržava, promjena
    /// vremena ih stopi u jedan unos; preklopljeni dio drugih unosa se reže.
    fn open_editor(self: &Rc<Self>, target: EditTarget, range: (DateTime<Local>, DateTime<Local>)) {
        self.close_editor(false);
        let key = self.day_key();
        let entries = self.entries();
        let history = self.app.engine.borrow().history.clone();

        let save = {
            let me = self.clone();
            let key = key.clone();
            let target = target.clone();
            move |text: String, start, end, kind| {
                me.close_editor(false);
                let key = key.clone();
                match target.row().cloned() {
                    Some(row) => me.app.clone().mutate(move |engine| {
                        engine.update_entries(&row, &key, &text, start, end, kind)
                    }),
                    None => me
                        .app
                        .clone()
                        .mutate(move |engine| engine.add_entry(&key, &text, start, end, kind)),
                }
                me.refresh();
            }
        };
        let delete = {
            let me = self.clone();
            let ids = target.row().map(|r| r.ids.clone()).unwrap_or_default();
            move || {
                me.close_editor(false);
                let ids = ids.clone();
                let key = key.clone();
                me.app.clone().mutate(move |engine| engine.delete_entries(&ids, &key));
                me.refresh();
            }
        };
        let cancel = {
            let me = self.clone();
            move || me.close_editor(true)
        };

        let editor = EntryEditor::new(
            target,
            range,
            *self.date.borrow(),
            entries.clone(),
            history,
            save,
            delete,
            cancel,
        );
        *self.editor.borrow_mut() = Some(editor.clone());
        let state = Rendered {
            day_key: self.day_key(),
            mode: *self.mode.borrow(),
            merging: self.app.engine.borrow().settings.merge_adjacent_entries,
            entries,
        };
        self.render(&state);
        *self.rendered.borrow_mut() = None;
        editor.focus_text();
    }

    /// Zatvara uređivač; sadržaj se slaže iznova kod idućeg `refresh` (odmah uz `refresh_now`).
    fn close_editor(self: &Rc<Self>, refresh_now: bool) {
        let had_editor = self.editor.borrow_mut().take().is_some();
        if had_editor {
            *self.rendered.borrow_mut() = None;
        }
        if refresh_now {
            self.refresh();
        }
    }

    fn copy(&self) {
        let text = Summarize::clipboard_text(*self.date.borrow(), &self.entries());
        self.window.clipboard().set_text(&text);
    }

    fn export_csv(&self) {
        let csv = Summarize::csv(&self.entries());
        let dialog = gtk4::FileDialog::builder()
            .initial_name(format!("lloyds-tracker-{}.csv", self.day_key()))
            .title("Export CSV")
            .build();
        dialog.save(
            Some(&self.window),
            None::<&gtk4::gio::Cancellable>,
            move |result| {
                if let Some(path) = result.ok().and_then(|f| f.path()) {
                    let _ = std::fs::write(path, &csv);
                }
            },
        );
    }

    pub fn present(&self) {
        self.window.present();
    }

    pub fn window(&self) -> &Window {
        &self.window
    }
}

/// Strelica za promjenu dana — veliko polje za klik (cijeli gumb, ne samo znak).
fn day_button(glyph: &str, tooltip: &str) -> Button {
    let button = Button::with_label(glyph);
    button.add_css_class("nav");
    button.set_tooltip_text(Some(tooltip));
    button
}

fn badge_box(caption: &str, value: &gtk4::Label) -> GtkBox {
    let b = ui::hbox(6);
    b.append(&ui::label(caption, &["section-label"]));
    b.append(value);
    b.add_css_class("badge");
    b
}

pub fn open_data_folder() {
    let uri = format!("file://{}", Store::directory().display());
    let _ = gtk4::gio::AppInfo::launch_default_for_uri(&uri, None::<&gtk4::gio::AppLaunchContext>);
}
