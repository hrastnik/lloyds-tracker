use chrono::{DateTime, Datelike, Duration, Local, TimeZone, Timelike};
use uuid::Uuid;

use crate::autostart::LaunchAtLogin;
use crate::idle::IdleMonitor;
use crate::models::{
    AppSettings, ChronoRow, Entry, EntryKind, PendingPause, PromptRequest, PromptResult, PromptSpan,
    PromptStyle, Summarize,
};
use crate::session::SessionMonitor;
use crate::store::Store;
use crate::theme::Fmt;
use crate::update::{AvailableUpdate, UpdateChecker};

/// Koliko prije automatskog zaustavljanja iskoči upozorenje.
const AUTO_STOP_LEAD: f64 = 60.0;

/// Prva provjera nove verzije malo nakon pokretanja (da se app slegne), dalje jednom
/// dnevno; nakon neuspjele (nema mreže) opet za sat.
const UPDATE_FIRST_CHECK_DELAY: i64 = 15;
const UPDATE_CHECK_INTERVAL: i64 = 24 * 3600;
const UPDATE_RETRY_INTERVAL: i64 = 3600;

/// Stanje ikone u traci — pandan macOS SF Symbolima (`clock` / `clock.fill` /
/// `pause.circle.fill` / `moon.zzz.fill`) i Windows `TrayState`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum TrayState {
    Idle,
    Tracking,
    Paused,
    AwaitingReturn,
}

/// Pauza do zadanog vremena ili do ručnog nastavka. Swift ovdje koristi
/// `Date.distantFuture` kao sentinel; u Rustu je jasnije kao varijanta.
#[derive(Debug, Clone, Copy, PartialEq)]
pub enum PauseUntil {
    Until(DateTime<Local>),
    Indefinite,
}

/// Ono što engine traži od UI sloja. Engine nikad ne zove prozore izravno — vraća efekte
/// koje aplikacija primijeni **nakon** što otpusti `RefCell` posudbu. Bez toga bi svaki
/// callback iz prozora natrag u engine srušio program na dvostrukoj posudbi.
#[derive(Debug, Clone)]
pub enum Effect {
    ShowPrompt {
        request: PromptRequest,
        style: PromptStyle,
        history: Vec<String>,
    },
    ClosePrompt,
    /// Produži period vidljivog prompta do nove granice (skupno vrijeme).
    ExtendPrompt(DateTime<Local>),
    /// Vrati već otvoreni prompt u prvi plan — "Zapiši sada" dok prompt visi.
    FocusPrompt,
    ShowStartupReminder {
        day_title: String,
        backfill_from: Option<DateTime<Local>>,
    },
    CloseStartupReminder,
    ShowAutoStopWarning {
        stop_at: DateTime<Local>,
        lead: f64,
    },
    CloseAutoStopWarning,
    /// Pitaj GitHub za zadnji release (na pomoćnom threadu) i rezultat vrati u
    /// [`TrackerEngine::on_update_checked`].
    CheckForUpdate,
    ShowUpdatePopup {
        update: AvailableUpdate,
        current_version: String,
    },
    CloseUpdatePopup,
    /// Release stranica nove verzije u pregledniku.
    OpenUpdatePage(String),
    OpenSummary,
    PlaySound,
}

pub struct TrackerEngine {
    // MARK: Javno stanje (UI ga čita nakon svakog poziva)
    pub is_tracking: bool,
    pub entries: Vec<Entry>,
    pub history: Vec<String>,
    pub pause_until: Option<PauseUntil>,
    pub next_prompt_at: Option<DateTime<Local>>,
    pub awaiting_return_since: Option<DateTime<Local>>,
    pub launch_at_login_status: Option<String>,
    /// Vrijeme automatskog zaustavljanja za trenutnu sesiju (None = isključeno).
    /// Produženja iz upozorenja mijenjaju samo ovo, ne i postavku.
    pub auto_stop_at: Option<DateTime<Local>>,
    pub settings: AppSettings,
    pub current_day_key: String,
    /// Novija verzija na GitHubu (None = nema je ili provjera nije rađena).
    pub available_update: Option<AvailableUpdate>,
    /// Ishod zadnje provjere nove verzije, za prikaz u postavkama.
    pub update_status: Option<String>,

    // MARK: Interno
    last_covered: DateTime<Local>,
    /// Kraj perioda vidljivog "običnog" prompta; produžuje se dok čeka odgovor.
    /// None znači da nema prompta koji se smije produžiti (npr. pauza/kraj dana).
    active_prompt_end: Option<DateTime<Local>>,
    /// Preskočeni periodi koji nisu susjedni sljedećem promptu (npr. između je pauza) —
    /// nose se dalje kao zasebni redovi dok se ne popune ili dok dan ne završi.
    carried_spans: Vec<PromptSpan>,
    /// Dan je zatvoren, a zadnji prompt još čeka odgovor — više se ništa ne zakazuje, a
    /// dan se ne smije zatvoriti dvaput (tick bi inače u krug otvarao isti prompt).
    awaiting_final_answer: bool,
    paused_since: Option<DateTime<Local>>,
    is_locked: bool,
    locked_at: Option<DateTime<Local>>,
    auto_stop_warning_shown: bool,
    /// Dan (dayKey) za koji je podsjetnik na početak radnog dana odrađen — prikazan ili
    /// je dan u međuvremenu pokrenut. Drži se u memoriji: kod restarta aplikacije ulogu
    /// ionako preuzima podsjetnik kod pokretanja.
    workday_reminder_day_key: Option<String>,
    next_update_check_at: DateTime<Local>,
    update_check_in_flight: bool,
    /// Nova verzija čiji pop-up čeka da se makne prompt/podsjetnik.
    pending_update_popup: Option<AvailableUpdate>,

    // MARK: Zrcalo stanja prozora (engine mora znati je li što otvoreno)
    prompt_visible: bool,
    reminder_visible: bool,
    /// Kad je podsjetnik prikazan i s kojom ponudom nadoknade — po tome se vidi je li
    /// otvoreni prozor u međuvremenu zastario (prenoćio, ili je radni dan tek počeo).
    reminder_shown_at: Option<DateTime<Local>>,
    reminder_backfill_from: Option<DateTime<Local>>,
    auto_stop_warning_visible: bool,

