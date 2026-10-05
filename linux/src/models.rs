use chrono::{DateTime, Local};
use serde::{Deserialize, Serialize};
use uuid::Uuid;

use crate::theme::Fmt;

// MARK: - Serde pomoćnici (shema dijeljena s macOS/Windows portom)

/// ISO 8601 UTC ("2026-07-15T07:45:00Z") — kako piše Swiftov `.iso8601` encoder.
/// Čitanje je popustljivo (prihvaća decimalne sekunde i bilo koji offset).
mod iso8601 {
    use chrono::{DateTime, Local};
    use serde::{Deserialize, Deserializer, Serializer};

    pub fn serialize<S: Serializer>(date: &DateTime<Local>, s: S) -> Result<S::Ok, S::Error> {
        let utc = date.with_timezone(&chrono::Utc);
        s.serialize_str(&utc.format("%Y-%m-%dT%H:%M:%SZ").to_string())
    }

    pub fn deserialize<'de, D: Deserializer<'de>>(d: D) -> Result<DateTime<Local>, D::Error> {
        let s = String::deserialize(d)?;
        DateTime::parse_from_rfc3339(&s)
            .map(|dt| dt.with_timezone(&Local))
            .map_err(serde::de::Error::custom)
    }
}

/// UUID velikim slovima s crticama — Swiftov `UUID` string oblik.
mod upper_uuid {
    use serde::{Deserialize, Deserializer, Serializer};
    use uuid::Uuid;

    pub fn serialize<S: Serializer>(id: &Uuid, s: S) -> Result<S::Ok, S::Error> {
        s.serialize_str(&id.hyphenated().to_string().to_uppercase())
    }

    pub fn deserialize<'de, D: Deserializer<'de>>(d: D) -> Result<Uuid, D::Error> {
        let s = String::deserialize(d)?;
        Ok(Uuid::parse_str(&s).unwrap_or_else(|_| Uuid::new_v4()))
    }
}

// MARK: - Entry

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum EntryKind {
    Work,
    Pause,
}

/// Polja idu **abecedno** jer macOS encoder piše `sortedKeys` — serde poštuje redoslijed
/// deklaracije, pa datoteke ostaju bajt-po-bajt usporedive među portovima.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct Entry {
    #[serde(with = "iso8601")]
    pub end: DateTime<Local>,
    #[serde(with = "upper_uuid")]
    pub id: Uuid,
    pub kind: EntryKind,
    #[serde(with = "iso8601")]
    pub start: DateTime<Local>,
    pub text: String,
}

impl Entry {
    pub fn new(start: DateTime<Local>, end: DateTime<Local>, text: impl Into<String>, kind: EntryKind) -> Self {
        Entry { end, id: Uuid::new_v4(), kind, start, text: text.into() }
    }

    /// Trajanje u sekundama.
    pub fn duration(&self) -> f64 {
        (self.end - self.start).num_milliseconds() as f64 / 1000.0
    }
}

// MARK: - Postavke

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum PromptStyle {
    Floating,
    Fullscreen,
}

impl PromptStyle {
    pub fn label(self) -> &'static str {
        match self {
            PromptStyle::Floating => "Floating panel (kut ekrana)",
            PromptStyle::Fullscreen => "Cijeli ekran (preko svega)",
        }
    }
}

// Defaulti su po polju (a ne `#[serde(default)]` na strukturi) da stari `settings.json`
// bez novih ključeva ne padne na `Default::default()` tipa (0 / false), nego da svako
// nedostajuće polje preuzme *svoj* default — kao macOS `decodeIfPresent`.
fn d_interval_minutes() -> i64 { 15 }
fn d_prompt_style() -> PromptStyle { PromptStyle::Floating }
fn d_true() -> bool { true }
fn d_false() -> bool { false }
fn d_idle_threshold() -> i64 { 5 }
fn d_history_limit() -> usize { 15 }
fn d_auto_stop_hour() -> u32 { 16 }
fn d_zero() -> u32 { 0 }
fn d_workday_hour() -> u32 { 8 }
fn d_workday_minute() -> u32 { 30 }

