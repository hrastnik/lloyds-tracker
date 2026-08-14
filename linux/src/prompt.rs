use std::cell::RefCell;
use std::collections::{BTreeSet, HashMap};
use std::rc::Rc;

use chrono::{DateTime, Local};
use gtk4::prelude::*;
use gtk4::{
    Align, Application, Box as GtkBox, Button, DrawingArea, Entry, EventControllerKey, Fixed,
    Overlay, Window,
};
use gtk4::glib::Propagation;
use gtk4::gdk::Key;

use crate::engine::grid_boundaries;
use crate::models::{PromptRequest, PromptSegment, PromptStyle};
use crate::theme::{Fmt, YELLOW_RGB};
use crate::ui;

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
    segments_box: GtkBox,
    bar_area: DrawingArea,
    bar_fixed: Fixed,
    split_buttons: RefCell<Vec<(i64, Button)>>,
    on_submit: Box<dyn Fn(Vec<PromptSegment>)>,
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
        on_submit: impl Fn(Vec<PromptSegment>) + 'static,
        on_snooze: impl Fn() + 'static,
    ) -> Rc<Self> {
        let start = request.start;
        let end = request.end.unwrap_or_else(Local::now).max(start);

        let mut texts = HashMap::new();
        texts.insert(start.timestamp(), history.first().cloned().unwrap_or_default());

        let content = ui::vbox(14);
        let segments_box = ui::vbox(8);
        let bar_area = DrawingArea::new();
        let bar_fixed = Fixed::new();

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
                focused: Some(start.timestamp()),
            }),
            segments_box,
            bar_area,
            bar_fixed,
            split_buttons: RefCell::new(Vec::new()),
            on_submit: Box::new(on_submit),
            on_snooze: Box::new(on_snooze),
        });

        prompt.build(&content);
        prompt.rebuild();
        prompt.window.present();
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
        let range = ui::label("", &["mono", "muted"]);
        header.append(&range);
        content.append(&header);

        // Raspon se osvježava kod produženja perioda, pa ga držimo pod ključem.
        unsafe { self.window.set_data("range-label", range) };

        if let Some(note) = &self.request.note {
            content.append(&ui::wrapped(note, &["accent"], if big { 70 } else { 55 }));
        }

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
        let overlay = Overlay::new();
        overlay.set_child(Some(&self.bar_area));
        self.bar_fixed.set_valign(Align::Start);
        overlay.add_overlay(&self.bar_fixed);
        content.append(&overlay);
        unsafe { self.window.set_data("bar-overlay", overlay) };

        content.append(&self.segments_box);

        // Podnožje: tipkovnički savjeti + odgoda.
        let hints = ui::hbox(14);
        hints.append(&ui::hint("↑↓", "povijest"));
        hints.append(&ui::hint("⏎", "spremi"));
        if self.style == PromptStyle::Floating && !self.prefill().is_empty() {
            hints.append(&ui::hint("esc", "isto kao zadnje"));
        }
        hints.append(&ui::hint("✂", "razbij period"));
        hints.append(&ui::spacer());
        if self.request.allow_snooze {
            let me = Rc::downgrade(self);
            hints.append(&ui::button("Odgodi 5 min", &["link"], move || {
                if let Some(me) = me.upgrade() {
                    me.window.close();
                    (me.on_snooze)();
                }
            }));
        }
        content.append(&hints);

        ui::pad(content, if big { 28 } else { 18 });
        content.set_size_request(if big { 560 } else { 420 }, -1);

        // Esc na razini prozora — u floating stilu sprema isto kao zadnji put.
        let key = EventControllerKey::new();
        {
            let me = Rc::downgrade(self);
            key.connect_key_pressed(move |_, keyval, _, _| {
                if keyval == Key::Escape {
                    if let Some(me) = me.upgrade() {
                        me.handle_escape();
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
                    "Odgovor je obavezan — upiši što radiš i stisni Enter.",
                    &["muted-dim"],
                ));

                self.window.set_child(Some(&outer));
                self.window.fullscreen();
            }
        }
    }

    fn prefill(&self) -> String {
        self.history.first().cloned().unwrap_or_default()
    }

    /// Unutarnje točke 5-min mreže na kojima se period može razdvojiti.
    fn boundaries(&self) -> Vec<DateTime<Local>> {
        let s = self.state.borrow();
        grid_boundaries(s.start, s.end)
    }

    /// Spojeni nizovi blokova s jednim opisom.
    fn segments(&self) -> Vec<(DateTime<Local>, DateTime<Local>)> {
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

    // MARK: - Ponovna izgradnja

    /// Poziva se kod razdvajanja/spajanja blokova i kod produženja perioda.
    fn rebuild(self: &Rc<Self>) {
        let segments = self.segments();
        let boundaries = self.boundaries();
        let big = self.big();

        {
            let s = self.state.borrow();
            if let Some(range) = unsafe { self.window.data::<gtk4::Label>("range-label") } {
                let range = unsafe { range.as_ref() };
                range.set_text(&format!("{} – {}", Fmt::hhmm(s.start), Fmt::hhmm(s.end)));
            }
        }

        // Traka je vidljiva samo kad period uopće ima gdje puknuti.
        if let Some(overlay) = unsafe { self.window.data::<Overlay>("bar-overlay") } {
            unsafe { overlay.as_ref() }.set_visible(!boundaries.is_empty());
        }

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

        // Redovi segmenata.
        while let Some(child) = self.segments_box.first_child() {
            self.segments_box.remove(&child);
        }
        let single = segments.len() == 1;
        for (from, to) in &segments {
            self.segments_box.append(&self.segment_row(*from, *to, single, big));
        }

        // Fokus na prvi prazan segment, inače na zapamćeni.
        let focus_key = self.state.borrow().focused;
        if let Some(key) = focus_key {
            if let Some(entry) = self.entry_for(key) {
                entry.grab_focus();
                entry.set_position(-1);
            }
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
        let mut child = self.segments_box.first_child();
        while let Some(row) = child {
            if let Some(data) = unsafe { row.data::<Entry>("segment-entry") } {
                let entry = unsafe { data.as_ref() };
                if unsafe { row.data::<i64>("segment-key") }
                    .map(|k| unsafe { *k.as_ref() })
                    == Some(key)
                {
                    return Some(entry.clone());
                }
            }
            child = row.next_sibling();
        }
        None
    }

    fn segment_row(self: &Rc<Self>, from: DateTime<Local>, to: DateTime<Local>, single: bool, big: bool) -> GtkBox {
        let key = from.timestamp();
        let row = ui::hbox(8);

        if !single {
            let time = ui::label(&format!("{}–{}", Fmt::hhmm(from), Fmt::hhmm(to)), &["mono", "muted"]);
            time.set_width_chars(if big { 12 } else { 11 });
            row.append(&time);
        }

        let entry = Entry::new();
        entry.add_css_class("brand");
        if big {
            entry.add_css_class("big");
        }
        entry.set_hexpand(true);
        entry.set_placeholder_text(Some("npr. Projekt X — opis zadatka"));
        entry.set_text(&self.state.borrow().texts.get(&key).cloned().unwrap_or_default());

        // Tekst se čuva na svaku promjenu, da preživi razdvajanje/spajanje i produženje.
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

        row.append(&entry);
        unsafe {
            row.set_data("segment-entry", entry);
            row.set_data("segment-key", key);
        }
        row
    }

    // MARK: - Traka blokova

    fn draw_bar(&self, cr: &gtk4::cairo::Context, width: f64) {
        let (start, end, focused) = {
            let s = self.state.borrow();
            (s.start, s.end, s.focused)
        };
        let total = ((end - start).num_seconds() as f64).max(1.0);
        let segments = self.segments();
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
        {
            let mut s = self.state.borrow_mut();
            if s.splits.contains(&key) {
                s.splits.remove(&key);
                s.focused = Some(s.start.timestamp());
            } else {
                s.splits.insert(key);
                s.texts.entry(key).or_default();
                s.focused = Some(key);
            }
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

    fn submit(self: &Rc<Self>) {
        let mut out = Vec::new();
        for (from, to) in self.segments() {
            let key = from.timestamp();
            let text = self.state.borrow().texts.get(&key).cloned().unwrap_or_default();
            let text = text.trim().to_string();
            if text.is_empty() {
                // Prazan segment — fokusiraj ga umjesto da spremimo nepotpun period.
                if let Some(entry) = self.entry_for(key) {
                    entry.grab_focus();
                }
                return;
            }
            out.push(PromptSegment { start: from, end: to, text });
        }
        if out.is_empty() {
            return;
        }
        self.window.close();
        (self.on_submit)(out);
    }

    fn handle_escape(self: &Rc<Self>) {
        // Fullscreen ili razdvojeno: esc namjerno ne radi ništa (odgovor je obavezan).
        if self.style != PromptStyle::Floating {
            return;
        }
        let prefill = self.prefill();
        if self.segments().len() != 1 || prefill.is_empty() {
            return;
        }
        let (start, end) = {
            let s = self.state.borrow();
            (s.start, s.end)
        };
        self.window.close();
        (self.on_submit)(vec![PromptSegment { start, end, text: prefill }]);
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