    effects: Vec<Effect>,
}

impl TrackerEngine {
    pub fn new() -> Self {
        let now = Local::now();
        let settings = Store::load_settings();
        let current_day_key = Store::day_key(now);
        let entries = Store::load_day(&current_day_key);
        let history = Store::load_history();

        // Postavke i stvarno stanje autostarta mogu se razići (prenesen `settings.json`,
        // obrisan `.desktop`, prvo pokretanje na novom računalu) — zato se kod svakog
        // pokretanja uskladi prema postavci.
        let launch_at_login_status = (settings.launch_at_login != LaunchAtLogin::is_enabled())
            .then(|| LaunchAtLogin::apply(settings.launch_at_login))
            .flatten();

        TrackerEngine {
            is_tracking: false,
            entries,
            history,
            pause_until: None,
            next_prompt_at: None,
            awaiting_return_since: None,
            launch_at_login_status,
            auto_stop_at: None,
            settings,
            current_day_key,
            available_update: None,
            update_status: None,
            last_covered: now,
            active_prompt_end: None,
            carried_spans: Vec::new(),
            awaiting_final_answer: false,
            paused_since: None,
            is_locked: false,
            locked_at: None,
            auto_stop_warning_shown: false,
            workday_reminder_day_key: None,
            next_update_check_at: now + Duration::seconds(UPDATE_FIRST_CHECK_DELAY),
            update_check_in_flight: false,
            pending_update_popup: None,
            prompt_visible: false,
            reminder_visible: false,
            reminder_shown_at: None,
            reminder_backfill_from: None,
            auto_stop_warning_visible: false,
            effects: Vec::new(),
        }
    }

    /// Aplikacija prazni red efekata nakon svakog poziva u engine.
    pub fn take_effects(&mut self) -> Vec<Effect> {
        std::mem::take(&mut self.effects)
    }

    pub fn interval_seconds(&self) -> i64 {
        self.settings.interval_minutes * 60
    }

    // MARK: - Podsjetnik na pokretanju

    /// Pop-up podsjetnik na pokretanju — samo ako je uključen u postavkama i dan još
    /// nije pokrenut (da se ne zaboravi startati tracking).
    pub fn show_startup_reminder_if_needed(&mut self) {
        if !self.settings.show_startup_reminder || self.is_tracking || self.prompt_visible {
            return;
        }
        if self.is_skipped_weekend(Local::now()) {
            return;
        }
        self.present_start_reminder(Local::now());
    }

    // MARK: - Kontrole

    /// `backfill_from` (iz podsjetnika na početak radnog dana) pomiče početak trackanja
    /// unatrag — prvi prompt onda pita za cijelo jutro, npr. 8:30–9:45.
    pub fn start(&mut self, backfill_from: Option<DateTime<Local>>) {
        let now = Local::now();
        self.current_day_key = Store::day_key(now);
        self.entries = Store::load_day(&self.current_day_key);
        self.workday_reminder_day_key = Some(self.current_day_key.clone());
        self.close_reminder();

        // Nadoknada vrijedi samo unutar današnjeg dana — sigurnosna ograda da početak
        // nikad ne padne u jučer (prvi prompt bi onda pitao za period od 25 h).
        let backfill = backfill_from.filter(|d| Store::day_key(*d) == self.current_day_key);

        // Trackaj od početka trenutnog intervala (npr. start u 9:56 uz 15 min → od 9:45).
        // Ako nakon toga već postoji neki unos, kreni od kraja zadnjeg unosa — kod
        // nadoknade da se popuni ostatak jutra, inače od početka trenutnog 5-min bloka
        // (da ne nastane dupli zapis).
        let mut cover_from = backfill.unwrap_or_else(|| grid_floor(now, self.interval_seconds()));
        if let Some(latest_end) = self.entries.iter().map(|e| e.end).max() {
            if latest_end > cover_from {
                cover_from = match backfill {
                    None => grid_floor(now, 300).max(latest_end),
                    Some(_) => latest_end,
                };
            }
        }
        self.last_covered = cover_from.min(now);
        self.pause_until = None;
        self.paused_since = None;
        self.awaiting_return_since = None;
        self.active_prompt_end = None;
        self.carried_spans.clear();
        self.awaiting_final_answer = false;
        self.next_prompt_at = Some(self.aligned_next_prompt(now));
        self.auto_stop_warning_shown = false;
        self.auto_stop_at = self.next_auto_stop(now);
        self.is_tracking = true;
    }

    pub fn stop(&mut self) {
        let now = Local::now();
        // Zaustavljanje u zadnjoj minuti prije auto-stopa (klik u upozorenju ili u meniju)
        // bilježi period do zakazanog vremena — inače dan završi minutu prije postavke
        // (npr. 16:59:46 umjesto 17:00).
        match self.auto_stop_at {
            Some(scheduled)
                if scheduled > now && (scheduled - now).num_milliseconds() as f64 / 1000.0 <= AUTO_STOP_LEAD =>
            {
                self.stop_at(scheduled)
            }
            _ => self.stop_at(now),
        }
    }

    /// `end_time` je kraj zadnjeg perioda — kod automatskog zaustavljanja to je zakazano
    /// vrijeme, a ne trenutak kad se odgovori na zadnji prompt (koji može biti i sutra).
    fn stop_at(&mut self, end_time: DateTime<Local>) {
        if !self.is_tracking || self.awaiting_final_answer {
            return;
        }
        let now = Local::now();
        self.cancel_auto_stop();
        if self.pause_until.is_some() {
            self.end_manual_pause(end_time);
        }
        self.close_prompt();
        self.active_prompt_end = None;

        let period_start = self.last_covered;
        let mut period_end = end_time;
        // Ako je korisnik odsutan (idle/zaključan ekran), odsutnost bilježimo kao pauzu,
        // a pitamo samo za rad do trenutka odsutnosti — inače bi cijela odsutnost
        // završila kao "rad".
        if let Some(gap_start) = self.awaiting_return_since.take() {
            let pause_start = gap_start.max(period_start);
            if end_time > pause_start {
                self.entries.push(Entry::new(pause_start, end_time, "Pauza (odsutnost)", EntryKind::Pause));
            }
            period_end = pause_start;
            self.last_covered = self.last_covered.max(end_time);
        }

        let has_period = (period_end - period_start).num_seconds() > 60;
        if has_period || !self.carried_spans.is_empty() {
            // Prompt koji je prenoćio (zatvoren laptop) odgovara se sutra, a period mu je
            // odrezan na kraj *njegovog* dana — to mora pisati, inače unos izgleda pogrešno.
            let overnight = Store::day_key(end_time) != Store::day_key(now);
            self.awaiting_final_answer = true;
            // Prompt koji visi samo zbog preskočenih redova nema svoj period.
            let mut request =
                PromptRequest::new(period_start, Some(if has_period { period_end } else { period_start }));
            request.is_final = true;
            request.note = Some(if overnight {
                format!(
                    "Prompt je prenoćio — period je odrezan na kraj radnog dana ({}).",
                    Fmt::hhmm(end_time)
                )
            } else {
                "Kraj dana — što si radio u zadnjem periodu?".into()
            });
            request.allow_snooze = false;
            self.show_prompt(request);
        } else {
            self.finalize_stop();
        }
    }