/// Polja abecedno — vidi komentar na `Entry`. Ključevi u JSON-u su camelCase
/// (`autoStopEnabled`), isti kao na macOS i Windows portu.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AppSettings {
    /// Automatsko zaustavljanje trackinga u zadano vrijeme — da tracking ne ostane
    /// pokrenut preko noći. Minutu prije iskoči upozorenje s opcijom produženja.
    #[serde(default = "d_true")]
    pub auto_stop_enabled: bool,
    #[serde(default = "d_auto_stop_hour")]
    pub auto_stop_hour: u32,
    #[serde(default = "d_zero")]
    pub auto_stop_minute: u32,
    #[serde(default = "d_history_limit")]
    pub history_limit: usize,
    /// Neaktivnost tipkovnice/miša dulje od praga → razdoblje se bilježi kao pauza.
    #[serde(default = "d_false")]
    pub idle_detection_enabled: bool,
    #[serde(default = "d_idle_threshold")]
    pub idle_threshold_minutes: i64,
    #[serde(default = "d_interval_minutes")]
    pub interval_minutes: i64,
    #[serde(default = "d_false")]
    pub launch_at_login: bool,
    /// Zaključan ekran → razdoblje odsutnosti se bilježi kao pauza.
    #[serde(default = "d_false")]
    pub lock_pause_enabled: bool,
    /// Kronološki pregled dana: susjedni unosi istog naziva (jedan završava kad drugi
    /// počinje) prikazuju se kao jedan unos.
    #[serde(default = "d_true")]
    pub merge_adjacent_entries: bool,
    #[serde(default = "d_prompt_style")]
    pub prompt_style: PromptStyle,
    #[serde(default = "d_true")]
    pub show_startup_reminder: bool,
    /// Subotom i nedjeljom ne iskače nijedan podsjetnik (ni u zadano vrijeme, ni kod
    /// pokretanja aplikacije). Tracking se i vikendom može pokrenuti ručno.
    #[serde(default = "d_true")]
    pub skip_weekend_reminders: bool,
    #[serde(default = "d_true")]
    pub sound_enabled: bool,
    /// Podsjetnik nudi i start od početka radnog dana — otvaranje laptopa u 9:30 se
    /// tako može upisati kao rad od 8:30 (jutro se nadoknadi).
    #[serde(default = "d_true")]
    pub workday_start_backfill_enabled: bool,
    /// Podsjetnik na početak radnog dana — iskoči u zadano vrijeme, a ako je računalo
    /// tad spavalo, čim se probudi.
    #[serde(default = "d_true")]
    pub workday_start_enabled: bool,
    #[serde(default = "d_workday_hour")]
    pub workday_start_hour: u32,
    #[serde(default = "d_workday_minute")]
    pub workday_start_minute: u32,
}

impl Default for AppSettings {
    fn default() -> Self {
        AppSettings {
            auto_stop_enabled: d_true(),
            auto_stop_hour: d_auto_stop_hour(),
            auto_stop_minute: d_zero(),
            history_limit: d_history_limit(),
            idle_detection_enabled: d_false(),
            idle_threshold_minutes: d_idle_threshold(),
            interval_minutes: d_interval_minutes(),
            launch_at_login: d_false(),
            lock_pause_enabled: d_false(),
            merge_adjacent_entries: d_true(),
            prompt_style: d_prompt_style(),
            show_startup_reminder: d_true(),
            skip_weekend_reminders: d_true(),
            sound_enabled: d_true(),
            workday_start_backfill_enabled: d_true(),
            workday_start_enabled: d_true(),
            workday_start_hour: d_workday_hour(),
            workday_start_minute: d_workday_minute(),
        }
    }
}

// MARK: - Prompt

/// Jedan blok (ili spojeni niz blokova) unutar prompt perioda, s pripadajućim opisom.
/// Prazan `text` znači da je blok **preskočen** — ne bilježi se, nego se vraća u sljedeći
/// prompt.
#[derive(Debug, Clone)]
pub struct PromptSegment {
    pub start: DateTime<Local>,
    pub end: DateTime<Local>,
    pub text: String,
}

/// Period bez opisa — preskočeni period koji se nosi u sljedeći prompt.
#[derive(Debug, Clone, Copy)]
pub struct PromptSpan {
    pub start: DateTime<Local>,
    pub end: DateTime<Local>,
}

impl PromptSpan {
    /// Trajanje u sekundama.
    pub fn duration(&self) -> f64 {
        (self.end - self.start).num_milliseconds() as f64 / 1000.0
    }
}

#[derive(Debug, Clone)]
pub struct PendingPause {
    pub start: DateTime<Local>,
    pub reason: String,
}

#[derive(Debug, Clone)]
pub struct PromptRequest {
    pub start: DateTime<Local>,
    /// Fiksni kraj perioda; None znači "do trenutka odgovora".
    pub end: Option<DateTime<Local>>,
    /// Preskočeni periodi koji nisu susjedni glavnom (npr. između je pauza) — prikazuju se
    /// kao zasebni redovi iznad njega. Susjedne engine stopi u glavni period.
    pub carried: Vec<PromptSpan>,
    pub pause_after: Option<PendingPause>,
    pub is_final: bool,
    /// Ručno pokrenut prompt ("Zapiši sada") — nosi i polje "nastavljam s".
    pub is_manual: bool,
    pub note: Option<String>,
    pub allow_snooze: bool,
}

impl PromptRequest {
    pub fn new(start: DateTime<Local>, end: Option<DateTime<Local>>) -> Self {
        PromptRequest {
            start,
            end,
            carried: Vec::new(),
            pause_after: None,
            is_final: false,
            is_manual: false,
            note: None,
            allow_snooze: true,
        }
    }
}

/// Odgovor na prompt. Segmenti bez teksta su preskočeni.
#[derive(Debug, Clone, Default)]
pub struct PromptResult {
    pub segments: Vec<PromptSegment>,
    /// Ručni prompt: čime korisnik nastavlja — postaje pre-fill sljedećeg prompta.
    pub next_up: Option<String>,
}

// MARK: - Dnevni pregled (grupiranje)

