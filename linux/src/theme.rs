use chrono::{DateTime, Datelike, Duration, Local, NaiveDate, Timelike};

// MARK: - Boje

// Lloyds Digital brand — https://lloyds-digital.com/
pub const YELLOW: &str = "#FBDE07";
pub const BLACK: &str = "#070707";
pub const GRAY: &str = "#C4C4C4";

/// Žuta i crna kao RGB komponente (0–1) za cairo crtanje tray ikona.
pub const YELLOW_RGB: (f64, f64, f64) = (0xFB as f64 / 255.0, 0xDE as f64 / 255.0, 0x07 as f64 / 255.0);
pub const BLACK_RGB: (f64, f64, f64) = (0x07 as f64 / 255.0, 0x07 as f64 / 255.0, 0x07 as f64 / 255.0);

// MARK: - Formatiranje (hr_HR)

/// Nazivi dana kao što ih daje `Locale("hr_HR")` na macOS-u — chrono nema lokalizaciju,
/// pa ih držimo ovdje da `dayTitle` ispadne identičan ("petak, 15.8.2026.").
const WEEKDAYS_HR: [&str; 7] = [
    "ponedjeljak",
    "utorak",
    "srijeda",
    "četvrtak",
    "petak",
    "subota",
    "nedjelja",
];

pub struct Fmt;

/// Najdulji unos koji smije prijeći ponoć kad je kraj upisan "prije" početka — 23:30–00:30
/// je unos preko ponoći, a 10:00–9:00 tipfeler (ne unos od 23 sata).
pub const MAX_OVERNIGHT_HOURS: i64 = 12;

impl Fmt {
    /// "HH:mm"
    pub fn hhmm(d: DateTime<Local>) -> String {
        format!("{:02}:{:02}", d.hour(), d.minute())
    }

    /// "yyyy-MM-dd" — ime dnevne JSON datoteke, isto na sva tri porta.
    pub fn day_key(d: DateTime<Local>) -> String {
        format!("{:04}-{:02}-{:02}", d.year(), d.month(), d.day())
    }

    /// "petak, 15.8.2026." — macOS `EEEE, d.M.yyyy.` uz hr locale.
    pub fn day_title(d: DateTime<Local>) -> String {
        let name = WEEKDAYS_HR[d.weekday().num_days_from_monday() as usize];
        format!("{}, {}.{}.{}.", name, d.day(), d.month(), d.year())
    }

    /// "2h 15m" / "2h" / "15m"
    pub fn dur(seconds: f64) -> String {
        let total = seconds.round().max(0.0) as i64;
        let h = total / 3600;
        let m = (total % 3600) / 60;
        if h > 0 && m > 0 {
            format!("{h}h {m}m")
        } else if h > 0 {
            format!("{h}h")
        } else {
            format!("{m}m")
        }
    }

    /// "1 blok", "2 bloka", "5 blokova", "21 blok", "12 blokova" — hrvatska množina.
    pub fn blocks(n: usize) -> String {
        let (last, last_two) = (n % 10, n % 100);
        if last == 1 && last_two != 11 {
            format!("{n} blok")
        } else if (2..=4).contains(&last) && !(12..=14).contains(&last_two) {
            format!("{n} bloka")
        } else {
            format!("{n} blokova")
        }
    }

    /// Vrijeme upisano rukom: "9", "930", "0930", "9:30", "9.30" ili "9,30".
    pub fn parse_time(input: &str) -> Option<(u32, u32)> {
        let s = input.trim().replace(['.', ','], ":");
        let digits = |p: &str| !p.is_empty() && p.chars().all(|c| c.is_ascii_digit());
        let (h, m) = if let Some((h, m)) = s.split_once(':') {
            if !(1..=2).contains(&h.len()) || m.len() != 2 || !digits(h) || !digits(m) {
                return None;
            }
            (h.parse().ok()?, m.parse().ok()?)
        } else {
            if !(1..=4).contains(&s.len()) || !digits(&s) {
                return None;
            }
            if s.len() <= 2 {
                (s.parse().ok()?, 0)
            } else {
                let (h, m) = s.split_at(s.len() - 2);
                (h.parse().ok()?, m.parse().ok()?)
            }
        };
        (h <= 23 && m <= 59).then_some((h, m))
    }

