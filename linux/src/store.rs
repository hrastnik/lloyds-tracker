use std::fs;
use std::path::PathBuf;

use chrono::{DateTime, Local};

use crate::models::{AppSettings, Entry};
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
        let mut entries: Vec<Entry> = fs::read_to_string(Self::day_path(key))
            .ok()
            .and_then(|s| serde_json::from_str(&s).ok())
            .unwrap_or_default();
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
        fs::read_to_string(Self::settings_path())
            .ok()
            .and_then(|s| serde_json::from_str(&s).ok())
            .unwrap_or_default()
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
        fs::read_to_string(Self::history_path())
            .ok()
            .and_then(|s| serde_json::from_str(&s).ok())
            .unwrap_or_default()
    }

    pub fn save_history(history: &[String]) {
        if let Ok(json) = serde_json::to_string_pretty(history) {
            write_atomic(&Self::history_path(), &json);
        }
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