pub struct GroupSummary {
    pub text: String,
    pub total: f64,
    pub ranges: Vec<(DateTime<Local>, DateTime<Local>)>,
}

/// Red kronološkog pregleda — jedan unos ili niz spojenih susjednih unosa istog naziva.
#[derive(Debug, Clone)]
pub struct ChronoRow {
    pub ids: Vec<Uuid>,
    pub start: DateTime<Local>,
    pub end: DateTime<Local>,
    pub text: String,
    pub kind: EntryKind,
}

impl ChronoRow {
    pub fn duration(&self) -> f64 {
        (self.end - self.start).num_milliseconds() as f64 / 1000.0
    }

    pub fn is_merged(&self) -> bool {
        self.ids.len() > 1
    }
}

pub struct Summarize;

impl Summarize {
    /// Kronološki popis unosa. Uz `merging` susjedni unosi istog naziva i vrste, gdje
    /// jedan završava kad drugi počinje, čine jedan red (14:45–15:00 + 15:00–15:15 →
    /// 14:45–15:15). Spajaju se samo neposredni susjedi, pa pauza ili drugi opis između
    /// prekida niz.
    pub fn chronology(entries: &[Entry], merging: bool) -> Vec<ChronoRow> {
        let mut sorted: Vec<&Entry> = entries.iter().collect();
        sorted.sort_by_key(|e| e.start);

        let mut rows: Vec<ChronoRow> = Vec::new();
        for e in sorted {
            let text = e.text.trim().to_string();
            let mergeable = merging
                && rows.last().is_some_and(|last| {
                    last.kind == e.kind
                        && last.text == text
                        && (e.start - last.end).num_seconds().abs() <= 1
                });
            if mergeable {
                let last = rows.last_mut().expect("provjereno iznad");
                last.ids.push(e.id);
                last.end = last.end.max(e.end);
            } else {
                rows.push(ChronoRow {
                    ids: vec![e.id],
                    start: e.start,
                    end: e.end,
                    text,
                    kind: e.kind,
                });
            }
        }
        rows
    }

    /// Grupira work unose po tekstu, spaja susjedne intervale istog teksta.
    pub fn groups(entries: &[Entry]) -> Vec<GroupSummary> {
        let mut work: Vec<&Entry> = entries.iter().filter(|e| e.kind == EntryKind::Work).collect();
        work.sort_by_key(|e| e.start);

        let mut groups: Vec<GroupSummary> = Vec::new();
        for e in work {
            let key = e.text.trim().to_string();
            match groups.iter_mut().find(|g| g.text == key) {
                Some(g) => {
                    let contiguous = g
                        .ranges
                        .last()
                        .is_some_and(|last| (e.start - last.1).num_seconds() < 90);
                    if contiguous {
                        let last = g.ranges.last_mut().expect("provjereno iznad");
                        last.1 = last.1.max(e.end);
                    } else {
                        g.ranges.push((e.start, e.end));
                    }
                    g.total += e.duration();
                }
                None => groups.push(GroupSummary {
                    text: key,
                    total: e.duration(),
                    ranges: vec![(e.start, e.end)],
                }),
            }
        }
        groups.sort_by(|a, b| b.total.total_cmp(&a.total));
        groups
    }

    pub fn work_total(entries: &[Entry]) -> f64 {
        entries.iter().filter(|e| e.kind == EntryKind::Work).map(Entry::duration).sum()
    }

    pub fn pause_total(entries: &[Entry]) -> f64 {
        entries.iter().filter(|e| e.kind == EntryKind::Pause).map(Entry::duration).sum()
    }

    pub fn clipboard_text(date: DateTime<Local>, entries: &[Entry]) -> String {
        let mut out = format!("LLOYDS TRACKER — {}\n", Fmt::day_title(date));
        out += &format!("Ukupno rad: {}", Fmt::dur(Self::work_total(entries)));
        let pauses = Self::pause_total(entries);
        if pauses > 0.0 {
            out += &format!(" · Pauze: {}", Fmt::dur(pauses));
        }
        out += "\n\n";
        for g in Self::groups(entries) {
            out += &format!("{} — {}\n", Fmt::dur(g.total), g.text);
            let ranges: Vec<String> = g
                .ranges
                .iter()
                .map(|(s, e)| format!("{}–{}", Fmt::hhmm(*s), Fmt::hhmm(*e)))
                .collect();
            out += &format!("    {}\n", ranges.join(" · "));
        }
        out
    }

    pub fn csv(entries: &[Entry]) -> String {
        let mut sorted: Vec<&Entry> = entries.iter().collect();
        sorted.sort_by_key(|e| e.start);

        let mut out = String::from("start,end,minutes,text,kind\n");
        for e in sorted {
            let text = e.text.replace('"', "\"\"");
            let minutes = (e.duration() / 60.0).round() as i64;
            let kind = match e.kind {
                EntryKind::Work => "work",
                EntryKind::Pause => "pause",
            };
            out += &format!(
                "{},{},{},\"{}\",{}\n",
                e.start.to_rfc3339(),
                e.end.to_rfc3339(),
                minutes,
                text,
                kind
            );
        }
        out
    }
}
