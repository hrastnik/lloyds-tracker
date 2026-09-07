use std::cell::RefCell;
use std::collections::{BTreeSet, HashMap, HashSet};
use std::rc::Rc;

use chrono::{DateTime, Local};
use gtk4::gdk::Key;
use gtk4::glib::Propagation;
use gtk4::prelude::*;
use gtk4::{
    Align, Application, Box as GtkBox, Button, DrawingArea, Entry, EventControllerKey, Fixed,
    Label, Orientation, Overlay, Separator, Window,
};

use crate::engine::grid_boundaries;
use crate::models::{PromptRequest, PromptResult, PromptSegment, PromptStyle};
use crate::theme::{Fmt, YELLOW_RGB};
use crate::ui;

/// Ključ polja "nastavljam s" u `texts` — dijeli ga s opisima segmenata, pa listanje
/// povijesti (↑/↓) radi i tamo. Nikad se ne poklapa s pravim vremenom bloka (macOS
/// `Date.distantFuture`).
const NEXT_UP_KEY: i64 = i64::MAX;

/// Prompt "Na čemu radiš?" — floating panel ili preko cijelog ekrana.
///
/// Razlika prema macOS-u: GTK4 nema API za pozicioniranje prozora (ni na Waylandu ni na
/// X11), pa floating panel ne sjeda u gornji desni kut nego ga smješta window manager.
/// Fullscreen stil radi jednako na oba porta i zato je preporučen na Linuxu.
pub struct PromptWindow {
    window: Window,
    request: PromptRequest,
    style: PromptStyle,
    history: Vec<String>,
    state: RefCell<PromptState>,
    /// Raspon u zaglavlju; uz preskočene redove ga zamjenjuje `period_label`.
    range_label: Label,
    /// "PERIOD hh:mm–hh:mm" nad glavnim periodom (samo kad ima preskočenih redova).
    period_label: Label,
    /// Glavni period — sakrije se kad je degeneriran a ima preskočenih redova.
    main_box: GtkBox,
    segments_box: GtkBox,
    bar_overlay: Overlay,
    bar_area: DrawingArea,
    bar_fixed: Fixed,
    scissors_hint: GtkBox,
    split_buttons: RefCell<Vec<(i64, Button)>>,
    /// Polja po ključu segmenta (+ `NEXT_UP_KEY`) — za fokus i listanje povijesti.
    fields: RefCell<HashMap<i64, Entry>>,
    on_submit: Box<dyn Fn(PromptResult)>,
    on_snooze: Box<dyn Fn()>,
}

struct PromptState {
    start: DateTime<Local>,
    /// Kraj perioda se uživo produžuje dok prompt čeka odgovor (skupno vrijeme).
    end: DateTime<Local>,
    /// Točke na kojima je period razdvojen; ključ je unix timestamp granice.
    splits: BTreeSet<i64>,
    /// Tekst po segmentu, ključ = početak segmenta. Preživljava spajanje/razdvajanje.
    texts: HashMap<i64, String>,
    drafts: HashMap<i64, String>,
    history_indices: HashMap<i64, i32>,
    focused: Option<i64>,
}

impl PromptWindow {
    pub fn new(
        app: &Application,
        request: PromptRequest,
        style: PromptStyle,
        history: Vec<String>,
        on_submit: impl Fn(PromptResult) + 'static,
        on_snooze: impl Fn() + 'static,
    ) -> Rc<Self> {
        let start = request.start;
        let end = request.end.unwrap_or_else(Local::now).max(start);

        // Pre-fill dobiva samo glavni period; preskočeni redovi ostaju prazni jer su
        // svjesno ostavljeni za kasnije — Enter ih ne smije napuniti zadnjim unosom.
        let mut texts = HashMap::new();
        texts.insert(start.timestamp(), history.first().cloned().unwrap_or_default());

        // Fokus ide na prvi red — s preskočenim periodima to je najstariji od njih.
        let first_key = request
            .carried
            .iter()
            .map(|span| span.start)
            .min()
            .unwrap_or(start)
            .timestamp();

        let content = ui::vbox(14);

        let prompt = Rc::new(PromptWindow {
            window: Window::builder().application(app).title("Lloyds Tracker").build(),
            request,
            style,
            history,
            state: RefCell::new(PromptState {
                start,
                end,
                splits: BTreeSet::new(),
                texts,
                drafts: HashMap::new(),
                history_indices: HashMap::new(),
                focused: Some(first_key),
            }),
            range_label: ui::label("", &["mono", "muted"]),
            period_label: ui::label("", &["section-label"]),
            main_box: ui::vbox(14),
            segments_box: ui::vbox(8),
            bar_overlay: Overlay::new(),
            bar_area: DrawingArea::new(),
            bar_fixed: Fixed::new(),
            scissors_hint: ui::hint("✂", "razbij period"),
            split_buttons: RefCell::new(Vec::new()),
            fields: RefCell::new(HashMap::new()),
            on_submit: Box::new(on_submit),
            on_snooze: Box::new(on_snooze),
        });

        prompt.build(&content);
        prompt.rebuild();

        // Fade + odgođeno preuzimanje tipkovnice: pop-up koji iskoči dok tipkaš u drugoj
        // aplikaciji ne smije presresti ostatak rečenice (ni pregaziti pre-fill).
        {
            let me = Rc::downgrade(&prompt);
            ui::PanelFade::appear_with(&prompt.window, move || {
                if let Some(me) = me.upgrade() {
                    me.focus_current_field();
                }
            });
        }
        prompt
    }

