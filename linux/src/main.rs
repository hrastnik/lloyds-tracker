mod auto_stop;
mod autostart;
mod engine;
mod entry_edit;
mod icon;
mod idle;
mod models;
mod prompt;
mod session;
mod settings_window;
mod sound;
mod startup_reminder;
mod store;
mod summary;
mod theme;
mod tray;
mod ui;
mod update;
mod update_popup;

use std::cell::RefCell;
use std::rc::Rc;
use std::time::Duration;

use gtk4::gdk::Display;
use gtk4::gio::prelude::*;
use gtk4::glib;
use gtk4::prelude::*;
use gtk4::{Application, CssProvider};
use ksni::blocking::{Handle, TrayMethods};

use crate::auto_stop::AutoStopWarning;
use crate::engine::{Effect, TrackerEngine};
use crate::prompt::PromptWindow;
use crate::settings_window::SettingsWindow;
use crate::startup_reminder::StartupReminder;
use crate::summary::SummaryWindow;
use crate::tray::{LloydsTray, TrayCommand, TraySnapshot};
use crate::update::UpdateChecker;
use crate::update_popup::UpdatePopup;

const APP_ID: &str = "com.lloydsdigital.LloydsTracker";

/// Aplikacija drži engine i sve otvorene prozore. Engine se nikad ne poziva izravno iz
/// callbacka nego kroz [`App::mutate`], koji nakon izmjene otpusti posudbu i tek onda
/// primijeni efekte — inače bi prozor koji zove engine, a engine njega, srušio program.
pub struct App {
    pub gtk: Application,
    pub engine: RefCell<TrackerEngine>,

    tray: RefCell<Option<Handle<LloydsTray>>>,
    last_snapshot: RefCell<Option<TraySnapshot>>,

    prompt: RefCell<Option<Rc<PromptWindow>>>,
    reminder: RefCell<Option<Rc<StartupReminder>>>,
    auto_stop: RefCell<Option<Rc<AutoStopWarning>>>,
    update_popup: RefCell<Option<Rc<UpdatePopup>>>,
    summary: RefCell<Option<Rc<SummaryWindow>>>,
    settings: RefCell<Option<Rc<SettingsWindow>>>,
}

impl App {
    fn new(gtk: &Application) -> Rc<Self> {
        Rc::new(App {
            gtk: gtk.clone(),
            engine: RefCell::new(TrackerEngine::new()),
            tray: RefCell::new(None),
            last_snapshot: RefCell::new(None),
            prompt: RefCell::new(None),
            reminder: RefCell::new(None),
            auto_stop: RefCell::new(None),
            update_popup: RefCell::new(None),
            summary: RefCell::new(None),
            settings: RefCell::new(None),
        })
    }

    // MARK: - Engine

    /// Jedini put do enginea. Posudba traje samo koliko i `f`, pa efekti (otvaranje
    /// prozora) smiju natrag u engine.
    pub fn mutate(self: Rc<Self>, f: impl FnOnce(&mut TrackerEngine)) {
        f(&mut self.engine.borrow_mut());
        self.pump();
    }

    /// Efekt može proizvesti novi efekt (npr. zatvaranje dana otvori pregled), pa se
    /// red prazni dok se ne smiri.
    fn pump(self: &Rc<Self>) {
        loop {
            let effects = self.engine.borrow_mut().take_effects();
            if effects.is_empty() {
                break;
            }
            for effect in effects {
                self.apply(effect);
            }
        }
        self.refresh_tray();
        self.refresh_windows();
    }