    fn finalize_stop(&mut self) {
        self.is_tracking = false;
        self.awaiting_final_answer = false;
        self.carried_spans.clear();
        self.cancel_auto_stop();
        self.next_prompt_at = None;
        self.awaiting_return_since = None;
        self.persist_day();
        self.effects.push(Effect::OpenSummary);
    }

    pub fn pause(&mut self, minutes: Option<i64>) {
        if !self.is_tracking || self.pause_until.is_some() {
            return;
        }
        let now = Local::now();
        self.paused_since = Some(now);
        self.pause_until = Some(match minutes {
            Some(m) => PauseUntil::Until(now + Duration::minutes(m)),
            // Do kraja dana / dok se ručno ne nastavi.
            None => PauseUntil::Indefinite,
        });
        self.close_prompt();
        self.active_prompt_end = None;
        let has_period = (now - self.last_covered).num_seconds() > 60;
        if has_period || !self.carried_spans.is_empty() {
            let mut request = PromptRequest::new(
                self.last_covered,
                Some(if has_period { now } else { self.last_covered }),
            );
            request.note = Some("Prije pauze — na čemu si radio?".into());
            request.allow_snooze = false;
            self.show_prompt(request);
        }
    }

    pub fn resume(&mut self) {
        if self.pause_until.is_none() {
            return;
        }
        self.end_manual_pause(Local::now());
    }

    /// Ručno pokrenut prompt ("Zapiši sada") — pita za period od zadnjeg zapisa do sada i
    /// nudi polje "nastavljam s", pa sljedeći prompt kreće s tim opisom. Ritam promptanja se
    /// ne dira: `next_prompt_at` se ne pomiče, a ako prompt preživi granicu intervala, period
    /// mu se samo produži (kao i običnom promptu).
    pub fn manual_prompt(&mut self) {
        if !self.can_prompt_now() {
            return;
        }
        if self.prompt_visible {
            self.effects.push(Effect::FocusPrompt);
            return;
        }
        let now = Local::now();
        self.active_prompt_end = Some(now);
        let mut request = PromptRequest::new(self.last_covered, Some(now));
        request.is_manual = true;
        request.note =
            Some("Ručni zapis — spremi period do sada, pa (ako želiš) upiši čime nastavljaš.".into());
        request.allow_snooze = false;
        self.show_prompt(request);
    }

    /// Ima li "Zapiši sada" sad smisla (inače ga meni u traci ne prikazuje).
    pub fn can_prompt_now(&self) -> bool {
        self.is_tracking
            && !self.awaiting_final_answer
            && self.pause_until.is_none()
            && self.awaiting_return_since.is_none()
    }

    pub fn snooze(&mut self, minutes: i64) {
        self.close_prompt();
        self.active_prompt_end = None;
        let now = Local::now();
        // Zaokruži na 5-min mrežu da periodi (i trajanja) ostanu poravnati.
        let target = snap_to_grid(now + Duration::minutes(minutes));
        self.next_prompt_at = Some(target.max(now + Duration::seconds(60)));
    }

    /// Briše jedan ili više unosa — spojeni red u kronološkom pregledu pokriva više unosa.
    pub fn delete_entries(&mut self, ids: &[Uuid], day_key: &str) {
        if day_key == self.current_day_key {
            self.entries.retain(|e| !ids.contains(&e.id));
            self.persist_day();
        } else {
            let mut day = Store::load_day(day_key);
            day.retain(|e| !ids.contains(&e.id));
            Store::save_day(day_key, &day);
        }
    }

    /// Ispravlja unos (ili unose) iza jednog reda kronološkog pregleda. Ako vremena ostanu
    /// ista, mijenja se samo opis i vrsta svakog bloka u redu (spojeni red ostaje spojen). Ako
    /// su vremena promijenjena, red postaje **jedan** unos — novi raspon se nema smisla
    /// razbijati po starim granicama blokova.
    pub fn update_entries(
        &mut self,
        row: &ChronoRow,
        day_key: &str,
        text: &str,
        start: DateTime<Local>,
        end: DateTime<Local>,
        kind: EntryKind,
    ) {
        let clean = text.trim().to_string();
        if clean.is_empty() || end <= start {
            return;
        }
        let times_changed = (start - row.start).num_seconds().abs() > 1
            || (end - row.end).num_seconds().abs() > 1;
        let ids = row.ids.clone();
        let keep_id = ids[0];

        let apply = |day: &mut Vec<Entry>| {
            if times_changed {
                day.retain(|e| !ids.contains(&e.id));
                let mut entry = Entry::new(start, end, clean.clone(), kind);
                entry.id = keep_id;
                day.push(entry);
            } else {
                for e in day.iter_mut().filter(|e| ids.contains(&e.id)) {
                    e.text = clean.clone();
                    e.kind = kind;
                }
            }
            day.sort_by_key(|e| e.start);
        };

        if day_key == self.current_day_key {
            let mut day = std::mem::take(&mut self.entries);
            apply(&mut day);
            self.entries = day;
            Store::save_day(&self.current_day_key, &self.entries);
        } else {
            let mut day = Store::load_day(day_key);
            apply(&mut day);
            Store::save_day(day_key, &day);
        }
    }