    fn big(&self) -> bool {
        self.style == PromptStyle::Fullscreen
    }

    fn build(self: &Rc<Self>, content: &GtkBox) {
        let big = self.big();

        // Zaglavlje: naslov + raspon perioda.
        let header = ui::hbox(8);
        header.append(&ui::swatch(9, "dot-on"));
        let title = ui::label("NA ČEMU RADIŠ?", &["big-title"]);
        if !big {
            title.remove_css_class("big-title");
            title.add_css_class("heading");
        }
        header.append(&title);
        header.append(&ui::spacer());
        header.append(&self.range_label);
        content.append(&header);

        if let Some(note) = &self.request.note {
            content.append(&ui::wrapped(note, &["accent"], if big { 70 } else { 55 }));
        }

        // Preskočeni periodi iz prijašnjih promptova su svoja skupina, iznad crte —
        // traka blokova (`✂`) i pre-fill vrijede samo za glavni period pod njom.
        if !self.request.carried.is_empty() {
            let section = ui::vbox(8);
            section.append(&ui::label(
                "PRESKOČENO PRIJE — POPUNI ILI OSTAVI ZA KASNIJE",
                &["section-label"],
            ));
            for (from, to) in self.carried_segments() {
                section.append(&self.segment_row(from, to, false, big, true));
            }
            content.append(&section);
            content.append(&Separator::new(Orientation::Horizontal));
        }

        // Uz preskočene redove period piše na svojoj sekciji, da se ne čita kao da
        // vrijedi za cijeli prompt.
        self.period_label.set_visible(!self.request.carried.is_empty());
        self.main_box.append(&self.period_label);

        // Traka blokova: cairo crta blokove i vremena, a škarice su pravi gumbi u Fixedu
        // iznad nje.
        self.bar_area.set_content_height(34);
        self.bar_area.set_hexpand(true);
        {
            let me = Rc::downgrade(self);
            self.bar_area.set_draw_func(move |_, cr, width, _| {
                if let Some(me) = me.upgrade() {
                    me.draw_bar(cr, width as f64);
                }
            });
        }
        {
            let me = Rc::downgrade(self);
            self.bar_area.connect_resize(move |_, _, _| {
                if let Some(me) = me.upgrade() {
                    me.reposition_split_buttons();
                }
            });
        }
        self.bar_overlay.set_child(Some(&self.bar_area));
        self.bar_fixed.set_valign(Align::Start);
        self.bar_overlay.add_overlay(&self.bar_fixed);
        self.main_box.append(&self.bar_overlay);
        self.main_box.append(&self.segments_box);
        content.append(&self.main_box);

        if self.request.is_manual {
            content.append(&self.next_up_field(big));
        }

        // Dva reda: gore tipkovnica, dolje akcije. U jednom redu se na 420 px natpisi
        // lome u dva reda.
        let footer = ui::vbox(10);
        let hints = ui::hbox(12);
        hints.append(&ui::hint("↑↓", "povijest"));
        hints.append(&ui::hint("⏎", "spremi"));
        hints.append(&ui::hint("esc", "preskoči"));
        hints.append(&self.scissors_hint);
        hints.append(&ui::spacer());
        footer.append(&hints);

        let actions = ui::hbox(10);
        actions.append(&ui::spacer());
        if self.request.allow_snooze {
            let me = Rc::downgrade(self);
            actions.append(&ui::button("Odgodi 5 min", &["link"], move || {
                if let Some(me) = me.upgrade() {
                    me.window.close();
                    (me.on_snooze)();
                }
            }));
        }
        let skip = {
            let me = Rc::downgrade(self);
            ui::button("Preskoči", &["skip"], move || {
                if let Some(me) = me.upgrade() {
                    me.skip();
                }
            })
        };
        skip.set_tooltip_text(Some("Ne bilježi ništa — period se vraća u sljedeći prompt"));
        actions.append(&skip);
        footer.append(&actions);
        content.append(&footer);

        ui::pad(content, if big { 28 } else { 18 });
        content.set_size_request(if big { 560 } else { 420 }, -1);

        // Esc na razini prozora — preskače cijeli prompt.
        let key = EventControllerKey::new();
        {
            let me = Rc::downgrade(self);
            key.connect_key_pressed(move |_, keyval, _, _| {
                if keyval == Key::Escape {
                    if let Some(me) = me.upgrade() {
                        me.skip();
                    }
                    return Propagation::Stop;
                }
                Propagation::Proceed
            });
        }
        self.window.add_controller(key);

        self.window.add_css_class("brand");
        match self.style {
            PromptStyle::Floating => {
                self.window.set_decorated(false);
                self.window.set_resizable(false);
                content.add_css_class("brand-card");
                self.window.set_child(Some(content));
            }
            PromptStyle::Fullscreen => {
                content.add_css_class("brand-panel");
                content.set_halign(Align::Center);
                content.set_valign(Align::Center);

                let outer = ui::vbox(28);
                outer.set_halign(Align::Center);
                outer.set_valign(Align::Center);
                outer.set_vexpand(true);

                let brand = ui::hbox(8);
                brand.set_halign(Align::Center);
                brand.append(&ui::swatch(26, "brand-square"));
                brand.append(&ui::label("LLOYDS TRACKER", &["heading"]));
                outer.append(&brand);
                outer.append(content);
                outer.append(&ui::label(
                    "Upiši što radiš i stisni ⏎ — ili preskoči (esc), pa te period čeka u sljedećem promptu.",
                    &["muted-dim"],
                ));

                self.window.set_child(Some(&outer));
                self.window.fullscreen();
            }
        }
    }

