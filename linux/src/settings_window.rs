use std::cell::Cell;
use std::rc::Rc;

use gtk4::prelude::*;
use gtk4::{
    Align, Box as GtkBox, DropDown, Label, Notebook, PolicyType, ScrolledWindow, SpinButton, Switch,
    Widget, Window,
};

use crate::models::{AppSettings, PromptStyle};
use crate::store::Store;
use crate::theme::Fmt;
use crate::summary::open_data_folder;
use crate::idle::IdleMonitor;
use crate::session::SessionMonitor;
use crate::{ui, App};

const INTERVALS: [i64; 7] = [5, 10, 15, 20, 30, 45, 60];
const HISTORY_LIMITS: [usize; 5] = [5, 10, 15, 25, 50];
const IDLE_THRESHOLDS: [i64; 4] = [3, 5, 10, 15];
const PROMPT_STYLES: [PromptStyle; 2] = [PromptStyle::Floating, PromptStyle::Fullscreen];

/// Postavke u tabovima — isti raspored i tekstovi kao macOS `SettingsView` i Windows
/// `SettingsForm`. Svaka promjena se odmah sprema (nema OK/Cancel), pa svaki handler
/// pokupi stanje svih kontrola i preda ga engineu.
pub struct SettingsWindow {
    window: Window,
    app: Rc<App>,
    /// Sprječava povratnu petlju dok programski postavljamo vrijednosti kontrola.
    updating: Cell<bool>,

    interval: DropDown,
    prompt_style: DropDown,
    sound: Switch,
    history_limit: DropDown,

    workday_enabled: Switch,
    workday_hour: SpinButton,
    workday_minute: SpinButton,
    workday_backfill: Switch,
    skip_weekends: Switch,
    workday_caption: Label,
    backfill_caption: Label,

    auto_stop_enabled: Switch,
    auto_stop_hour: SpinButton,
    auto_stop_minute: SpinButton,

    idle_enabled: Switch,
    idle_threshold: DropDown,
    lock_pause: Switch,

    launch_at_login: Switch,
    launch_status: Label,
    startup_reminder: Switch,
}

impl SettingsWindow {
    pub fn new(app: &Rc<App>) -> Rc<Self> {
        let s = app.engine.borrow().settings.clone();

        let window = Window::builder()
            .application(&app.gtk)
            .title("Postavke")
            .default_width(520)
            .default_height(680)
            .icon_name("lloyds-tracker")
            .build();
        window.add_css_class("brand");

        let me = Rc::new(SettingsWindow {
            window: window.clone(),
            app: app.clone(),
            updating: Cell::new(true),

            interval: dropdown_minutes(&INTERVALS),
            prompt_style: DropDown::from_strings(
                PROMPT_STYLES.map(|p| p.label()).as_ref(),
            ),
            sound: Switch::new(),
            history_limit: DropDown::from_strings(
                &HISTORY_LIMITS.map(|n| n.to_string()).iter().map(String::as_str).collect::<Vec<_>>(),
            ),

            workday_enabled: Switch::new(),
            workday_hour: ui::spin(0.0, 23.0),
            workday_minute: ui::spin(0.0, 59.0),
            workday_backfill: Switch::new(),
            skip_weekends: Switch::new(),
            workday_caption: ui::wrapped("", &["caption"], 58),
            backfill_caption: ui::wrapped("", &["caption"], 58),

            auto_stop_enabled: Switch::new(),
            auto_stop_hour: ui::spin(0.0, 23.0),
            auto_stop_minute: ui::spin(0.0, 59.0),

            idle_enabled: Switch::new(),
            idle_threshold: dropdown_minutes(&IDLE_THRESHOLDS),
            lock_pause: Switch::new(),

            launch_at_login: Switch::new(),
            launch_status: ui::wrapped("", &["caption", "warn"], 58),
            startup_reminder: Switch::new(),
        });

        // MARK: Početne vrijednosti
        me.interval.set_selected(index_of(&INTERVALS, s.interval_minutes));
        me.prompt_style.set_selected(index_of(&PROMPT_STYLES, s.prompt_style));
        me.sound.set_active(s.sound_enabled);
        me.history_limit.set_selected(index_of(&HISTORY_LIMITS, s.history_limit));
        me.workday_enabled.set_active(s.workday_start_enabled);
        me.workday_hour.set_value(s.workday_start_hour as f64);
        me.workday_minute.set_value(s.workday_start_minute as f64);
        me.workday_backfill.set_active(s.workday_start_backfill_enabled);
        me.skip_weekends.set_active(s.skip_weekend_reminders);
        me.auto_stop_enabled.set_active(s.auto_stop_enabled);
        me.auto_stop_hour.set_value(s.auto_stop_hour as f64);
        me.auto_stop_minute.set_value(s.auto_stop_minute as f64);
        me.idle_enabled.set_active(s.idle_detection_enabled);
        me.idle_threshold.set_selected(index_of(&IDLE_THRESHOLDS, s.idle_threshold_minutes));
        me.lock_pause.set_active(s.lock_pause_enabled);
        me.launch_at_login.set_active(s.launch_at_login);
        me.startup_reminder.set_active(s.show_startup_reminder);

        let notebook = Notebook::new();
        notebook.append_page(&tab(&me.prompt_tab()), Some(&Label::new(Some("Promptanje"))));
        notebook.append_page(&tab(&me.day_tab()), Some(&Label::new(Some("Radni dan"))));
        notebook.append_page(&tab(&me.system_tab()), Some(&Label::new(Some("Sustav"))));
        window.set_child(Some(&notebook));

        me.connect_all();
        me.updating.set(false);
        me.sync_enabled();
        me.refresh_captions();
        window.present();
        me
    }

