use chrono::{DateTime, Local, SecondsFormat, Timelike, Utc};
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

/// Isto kao `iso8601`, za neobavezno vrijeme: `None` se ne piše (kao Swiftov
/// `encodeIfPresent`), a nedostajući ključ (uz `#[serde(default)]`) ili `null` daju `None`.
mod iso8601_opt {
    use chrono::{DateTime, Local};
    use serde::{Deserialize, Deserializer, Serializer};

    pub fn serialize<S: Serializer>(date: &Option<DateTime<Local>>, s: S) -> Result<S::Ok, S::Error> {
        match date {
            Some(date) => super::iso8601::serialize(date, s),
            None => s.serialize_none(),
        }
    }

    pub fn deserialize<'de, D: Deserializer<'de>>(d: D) -> Result<Option<DateTime<Local>>, D::Error> {
        Option::<String>::deserialize(d)?
            .map(|s| {
                DateTime::parse_from_rfc3339(&s)
                    .map(|dt| dt.with_timezone(&Local))
                    .map_err(serde::de::Error::custom)
            })
            .transpose()
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
fn d_empty() -> String { String::new() }

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
    /// Jednom dnevno provjeri na GitHubu postoji li nova verzija (samo javlja, ne instalira).
    #[serde(default = "d_true")]
    pub update_check_enabled: bool,
    /// Verzija za koju je pop-up o novoj verziji već prikazan — javlja se jednom po verziji,
    /// a dalje samo u meniju.
    #[serde(default = "d_empty")]
    pub update_notified_version: String,
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
            update_check_enabled: d_true(),
            update_notified_version: d_empty(),
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

/// Period bez opisa — preskočeni period koji se nosi u sljedeći prompt. Polja abecedno
/// (sprema se uz sesiju, vidi `SavedSession`).
#[derive(Debug, Clone, Copy, PartialEq, Serialize, Deserialize)]
pub struct PromptSpan {
    #[serde(with = "iso8601")]
    pub end: DateTime<Local>,
    #[serde(with = "iso8601")]
    pub start: DateTime<Local>,
}

/// Trackanje u tijeku, spremljeno u `session.json` — da ga gašenje računala (ili izlaz iz
/// aplikacije) ne izgubi. Kod idućeg pokretanja se nastavi ili zatvori na kraju radnog dana.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SavedSession {
    /// Zakazano automatsko zaustavljanje (s eventualnim produženjem).
    #[serde(default, with = "iso8601_opt", skip_serializing_if = "Option::is_none")]
    pub auto_stop_at: Option<DateTime<Local>>,
    /// Preskočeni periodi koji se nose u sljedeći prompt.
    #[serde(default)]
    pub carried: Vec<PromptSpan>,
    pub day_key: String,
    /// Dan je već zatvoren i zadnji prompt čeka odgovor — kraj njegovog perioda.
    #[serde(default, with = "iso8601_opt", skip_serializing_if = "Option::is_none")]
    pub final_end: Option<DateTime<Local>>,
    #[serde(with = "iso8601")]
    pub last_covered: DateTime<Local>,
}

impl PromptSpan {
    /// Trajanje u sekundama.
    pub fn duration(&self) -> f64 {
        (self.end - self.start).num_milliseconds() as f64 / 1000.0
    }
}

/// Odsutnost iza perioda povratnog prompta — pauza je već zapisana, `end` je trenutak
/// povratka (od njega kreće sljedeći period, a ne od trenutka odgovora).
#[derive(Debug, Clone, Copy)]
pub struct PendingPause {
    pub start: DateTime<Local>,
    pub end: DateTime<Local>,
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
#[derive(Debug, Clone, PartialEq)]
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

/// Stavka kronološkog pregleda: red s unosom ili rupa između dva unosa (nezabilježeno
/// vrijeme) — rupa se klikom popuni novim unosom.
#[derive(Debug, Clone)]
pub enum TimelineItem {
    Row(ChronoRow),
    Gap(DateTime<Local>, DateTime<Local>),
}

pub struct Summarize;

impl Summarize {
    /// Redovi kronološkog pregleda s rupama između njih. Rupa je razmak od barem minute
    /// između kraja dosad najkasnijeg unosa i početka sljedećeg.
    pub fn timeline(rows: &[ChronoRow]) -> Vec<TimelineItem> {
        let mut items = Vec::new();
        let mut last_end: Option<DateTime<Local>> = None;
        for row in rows {
            if let Some(last) = last_end {
                if (row.start - last).num_seconds() >= 60 {
                    items.push(TimelineItem::Gap(last, row.start));
                }
            }
            items.push(TimelineItem::Row(row.clone()));
            last_end = Some(last_end.map_or(row.end, |l| l.max(row.end)));
        }
        items
    }