    // MARK: - Segmenti

    /// Unutarnje točke 5-min mreže na kojima se period može razdvojiti.
    fn boundaries(&self) -> Vec<DateTime<Local>> {
        let s = self.state.borrow();
        grid_boundaries(s.start, s.end)
    }

    /// Preskočeni periodi iz prijašnjih promptova, najstariji prvi.
    fn carried_segments(&self) -> Vec<(DateTime<Local>, DateTime<Local>)> {
        let mut spans: Vec<_> = self
            .request
            .carried
            .iter()
            .map(|span| (span.start, span.end))
            .collect();
        spans.sort_by_key(|(start, _)| *start);
        spans
    }

    /// Spojeni nizovi blokova glavnog perioda s jednim opisom.
    fn main_segments(&self) -> Vec<(DateTime<Local>, DateTime<Local>)> {
        let s = self.state.borrow();
        let mut result = Vec::new();
        let mut from = s.start;
        for b in grid_boundaries(s.start, s.end) {
            if s.splits.contains(&b.timestamp()) {
                result.push((from, b));
                from = b;
            }
        }
        result.push((from, s.end));
        result
    }

    /// Degeneriran glavni period (npr. ručni prompt odmah nakon odgovora, ili dan zatvoren
    /// točno na granici) skriva se ako ima preskočenih redova — inače ostaje kao jedini red.
    fn shows_main_period(&self) -> bool {
        let s = self.state.borrow();
        s.end > s.start || self.request.carried.is_empty()
    }

    fn segments(&self) -> Vec<(DateTime<Local>, DateTime<Local>)> {
        let mut all = self.carried_segments();
        if self.shows_main_period() {
            all.extend(self.main_segments());
        }
        all
    }

    // MARK: - Ponovna izgradnja