    /// Sljedeći prompt poravnat s početkom sata (npr. 15 min → :00, :15, :30, :45).
    /// Interval koji ne dijeli sat (20, 45) resetira se na svakom punom satu.
    fn aligned_next_prompt(&self, date: DateTime<Local>) -> DateTime<Local> {
        let interval = self.interval_seconds();
        let hour_start = hour_start(date);
        let elapsed = (date - hour_start).num_milliseconds() as f64 / 1000.0;
        let steps = (elapsed / interval as f64).floor() as i64 + 1;
        let next = hour_start + Duration::seconds(steps * interval);
        let next_hour = hour_start + Duration::hours(1);
        next.min(next_hour)
    }

    // MARK: - Automatsko zaustavljanje

    /// Sljedeće zaustavljanje u zadano vrijeme dana; ako je to vrijeme danas već prošlo,
    /// zakazuje se za sutra (npr. start u 20:00 uz auto-stop 16:00).
    fn next_auto_stop(&self, date: DateTime<Local>) -> Option<DateTime<Local>> {
        if !self.settings.auto_stop_enabled {
            return None;
        }
        let target = at_time(date, self.settings.auto_stop_hour, self.settings.auto_stop_minute)?;
        Some(if target > date { target } else { target + Duration::days(1) })
    }

    fn cancel_auto_stop(&mut self) {
        self.auto_stop_at = None;
        self.auto_stop_warning_shown = false;
        self.close_auto_stop_warning();
    }

    fn close_auto_stop_warning(&mut self) {
        self.auto_stop_warning_visible = false;
        self.effects.push(Effect::CloseAutoStopWarning);
    }

    /// Produži današnje zaustavljanje — računa se od zakazanog vremena (16:00 + 30 → 16:30).
    /// Postavka se ne mijenja, pa sutra opet vrijedi zadano vrijeme.
    pub fn extend_auto_stop(&mut self, minutes: i64) {
        let Some(current) = self.auto_stop_at else { return };
        self.close_auto_stop_warning();
        self.auto_stop_warning_shown = false;
        self.auto_stop_at = Some(current.max(Local::now()) + Duration::minutes(minutes));
    }

    /// Kraj radnog dana za sesiju koja je prenoćila — None dok je sesija još u svom danu.
    ///
    /// Laptop se zatvori s otvorenim promptom, a odgovor dođe sutra: bez ove ograde bi period
    /// tekao cijelu noć i završio kao višesatni unos s jučerašnjim datumom. Rez je vrijeme iz
    /// sekcije "Automatsko zaustavljanje" na dan sesije, a rad koji je već zabilježen i preko
    /// njega (produženja iz upozorenja, ili isključen auto-stop) se poštuje.
    fn overnight_cutoff(&self, now: DateTime<Local>) -> Option<DateTime<Local>> {
        if Store::day_key(now) == self.current_day_key {
            return None;
        }
        // Sesija koja legitimno prelazi u novi dan (start u 20:00 uz auto-stop 16:00 → stop je
        // zakazan za sutra) se ne prekida.
        if self.settings.auto_stop_enabled && self.auto_stop_at.is_some_and(|at| at > now) {
            return None;
        }
        let day = chrono::NaiveDate::parse_from_str(&self.current_day_key, "%Y-%m-%d").ok()?;
        let hour = self.settings.auto_stop_hour.min(23);
        let minute = self.settings.auto_stop_minute.min(59);
        let configured = day
            .and_hms_opt(hour, minute, 0)
            .and_then(|naive| Local.from_local_datetime(&naive).single());
        // Rad zabilježen i preko zadanog vremena (isključen auto-stop, rad poslije ponoći) se
        // ne vuče unatrag — takav dan se zatvara na svojoj granici, u ponoć.
        match configured {
            Some(at) if at > self.last_covered => Some(at),
            _ => {
                let day_end = day
                    .succ_opt()?
                    .and_hms_opt(0, 0, 0)
                    .and_then(|naive| Local.from_local_datetime(&naive).single())?;
                Some(day_end.max(self.last_covered))
            }
        }
    }

    fn show_auto_stop_warning(&mut self, stop_at: DateTime<Local>) {
        self.auto_stop_warning_shown = true;
        self.auto_stop_warning_visible = true;
        self.effects.push(Effect::CloseUpdatePopup);
        if self.settings.sound_enabled {
            self.effects.push(Effect::PlaySound);
        }
        self.effects.push(Effect::ShowAutoStopWarning { stop_at, lead: AUTO_STOP_LEAD });
    }

    // MARK: - Početak radnog dana

    /// Početak radnog dana na dan `date` (None kad je podsjetnik isključen).
    fn workday_start(&self, date: DateTime<Local>) -> Option<DateTime<Local>> {
        if !self.settings.workday_start_enabled {
            return None;
        }
        at_time(
            date,
            self.settings.workday_start_hour.min(23),
            self.settings.workday_start_minute.min(59),
        )
    }

    /// Subota ili nedjelja uz uključeno "Preskoči vikende" — tad ne iskače nijedan
    /// podsjetnik. Fiksno sub/ned na sva tri porta (macOS namjerno ne koristi
    /// `isDateInWeekend`, koji ovisi o regiji).
    fn is_skipped_weekend(&self, date: DateTime<Local>) -> bool {
        self.settings.skip_weekend_reminders
            && matches!(date.weekday(), chrono::Weekday::Sat | chrono::Weekday::Sun)
    }

    /// Vrijeme od kojeg podsjetnik nudi nadoknadu ("Start od 8:30") — None kad je opcija
    /// isključena, kad radni dan još nije počeo ili kad je razmak premali da bi se
    /// nadoknada uopće razlikovala od starta od sada.
    fn backfill_start(&self, now: DateTime<Local>) -> Option<DateTime<Local>> {
        if !self.settings.workday_start_backfill_enabled {
            return None;
        }
        let start = self.workday_start(now)?;
        ((now - start).num_seconds() >= 300).then_some(start)
    }