    // MARK: - Tabovi

    fn prompt_tab(self: &Rc<Self>) -> GtkBox {
        let page = ui::vbox(18);

        let promptanje = section("PROMPTANJE");
        promptanje.append(&row("Interval", &self.interval));
        promptanje.append(&row("Stil prompta", &self.prompt_style));
        promptanje.append(&row("Zvuk kod prompta", &self.sound));
        if !crate::sound::is_available() {
            promptanje.append(&ui::wrapped(
                "Za zvuk je potreban jedan od: canberra-gtk-play, paplay, pw-play ili aplay.",
                &["caption", "warn"],
                58,
            ));
        }
        page.append(&promptanje);

        let history = section("POVIJEST");
        history.append(&row("Broj zapamćenih unosa", &self.history_limit));
        history.append(&ui::wrapped(
            "Koliko se nedavnih unosa pamti za pre-fill i listanje (↑/↓) u promptu.",
            &["caption"],
            58,
        ));
        page.append(&history);

        page
    }

    fn day_tab(self: &Rc<Self>) -> GtkBox {
        let page = ui::vbox(18);

        let start = section("POČETAK RADNOG DANA");
        start.append(&row("Podsjetnik u zadano vrijeme", &self.workday_enabled));
        start.append(&self.workday_caption);
        start.append(&row("Vrijeme", &ui::time_box(&self.workday_hour, &self.workday_minute)));
        start.append(&row("Ponudi i nadoknadu od tog vremena", &self.workday_backfill));
        start.append(&self.backfill_caption);
        start.append(&row("Preskoči vikende", &self.skip_weekends));
        start.append(&ui::wrapped(
            "Subotom i nedjeljom nema podsjetnika u zadano vrijeme ni kod pokretanja aplikacije.",
            &["caption"],
            58,
        ));
        page.append(&start);

        let auto_stop = section("AUTOMATSKO ZAUSTAVLJANJE");
        auto_stop.append(&row("Zaustavi tracking u zadano vrijeme", &self.auto_stop_enabled));
        auto_stop.append(&row("Vrijeme", &ui::time_box(&self.auto_stop_hour, &self.auto_stop_minute)));
        auto_stop.append(&ui::wrapped(
            "Minutu prije iskoči upozorenje s produženjem (+15 / +30 / +45 / +1 h), koje vrijedi \
             samo za taj dan. Bez reakcije dan se sam zatvara — pa tracking ne ostane pokrenut \
             preko noći.",
            &["caption"],
            58,
        ));
        page.append(&auto_stop);

        let away = section("ODSUTNOST");
        away.append(&row("Detekcija neaktivnosti (tipkovnica/miš)", &self.idle_enabled));
        away.append(&row("Prag neaktivnosti", &self.idle_threshold));
        away.append(&row("Bilježi pauzu kad je ekran zaključan", &self.lock_pause));
        away.append(&ui::wrapped(
            "Uključeno: razdoblje odsutnosti se bilježi kao pauza, a prompt čeka da se vratiš. \
             Isključeno: prompt te u zakazano vrijeme samo pita što si radio.",
            &["caption"],
            58,
        ));
        // Na Linuxu neaktivnost i zaključavanje ovise o desktopu (GNOME/KDE), pa ovdje
        // piše što je stvarno pronađeno — inače bi uključena opcija tiho ne radila.
        away.append(&ui::wrapped(
            &format!(
                "Neaktivnost: {} · Zaključavanje: {}",
                IdleMonitor::backend_label(),
                SessionMonitor::backend_label()
            ),
            &["caption", "mono"],
            58,
        ));
        page.append(&away);

        page
    }