    /// Poziva se kod razdvajanja/spajanja blokova i kod produženja perioda. Preskočeni
    /// redovi se ne prekrajaju — oni se ne mijenjaju dok je prompt otvoren.
    fn rebuild(self: &Rc<Self>) {
        let big = self.big();
        let shows_main = self.shows_main_period();
        let main = self.main_segments();
        let boundaries = if shows_main { self.boundaries() } else { Vec::new() };
        let (start, end) = {
            let s = self.state.borrow();
            (s.start, s.end)
        };

        self.range_label
            .set_text(&format!("{} – {}", Fmt::hhmm(start), Fmt::hhmm(end)));
        self.range_label
            .set_visible(shows_main && self.request.carried.is_empty());
        self.period_label
            .set_text(&format!("PERIOD {}–{}", Fmt::hhmm(start), Fmt::hhmm(end)));
        self.main_box.set_visible(shows_main);
        // Traka je vidljiva samo kad period uopće ima gdje puknuti.
        self.bar_overlay.set_visible(!boundaries.is_empty());
        self.scissors_hint
            .set_visible(main.len() == 1 && shows_main && !boundaries.is_empty());

        // Škarice.
        for (_, button) in self.split_buttons.borrow_mut().drain(..) {
            self.bar_fixed.remove(&button);
        }
        for b in &boundaries {
            let key = b.timestamp();
            let is_split = self.state.borrow().splits.contains(&key);
            let button = Button::with_label(if is_split { "×" } else { "✂" });
            button.add_css_class("split");
            if is_split {
                button.add_css_class("on");
            }
            button.set_tooltip_text(Some(&if is_split {
                "Spoji blokove".to_string()
            } else {
                format!("Razdvoji u {}", Fmt::hhmm(*b))
            }));
            let me = Rc::downgrade(self);
            button.connect_clicked(move |_| {
                if let Some(me) = me.upgrade() {
                    me.toggle_split(key);
                }
            });
            self.bar_fixed.put(&button, 0.0, 0.0);
            self.split_buttons.borrow_mut().push((key, button));
        }
        self.reposition_split_buttons();
        self.bar_area.queue_draw();

        // Redovi glavnog perioda; polja preskočenih redova i "nastavljam s" ostaju.
        while let Some(child) = self.segments_box.first_child() {
            self.segments_box.remove(&child);
        }
        let keep: HashSet<i64> = self
            .carried_segments()
            .iter()
            .map(|(from, _)| from.timestamp())
            .chain(std::iter::once(NEXT_UP_KEY))
            .collect();
        self.fields.borrow_mut().retain(|key, _| keep.contains(key));

        // Nerazbijen period nosi vrijeme u naslovu (ili na sekciji), pa se u redu ne
        // ponavlja.
        let single = main.len() == 1;
        for (from, to) in &main {
            let row = self.segment_row(*from, *to, single, big, false);
            self.segments_box.append(&row);
        }

        self.focus_current_field();
    }

    /// Fokus na zapamćeni segment (ili prvi red, ako je taj u međuvremenu otpao).
    fn focus_current_field(&self) {
        let key = self.state.borrow().focused;
        let entry = key
            .and_then(|key| self.entry_for(key))
            .or_else(|| self.segments().first().and_then(|(from, _)| self.entry_for(from.timestamp())));
        if let Some(entry) = entry {
            entry.grab_focus();
            entry.set_position(-1);
        }
    }

    fn reposition_split_buttons(&self) {
        let width = self.bar_area.width() as f64;
        if width <= 0.0 {
            return;
        }
        let (start, end) = {
            let s = self.state.borrow();
            (s.start, s.end)
        };
        let total = ((end - start).num_seconds() as f64).max(1.0);
        for (key, button) in self.split_buttons.borrow().iter() {
            let offset = (*key - start.timestamp()) as f64;
            let x = offset / total * width;
            self.bar_fixed.move_(button, x - 10.0, 0.0);
        }
    }

    fn entry_for(&self, key: i64) -> Option<Entry> {
        self.fields.borrow().get(&key).cloned()
    }

    fn segment_row(
        self: &Rc<Self>,
        from: DateTime<Local>,
        to: DateTime<Local>,
        single: bool,
        big: bool,
        carried: bool,
    ) -> GtkBox {
        let key = from.timestamp();
        let row = ui::hbox(8);

        if !single {
            let text = if carried {
                format!("↩ {}–{}", Fmt::hhmm(from), Fmt::hhmm(to))
            } else {
                format!("{}–{}", Fmt::hhmm(from), Fmt::hhmm(to))
            };
            let time = ui::label(&text, &["mono", if carried { "muted-faint" } else { "muted" }]);
            time.set_width_chars(if carried {
                13
            } else if big {
                12
            } else {
                11
            });
            if carried {
                time.set_tooltip_text(Some("Preskočeni period iz prijašnjeg prompta"));
            }
            row.append(&time);
        }

        let entry = Entry::new();
        entry.add_css_class("brand");
        if big {
            entry.add_css_class("big");
        }
        if carried {
            entry.add_css_class("carried");
        }
        entry.set_hexpand(true);
        entry.set_placeholder_text(Some(if carried {
            "preskočeno — upiši ili ostavi prazno"
        } else {
            "npr. Projekt X — opis zadatka"
        }));
        self.wire_field(&entry, key);

        row.append(&entry);
        row
    }

