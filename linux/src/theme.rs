use chrono::{DateTime, Datelike, Local, Timelike};

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
label {{ color: #ffffff; }}
.muted {{ color: {gray}; font-size: 11px; }}
.muted-dim {{ color: alpha({gray}, 0.7); font-size: 10px; }}
.mono {{ font-family: monospace; }}
.muted-faint {{ color: alpha({gray}, 0.55); font-size: 10px; font-weight: 600; }}
.footnote {{ color: alpha({gray}, 0.5); font-size: 10px; }}
.accent {{ color: {yellow}; }}
/* Upozorenje u ispravku unosa (macOS `Color.orange`). */
.warn {{ color: #ffa500; }}
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
button.outline {{
    background: transparent; background-image: none;
    color: {gray};
    border: 1px solid alpha(#ffffff, 0.2); border-radius: 8px; padding: 8px 14px;
}}
button.outline:hover {{ background: alpha(#ffffff, 0.06); }}
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

.kbd {{
    background: alpha(#ffffff, 0.12); border-radius: 4px;
    font-family: monospace; font-size: 10px; font-weight: bold;
    padding: 2px 5px; color: {gray};
}}
.row-card {{ background: alpha(#ffffff, 0.04); border-radius: 8px; }}
.row-card-dim {{ background: alpha(#ffffff, 0.03); border-radius: 6px; }}
.badge {{ background: alpha(#ffffff, 0.06); border-radius: 6px; padding: 5px 10px; }}
.pause-text {{ color: alpha({gray}, 0.6); font-style: italic; }}

/* Postavke koriste nativni Notebook — samo mu uskladimo podlogu. */
notebook > header {{ background: {black}; }}
notebook > header > tabs > tab {{ color: {gray}; }}
notebook > header > tabs > tab:checked {{ color: {yellow}; }}
notebook > stack {{ background: {black}; }}
scrolledwindow, viewport, listbox, list, row {{ background: transparent; }}
separator {{ background: alpha(#ffffff, 0.1); }}
checkbutton check {{ border-color: alpha({yellow}, 0.6); }}
checkbutton check:checked {{ background: {yellow}; color: {black}; }}
",
        black = BLACK,
        yellow = YELLOW,
        gray = GRAY,
    )
}