    /// Isti dan kao `day`, u zadani sat i minutu (sekunde na nulu, kao macOS DatePicker).
    pub fn at(day: DateTime<Local>, hour: u32, minute: u32) -> DateTime<Local> {
        Self::on_date(day.date_naive(), hour, minute).unwrap_or(day)
    }

    /// Kalendarski dan `date` u zadani sat i minutu (po datumu, ne +24 h — da prelazak na
    /// ljetno/zimsko vrijeme ne pomakne dan). Sat koji se ujesen ponavlja uzima prvi put, a
    /// vrijeme koje u proljetnom skoku ne postoji (2:30) pomiče sat naprijed — DST nikad ne
    /// daje None (inače se tiho gase auto-stop i podsjetnik, a upisano vrijeme postane dan).
    pub fn on_date(date: NaiveDate, hour: u32, minute: u32) -> Option<DateTime<Local>> {
        let naive = date.and_hms_opt(hour, minute, 0)?;
        naive
            .and_local_timezone(Local)
            .earliest()
            .or_else(|| (naive + Duration::hours(1)).and_local_timezone(Local).earliest())
    }

    /// Tumači vremena iz uređivača unosa ("9:30") za dan pregleda `day`:
    /// - vrijeme koje se nije promijenilo (isti HH:mm kao u `original`) vraća originalni
    ///   trenutak, sa sekundama — inače bi ispravak samo opisa pomaknuo unos za par sekundi
    ///   (pa spojio blokove i rezao susjede);
    /// - unos koji je već iza ponoći (u fileu prethodnog dana) i dalje se tumači iza ponoći;
    /// - kraj koji nije nakon početka je sutradan (…–00:00, 23:30–00:30), do `MAX_OVERNIGHT_HOURS`.
    pub fn edit_range(
        start_text: &str,
        end_text: &str,
        day: DateTime<Local>,
        original: Option<(DateTime<Local>, DateTime<Local>)>,
    ) -> (Option<DateTime<Local>>, Option<DateTime<Local>>) {
        let date = day.date_naive();
        let next_date = date.succ_opt().unwrap_or(date);
        let next_day = Self::on_date(next_date, 0, 0);
        let unchanged = |(h, m): (u32, u32), at: DateTime<Local>| at.hour() == h && at.minute() == m;

        let start = Self::parse_time(start_text).and_then(|t| match original {
            Some((o_start, _)) if unchanged(t, o_start) => Some(o_start),
            Some((o_start, o_end)) if next_day.is_some_and(|n| o_end > n) => {
                // Unos koji seže iza ponoći: bliži od dva kandidata originalnom početku
                // (00:10 za unos 23:30–00:30 je sutradan, 23:45 isti dan).
                let same = Self::on_date(date, t.0, t.1);
                let following = Self::on_date(next_date, t.0, t.1);
                let dist = |d: Option<DateTime<Local>>| d.map(|d| (d - o_start).num_seconds().abs());
                match (dist(same), dist(following)) {
                    (Some(a), Some(b)) if b < a => following,
                    (None, Some(_)) => following,
                    _ => same,
                }
            }
            _ => Self::on_date(date, t.0, t.1),
        });

        let end = Self::parse_time(end_text).and_then(|t| match original {
            Some((_, o_end)) if unchanged(t, o_end) => Some(o_end),
            _ => {
                let base = start.map_or(date, |s| s.date_naive());
                let e = Self::on_date(base, t.0, t.1)?;
                match start {
                    Some(s) if e <= s => {
                        let rolled = base.succ_opt().and_then(|next| Self::on_date(next, t.0, t.1));
                        match rolled {
                            Some(r) if r - s <= Duration::hours(MAX_OVERNIGHT_HOURS) => Some(r),
                            _ => Some(e),
                        }
                    }
                    _ => Some(e),
                }
            }
        });
        (start, end)
    }