    /// Unosi koji se preklapaju s rasponom (bez onih koje upravo ispravljamo).
    pub fn overlapping<'a>(
        entries: &'a [Entry],
        start: DateTime<Local>,
        end: DateTime<Local>,
        excluding: &[Uuid],
    ) -> Vec<&'a Entry> {
        entries
            .iter()
            .filter(|e| !excluding.contains(&e.id) && e.start < end && e.end > start)
            .collect()
    }

    /// Novi/ispravljeni unos "pobjeđuje": ostali unosi se skrate, razdvoje ili maknu tako
    /// da ništa ne pokriva `start…end` — inače bi se isto vrijeme brojalo dvaput.
    pub fn carve(day: &mut Vec<Entry>, start: DateTime<Local>, end: DateTime<Local>, excluding: &[Uuid]) {
        let mut out = Vec::with_capacity(day.len() + 1);
        for e in day.drain(..) {
            if excluding.contains(&e.id) || !(e.start < end && e.end > start) {
                out.push(e);
                continue;
            }
            if e.start < start {
                let mut head = e.clone();
                head.end = start;
                out.push(head);
            }
            if e.end > end {
                let mut tail = e.clone();
                // Ako je i glava ostala, rep je novi unos (ne smiju dijeliti id).
                if e.start < start {
                    tail.id = Uuid::new_v4();
                }
                tail.start = end;
                out.push(tail);
            }
        }
        *day = out;
    }

    /// Dijelovi `start…end` koje ne pokriva nijedan unos. Automatski unosi (odgovor na
    /// prompt, pauza, odsutnost) pune samo njih — ručno dodan ili ispravljen unos iz
    /// pregleda dana ima prednost i ništa se ne broji dvaput.
    pub fn uncovered(
        entries: &[Entry],
        start: DateTime<Local>,
        end: DateTime<Local>,
    ) -> Vec<(DateTime<Local>, DateTime<Local>)> {
        let mut sorted: Vec<&Entry> = entries.iter().collect();
        sorted.sort_by_key(|e| e.start);
        let mut parts = Vec::new();
        let mut cursor = start;
        for e in sorted {
            if e.end <= cursor || e.start >= end {
                continue;
            }
            if e.start > cursor {
                parts.push((cursor, e.start));
            }
            cursor = cursor.max(e.end);
            if cursor >= end {
                break;
            }
        }
        if cursor < end {
            parts.push((cursor, end));
        }
        parts
    }

    /// Prijedlog raspona za "Dodaj unos": od kraja zadnjeg unosa (ili početka radnog dana
    /// ako ih nema) u trajanju jednog intervala, unutar istog dana. Danas najkasnije do
    /// sada — unos ne smije završiti u budućnosti (vidi `entry_edit.rs`).
    pub fn suggested_range(
        entries: &[Entry],
        day: DateTime<Local>,
        settings: &AppSettings,
        now: DateTime<Local>,
    ) -> (DateTime<Local>, DateTime<Local>) {
        let day_start = Fmt::at(day, 0, 0);
        let mut day_end = Fmt::at(day, 23, 59);
        if now.date_naive() == day.date_naive() {
            day_end = day_end.min(Fmt::at(now, now.hour(), now.minute()));
        }
        let interval = chrono::Duration::minutes(settings.interval_minutes);
        let workday = Fmt::at(day, settings.workday_start_hour, settings.workday_start_minute);
        let last = entries
            .iter()
            .map(|e| e.end)
            .max()
            .unwrap_or_else(|| workday.min(day_end - interval));
        let start = day_start.max(last.min(day_end - chrono::Duration::minutes(1)));
        let end = (start + interval).min(day_end);
        (start, end)
    }

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