    /// Podsjetnik u zadano vrijeme: iskoči kad radni dan počne, a ako je računalo tada
    /// spavalo — čim se probudi i otključa (timer se nakon buđenja nastavi vrtjeti, pa ga
    /// uhvati prvi idući tick). Javlja se jednom dnevno.
    fn check_workday_start(&mut self, now: DateTime<Local>) {
        if !self.settings.workday_start_enabled || self.is_tracking || self.is_locked || self.prompt_visible {
            return;
        }
        if self.is_skipped_weekend(now) {
            return;
        }
        let Some(start) = self.workday_start(now) else { return };
        if now < start {
            return;
        }

        // Otvoreni podsjetnik čeka odgovor koliko treba, pa može biti od jučer (prenoćio)
        // ili od prije početka radnog dana, kad nadoknada još nije bila u ponudi. Nosi
        // zastarjeli naslov i ponudu, a blokira i današnji podsjetnik — zamijenimo ga
        // svježim; ako nije zastario, pustimo ga na miru.
        let mut play_sound = self.settings.sound_enabled;
        if self.reminder_visible {
            let from_previous_day = self
                .reminder_shown_at
                .map(|shown| Store::day_key(shown) != Store::day_key(now))
                .unwrap_or(true);
            let backfill_appeared =
                self.reminder_backfill_from.is_none() && self.backfill_start(now).is_some();
            if !from_previous_day && !backfill_appeared {
                return;
            }
            // Prozor od danas je već na ekranu i zvuk je uz njega odsvirao — mijenja mu se
            // samo ponuda, pa ide bez zvuka.
            if !from_previous_day {
                play_sound = false;
            }
            self.close_reminder();
        } else if self.workday_reminder_day_key.as_deref() == Some(Store::day_key(now).as_str()) {
            return;
        }

        if play_sound {
            // Za razliku od podsjetnika na pokretanju, ovaj lako iskoči dok nisi za
            // ekranom (npr. čim se laptop probudi), pa ga prati i zvuk.
            self.effects.push(Effect::PlaySound);
        }
        self.present_start_reminder(now);
    }

    /// Zajednički pop-up za oba podsjetnika (pokretanje aplikacije i početak radnog dana).
    fn present_start_reminder(&mut self, now: DateTime<Local>) {
        // Podsjetnik prikazan prije početka radnog dana ne troši današnji termin — u
        // zadano vrijeme svejedno iskoči (npr. pokretanje u 7:00, radni dan u 8:30).
        if let Some(start) = self.workday_start(now) {
            if now >= start {
                self.workday_reminder_day_key = Some(Store::day_key(now));
            }
        }
        let backfill_from = self.backfill_start(now);
        self.effects.push(Effect::CloseUpdatePopup);
        self.reminder_visible = true;
        self.reminder_shown_at = Some(Local::now());
        self.reminder_backfill_from = backfill_from;
        self.effects.push(Effect::ShowStartupReminder {
            day_title: Fmt::day_title(now),
            backfill_from,
        });
    }

    // MARK: - Povratne informacije iz UI sloja

    /// Podsjetnik: Start. Vrijeme nadoknade se računa u trenutku klika, a ne prikaza —
    /// inače bi podsjetnik koji je prenoćio startao dan od jučerašnjeg početka.
    pub fn on_reminder_start(&mut self, use_backfill: bool) {
        self.reminder_visible = false;
        self.reminder_shown_at = None;
        self.reminder_backfill_from = None;
        let backfill = if use_backfill { self.backfill_start(Local::now()) } else { None };
        self.start(backfill);
    }

    pub fn on_reminder_dismissed(&mut self) {
        self.reminder_visible = false;
        self.reminder_shown_at = None;
        self.reminder_backfill_from = None;
    }

    pub fn on_auto_stop_warning_dismissed(&mut self) {
        self.auto_stop_warning_shown = true;
        self.auto_stop_warning_visible = false;
    }

    // MARK: - Nova verzija

    fn check_for_update_if_due(&mut self, now: DateTime<Local>) {
        if !self.settings.update_check_enabled || self.update_check_in_flight || now < self.next_update_check_at {
            return;
        }
        self.check_for_update();
    }

    /// Pita GitHub za zadnji release — iz tick petlje i ručno ("Provjeri sada" u
    /// postavkama). Sam zahtjev ide na pomoćni thread (`Effect::CheckForUpdate`), a
    /// rezultat stiže u `on_update_checked`.
    pub fn check_for_update(&mut self) {
        if self.update_check_in_flight {
            return;
        }
        self.update_check_in_flight = true;
        self.update_status = Some("Provjeravam…".into());
        self.effects.push(Effect::CheckForUpdate);
    }

    pub fn on_update_checked(&mut self, result: Result<AvailableUpdate, String>) {
        self.update_check_in_flight = false;
        let now = Local::now();
        match result {
            Ok(latest) => {
                self.next_update_check_at = now + Duration::seconds(UPDATE_CHECK_INTERVAL);
                self.handle_latest(latest);
            }
            Err(_) => {
                self.next_update_check_at = now + Duration::seconds(UPDATE_RETRY_INTERVAL);
                // Provjera je isključena dok je zahtjev bio u tijeku — status ostaje prazan.
                if self.settings.update_check_enabled {
                    self.update_status =
                        Some("Provjera nije uspjela (nema mreže?). Pokušat ću opet za sat.".into());
                }
            }
        }
    }

    fn handle_latest(&mut self, latest: AvailableUpdate) {
        // Provjera je isključena dok je zahtjev bio u tijeku.
        if !self.settings.update_check_enabled {
            return;
        }
        if !UpdateChecker::is_newer(&latest.version, UpdateChecker::current_version()) {
            self.available_update = None;
            self.update_status = Some("Imaš najnoviju verziju.".into());
            return;
        }
        self.update_status = Some(format!("Dostupna je verzija {}.", latest.version));
        if self.settings.update_notified_version != latest.version {
            self.pending_update_popup = Some(latest.clone());
        }
        self.available_update = Some(latest);
        self.show_pending_update_popup();
    }

    /// Pop-up se ne gura preko prompta, podsjetnika ni upozorenja, a ni na zaključan
    /// ekran — čeka da se maknu, tick ga onda pokuša opet.
    fn show_pending_update_popup(&mut self) {
        if self.pending_update_popup.is_none()
            || self.is_locked
            || self.prompt_visible
            || self.reminder_visible
            || self.auto_stop_warning_visible
        {
            return;
        }
        let Some(update) = self.pending_update_popup.take() else { return };
        // Javlja se jednom po verziji — pamti se odmah, i kad se pop-up samo zatvori.
        self.settings.update_notified_version = update.version.clone();
        Store::save_settings(&self.settings);
        self.effects.push(Effect::ShowUpdatePopup {
            update,
            current_version: UpdateChecker::current_version().to_string(),
        });
    }