    fn apply(self: &Rc<Self>, effect: Effect) {
        match effect {
            Effect::ShowPrompt { request, style, history } => {
                if let Some(old) = self.prompt.borrow_mut().take() {
                    old.close();
                }
                let submit_request = request.clone();
                let me = self.clone();
                let on_submit = move |result| {
                    let request = submit_request.clone();
                    me.clone()
                        .mutate(move |engine| engine.on_prompt_submitted(&request, result));
                };
                let me = self.clone();
                let on_snooze = move || me.clone().mutate(|engine| engine.on_prompt_snoozed());

                let window = PromptWindow::new(
                    &self.gtk,
                    request,
                    style,
                    history,
                    on_submit,
                    on_snooze,
                );
                *self.prompt.borrow_mut() = Some(window);
            }
            Effect::ClosePrompt => {
                if let Some(window) = self.prompt.borrow_mut().take() {
                    window.close();
                }
            }
            Effect::ExtendPrompt(end) => {
                let window = self.prompt.borrow().clone();
                if let Some(window) = window {
                    window.extend(end);
                }
            }
            Effect::FocusPrompt => {
                let window = self.prompt.borrow().clone();
                if let Some(window) = window {
                    window.focus();
                }
            }
            Effect::ShowStartupReminder { day_title, backfill_from } => {
                if let Some(old) = self.reminder.borrow_mut().take() {
                    old.close();
                }
                let me = self.clone();
                let on_start = move |use_backfill: bool| {
                    // Točno vrijeme nadoknade računa engine u trenutku klika — prozor je
                    // mogao prenoćiti, pa bi zapamćeni datum pomaknuo početak na jučer.
                    me.clone().mutate(move |engine| engine.on_reminder_start(use_backfill));
                };
                let me = self.clone();
                let on_dismiss = move || me.clone().mutate(|engine| engine.on_reminder_dismissed());

                let window = StartupReminder::new(
                    &self.gtk,
                    &day_title,
                    backfill_from,
                    on_start,
                    on_dismiss,
                );
                *self.reminder.borrow_mut() = Some(window);
            }
            Effect::CloseStartupReminder => {
                if let Some(window) = self.reminder.borrow_mut().take() {
                    window.close();
                }
            }
            Effect::ShowAutoStopWarning { stop_at, lead } => {
                if let Some(old) = self.auto_stop.borrow_mut().take() {
                    old.close();
                }
                let me = self.clone();
                let on_extend = move |minutes: i64| {
                    me.clone().mutate(move |engine| engine.extend_auto_stop(minutes));
                };
                let me = self.clone();
                let on_stop_now = move || me.clone().mutate(|engine| engine.stop());
                let me = self.clone();
                let on_dismiss =
                    move || me.clone().mutate(|engine| engine.on_auto_stop_warning_dismissed());

                let window = AutoStopWarning::new(
                    &self.gtk,
                    stop_at,
                    lead,
                    on_extend,
                    on_stop_now,
                    on_dismiss,
                );
                *self.auto_stop.borrow_mut() = Some(window);
            }
            Effect::CloseAutoStopWarning => {
                if let Some(window) = self.auto_stop.borrow_mut().take() {
                    window.close();
                }
            }
            Effect::CheckForUpdate => self.check_for_update(),
            Effect::ShowUpdatePopup { update, current_version } => {
                if let Some(old) = self.update_popup.borrow_mut().take() {
                    old.close();
                }
                let me = self.clone();
                let on_download = move || me.clone().mutate(|engine| engine.open_update_page());
                let window = UpdatePopup::new(&self.gtk, &update, &current_version, on_download);
                *self.update_popup.borrow_mut() = Some(window);
            }
            Effect::CloseUpdatePopup => {
                if let Some(window) = self.update_popup.borrow_mut().take() {
                    window.close();
                }
            }
            Effect::OpenUpdatePage(url) => UpdateChecker::open_page(&url),
            Effect::OpenSummary => self.open_summary(),
            Effect::PlaySound => sound::play_prompt_sound(),
        }
    }

    // MARK: - Nova verzija

    /// HTTP zahtjev blokira (do 15 s), pa ide na zaseban thread; rezultat se kanalom vraća
    /// na GTK thread (kao naredbe iz trake) i ulazi u engine kroz `mutate`.
    fn check_for_update(self: &Rc<Self>) {
        let (tx, rx) = async_channel::bounded(1);
        std::thread::spawn(move || {
            let _ = tx.send_blocking(UpdateChecker::fetch_latest());
        });
        let app = self.clone();
        glib::spawn_future_local(async move {
            let result = rx
                .recv()
                .await
                .unwrap_or_else(|_| Err("thread za provjeru je pao".into()));
            if let Err(error) = &result {
                eprintln!("Provjera nove verzije nije uspjela: {error}");
            }
            app.mutate(move |engine| engine.on_update_checked(result));
        });
    }

    // MARK: - Prozori

    fn open_summary(self: &Rc<Self>) {
        let existing = self.summary.borrow().clone();
        match existing {
            Some(window) => window.present(),
            None => *self.summary.borrow_mut() = Some(SummaryWindow::new(self)),
        }
    }

    fn open_settings(self: &Rc<Self>) {
        let existing = self.settings.borrow().clone();
        match existing {
            Some(window) => window.present(),
            None => *self.settings.borrow_mut() = Some(SettingsWindow::new(self)),
        }
    }