    /// Ručni prompt: čime korisnik nastavlja. Ne bilježi se kao unos — samo pre-fillava
    /// sljedeći prompt, pa se prebacivanje na drugi projekt zapiše u jednom koraku.
    fn next_up_field(self: &Rc<Self>, big: bool) -> GtkBox {
        let box_ = ui::vbox(6);
        box_.append(&ui::label("NASTAVLJAM S — NIJE OBAVEZNO", &["section-label"]));

        let entry = Entry::new();
        entry.add_css_class("brand");
        entry.add_css_class("next-up");
        if big {
            entry.add_css_class("big");
        }
        entry.set_hexpand(true);
        entry.set_placeholder_text(Some("npr. Projekt B — hitni fix"));
        self.wire_field(&entry, NEXT_UP_KEY);
        box_.append(&entry);

        box_.append(&ui::label("Sljedeći prompt kreće s ovim opisom.", &["footnote"]));
        box_
    }

    /// Tekst se čuva na svaku promjenu, da preživi razdvajanje/spajanje i produženje.
    fn wire_field(self: &Rc<Self>, entry: &Entry, key: i64) {
        entry.set_text(&self.state.borrow().texts.get(&key).cloned().unwrap_or_default());
        {
            let me = Rc::downgrade(self);
            entry.connect_changed(move |e| {
                if let Some(me) = me.upgrade() {
                    me.state.borrow_mut().texts.insert(key, e.text().to_string());
                }
            });
        }
        {
            let me = Rc::downgrade(self);
            entry.connect_activate(move |_| {
                if let Some(me) = me.upgrade() {
                    me.submit();
                }
            });
        }
        {
            let me = Rc::downgrade(self);
            entry.connect_has_focus_notify(move |e| {
                if e.has_focus() {
                    if let Some(me) = me.upgrade() {
                        me.state.borrow_mut().focused = Some(key);
                        me.bar_area.queue_draw();
                    }
                }
            });
        }
        let key_controller = EventControllerKey::new();
        {
            let me = Rc::downgrade(self);
            key_controller.connect_key_pressed(move |_, keyval, _, _| {
                let Some(me) = me.upgrade() else { return Propagation::Proceed };
                match keyval {
                    Key::Up => {
                        me.cycle_history(key, true);
                        Propagation::Stop
                    }
                    Key::Down => {
                        me.cycle_history(key, false);
                        Propagation::Stop
                    }
                    _ => Propagation::Proceed,
                }
            });
        }
        entry.add_controller(key_controller);
        self.fields.borrow_mut().insert(key, entry.clone());
    }

    // MARK: - Traka blokova

    fn draw_bar(&self, cr: &gtk4::cairo::Context, width: f64) {
        let (start, end, focused) = {
            let s = self.state.borrow();
            (s.start, s.end, s.focused)
        };
        let total = ((end - start).num_seconds() as f64).max(1.0);
        let segments = self.main_segments();
        let single = segments.len() == 1;

        for (from, to) in &segments {
            let x = (*from - start).num_seconds() as f64 / total * width;
            let w = (*to - *from).num_seconds() as f64 / total * width;
            let active = single || focused == Some(from.timestamp());
            cr.set_source_rgba(YELLOW_RGB.0, YELLOW_RGB.1, YELLOW_RGB.2, if active { 0.85 } else { 0.4 });
            rounded(cr, x + 2.0, 0.0, (w - 4.0).max(6.0), 20.0, 6.0);
            let _ = cr.fill();
        }

        // Vremena granica ispod trake.
        cr.select_font_face("monospace", gtk4::cairo::FontSlant::Normal, gtk4::cairo::FontWeight::Normal);
        cr.set_font_size(9.0);
        let splits = &self.state.borrow().splits;
        for b in grid_boundaries(start, end) {
            let x = (b - start).num_seconds() as f64 / total * width;
            let text = Fmt::hhmm(b);
            let extents = cr.text_extents(&text).map(|e| e.width()).unwrap_or(20.0);
            if splits.contains(&b.timestamp()) {
                cr.set_source_rgba(YELLOW_RGB.0, YELLOW_RGB.1, YELLOW_RGB.2, 1.0);
            } else {
                cr.set_source_rgba(0.77, 0.77, 0.77, 0.55);
            }
            cr.move_to(x - extents / 2.0, 32.0);
            let _ = cr.show_text(&text);
        }
    }