    /// Release stranica nove verzije (iz menija u traci i iz pop-upa).
    pub fn open_update_page(&mut self) {
        let url = self
            .available_update
            .as_ref()
            .map(|u| u.url.clone())
            .unwrap_or_else(|| UpdateChecker::LATEST_RELEASE_PAGE.to_string());
        self.effects.push(Effect::OpenUpdatePage(url));
    }

    // MARK: - Tick petlja

    pub fn tick(&mut self) {
        let now = Local::now();

        // Zaključan ekran se poll-a — vidi `SessionMonitor`. Prijelaz u zaključano pamti
        // trenutak, jer se od njega mjeri odsutnost.
        let locked = SessionMonitor::is_locked();
        if locked && !self.is_locked {
            self.locked_at = Some(now);
        }
        self.is_locked = locked;

        self.check_workday_start(now);
        self.check_for_update_if_due(now);
        self.show_pending_update_popup();
        // Dan je zatvoren, a zadnji prompt čeka odgovor — više se ništa ne zakazuje.
        if !self.is_tracking || self.awaiting_final_answer {
            return;
        }

        // Prije svega ostalog — auto-stop vrijedi i kad je pauzirano ili se čeka povratak.
        if self.settings.auto_stop_enabled {
            if let Some(stop_at) = self.auto_stop_at {
                if now >= stop_at {
                    self.stop_at(stop_at);
                    return;
                }
                if !self.auto_stop_warning_shown
                    && (stop_at - now).num_milliseconds() as f64 / 1000.0 <= AUTO_STOP_LEAD
                {
                    self.show_auto_stop_warning(stop_at);
                }
            }
        }

        // Ograda za sesiju koja je prenoćila (zatvoren laptop, često s otvorenim promptom):
        // period ne smije prijeći u novi dan.
        if let Some(cutoff) = self.overnight_cutoff(now) {
            self.stop_at(cutoff);
            return;
        }

        if let Some(pause) = self.pause_until {
            if let PauseUntil::Until(until) = pause {
                if now >= until {
                    self.end_manual_pause(now);
                }
            }
            return;
        }

        if let Some(gap_start) = self.awaiting_return_since {
            if !self.is_locked && IdleMonitor::idle_seconds() < 5.0 {
                self.handle_return(gap_start, now);
            }
            return;
        }

        if self.prompt_visible {
            // Neodgovoren prompt "preživio" je granicu intervala — ne otvaramo drugi
            // prompt, nego produžimo period na postojećem (skupno vrijeme).
            if let Some(next) = self.next_prompt_at {
                if now >= next && self.active_prompt_end.is_some() {
                    self.extend_active_prompt(next);
                }
            }
            return;
        }
        if let Some(next) = self.next_prompt_at {
            if now >= next {
                self.attempt_prompt(now);
            }
        }
    }

    fn attempt_prompt(&mut self, now: DateTime<Local>) {
        let idle = IdleMonitor::idle_seconds();
        if self.settings.lock_pause_enabled && self.is_locked {
            self.awaiting_return_since = Some(self.locked_at.unwrap_or(now).max(self.last_covered));
        } else if self.settings.idle_detection_enabled
            && idle >= (self.settings.idle_threshold_minutes * 60) as f64
        {
            let since = now - Duration::seconds(idle as i64);
            self.awaiting_return_since = Some(since.max(self.last_covered));
        } else {
            // Kraj perioda je zakazano (poravnato) vrijeme prompta, ne trenutak odgovora —
            // tako su unosi uvijek točno na 5-min mreži, a kašnjenje odgovora se
            // prelijeva u sljedeći period.
            let end = self.next_prompt_at.unwrap_or(now);
            self.active_prompt_end = Some(end);
            let mut request = PromptRequest::new(self.last_covered, Some(end));
            request.allow_snooze = self.settings.prompt_style == PromptStyle::Floating;
            self.show_prompt(request);
            // Iduća granica na kojoj će se ovaj prompt produžiti (a ne otvoriti novi).
            self.next_prompt_at = Some(self.aligned_next_prompt(end));
        }
    }

    /// Produži vidljivi prompt do nove granice intervala i pomakni sljedeću granicu.
    fn extend_active_prompt(&mut self, boundary: DateTime<Local>) {
        self.active_prompt_end = Some(boundary);
        self.effects.push(Effect::ExtendPrompt(boundary));
        self.next_prompt_at = Some(self.aligned_next_prompt(boundary));
    }

    fn handle_return(&mut self, gap_start: DateTime<Local>, now: DateTime<Local>) {
        self.awaiting_return_since = None;
        let gap_minutes = ((now - gap_start).num_seconds() / 60).max(1);

        if (gap_start - self.last_covered).num_seconds() > 60 {
            let mut request = PromptRequest::new(self.last_covered, Some(gap_start));
            request.pause_after = Some(PendingPause {
                start: gap_start,
                reason: "Pauza (odsutnost)".into(),
            });
            request.note = Some(format!(
                "Bio si odsutan ~{gap_minutes} min — to razdoblje bit će označeno kao pauza."
            ));
            request.allow_snooze = false;
            self.show_prompt(request);
        } else {
            if now > gap_start {
                let start = gap_start.max(self.last_covered);
                self.entries.push(Entry::new(start, now, "Pauza (odsutnost)", EntryKind::Pause));
                self.persist_day();
            }
            self.last_covered = now;
            self.next_prompt_at = Some(self.aligned_next_prompt(now));
        }
    }

    fn end_manual_pause(&mut self, now: DateTime<Local>) {
        if let Some(since) = self.paused_since {
            // Ako je prompt prije pauze preskočen, taj period nije susjedan onome što
            // slijedi (pauza je između) — nosi se dalje kao zasebni red.
            self.carry(PromptSpan { start: self.last_covered, end: since });
            if now > since {
                let start = since.max(self.last_covered);
                self.entries.push(Entry::new(start, now, "Pauza", EntryKind::Pause));
            }
        }
        self.paused_since = None;
        self.pause_until = None;
        self.last_covered = self.last_covered.max(now);
        self.next_prompt_at = Some(self.aligned_next_prompt(now));
        self.persist_day();
    }

