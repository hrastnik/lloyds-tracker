use std::cell::RefCell;

use zbus::blocking::Connection;

/// Je li ekran zaključan — Linux pandan macOS `com.apple.screenIsLocked` notifikacijama
/// i Windows `SystemEvents.SessionSwitch`.
///
/// Umjesto pretplate na signale (koja bi tražila poseban thread i most prema GTK petlji)
/// stanje se **poll-a** iz iste 1-sekundne tick petlje kao i sve ostalo u engineu. Engine
/// ionako čita samo `is_locked`, pa je razlika neprimjetna: prijelaz se uhvati unutar
/// sekunde, što je točnije nego što se pauze i tako bilježe (5-min mreža).
///
/// Redom se probaju:
/// 1. `org.gnome.ScreenSaver.GetActive` — GNOME.
/// 2. `org.freedesktop.ScreenSaver.GetActive` — KDE Plasma i ostali.
/// 3. `org.freedesktop.login1` svojstvo `LockedHint` — systemd-logind, kad screensaver
///    servisa nema.
pub struct SessionMonitor;

/// Kandidat za detekciju zaključavanja: koji backend i kako se pita.
type Probe = (Backend, fn() -> Option<bool>);

#[derive(Clone, Copy, PartialEq)]
enum Backend {
    Unknown,
    GnomeScreenSaver,
    FreedesktopScreenSaver,
    Logind,
    None,
}

thread_local! {
    static BACKEND: RefCell<Backend> = const { RefCell::new(Backend::Unknown) };
    static SESSION: RefCell<Option<Option<Connection>>> = const { RefCell::new(None) };
    static SYSTEM: RefCell<Option<Option<Connection>>> = const { RefCell::new(None) };
}

fn session_bus<T>(f: impl FnOnce(&Connection) -> Option<T>) -> Option<T> {
    SESSION.with(|cell| {
        let mut slot = cell.borrow_mut();
        let conn = slot.get_or_insert_with(|| Connection::session().ok());
        conn.as_ref().and_then(f)
    })
}

fn system_bus<T>(f: impl FnOnce(&Connection) -> Option<T>) -> Option<T> {
    SYSTEM.with(|cell| {
        let mut slot = cell.borrow_mut();
        let conn = slot.get_or_insert_with(|| Connection::system().ok());
        conn.as_ref().and_then(f)
    })
}

impl SessionMonitor {
    pub fn is_locked() -> bool {
        let backend = BACKEND.with(|b| *b.borrow());
        match backend {
            Backend::GnomeScreenSaver => gnome_locked().unwrap_or(false),
            Backend::FreedesktopScreenSaver => freedesktop_locked().unwrap_or(false),
            Backend::Logind => logind_locked().unwrap_or(false),
            Backend::None => false,
            Backend::Unknown => {
                let probes: [Probe; 3] = [
                    (Backend::GnomeScreenSaver, gnome_locked),
                    (Backend::FreedesktopScreenSaver, freedesktop_locked),
                    (Backend::Logind, logind_locked),
                ];
                for (kind, probe) in probes {
                    if let Some(locked) = probe() {
                        BACKEND.with(|b| *b.borrow_mut() = kind);
                        return locked;
                    }
                }
                BACKEND.with(|b| *b.borrow_mut() = Backend::None);
                false
            }
        }
    }

    /// Ime aktivnog backenda za prikaz u postavkama.
    pub fn backend_label() -> &'static str {
        let _ = Self::is_locked();
        match BACKEND.with(|b| *b.borrow()) {
            Backend::GnomeScreenSaver => "org.gnome.ScreenSaver",
            Backend::FreedesktopScreenSaver => "org.freedesktop.ScreenSaver",
            Backend::Logind => "org.freedesktop.login1 (LockedHint)",
            Backend::None | Backend::Unknown => "nije dostupno na ovoj sesiji",
        }
    }
}

fn gnome_locked() -> Option<bool> {
    session_bus(|conn| {
        let reply = conn
            .call_method(
                Some("org.gnome.ScreenSaver"),
                "/org/gnome/ScreenSaver",
                Some("org.gnome.ScreenSaver"),
                "GetActive",
                &(),
            )
            .ok()?;
        reply.body().deserialize::<bool>().ok()
    })
}

fn freedesktop_locked() -> Option<bool> {
    session_bus(|conn| {
        for path in ["/org/freedesktop/ScreenSaver", "/ScreenSaver"] {
            let reply = conn.call_method(
                Some("org.freedesktop.ScreenSaver"),
                path,
                Some("org.freedesktop.ScreenSaver"),
                "GetActive",
                &(),
            );
            if let Ok(reply) = reply {
                if let Ok(active) = reply.body().deserialize::<bool>() {
                    return Some(active);
                }
            }
        }
        None
    })
}

/// systemd-logind: `/org/freedesktop/login1/session/auto` je uvijek sesija pozivatelja.
fn logind_locked() -> Option<bool> {
    system_bus(|conn| {
        let reply = conn
            .call_method(
                Some("org.freedesktop.login1"),
                "/org/freedesktop/login1/session/auto",
                Some("org.freedesktop.DBus.Properties"),
                "Get",
                &("org.freedesktop.login1.Session", "LockedHint"),
            )
            .ok()?;
        let value: zbus::zvariant::OwnedValue = reply.body().deserialize().ok()?;
        bool::try_from(value).ok()
    })
}
