use std::cell::RefCell;

use zbus::blocking::Connection;

/// Detekcija neaktivnosti (tipkovnica/miš) — Linux pandan macOS
/// `CGEventSource.secondsSinceLastEventType` i Windows `GetLastInputInfo`.
///
/// Nema jednog API-ja koji radi svugdje, pa se redom probaju poznati DBus servisi i
/// zapamti onaj koji odgovori:
///
/// 1. `org.gnome.Mutter.IdleMonitor` — GNOME, radi i na Waylandu i na X11.
/// 2. `org.freedesktop.ScreenSaver.GetSessionIdleTime` — KDE Plasma.
///
/// Ako nijedan ne postoji (npr. sesija bez ta dva servisa), vraća 0 — tj. "korisnik je
/// aktivan". To je namjerno sigurna strana: detekcija neaktivnosti je po defaultu
/// isključena, a kad je uključena radije se ne bilježi pauza nego da se lažno bilježi.
pub struct IdleMonitor;

#[derive(Clone, Copy, PartialEq)]
enum Backend {
    /// Nije još isprobano.
    Unknown,
    Mutter,
    ScreenSaver,
    /// Ništa ne odgovara — više ne pokušavamo.
    None,
}

thread_local! {
    static BACKEND: RefCell<Backend> = const { RefCell::new(Backend::Unknown) };
    static SESSION: RefCell<Option<Option<Connection>>> = const { RefCell::new(None) };
}

/// Session bus konekcija se otvara jednom i drži — otvaranje po pozivu (1×/s) bilo bi
/// skupo. `None` znači da bus nije dostupan.
fn session_bus<T>(f: impl FnOnce(&Connection) -> Option<T>) -> Option<T> {
    SESSION.with(|cell| {
        let mut slot = cell.borrow_mut();
        let conn = slot.get_or_insert_with(|| Connection::session().ok());
        conn.as_ref().and_then(f)
    })
}

impl IdleMonitor {
    /// Sekunde od zadnjeg korisničkog inputa.
    pub fn idle_seconds() -> f64 {
        let backend = BACKEND.with(|b| *b.borrow());
        match backend {
            Backend::Mutter => mutter_idle().unwrap_or(0.0),
            Backend::ScreenSaver => screensaver_idle().unwrap_or(0.0),
            Backend::None => 0.0,
            Backend::Unknown => {
                if let Some(secs) = mutter_idle() {
                    BACKEND.with(|b| *b.borrow_mut() = Backend::Mutter);
                    return secs;
                }
                if let Some(secs) = screensaver_idle() {
                    BACKEND.with(|b| *b.borrow_mut() = Backend::ScreenSaver);
                    return secs;
                }
                BACKEND.with(|b| *b.borrow_mut() = Backend::None);
                0.0
            }
        }
    }

    /// Ime aktivnog backenda za prikaz u postavkama — da se odmah vidi hoće li
    /// detekcija neaktivnosti uopće raditi na ovoj sesiji.
    pub fn backend_label() -> &'static str {
        // Prvi poziv ujedno i detektira backend.
        let _ = Self::idle_seconds();
        match BACKEND.with(|b| *b.borrow()) {
            Backend::Mutter => "org.gnome.Mutter.IdleMonitor",
            Backend::ScreenSaver => "org.freedesktop.ScreenSaver",
            Backend::None | Backend::Unknown => "nije dostupno na ovoj sesiji",
        }
    }
}

/// GNOME: milisekunde od zadnjeg inputa.
fn mutter_idle() -> Option<f64> {
    session_bus(|conn| {
        let reply = conn
            .call_method(
                Some("org.gnome.Mutter.IdleMonitor"),
                "/org/gnome/Mutter/IdleMonitor/Core",
                Some("org.gnome.Mutter.IdleMonitor"),
                "GetIdletime",
                &(),
            )
            .ok()?;
        let ms: u64 = reply.body().deserialize().ok()?;
        Some(ms as f64 / 1000.0)
    })
}

/// KDE: sekunde od zadnjeg inputa.
fn screensaver_idle() -> Option<f64> {
    session_bus(|conn| {
        for path in ["/org/freedesktop/ScreenSaver", "/ScreenSaver"] {
            let reply = conn.call_method(
                Some("org.freedesktop.ScreenSaver"),
                path,
                Some("org.freedesktop.ScreenSaver"),
                "GetSessionIdleTime",
                &(),
            );
            if let Ok(reply) = reply {
                if let Ok(secs) = reply.body().deserialize::<u32>() {
                    return Some(secs as f64);
                }
            }
        }
        None
    })
}