    // MARK: - Prompt

    fn show_prompt(&mut self, mut request: PromptRequest) {
        // Preskočeni periodi idu uz svaki prompt. Oni susjedni glavnom periodu stope se u
        // njega (jedan period koji se može razbiti s `✂`), ostali se prikazuju kao zasebni
        // redovi iznad.
        let mut carried = self.carried_spans.clone();
        while carried
            .last()
            .is_some_and(|last| (request.start - last.end).num_seconds().abs() <= 1)
        {
            let last = carried.pop().expect("provjereno iznad");
            request.start = last.start;
            // Prompt bez vlastitog perioda (samo preskočeni redovi) preuzima kraj spojenog
            // perioda — inače bi ostao degeneriran i to vrijeme bi se izgubilo.
            request.end = Some(request.end.unwrap_or(last.end).max(last.end));
        }
        request.carried = carried;

        if self.settings.sound_enabled {
            self.effects.push(Effect::PlaySound);
        }
        // Isti kut ekrana — prompt ima prednost, obavijest o verziji ostaje u meniju.
        self.effects.push(Effect::CloseUpdatePopup);
        self.prompt_visible = true;
        self.effects.push(Effect::ShowPrompt {
            request,
            style: self.settings.prompt_style,
            history: self.history.clone(),
        });
    }

    fn close_prompt(&mut self) {
        if self.prompt_visible {
            self.prompt_visible = false;
            self.effects.push(Effect::ClosePrompt);
        }
    }

    fn close_reminder(&mut self) {
        if self.reminder_visible {
            self.reminder_visible = false;
            self.reminder_shown_at = None;
            self.reminder_backfill_from = None;
            self.effects.push(Effect::CloseStartupReminder);
        }
    }

    pub fn on_prompt_snoozed(&mut self) {
        self.prompt_visible = false;
        self.snooze(5);
    }

    /// Dodaje preskočeni period u popis koji se nosi u sljedeći prompt; susjedni se stapaju.
    fn carry(&mut self, span: PromptSpan) {
        if span.duration() <= 60.0 {
            return;
        }
        match self.carried_spans.last_mut() {
            Some(last) if (span.start - last.end).num_seconds().abs() <= 1 => {
                last.end = last.end.max(span.end);
            }
            _ => self.carried_spans.push(span),
        }
    }

    /// Segment bez teksta je **preskočen** — ne bilježi se. Ako je na kraju perioda,
    /// `last_covered` ostaje gdje je bio, pa isti period iskoči u sljedećem promptu (produžen
    /// za novi interval). Preskočeni period nakon kojeg ima zabilježenog vremena nosi se dalje
    /// kao zasebni red.
    pub fn on_prompt_submitted(&mut self, request: &PromptRequest, result: PromptResult) {
        self.prompt_visible = false;
        let now = Local::now();
        // Produženi kraj (ako je prompt čekao preko granica) ima prednost nad izvornim.
        let effective_end = self.active_prompt_end.or(request.end);
        self.active_prompt_end = None;
        // Svi prikazani preskočeni periodi vratili su se u odgovoru — popis se gradi ispočetka.
        self.carried_spans.clear();

        let mut segments = result.segments;
        segments.sort_by_key(|s| s.start);
        let texts: Vec<String> = segments.iter().map(|s| s.text.trim().to_string()).collect();

        // Niz praznih segmenata na kraju glavnog perioda samo "otkriva" vrijeme unatrag.
        let mut covered_end = effective_end
            .or_else(|| segments.last().map(|s| s.end))
            .unwrap_or(request.start);
        let mut last_filled = segments.len() as i64 - 1;
        while last_filled >= 0
            && texts[last_filled as usize].is_empty()
            && segments[last_filled as usize].start >= request.start
        {
            covered_end = segments[last_filled as usize].start;
            last_filled -= 1;
        }

        for (i, seg) in segments.iter().enumerate() {
            if (seg.end - seg.start).num_seconds() <= 5 {
                continue;
            }
            if texts[i].is_empty() {
                if (i as i64) <= last_filled {
                    self.carry(PromptSpan { start: seg.start, end: seg.end });
                }
            } else {
                self.entries.push(Entry::new(seg.start, seg.end, texts[i].clone(), EntryKind::Work));
                // Kronološki redoslijed → zadnji segment završi kao history[0] (prefill za
                // idući prompt).
                self.push_history(&texts[i]);
            }
        }
        // "Nastavljam s" iz ručnog prompta se ne bilježi kao unos, nego ide samo u povijest —
        // time postaje pre-fill sljedećeg prompta.
        if let Some(next_up) = result.next_up.as_deref() {
            self.push_history(next_up);
        }

        match &request.pause_after {
            Some(pending) if now > pending.start => {
                // Odsutnost se bilježi kao pauza; preskočeni rad prije nje nije susjedan onome
                // što slijedi, pa se nosi dalje zasebno.
                self.carry(PromptSpan {
                    start: covered_end,
                    end: pending.start.min(effective_end.unwrap_or(pending.start)),
                });
                self.entries.push(Entry::new(pending.start, now, pending.reason.clone(), EntryKind::Pause));
                self.last_covered = self.last_covered.max(now);
            }
            _ => {
                self.last_covered = self.last_covered.max(covered_end);
            }
        }
        self.persist_day();

        if request.is_final {
            // Dan je zatvoren — nema sljedećeg prompta koji bi pitao za preskočene periode.
            self.finalize_stop();
        } else if self.pause_until.is_none() {
            let from = now.max(self.last_covered);
            self.next_prompt_at = Some(self.aligned_next_prompt(from));
        }
    }

    fn push_history(&mut self, text: &str) {
        let t = text.trim();
        if t.is_empty() {
            return;
        }
        self.history.retain(|h| h != t);
        self.history.insert(0, t.to_string());
        self.history.truncate(self.settings.history_limit);
        Store::save_history(&self.history);
    }

    fn persist_day(&mut self) {
        self.entries.sort_by_key(|e| e.start);
        Store::save_day(&self.current_day_key, &self.entries);
    }

    // MARK: - Postavke

