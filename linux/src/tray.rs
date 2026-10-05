use async_channel::Sender;
use ksni::menu::{StandardItem, SubMenu};
use ksni::{Icon, MenuItem, ToolTip};

use crate::engine::{TrackerEngine, TrayState};
use crate::icon::tray_icon;
use crate::models::{Entry, EntryKind};
use crate::theme::Fmt;

/// Što traka traži od aplikacije. Callbackovi ksni-ja trče na svom threadu, pa ne diraju
/// engine izravno nego pošalju naredbu koju GTK main thread pokupi (`main.rs`).
#[derive(Debug, Clone, Copy)]
pub enum TrayCommand {
    Start,
    /// None = pauza do ručnog nastavka.
    Pause(Option<i64>),
    Resume,
    /// "Zapiši sada" — ručni prompt za period do sada.
    PromptNow,
    Stop,
    OpenSummary,
    OpenSettings,
    /// Release stranica nove verzije (ništa se ne instalira samo).
    OpenUpdatePage,
    Quit,
}

/// Sve što meni treba nacrtati. Puni ga GTK thread, čita ksni thread.
#[derive(Debug, Clone, PartialEq)]
pub struct TraySnapshot {
    pub state: TrayState,
    pub day_title: String,
    pub status: String,
    pub next_prompt: Option<String>,
    pub auto_stop: Option<String>,
    pub total: Option<String>,
    pub entries: Vec<String>,
    pub hidden: usize,
    pub is_tracking: bool,
    pub is_paused: bool,
    pub can_prompt_now: bool,
    /// Novija verzija na GitHubu (None = nema je).
    pub update: Option<String>,
}

/// Koliko zadnjih unosa stane u meni — isto kao macOS popover.
const MAX_VISIBLE_ENTRIES: usize = 8;

impl TraySnapshot {
    pub fn from_engine(engine: &TrackerEngine) -> Self {
        let visible: Vec<&Entry> = engine.entries.iter().rev().take(MAX_VISIBLE_ENTRIES).collect();
        let total = engine.work_total();

        TraySnapshot {
            state: engine.tray_state(),
            day_title: Fmt::day_title(chrono::Local::now()),
            status: engine.status_text(),
            // U DBus meni ide vrijeme, a ne živo odbrojavanje: meni crta host i osvježava
            // se samo kad se promijeni, pa bi sekundni countdown značio DBus promet
            // svake sekunde bez ikakve koristi.
            next_prompt: engine
                .is_tracking
                .then_some(())
                .and(engine.pause_until.is_none().then_some(()))
                .and(engine.next_prompt_at)
                .map(|at| format!("Sljedeći prompt u {}", Fmt::hhmm(at))),
            auto_stop: engine.auto_stop_text(),
            total: (total > 0.0).then(|| format!("ukupno {}", Fmt::dur(total))),
            entries: visible.iter().map(|e| entry_line(e)).collect(),
            hidden: engine.entries.len().saturating_sub(MAX_VISIBLE_ENTRIES),
            is_tracking: engine.is_tracking,
            is_paused: engine.pause_until.is_some(),
            can_prompt_now: engine.can_prompt_now(),
            update: engine.available_update.as_ref().map(|u| u.version.clone()),
        }
    }
}

fn entry_line(entry: &Entry) -> String {
    let text = if entry.kind == EntryKind::Pause {
        format!("({})", entry.text)
    } else {
        entry.text.clone()
    };
    format!(
        "{}–{}   {}   {}",
        Fmt::hhmm(entry.start),
        Fmt::hhmm(entry.end),
        text,
        Fmt::dur(entry.duration())
    )
}

pub struct LloydsTray {
    snapshot: TraySnapshot,
    tx: Sender<TrayCommand>,
    icons: TrayIcons,
}

/// Ikone se iscrtaju jednom kod pokretanja (na GTK threadu, gdje cairo ionako živi) i
/// dalje su samo baferi bajtova.
struct TrayIcons {
    idle: Icon,
    tracking: Icon,
    paused: Icon,
    awaiting: Icon,
}

impl LloydsTray {
    pub fn new(snapshot: TraySnapshot, tx: Sender<TrayCommand>) -> Self {
        LloydsTray {
            snapshot,
            tx,
            icons: TrayIcons {
                idle: tray_icon(TrayState::Idle),
                tracking: tray_icon(TrayState::Tracking),
                paused: tray_icon(TrayState::Paused),
                awaiting: tray_icon(TrayState::AwaitingReturn),
            },
        }
    }