    /// Isti CSV kao macOS/Windows: vremena u UTC-u bez frakcije (`2026-10-20T06:00:00Z`,
    /// kao `ISO8601DateFormatter`), minute zaokružene s polovicom od nule.
    pub fn csv(entries: &[Entry]) -> String {
        let iso = |d: DateTime<Local>| {
            d.with_timezone(&Utc).to_rfc3339_opts(SecondsFormat::Secs, true)
        };
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
                iso(e.start),
                iso(e.end),
                minutes,
                text,
                kind
            );
        }
        out
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::theme::Fmt;

    fn at(h: u32, m: u32) -> DateTime<Local> {
        Fmt::at(Local::now(), h, m)
    }

    #[test]
    fn carve_trims_splits_and_removes() {
        let mut day = vec![
            Entry::new(at(9, 0), at(10, 0), "A", EntryKind::Work),
            Entry::new(at(10, 0), at(10, 30), "B", EntryKind::Work),
            Entry::new(at(10, 30), at(12, 0), "C", EntryKind::Work),
        ];
        let c_id = day[2].id;
        // 9:45–11:00 skrati A, makne B, odreže početak C-a.
        Summarize::carve(&mut day, at(9, 45), at(11, 0), &[]);
        let ranges: Vec<_> = day.iter().map(|e| (e.text.as_str(), e.start, e.end)).collect();
        assert_eq!(ranges, vec![("A", at(9, 0), at(9, 45)), ("C", at(11, 0), at(12, 0))]);
        assert_eq!(day[1].id, c_id);

        // Unos usred dugog unosa ga razdvoji; rep dobije novi id.
        Summarize::carve(&mut day, at(11, 15), at(11, 30), &[]);
        assert_eq!(day.len(), 3);
        assert_eq!((day[1].start, day[1].end, day[1].id), (at(11, 0), at(11, 15), c_id));
        assert_eq!((day[2].start, day[2].end), (at(11, 30), at(12, 0)));
        assert_ne!(day[2].id, c_id);
    }

    #[test]
    fn csv_uses_utc_seconds_and_rounds_half_up() {
        // 7,5 min → 8 (macOS `.rounded()`), frakcija sekunde se ne piše.
        let start = at(8, 0) + chrono::Duration::milliseconds(500);
        let end = at(8, 7) + chrono::Duration::seconds(30) + chrono::Duration::milliseconds(500);
        let csv = Summarize::csv(&[Entry::new(start, end, "Rad \"X\"", EntryKind::Work)]);
        let utc = |d: DateTime<Local>| {
            d.with_timezone(&Utc).format("%Y-%m-%dT%H:%M:%SZ").to_string()
        };
        let row = format!("{},{},8,\"Rad \"\"X\"\"\",work\n", utc(start), utc(end));
        assert_eq!(csv, format!("start,end,minutes,text,kind\n{row}"));
    }

    #[test]
    fn uncovered_parts() {
        let entries = vec![
            Entry::new(at(10, 10), at(10, 20), "A", EntryKind::Work),
            Entry::new(at(10, 40), at(10, 50), "B", EntryKind::Work),
        ];
        assert_eq!(Summarize::uncovered(&[], at(10, 0), at(10, 30)), vec![(at(10, 0), at(10, 30))]);
        assert_eq!(
            Summarize::uncovered(&entries, at(10, 0), at(11, 0)),
            vec![(at(10, 0), at(10, 10)), (at(10, 20), at(10, 40)), (at(10, 50), at(11, 0))]
        );
        assert_eq!(Summarize::uncovered(&entries, at(10, 15), at(10, 30)), vec![(at(10, 20), at(10, 30))]);
        assert!(Summarize::uncovered(&entries, at(10, 12), at(10, 18)).is_empty());
        assert!(Summarize::uncovered(&entries, at(10, 30), at(10, 0)).is_empty());
        let overlapping = vec![
            Entry::new(at(10, 0), at(10, 20), "A", EntryKind::Work),
            Entry::new(at(10, 5), at(10, 25), "B", EntryKind::Work),
        ];
        assert_eq!(
            Summarize::uncovered(&overlapping, at(9, 50), at(10, 30)),
            vec![(at(9, 50), at(10, 0)), (at(10, 25), at(10, 30))]
        );
    }

    #[test]
    fn suggested_range_stays_in_the_past() {
        let settings = AppSettings {
            interval_minutes: 15,
            workday_start_hour: 9,
            workday_start_minute: 0,
            ..AppSettings::default()
        };
        let entries = vec![Entry::new(at(10, 10), at(10, 50), "A", EntryKind::Work)];
        let tomorrow = at(12, 0) + chrono::Duration::days(1);
        // Prošli dan: od kraja zadnjeg unosa / od početka radnog dana.
        assert_eq!(Summarize::suggested_range(&entries, at(0, 0), &settings, tomorrow), (at(10, 50), at(11, 5)));
        assert_eq!(Summarize::suggested_range(&[], at(0, 0), &settings, tomorrow), (at(9, 0), at(9, 15)));
        // Danas: najkasnije do sada (cijele minute).
        let now = at(10, 58) + chrono::Duration::seconds(30);
        assert_eq!(Summarize::suggested_range(&entries, at(0, 0), &settings, now), (at(10, 50), at(10, 58)));
        let early = at(8, 0) + chrono::Duration::seconds(40);
        assert_eq!(Summarize::suggested_range(&[], at(0, 0), &settings, early), (at(7, 45), at(8, 0)));
    }

    #[test]
    fn timeline_shows_gaps() {
        let entries = vec![
            Entry::new(at(9, 0), at(10, 0), "A", EntryKind::Work),
            Entry::new(at(10, 30), at(11, 0), "B", EntryKind::Work),
        ];
        let rows = Summarize::chronology(&entries, false);
        let items = Summarize::timeline(&rows);
        assert_eq!(items.len(), 3);
        assert!(matches!(items[1], TimelineItem::Gap(s, e) if s == at(10, 0) && e == at(10, 30)));
    }

    #[test]
    fn saved_session_matches_shared_schema() {
        let session = SavedSession {
            auto_stop_at: None,
            carried: vec![PromptSpan { end: at(9, 30), start: at(9, 15) }],
            day_key: "2026-07-15".into(),
            final_end: None,
            last_covered: at(9, 45),
        };
        let json = serde_json::to_string(&session).unwrap();
        // Ključevi abecedno i camelCase kao macOS `sortedKeys`; prazni opcionalni se ne pišu.
        let keys: Vec<_> = ["\"carried\"", "\"end\"", "\"start\"", "\"dayKey\"", "\"lastCovered\""]
            .iter()
            .map(|k| json.find(k).unwrap())
            .collect();
        assert!(keys.windows(2).all(|w| w[0] < w[1]), "{json}");
        assert!(!json.contains("autoStopAt") && !json.contains("finalEnd"));
        assert_eq!(serde_json::from_str::<SavedSession>(&json).unwrap(), session);

        // Stari / ručno pisani file bez opcionalnih ključeva se i dalje čita.
        let minimal: SavedSession =
            serde_json::from_str(r#"{"dayKey":"2026-07-15","lastCovered":"2026-07-15T07:45:00Z"}"#).unwrap();
        assert!(minimal.carried.is_empty() && minimal.auto_stop_at.is_none() && minimal.final_end.is_none());
    }
}