    fn system_tab(self: &Rc<Self>) -> GtkBox {
        let page = ui::vbox(18);

        let system = section("SUSTAV");
        system.append(&row("Pokreni kod prijave (autostart)", &self.launch_at_login));
        system.append(&self.launch_status);
        system.append(&row("Podsjetnik kod pokretanja (pop-up)", &self.startup_reminder));
        system.append(&ui::wrapped(
            "Kad se app pokrene, iskoči pop-up da te podsjeti da pokreneš radni dan ako tracking \
             još nije aktivan.",
            &["caption"],
            58,
        ));
        page.append(&system);

        let data = section("PODACI");
        let location = ui::wrapped(&Store::directory().display().to_string(), &["mono", "caption"], 58);
        location.set_selectable(true);
        data.append(&row_label("Lokacija", &location));
        let open = ui::button("Otvori folder s podacima", &["pill"], open_data_folder);
        open.set_halign(Align::Start);
        data.append(&open);
        page.append(&data);

        page
    }

    // MARK: - Promjene

    fn connect_all(self: &Rc<Self>) {
        macro_rules! on_switch {
            ($($w:ident),*) => {$({
                let me = self.clone();
                self.$w.connect_active_notify(move |_| me.apply());
            })*};
        }
        macro_rules! on_dropdown {
            ($($w:ident),*) => {$({
                let me = self.clone();
                self.$w.connect_selected_notify(move |_| me.apply());
            })*};
        }
        macro_rules! on_spin {
            ($($w:ident),*) => {$({
                let me = self.clone();
                self.$w.connect_value_changed(move |_| me.apply());
            })*};
        }

        on_switch!(
            sound,
            workday_enabled,
            workday_backfill,
            skip_weekends,
            auto_stop_enabled,
            idle_enabled,
            lock_pause,
            launch_at_login,
            startup_reminder
        );
        on_dropdown!(interval, prompt_style, history_limit, idle_threshold);
        on_spin!(workday_hour, workday_minute, auto_stop_hour, auto_stop_minute);
    }

    /// Skuplja stanje svih kontrola i predaje ga engineu (koji ga sprema i primijeni).
    fn apply(self: &Rc<Self>) {
        if self.updating.get() {
            return;
        }
        let settings = AppSettings {
            auto_stop_enabled: self.auto_stop_enabled.is_active(),
            auto_stop_hour: self.auto_stop_hour.value_as_int() as u32,
            auto_stop_minute: self.auto_stop_minute.value_as_int() as u32,
            history_limit: HISTORY_LIMITS[self.history_limit.selected() as usize],
            idle_detection_enabled: self.idle_enabled.is_active(),
            idle_threshold_minutes: IDLE_THRESHOLDS[self.idle_threshold.selected() as usize],
            interval_minutes: INTERVALS[self.interval.selected() as usize],
            launch_at_login: self.launch_at_login.is_active(),
            lock_pause_enabled: self.lock_pause.is_active(),
            merge_adjacent_entries: self.app.engine.borrow().settings.merge_adjacent_entries,
            prompt_style: PROMPT_STYLES[self.prompt_style.selected() as usize],
            show_startup_reminder: self.startup_reminder.is_active(),
            skip_weekend_reminders: self.skip_weekends.is_active(),
            sound_enabled: self.sound.is_active(),
            workday_start_backfill_enabled: self.workday_backfill.is_active(),
            workday_start_enabled: self.workday_enabled.is_active(),
            workday_start_hour: self.workday_hour.value_as_int() as u32,
            workday_start_minute: self.workday_minute.value_as_int() as u32,
        };
        self.app.clone().mutate(|engine| engine.update_settings(settings));
        self.sync_enabled();
        self.refresh_captions();
    }