    /// Pregled dana mora pratiti promjene (novi unos, brisanje, kraj dana); zatvorene
    /// prozore ovdje i otpuštamo, da se idući put otvori svjež.
    fn refresh_windows(self: &Rc<Self>) {
        let summary = self.summary.borrow().clone();
        if let Some(window) = summary {
            if window.window().is_visible() {
                window.refresh();
            } else {
                *self.summary.borrow_mut() = None;
            }
        }
        let settings = self.settings.borrow().clone();
        if let Some(window) = settings {
            if window.window().is_visible() {
                // Ishod provjere nove verzije stiže asinkrono, pa ga postavke prate.
                window.refresh_update_status();
            } else {
                *self.settings.borrow_mut() = None;
            }
        }
    }

    // MARK: - Traka

    /// Meni se prekraja samo kad se stvarno promijenio — svaki `update` je DBus promet
    /// prema hostu koji meni crta.
    fn refresh_tray(self: &Rc<Self>) {
        let snapshot = TraySnapshot::from_engine(&self.engine.borrow());
        if self.last_snapshot.borrow().as_ref() == Some(&snapshot) {
            return;
        }
        *self.last_snapshot.borrow_mut() = Some(snapshot.clone());
        if let Some(handle) = self.tray.borrow().as_ref() {
            handle.update(move |tray| tray.set_snapshot(snapshot));
        }
    }

    fn handle_command(self: &Rc<Self>, command: TrayCommand) {
        match command {
            TrayCommand::Start => self.clone().mutate(|engine| engine.start(None)),
            TrayCommand::Pause(minutes) => self.clone().mutate(|engine| engine.pause(minutes)),
            TrayCommand::Resume => self.clone().mutate(|engine| engine.resume()),
            TrayCommand::PromptNow => self.clone().mutate(|engine| engine.manual_prompt()),
            TrayCommand::Stop => self.clone().mutate(|engine| engine.stop()),
            TrayCommand::OpenSummary => self.open_summary(),
            TrayCommand::OpenSettings => self.open_settings(),
            TrayCommand::OpenUpdatePage => self.clone().mutate(|engine| engine.open_update_page()),
            TrayCommand::Quit => self.gtk.quit(),
        }
    }
}

fn main() -> glib::ExitCode {
    let gtk = Application::builder()
        .application_id(APP_ID)
        // Aplikacija živi u traci i normalno nema nijedan otvoren prozor.
        .flags(gtk4::gio::ApplicationFlags::empty())
        .build();

    gtk.connect_startup(|_| install_css());

    let app_cell: Rc<RefCell<Option<Rc<App>>>> = Rc::new(RefCell::new(None));
    gtk.connect_activate(move |gtk| {
        // Drugo pokretanje samo probudi postojeću instancu (GTK ga sam preusmjeri ovamo).
        if let Some(app) = app_cell.borrow().as_ref() {
            app.open_summary();
            return;
        }
        let app = App::new(gtk);
        *app_cell.borrow_mut() = Some(app.clone());
        start(app);
    });

    gtk.run()
}

fn start(app: Rc<App>) {
    // Bez otvorenog prozora GTK bi aplikaciju odmah ugasio; traka je ovdje jedini UI.
    let hold = app.gtk.hold();

    // MARK: Traka
    let (tx, rx) = async_channel::unbounded::<TrayCommand>();
    let snapshot = TraySnapshot::from_engine(&app.engine.borrow());
    *app.last_snapshot.borrow_mut() = Some(snapshot.clone());
    match LloydsTray::new(snapshot, tx).spawn() {
        Ok(handle) => *app.tray.borrow_mut() = Some(handle),
        // Bez StatusNotifierItem hosta (npr. GNOME bez AppIndicator ekstenzije) nema
        // ikone u traci, ali prompt i podsjetnici i dalje rade — zato se ne gasimo.
        Err(error) => eprintln!("Ikona u traci nije dostupna: {error}"),
    }

    // Naredbe iz ksni threada stižu kanalom i izvršavaju se na GTK threadu.
    {
        let app = app.clone();
        glib::spawn_future_local(async move {
            let _hold = hold;
            while let Ok(command) = rx.recv().await {
                app.handle_command(command);
            }
        });
    }

    // MARK: Otkucaj
    {
        let app = app.clone();
        glib::timeout_add_local(Duration::from_secs(1), move || {
            app.clone().mutate(|engine| engine.tick());
            glib::ControlFlow::Continue
        });
    }

    app.clone().mutate(|engine| engine.show_startup_reminder_if_needed());
}

fn install_css() {
    let provider = CssProvider::new();
    provider.load_from_data(&theme::css());
    if let Some(display) = Display::default() {
        gtk4::style_context_add_provider_for_display(
            &display,
            &provider,
            gtk4::STYLE_PROVIDER_PRIORITY_APPLICATION,
        );
    }
}
