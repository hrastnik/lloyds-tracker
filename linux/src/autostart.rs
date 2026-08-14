use std::fs;
use std::path::PathBuf;

/// Pokretanje kod prijave preko XDG autostarta — Linux pandan macOS `SMAppService` i
/// Windows `HKCU\...\Run` ključa. Datoteka je običan `.desktop` u
/// `~/.config/autostart/`, što poštuju GNOME, KDE i praktički svi ostali desktopi.
pub struct LaunchAtLogin;

const FILE_NAME: &str = "lloyds-tracker.desktop";

impl LaunchAtLogin {
    fn autostart_dir() -> PathBuf {
        let base = std::env::var_os("XDG_CONFIG_HOME")
            .map(PathBuf::from)
            .filter(|p| p.is_absolute())
            .unwrap_or_else(|| {
                let home = std::env::var_os("HOME").map(PathBuf::from).unwrap_or_default();
                home.join(".config")
            });
        base.join("autostart")
    }

    fn desktop_path() -> PathBuf {
        Self::autostart_dir().join(FILE_NAME)
    }

    pub fn is_enabled() -> bool {
        Self::desktop_path().exists()
    }

    /// Primjenjuje željeno stanje. Vraća `None` kod uspjeha, ili poruku na hrvatskom za
    /// postavke (pandan macOS `launchAtLoginStatus`).
    pub fn apply(enabled: bool) -> Option<String> {
        let path = Self::desktop_path();
        if !enabled {
            return match fs::remove_file(&path) {
                Ok(()) => None,
                // Već ne postoji — to je traženo stanje, ne greška.
                Err(e) if e.kind() == std::io::ErrorKind::NotFound => None,
                Err(e) => Some(format!("Greška: {e}")),
            };
        }

        let exe = match std::env::current_exe() {
            Ok(p) => p,
            Err(e) => return Some(format!("Greška: ne mogu odrediti putanju aplikacije ({e}).")),
        };
        if let Err(e) = fs::create_dir_all(Self::autostart_dir()) {
            return Some(format!("Greška: ne mogu stvoriti {}: {e}", Self::autostart_dir().display()));
        }
        let contents = desktop_entry(&exe.display().to_string());
        match fs::write(&path, contents) {
            Ok(()) => None,
            Err(e) => Some(format!("Greška: {e}")),
        }
    }
}

/// `Exec` pokazuje na trenutno pokrenuti binary, pa autostart radi i kad je aplikacija
/// raspakirana u `~/.local/bin` i kad je instalirana sistemski.
fn desktop_entry(exec_path: &str) -> String {
    format!(
        "[Desktop Entry]
Type=Application
Name=Lloyds Tracker
Comment=Praćenje vremena u traci
Exec={exec_path}
Icon=lloyds-tracker
Terminal=false
Categories=Utility;
X-GNOME-Autostart-enabled=true
"
    )
}