    // MARK: - Akcije

    fn toggle_split(self: &Rc<Self>, key: i64) {
        let inserting = !self.state.borrow().splits.contains(&key);
        if inserting {
            let mut s = self.state.borrow_mut();
            s.splits.insert(key);
            s.texts.entry(key).or_default();
            s.focused = Some(key);
        } else {
            self.state.borrow_mut().splits.remove(&key);
            let segments = self.main_segments();
            let target = segments
                .iter()
                .rev()
                .find(|(from, _)| from.timestamp() <= key)
                .map(|(from, _)| from.timestamp())
                .unwrap_or_else(|| self.state.borrow().start.timestamp());
            self.state.borrow_mut().focused = Some(target);
        }
        self.rebuild();
    }

    fn cycle_history(self: &Rc<Self>, key: i64, older: bool) {
        if self.history.is_empty() {
            return;
        }
        let new_text = {
            let mut s = self.state.borrow_mut();
            if !s.history_indices.contains_key(&key) {
                let draft = s.texts.get(&key).cloned().unwrap_or_default();
                s.drafts.insert(key, draft);
            }
            let mut idx = s.history_indices.get(&key).copied().unwrap_or(-1);
            idx += if older { 1 } else { -1 };
            if idx < 0 {
                s.history_indices.remove(&key);
                s.drafts.get(&key).cloned().unwrap_or_default()
            } else {
                let idx = idx.min(self.history.len() as i32 - 1);
                s.history_indices.insert(key, idx);
                self.history[idx as usize].clone()
            }
        };
        if let Some(entry) = self.entry_for(key) {
            entry.set_text(&new_text);
            entry.set_position(-1);
        }
    }

    /// Odgovor nije obavezan: segmenti bez teksta su preskočeni i engine ih vraća u
    /// sljedeći prompt.
    fn submit(self: &Rc<Self>) {
        self.finish(false);
    }

    fn skip(self: &Rc<Self>) {
        self.finish(true);
    }

    fn finish(self: &Rc<Self>, skip_all: bool) {
        let texts = self.state.borrow().texts.clone();
        let segments = self
            .segments()
            .into_iter()
            .map(|(start, end)| PromptSegment {
                start,
                end,
                text: if skip_all {
                    String::new()
                } else {
                    texts.get(&start.timestamp()).cloned().unwrap_or_default()
                },
            })
            .collect();
        // "Nastavljam s" vrijedi i kad se period preskoči — prebacivanje na drugi projekt
        // je jedini razlog zašto je to polje tamo.
        let next_up = self
            .request
            .is_manual
            .then(|| texts.get(&NEXT_UP_KEY).cloned().unwrap_or_default());

        self.window.close();
        (self.on_submit)(PromptResult { segments, next_up });
    }

    /// Produži period vidljivog prompta do nove granice (skupno vrijeme). Upisani tekst i
    /// podjele ostaju — mijenja se samo kraj i, po potrebi, mreža granica.
    pub fn extend(self: &Rc<Self>, end: DateTime<Local>) {
        {
            let mut s = self.state.borrow_mut();
            s.end = end.max(s.start);
        }
        self.rebuild();
    }

    /// Vraća već otvoreni prompt u prvi plan — "Zapiši sada" dok prompt visi.
    pub fn focus(&self) {
        self.window.present();
        self.focus_current_field();
    }

    pub fn close(&self) {
        self.window.close();
    }
}

fn rounded(cr: &gtk4::cairo::Context, x: f64, y: f64, w: f64, h: f64, r: f64) {
    use std::f64::consts::PI;
    let r = r.min(w / 2.0).min(h / 2.0);
    cr.new_sub_path();
    cr.arc(x + w - r, y + r, r, -PI / 2.0, 0.0);
    cr.arc(x + w - r, y + h - r, r, 0.0, PI / 2.0);
    cr.arc(x + r, y + h - r, r, PI / 2.0, PI);
    cr.arc(x + r, y + r, r, PI, 1.5 * PI);
    cr.close_path();
}
