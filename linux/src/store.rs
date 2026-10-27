use std::fs;
use std::io;
use std::path::{Path, PathBuf};

use chrono::{DateTime, Local};
use serde::de::DeserializeOwned;

use crate::models::{AppSettings, Entry, SavedSession};
use crate::theme::Fmt;

/// JSON pohrana u `$XDG_DATA_HOME/LloydsTracker/` (praktički `~/.local/share/LloydsTracker/`).
/// Isti format kao macOS `~/Library/Application Support/LloydsTracker/` i Windows
/// `%APPDATA%\LloydsTracker\` — datoteke se mogu prenositi među portovima.
pub struct Store;

impl Store {
    pub fn directory() -> PathBuf {
        let base = std::env::var_os("XDG_DATA_HOME")
            .map(PathBuf::from)
            .filter(|p| p.is_absolute())
            .unwrap_or_else(|| {
                let home = std::env::var_os("HOME").map(PathBuf::from).unwrap_or_default();
                home.join(".local").join("share")
            });
        let dir = base.join("LloydsTracker");
        let _ = fs::create_dir_all(&dir);
        dir
    }

    pub fn day_key(date: DateTime<Local>) -> String {
        Fmt::day_key(date)
    }

    pub fn day_path(key: &str) -> PathBuf {
        Self::directory().join(format!("{key}.json"))
    }

    pub fn load_day(key: &str) -> Vec<Entry> {
        let mut entries: Vec<Entry> = load(&Self::day_path(key)).unwrap_or_default();
        entries.sort_by_key(|e| e.start);
        entries
    }

    pub fn save_day(key: &str, entries: &[Entry]) {
        let mut sorted = entries.to_vec();
        sorted.sort_by_key(|e| e.start);
        if let Ok(json) = serde_json::to_string_pretty(&sorted) {
            write_atomic(&Self::day_path(key), &json);
        }
    }

    // MARK: Settings

    fn settings_path() -> PathBuf {
        Self::directory().join("settings.json")
    }

    pub fn load_settings() -> AppSettings {
        load(&Self::settings_path()).unwrap_or_default()
    }

    pub fn save_settings(settings: &AppSettings) {
        if let Ok(json) = serde_json::to_string_pretty(settings) {
            write_atomic(&Self::settings_path(), &json);
        }
    }

    // MARK: History

    fn history_path() -> PathBuf {
        Self::directory().join("history.json")
    }

    pub fn load_history() -> Vec<String> {
        load(&Self::history_path()).unwrap_or_default()
    }

    pub fn save_history(history: &[String]) {
        if let Ok(json) = serde_json::to_string_pretty(history) {
            write_atomic(&Self::history_path(), &json);
        }
    }

    // MARK: Sesija

    fn session_path() -> PathBuf {
        Self::directory().join("session.json")
    }

    pub fn load_session() -> Option<SavedSession> {
        load(&Self::session_path())
    }

    /// `None` briše datoteku — tracking nije u tijeku.
    pub fn save_session(session: Option<&SavedSession>) {
        match session {
            None => {
                let _ = fs::remove_file(Self::session_path());
            }
            Some(session) => {
                if let Ok(json) = serde_json::to_string_pretty(session) {
                    write_atomic(&Self::session_path(), &json);
                }
            }
        }
    }
}

// MARK: Pokvarene datoteke

/// Nepostojeća datoteka → `None`. Datoteka koja se ne da pročitati ili dekodirati (krnja,
/// ručno pokvarena, novija shema) se makne u stranu prije nego što vrati `None` — inače bi je
/// sljedeći zapis prebrisao praznim danom / defaultnim postavkama.
fn load<T: DeserializeOwned>(path: &Path) -> Option<T> {
    match fs::read_to_string(path) {
        Err(e) if e.kind() == io::ErrorKind::NotFound => None,
        Ok(s) => match serde_json::from_str(&s) {
            Ok(value) => Some(value),
            Err(_) => {
                quarantine(path);
                None
            }
        },
        Err(_) => {
            quarantine(path);
            None
        }
    }
}

/// `2026-07-20.json` → `2026-07-20.json.corrupt-20260720-143000` (ne završava na `.json`,
/// pa je app više ne čita ni ne prepisuje).
fn quarantine(path: &Path) {
    let mut name = path.file_name().unwrap_or_default().to_os_string();
    name.push(format!(".corrupt-{}", Local::now().format("%Y%m%d-%H%M%S")));
    let target = path.with_file_name(name);
    match fs::rename(path, &target) {
        Ok(()) => eprintln!(
            "LloydsTracker: neispravan {} premješten u {}",
            path.display(),
            target.display()
        ),
        Err(e) => eprintln!(
            "LloydsTracker: neispravan {} se ne da premjestiti: {e}",
            path.display()
        ),
    }
}

/// Upis preko temp datoteke + rename, da pad usred pisanja ne ostavi krnji JSON.
/// Neuspjeh se guta — tracker nikad ne smije pasti zbog zapisa na disk.
fn write_atomic(path: &PathBuf, contents: &str) {
    let tmp = path.with_extension("json.tmp");
    if fs::write(&tmp, contents).is_ok() {
        let _ = fs::rename(&tmp, path);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn temp_dir(name: &str) -> PathBuf {
        let dir = std::env::temp_dir().join(format!("lloyds-store-{name}-{}", std::process::id()));
        let _ = fs::remove_dir_all(&dir);
        fs::create_dir_all(&dir).unwrap();
        dir
    }

    fn names(dir: &Path) -> Vec<String> {
        let mut v: Vec<String> = fs::read_dir(dir)
            .unwrap()
            .map(|e| e.unwrap().file_name().to_string_lossy().into_owned())
            .collect();
        v.sort();
        v
    }

    #[test]
    fn corrupt_file_is_moved_aside() {
        let dir = temp_dir("corrupt");
        let day = dir.join("2026-10-03.json");

        // Nepostojeći → None, ništa se ne stvara.
        assert!(load::<Vec<Entry>>(&day).is_none());
        assert!(names(&dir).is_empty());

        // Ispravan → učita se, ostaje gdje je.
        fs::write(&day, "[]").unwrap();
        assert_eq!(load::<Vec<Entry>>(&day).map(|v| v.len()), Some(0));
        assert_eq!(names(&dir), ["2026-10-03.json"]);

        // Pokvaren → None + premješten, sadržaj sačuvan; sljedeći zapis ga ne dira.
        fs::write(&day, "[{\"broken\": ").unwrap();
        assert!(load::<Vec<Entry>>(&day).is_none());
        let files = names(&dir);
        assert_eq!(files.len(), 1);
        assert!(files[0].starts_with("2026-10-03.json.corrupt-"), "{files:?}");
        assert_eq!(fs::read_to_string(dir.join(&files[0])).unwrap(), "[{\"broken\": ");
        write_atomic(&day, "[]");
        assert_eq!(names(&dir).len(), 2);

        // Prazna datoteka i "null" su isto pokvareni (app ih nikad ne piše).
        let settings = dir.join("settings.json");
        fs::write(&settings, "").unwrap();
        assert!(load::<AppSettings>(&settings).is_none());
        let history = dir.join("history.json");
        fs::write(&history, "null").unwrap();
        assert!(load::<Vec<String>>(&history).is_none());
        let files = names(&dir);
        assert!(files.iter().any(|f| f.starts_with("settings.json.corrupt-")), "{files:?}");
        assert!(files.iter().any(|f| f.starts_with("history.json.corrupt-")), "{files:?}");

        let _ = fs::remove_dir_all(&dir);
    }
}