    /// Kontrole koje ovise o svom prekidaču.
    fn sync_enabled(&self) {
        let workday = self.workday_enabled.is_active();
        self.workday_hour.set_sensitive(workday);
        self.workday_minute.set_sensitive(workday);
        self.workday_backfill.set_sensitive(workday);
        let auto_stop = self.auto_stop_enabled.is_active();
        self.auto_stop_hour.set_sensitive(auto_stop);
        self.auto_stop_minute.set_sensitive(auto_stop);
        self.idle_threshold.set_sensitive(self.idle_enabled.is_active());
    }

    /// Opisi opcija govore o konkretnom satu ("Start od 08:30"), pa se osvježavaju kad
    /// se vrijeme početka radnog dana promijeni.
    fn refresh_captions(&self) {
        let start = self.workday_hour.value_as_int() * 60 + self.workday_minute.value_as_int();
        let clock = Fmt::clock(start);
        // Buđenje laptopa u primjeru je 1 h 15 min nakon početka, da primjer ostane
        // smislen i kad je početak pomaknut (10:00 → 11:15).
        let example = Fmt::clock(start + 75);

        self.workday_caption.set_text(&format!(
            "Svaki dan u {clock} iskoči pop-up i pita želiš li pokrenuti radni dan — ali samo ako \
             tracking već nije aktivan. Ako je računalo u {clock} bilo ugašeno ili je spavalo, \
             podsjetnik ne propada: iskoči čim ga probudiš. Javlja se jednom dnevno."
        ));
        self.backfill_caption.set_text(&format!(
            "Određuje što pop-up nudi kad ga otvoriš nakon {clock}. Uključeno: gumb „Start od \
             {clock}” upisuje dan od {clock}, pa te prvi prompt pita što si radio od {clock} do \
             sada — npr. probudiš laptop u {example} i prompt te pita za {clock}–{example}. Uz \
             njega ostaje i „Počni tek od sada”. Isključeno: pop-up ima samo „Start” i tracking \
             teče od trenutka klika — jutro ostaje neupisano."
        ));

        let status = self.app.engine.borrow().launch_at_login_status.clone();
        self.launch_status.set_text(status.as_deref().unwrap_or(""));
        self.launch_status.set_visible(status.is_some());
    }

    pub fn present(&self) {
        self.window.present();
    }

    pub fn window(&self) -> &Window {
        &self.window
    }
}

// MARK: - Graditelji

fn tab(page: &GtkBox) -> ScrolledWindow {
    ui::pad(page, 18);
    ScrolledWindow::builder()
        .hscrollbar_policy(PolicyType::Never)
        .child(page)
        .build()
}

fn section(title: &str) -> GtkBox {
    let b = ui::vbox(8);
    b.append(&ui::label(title, &["section-label"]));
    b
}

/// Red "opis ————— kontrola".
fn row(caption: &str, control: &impl IsA<Widget>) -> GtkBox {
    let r = ui::hbox(12);
    r.append(&ui::label(caption, &[]));
    r.append(&ui::spacer());
    let w = control.as_ref();
    w.set_valign(Align::Center);
    r.append(w);
    r
}

/// Red gdje je desna strana tekst koji se lomi (npr. lokacija podataka).
fn row_label(caption: &str, value: &Label) -> GtkBox {
    let r = ui::vbox(2);
    r.append(&ui::label(caption, &[]));
    r.append(value);
    r
}

fn dropdown_minutes(values: &[i64]) -> DropDown {
    let labels: Vec<String> = values.iter().map(|m| format!("{m} min")).collect();
    DropDown::from_strings(&labels.iter().map(String::as_str).collect::<Vec<_>>())
}

fn index_of<T: PartialEq>(values: &[T], value: T) -> u32 {
    values.iter().position(|v| *v == value).unwrap_or(0) as u32
}