    pub fn update_settings(&mut self, new: AppSettings) {
        let old = std::mem::replace(&mut self.settings, new);
        if old == self.settings {
            return;
        }
        Store::save_settings(&self.settings);
        self.settings_changed(&old);
    }

    fn settings_changed(&mut self, old: &AppSettings) {
        if old.interval_minutes != self.settings.interval_minutes
            && self.is_tracking
            && self.pause_until.is_none()
        {
            self.next_prompt_at = Some(self.aligned_next_prompt(Local::now()));
        }
        if old.history_limit != self.settings.history_limit
            && self.history.len() > self.settings.history_limit
        {
            self.history.truncate(self.settings.history_limit);
            Store::save_history(&self.history);
        }
        if old.launch_at_login != self.settings.launch_at_login {
            self.launch_at_login_status = LaunchAtLogin::apply(self.settings.launch_at_login);
        }
        // Promjena vremena/uključenosti poništava eventualno današnje produženje.
        if old.auto_stop_enabled != self.settings.auto_stop_enabled
            || old.auto_stop_hour != self.settings.auto_stop_hour
            || old.auto_stop_minute != self.settings.auto_stop_minute
        {
            self.close_auto_stop_warning();
            self.auto_stop_warning_shown = false;
            self.auto_stop_at = if self.is_tracking { self.next_auto_stop(Local::now()) } else { None };
        }
        if old.update_check_enabled != self.settings.update_check_enabled {
            if self.settings.update_check_enabled {
                self.next_update_check_at = Local::now();
            } else {
                self.available_update = None;
                self.pending_update_popup = None;
                self.update_status = None;
                self.effects.push(Effect::CloseUpdatePopup);
            }
        }
        if old.workday_start_enabled != self.settings.workday_start_enabled
            || old.workday_start_hour != self.settings.workday_start_hour
            || old.workday_start_minute != self.settings.workday_start_minute
            || old.skip_weekend_reminders != self.settings.skip_weekend_reminders
        {
            // Novo vrijeme vrijedi od idućeg početka radnog dana: ako je današnji već
            // prošao, danas se više ne javlja — inače bi pop-up iskočio čim se u
            // postavkama namjesti raniji sat (ili vikendom isključi "Preskoči vikende").
            let now = Local::now();
            let started = self.workday_start(now).map(|s| now >= s).unwrap_or(false);
            self.workday_reminder_day_key = started.then(|| Store::day_key(now));
        }
    }

    // MARK: - Pomoćno za UI

    pub fn tray_state(&self) -> TrayState {
        if !self.is_tracking {
            return TrayState::Idle;
        }
        if self.pause_until.is_some() {
            return TrayState::Paused;
        }
        if self.awaiting_return_since.is_some() {
            return TrayState::AwaitingReturn;
        }
        TrayState::Tracking
    }

    pub fn status_text(&self) -> String {
        if !self.is_tracking {
            return "Nije pokrenuto".into();
        }
        match self.pause_until {
            Some(PauseUntil::Indefinite) => return "Pauzirano do nastavka".into(),
            Some(PauseUntil::Until(until)) => return format!("Pauzirano do {}", Fmt::hhmm(until)),
            None => {}
        }
        if let Some(since) = self.awaiting_return_since {
            return format!("Odsutan od {} — čekam povratak", Fmt::hhmm(since));
        }
        "Trackam".into()
    }

    /// "Auto-stop u 16:00" — None kad je isključeno ili kad tracking nije aktivan.
    pub fn auto_stop_text(&self) -> Option<String> {
        if !self.is_tracking {
            return None;
        }
        let at = self.auto_stop_at?;
        let today = at.date_naive() == Local::now().date_naive();
        Some(if today {
            format!("Auto-stop u {}", Fmt::hhmm(at))
        } else {
            format!("Auto-stop sutra u {}", Fmt::hhmm(at))
        })
    }

    pub fn work_total(&self) -> f64 {
        Summarize::work_total(&self.entries)
    }
}

// MARK: - Vremenska mreža

fn hour_start(d: DateTime<Local>) -> DateTime<Local> {
    d.with_minute(0)
        .and_then(|d| d.with_second(0))
        .and_then(|d| d.with_nanosecond(0))
        .unwrap_or(d)
}

/// Početak bloka mreže u kojem se `date` nalazi (npr. 9:56 uz step 900 → 9:45).
fn grid_floor(date: DateTime<Local>, step: i64) -> DateTime<Local> {
    let hs = hour_start(date);
    let elapsed = (date - hs).num_milliseconds() as f64 / 1000.0;
    hs + Duration::seconds((elapsed / step as f64).floor() as i64 * step)
}

/// Najbliža točka 5-minutne mreže (npr. 10:17:40 → 10:20).
fn snap_to_grid(date: DateTime<Local>) -> DateTime<Local> {
    let hs = hour_start(date);
    let elapsed = (date - hs).num_milliseconds() as f64 / 1000.0;
    hs + Duration::seconds((elapsed / 300.0).round() as i64 * 300)
}

/// Isti dan kao `date`, ali u `hour:minute`. None samo kod nemogućeg lokalnog vremena
/// (DST skok), gdje pozivatelj svejedno ima razuman fallback.
fn at_time(date: DateTime<Local>, hour: u32, minute: u32) -> Option<DateTime<Local>> {
    Local
        .with_ymd_and_hms(date.year(), date.month(), date.day(), hour, minute, 0)
        .single()
}

/// Točke 5-min mreže strogo unutar perioda (min. 2 min od rubova). Za jako duge periode
/// mreža se prorjeđuje da ne bude više od 12 blokova. Koristi ih traka za razbijanje
/// perioda u promptu.
pub fn grid_boundaries(start: DateTime<Local>, end: DateTime<Local>) -> Vec<DateTime<Local>> {
    if (end - start).num_seconds() <= 240 {
        return Vec::new();
    }
    let hs = hour_start(start);
    for step_minutes in [5_i64, 10, 15, 30, 60] {
        let step = Duration::minutes(step_minutes);
        let mut t = hs;
        while t <= start + Duration::seconds(120) {
            t += step;
        }
        let mut points = Vec::new();
        while t <= end - Duration::seconds(120) {
            points.push(t);
            t += step;
        }
        if points.len() <= 11 {
            return points;
        }
    }
    Vec::new()
}