    pub fn set_snapshot(&mut self, snapshot: TraySnapshot) {
        self.snapshot = snapshot;
    }

    /// Kanal je neograničen, pa `try_send` nikad ne blokira ksni thread — a i da
    /// aplikacija zapne, traka ostaje responzivna.
    fn send(&self, command: TrayCommand) {
        let _ = self.tx.try_send(command);
    }
}

/// Neaktivna stavka — nosi informaciju, ne akciju (status, ukupno, popis unosa).
fn info(label: impl Into<String>) -> MenuItem<LloydsTray> {
    StandardItem {
        label: label.into(),
        enabled: false,
        ..Default::default()
    }
    .into()
}

fn action(label: impl Into<String>, command: TrayCommand) -> MenuItem<LloydsTray> {
    StandardItem {
        label: label.into(),
        activate: Box::new(move |tray: &mut LloydsTray| tray.send(command)),
        ..Default::default()
    }
    .into()
}

impl ksni::Tray for LloydsTray {
    fn id(&self) -> String {
        "lloyds-tracker".into()
    }

    fn title(&self) -> String {
        "Lloyds Tracker".into()
    }

    fn icon_pixmap(&self) -> Vec<Icon> {
        let icon = match self.snapshot.state {
            TrayState::Idle => &self.icons.idle,
            TrayState::Tracking => &self.icons.tracking,
            TrayState::Paused => &self.icons.paused,
            TrayState::AwaitingReturn => &self.icons.awaiting,
        };
        vec![icon.clone()]
    }

    fn tool_tip(&self) -> ToolTip {
        ToolTip {
            title: "Lloyds Tracker".into(),
            description: self.snapshot.status.clone(),
            ..Default::default()
        }
    }

    /// Lijevi klik na ikonu — na Linuxu host ionako najčešće otvori meni, ali gdje
    /// prosljeđuje `Activate` otvaramo pregled dana (pandan macOS popoveru).
    fn activate(&mut self, _x: i32, _y: i32) {
        self.send(TrayCommand::OpenSummary);
    }

    fn menu(&self) -> Vec<MenuItem<Self>> {
        let s = &self.snapshot;
        let mut items: Vec<MenuItem<Self>> = Vec::new();

        // Obavijest o novoj verziji — otvara release stranicu. Na vrhu, da se vidi.
        if let Some(version) = &s.update {
            items.push(action(format!("Nova verzija {version} · Preuzmi"), TrayCommand::OpenUpdatePage));
            items.push(MenuItem::Separator);
        }

        // Zaglavlje: dan i stanje.
        items.push(info(format!("LLOYDS TRACKER — {}", s.day_title)));
        items.push(info(s.status.clone()));
        if let Some(next) = &s.next_prompt {
            items.push(info(next.clone()));
        }
        if let Some(auto_stop) = &s.auto_stop {
            items.push(info(auto_stop.clone()));
        }
        items.push(MenuItem::Separator);

        // Kontrole. "Zapiši sada" je nad njima, kao u macOS popoveru.
        if s.can_prompt_now {
            items.push(action("Zapiši sada", TrayCommand::PromptNow));
        }
        if s.is_tracking {
            if s.is_paused {
                items.push(action("Nastavi", TrayCommand::Resume));
            } else {
                items.push(
                    SubMenu {
                        label: "Pauziraj".into(),
                        submenu: vec![
                            action("15 minuta", TrayCommand::Pause(Some(15))),
                            action("30 minuta", TrayCommand::Pause(Some(30))),
                            action("1 sat", TrayCommand::Pause(Some(60))),
                            action("Do nastavka", TrayCommand::Pause(None)),
                        ],
                        ..Default::default()
                    }
                    .into(),
                );
            }
            items.push(action("Završi dan", TrayCommand::Stop));
        } else {
            items.push(action("Start — počni radni dan", TrayCommand::Start));
        }
        items.push(MenuItem::Separator);

        // Današnji unosi.
        items.push(info(match &s.total {
            Some(total) => format!("DANAS — {total}"),
            None => "DANAS".to_string(),
        }));
        if s.entries.is_empty() {
            items.push(info("Još nema unosa."));
        } else {
            for line in &s.entries {
                items.push(info(line.clone()));
            }
            if s.hidden > 0 {
                items.push(info(format!("… i još {} ranijih", s.hidden)));
            }
        }
        items.push(MenuItem::Separator);

        items.push(action("Pregled dana", TrayCommand::OpenSummary));
        items.push(action("Postavke…", TrayCommand::OpenSettings));
        items.push(action("Izlaz", TrayCommand::Quit));
        items
    }
}