    /// "04:32" — odbrojavanje do prompta / auto-stopa.
    pub fn countdown(seconds: f64) -> String {
        let total = seconds.round().max(0.0) as i64;
        format!("{:02}:{:02}", total / 60, total % 60)
    }

    /// "08:30" iz minuta u danu — za opise opcija u postavkama.
    pub fn clock(minutes_of_day: i32) -> String {
        let m = minutes_of_day.rem_euclid(1440);
        format!("{:02}:{:02}", m / 60, m % 60)
    }
}

// MARK: - CSS

/// Cijeli UI je tamni (crna podloga, žuti akcenti) — GTK tema korisnika se ne pita, kao
/// što macOS port forsira `.darkAqua`, a Windows crta svoje kontrole. Klase se koriste
/// kroz sve prozore, pa je stil na jednom mjestu.
pub fn css() -> String {
    format!(
        "
window.brand {{
    background-color: {black};
    color: #ffffff;
}}
.brand-card {{
    background-color: {black};
    border: 1px solid alpha({yellow}, 0.35);
    border-radius: 16px;
}}
.brand-panel {{
    background-color: alpha(#ffffff, 0.04);
    border: 1px solid alpha({yellow}, 0.3);
    border-radius: 20px;
}}
/* Boja teksta se nasljeđuje od `window.brand` (bijela) ili gumba (npr. crna na žutom) —
   globalni `label {{ color }}` bi pregazio boju svakog gumba. */
.muted {{ color: {gray}; font-size: 11px; }}
.muted-dim {{ color: alpha({gray}, 0.7); font-size: 10px; }}
.mono {{ font-family: monospace; }}
.muted-faint {{ color: alpha({gray}, 0.55); font-size: 10px; font-weight: 600; }}
.footnote {{ color: alpha({gray}, 0.5); font-size: 10px; }}
.accent {{ color: {yellow}; }}
/* Opisi opcija u postavkama (macOS `.caption` + `.secondary`, Windows 8 pt sivo). Prije
   `.warn`, da greška autostarta (`caption warn`) ostane narančasta. */
.caption {{ color: alpha({gray}, 0.7); font-size: 10px; }}
/* Upozorenje u ispravku unosa (macOS `Color.orange`). */
.warn {{ color: #ffa500; }}
/* Traka do auto-stopa — tanka žuta kapsula na blijedoj podlozi, kao na macOS-u. */
progressbar.accent > trough {{
    min-height: 4px; background: alpha(#ffffff, 0.12); background-image: none;
    border: none; border-radius: 999px;
}}
progressbar.accent > trough > progress {{
    min-height: 4px; background: {yellow}; background-image: none;
    border: none; border-radius: 999px; margin: 0;
}}
.heading {{ font-weight: 900; font-size: 13px; letter-spacing: 2px; }}
.heading-light {{ font-weight: 300; font-size: 13px; letter-spacing: 2px; color: {gray}; }}
.big-title {{ font-weight: 900; font-size: 20px; }}
.section-label {{
    font-weight: 900; font-size: 10px; letter-spacing: 1.5px; color: {gray};
}}
.brand-square {{ background-color: {yellow}; }}
.dot-on {{ background-color: {yellow}; border-radius: 999px; }}
.dot-off {{ background-color: alpha({gray}, 0.5); border-radius: 999px; }}
.dot-pause {{ background-color: #ffa028; border-radius: 999px; }}
.dot-away {{ background-color: #4a9bff; border-radius: 999px; }}

/* Gumbi — GTK default na crnoj podlozi ispadne nevidljiv, pa se stiliziraju ručno. */
button.yellow {{
    background: {yellow}; background-image: none;
    color: {black}; font-weight: bold;
    border: none; border-radius: 8px; padding: 8px 14px;
}}
button.yellow:hover {{ background: shade({yellow}, 1.08); }}
button.yellow:disabled {{ background: alpha({yellow}, 0.35); }}
/* Strelice ‹ › uz Start u podsjetniku (pomiču početak za jedan interval). */
button.step {{ padding: 8px 10px; font-size: 15px; }}
button.step:disabled {{ color: alpha({black}, 0.5); }}
button.outline {{
    background: transparent; background-image: none;
    color: {gray};
    border: 1px solid alpha(#ffffff, 0.2); border-radius: 8px; padding: 8px 14px;
}}
button.outline:hover {{ background: alpha(#ffffff, 0.06); }}
/* Odabrana opcija para (Grupirano/Kronološki, Rad/Pauza) — kao macOS segmented picker. */
button.outline:checked {{
    background: alpha({yellow}, 0.12); color: {yellow}; border-color: alpha({yellow}, 0.5);
}}
button.pill {{
    background: alpha({yellow}, 0.12); background-image: none;
    color: {yellow}; font-weight: 600;
    border: 1px solid alpha({yellow}, 0.5); border-radius: 8px; padding: 6px 12px;
}}
button.pill-red {{
    background: alpha(#ff5c4f, 0.12); background-image: none;
    color: #ff5c4f; font-weight: 600;
    border: 1px solid alpha(#ff5c4f, 0.5); border-radius: 8px; padding: 6px 12px;
}}
button.link {{
    background: none; background-image: none; border: none; box-shadow: none;
    color: {gray}; font-size: 11px; padding: 4px 6px;
    text-decoration: underline;
}}
button.link:hover {{ color: {yellow}; }}
button.skip {{
    background: transparent; background-image: none;
    color: {gray}; font-size: 11px; font-weight: 600;
    border: 1px solid alpha(#ffffff, 0.22); border-radius: 6px; padding: 5px 12px;
}}
button.skip:hover {{ background: alpha(#ffffff, 0.06); }}
button.split {{
    background: {black}; background-image: none;
    color: {gray};
    border: 1px solid alpha(#ffffff, 0.35); border-radius: 999px;
    padding: 0; min-width: 20px; min-height: 20px;
    font-size: 9px; font-weight: bold;
}}
button.split.on {{ color: {yellow}; border-color: {yellow}; }}
/* Strelice ‹ › za promjenu dana u pregledu — veliko polje za klik, ne samo znak. */
button.nav {{
    background: transparent; background-image: none; border: none; box-shadow: none;
    color: {yellow}; font-size: 18px; font-weight: bold;
    border-radius: 8px; padding: 0; min-width: 40px; min-height: 32px;
}}
button.nav:hover {{ background: alpha({yellow}, 0.12); }}
button.nav:disabled {{ color: alpha({yellow}, 0.3); background: transparent; }}

/* Kronološki pregled: cijeli red je gumb koji otvara uređivač. */
button.row {{
    background: alpha(#ffffff, 0.03); background-image: none; color: #ffffff;
    border: none; box-shadow: none; border-radius: 8px; padding: 7px 10px;
}}
button.row:hover {{ background: alpha(#ffffff, 0.08); }}
.edit-hint {{ color: alpha({gray}, 0.35); font-size: 12px; }}
button.row:hover .edit-hint {{ color: {yellow}; }}
/* Nezabilježeno vrijeme između unosa. */
button.gap {{
    background: transparent; background-image: none; box-shadow: none;
    border: 1px dashed alpha(#ffffff, 0.12); border-radius: 6px; padding: 4px 10px;
}}
button.gap:hover {{ background: alpha({yellow}, 0.08); border-color: alpha(#ffffff, 0.25); }}
.gap-text {{ color: alpha({gray}, 0.55); font-size: 10px; }}
button.gap:hover .gap-text {{ color: {yellow}; }}
.gap-hint {{ font-weight: 600; opacity: 0; }}
button.gap:hover .gap-hint {{ opacity: 1; }}
button.add {{
    background: transparent; background-image: none; box-shadow: none;
    color: {yellow}; font-weight: 600;
    border: 1px solid alpha({yellow}, 0.4); border-radius: 6px; padding: 7px 14px;
}}
button.add:hover {{ background: alpha({yellow}, 0.12); }}
button.danger {{
    background: alpha(#ff5c4f, 0.1); background-image: none; box-shadow: none;
    color: #ff5c4f; font-weight: 600;
    border: none; border-radius: 8px; padding: 8px 12px;
}}
button.danger:hover {{ background: alpha(#ff5c4f, 0.18); }}
.editor-card {{
    background: alpha(#ffffff, 0.06);
    border: 1px solid alpha({yellow}, 0.5); border-radius: 8px;
    padding: 12px;
}}

entry.brand {{
    background: alpha(#ffffff, 0.07); background-image: none;
    color: #ffffff; caret-color: {yellow};
    border: 1px solid alpha({yellow}, 0.25); border-radius: 10px;
    padding: 10px; font-size: 14px;
}}
entry.brand.big {{ font-size: 17px; padding: 14px; }}
/* Preskočeni period iz prijašnjeg prompta — blijeđa podloga od glavnog. */
entry.brand.carried {{ background: alpha(#ffffff, 0.04); }}
/* Polje `nastavljam s` u ručnom promptu — nije obavezno, pa je i vizualno tiše. */
entry.brand.next-up {{
    background: alpha(#ffffff, 0.05); border-color: alpha({yellow}, 0.18);
    font-size: 13px; padding: 9px;
}}
entry.brand.next-up.big {{ font-size: 15px; padding: 12px; }}
entry.brand:focus {{ border-color: alpha({yellow}, 0.8); }}
/* Uređivač unosa u pregledu — manja polja od prompta. */
entry.brand.compact {{ font-size: 13px; padding: 6px 9px; border-radius: 7px; }}
entry.brand.time {{
    font-family: monospace; font-size: 13px; font-weight: 600;
    padding: 6px; border-radius: 7px;
}}
entry.brand.invalid {{ border-color: #ffa500; }}

.kbd {{
    background: alpha(#ffffff, 0.12); border-radius: 4px;
    font-family: monospace; font-size: 10px; font-weight: bold;
    padding: 2px 5px; color: {gray};
}}
.row-card {{ background: alpha(#ffffff, 0.04); border-radius: 8px; }}
.row-card-dim {{ background: alpha(#ffffff, 0.03); border-radius: 6px; }}
.badge {{ background: alpha(#ffffff, 0.06); border-radius: 6px; padding: 5px 10px; }}
.pause-text {{ color: alpha({gray}, 0.6); font-style: italic; }}

/* Nativni widgeti (postavke, uređivač unosa) stiliziraju se do kraja, ne samo boja teksta —
   na svijetloj temi bez tamne varijante bi inače ostali svijetli (s plavim akcentom), a
   s bijelim tekstom naslijeđenim od `window.brand` i nečitljivi. */
notebook {{ border: none; background: {black}; }}
notebook > header {{
    background: {black}; border: none;
    box-shadow: inset 0 -1px alpha(#ffffff, 0.1);
}}
notebook > header > tabs > tab {{ color: {gray}; box-shadow: none; }}
notebook > header > tabs > tab:hover {{ color: #ffffff; box-shadow: inset 0 -2px alpha({yellow}, 0.35); }}
notebook > header > tabs > tab:checked {{ color: {yellow}; box-shadow: inset 0 -2px {yellow}; }}
notebook > stack {{ background: {black}; }}
scrolledwindow, viewport, listbox, list, row {{ background: transparent; }}
separator {{ background: alpha(#ffffff, 0.1); }}
checkbutton check {{ border-color: alpha({yellow}, 0.6); }}
checkbutton check:checked {{ background: {yellow}; color: {black}; }}

dropdown > button {{
    background: alpha(#ffffff, 0.07); background-image: none; box-shadow: none;
    color: #ffffff; border: 1px solid alpha(#ffffff, 0.2); border-radius: 8px;
}}
dropdown > button:hover {{ background: alpha(#ffffff, 0.11); }}
dropdown > button:checked {{ border-color: alpha({yellow}, 0.6); }}
popover > contents {{
    background: #1a1a1a; color: #ffffff;
    border: 1px solid alpha({yellow}, 0.3); border-radius: 10px; box-shadow: none;
}}
popover > arrow {{ background: #1a1a1a; border-color: alpha({yellow}, 0.3); }}
popover listview, popover row {{ background: transparent; color: #ffffff; }}
popover row:hover {{ background: alpha(#ffffff, 0.08); }}
popover row:selected {{ background: alpha({yellow}, 0.12); color: {yellow}; }}

spinbutton {{
    background: alpha(#ffffff, 0.07); background-image: none; box-shadow: none;
    color: #ffffff; border: 1px solid alpha(#ffffff, 0.2); border-radius: 8px;
}}
spinbutton:focus-within {{ border-color: alpha({yellow}, 0.8); }}
spinbutton > text {{
    background: transparent; background-image: none; box-shadow: none; border: none;
    color: #ffffff; caret-color: {yellow};
}}
spinbutton > button {{
    background: transparent; background-image: none; box-shadow: none;
    color: {gray}; border: none;
}}
spinbutton > button:hover {{ background: alpha(#ffffff, 0.06); color: {yellow}; }}

switch {{
    background: alpha(#ffffff, 0.15); background-image: none; box-shadow: none;
    border: none; color: transparent;
}}
switch:checked {{ background: {yellow}; }}
switch > slider {{ background: #ffffff; background-image: none; border: none; box-shadow: none; }}

selection {{ background: alpha({yellow}, 0.35); color: #ffffff; }}
",
        black = BLACK,
        yellow = YELLOW,
        gray = GRAY,
    )
}

#[cfg(test)]
mod tests {
    use super::Fmt;
    use chrono::{DateTime, Local, TimeZone};

    /// Zone bez DST-a ovo prolaze trivijalno; pravi slučaj: `TZ=Europe/Zagreb cargo test`.
    #[test]
    fn on_date_survives_dst_switches() {
        use chrono::{NaiveDate, Timelike};
        let spring = NaiveDate::from_ymd_opt(2026, 3, 29).unwrap();
        let autumn = NaiveDate::from_ymd_opt(2026, 10, 25).unwrap();
        // 2:30 u proljetnom skoku ne postoji → sat naprijed, ali isti dan.
        let gap = Fmt::on_date(spring, 2, 30).unwrap();
        assert_eq!((gap.date_naive(), gap.minute()), (spring, 30));
        // Sat koji se ujesen ponavlja → prvi put (raniji trenutak).
        let repeated = Fmt::on_date(autumn, 2, 30).unwrap();
        assert_eq!((repeated.date_naive(), repeated.hour(), repeated.minute()), (autumn, 2, 30));
        // Kraj dana od 23 i 25 sati je i dalje 23:59 tog dana.
        for day in [spring, autumn] {
            let end = Fmt::at(Fmt::on_date(day, 12, 0).unwrap(), 23, 59);
            assert_eq!((end.date_naive(), end.hour(), end.minute()), (day, 23, 59));
        }
    }

    #[test]
    fn blocks_use_croatian_plural() {
        for (n, expected) in [
            (1, "1 blok"),
            (2, "2 bloka"),
            (4, "4 bloka"),
            (5, "5 blokova"),
            (11, "11 blokova"),
            (12, "12 blokova"),
            (14, "14 blokova"),
            (21, "21 blok"),
            (22, "22 bloka"),
            (25, "25 blokova"),
            (111, "111 blokova"),
            (112, "112 blokova"),
            (122, "122 bloka"),
        ] {
            assert_eq!(Fmt::blocks(n), expected);
        }
    }

    #[test]
    fn parse_time_accepts_common_forms() {
        for (input, expected) in [
            ("9", Some((9, 0))),
            ("930", Some((9, 30))),
            ("0930", Some((9, 30))),
            ("9:30", Some((9, 30))),
            (" 9.30 ", Some((9, 30))),
            ("9,30", Some((9, 30))),
            ("23:59", Some((23, 59))),
            ("24:00", None),
            ("9:3", None),
            ("960", None),
            ("abc", None),
            ("", None),
        ] {
            assert_eq!(Fmt::parse_time(input), expected, "{input:?}");
        }
    }

    fn at(day: u32, h: u32, m: u32, s: u32) -> DateTime<Local> {
        Local.with_ymd_and_hms(2026, 10, day, h, m, s).unwrap()
    }

    #[test]
    fn edit_range_keeps_unchanged_times() {
        let day = at(15, 0, 0, 0);
        let original = Some((at(15, 9, 14, 37), at(15, 10, 2, 5)));
        let range = |s, e| Fmt::edit_range(s, e, day, original);
        assert_eq!(range("09:14", "10:02"), (Some(at(15, 9, 14, 37)), Some(at(15, 10, 2, 5))));
        assert_eq!(range("914", "10:02"), (Some(at(15, 9, 14, 37)), Some(at(15, 10, 2, 5))));
        assert_eq!(range("09:00", "10:02"), (Some(at(15, 9, 0, 0)), Some(at(15, 10, 2, 5))));
        assert_eq!(Fmt::edit_range("x", "25:00", day, None), (None, None));
    }

    #[test]
    fn edit_range_around_midnight() {
        let day = at(15, 0, 0, 0);
        let r = |s, e, o| Fmt::edit_range(s, e, day, o);
        // Kraj u ponoć — zadržan i upisan.
        assert_eq!(r("23:00", "00:00", Some((at(15, 23, 0, 0), at(16, 0, 0, 0)))), (Some(at(15, 23, 0, 0)), Some(at(16, 0, 0, 0))));
        assert_eq!(r("22:00", "0000", Some((at(15, 21, 0, 0), at(15, 22, 0, 0)))), (Some(at(15, 22, 0, 0)), Some(at(16, 0, 0, 0))));
        // Preko ponoći, a tipfeler ostaje nevaljan.
        assert_eq!(r("23:30", "00:30", None), (Some(at(15, 23, 30, 0)), Some(at(16, 0, 30, 0))));
        assert_eq!(r("10:00", "09:00", None), (Some(at(15, 10, 0, 0)), Some(at(15, 9, 0, 0))));
        // Unos iza ponoći u jučerašnjem fileu ostaje iza ponoći.
        let after = Some((at(16, 0, 30, 0), at(16, 1, 0, 0)));
        assert_eq!(r("00:15", "01:00", after), (Some(at(16, 0, 15, 0)), Some(at(16, 1, 0, 0))));
        assert_eq!(r("23:50", "01:00", after), (Some(at(15, 23, 50, 0)), Some(at(16, 1, 0, 0))));
        let crossing = Some((at(15, 23, 30, 0), at(16, 0, 30, 0)));
        assert_eq!(r("00:10", "00:30", crossing), (Some(at(16, 0, 10, 0)), Some(at(16, 0, 30, 0))));
        assert_eq!(r("23:45", "00:30", crossing), (Some(at(15, 23, 45, 0)), Some(at(16, 0, 30, 0))));
        // Dnevni unos ostaje u danu pregleda.
        assert_eq!(r("00:30", "14:00", Some((at(15, 13, 0, 0), at(15, 14, 0, 0)))), (Some(at(15, 0, 30, 0)), Some(at(15, 14, 0, 0))));
    }
}
