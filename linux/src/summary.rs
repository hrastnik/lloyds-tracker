use std::cell::RefCell;
use std::rc::Rc;

use chrono::{DateTime, Duration, Local};
use gtk4::prelude::*;
use gtk4::{Align, Box as GtkBox, CheckButton, PolicyType, ScrolledWindow, ToggleButton, Window};

use crate::models::{Entry, EntryKind, Summarize};
use crate::store::Store;
use crate::theme::Fmt;
use crate::ui;
use crate::App;

#[derive(Clone, Copy, PartialEq)]
enum Mode {
    Grouped,
    Chronological,
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
    next_button: gtk4::Button,
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
        let prev_button = ui::button("‹", &["link"], || {});
        let next_button = ui::button("›", &["link"], || {});
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
            prev_button.connect_clicked(move |_| {
                let new = *me.date.borrow() - Duration::days(1);
                *me.date.borrow_mut() = new;
                me.refresh();
            });
        }
        {
            let me = summary.clone();
            next_button.connect_clicked(move |_| {
                let new = *me.date.borrow() + Duration::days(1);
                *me.date.borrow_mut() = new;
                me.refresh();
            });
        }
        {
            let me = summary.clone();
            grouped_toggle.connect_toggled(move |b| {
                if b.is_active() {
                    *me.mode.borrow_mut() = Mode::Grouped;
                    me.refresh();
                }
            });
        }
        {
            let me = summary.clone();
            chrono_toggle.connect_toggled(move |b| {
                if b.is_active() {
                    *me.mode.borrow_mut() = Mode::Chronological;
                    me.refresh();
                }
            });
        }
        {
            let me = summary.clone();
            merge_check.connect_toggled(move |b| {
                let on = b.is_active();
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

        while let Some(child) = self.content.first_child() {
            self.content.remove(&child);
        }

        if entries.is_empty() {
            let empty = ui::label("Nema unosa za ovaj dan.", &["muted"]);
            empty.set_halign(Align::Center);
            empty.set_vexpand(true);
            self.content.append(&empty);
            return;
        }

        match mode {
            Mode::Grouped => {
                for group in Summarize::groups(&entries) {
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
                let merging = self.app.engine.borrow().settings.merge_adjacent_entries;
                for chrono_row in Summarize::chronology(&entries, merging) {
                    let row = ui::hbox(10);
                    row.append(&ui::label(
                        &format!("{}–{}", Fmt::hhmm(chrono_row.start), Fmt::hhmm(chrono_row.end)),
                        &["mono", "muted"],
                    ));
                    let text = ui::label(&chrono_row.text, &[]);
                    if chrono_row.kind == EntryKind::Pause {
                        text.add_css_class("pause-text");
                    }
                    row.append(&text);
                    row.append(&ui::spacer());
                    if chrono_row.is_merged() {
                        row.append(&ui::label(
                            &format!("{}×", chrono_row.ids.len()),
                            &["mono", "accent"],
                        ));
                    }
                    row.append(&ui::label(&Fmt::dur(chrono_row.duration()), &["mono", "muted-dim"]));

                    let ids = chrono_row.ids.clone();
                    let key = self.day_key();
                    let me = self.clone();
                    let delete = ui::button("🗑", &["link"], move || {
                        let ids = ids.clone();
                        let key = key.clone();
                        me.app.clone().mutate(|engine| engine.delete_entries(&ids, &key));
                        me.refresh();
                    });
                    delete.set_tooltip_text(Some(if chrono_row.is_merged() {
                        "Obriši unos (više blokova)"
                    } else {
                        "Obriši unos"
                    }));
                    row.append(&delete);

                    row.add_css_class("row-card-dim");
                    ui::pad(&row, 8);
                    self.content.append(&row);
                }
            }
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
