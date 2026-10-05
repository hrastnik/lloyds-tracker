use std::time::Duration;

use serde::Deserialize;

/// Nova verzija objavljena na GitHubu.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AvailableUpdate {
    pub version: String,
    /// Release stranica s .zip / .exe / .tar.gz za preuzimanje.
    pub url: String,
}

/// Provjera nove verzije preko GitHub API-ja (zadnji release). Samo javlja — ništa ne
/// skida ni ne instalira; korisnik preuzima s release stranice.
pub struct UpdateChecker;

impl UpdateChecker {
    pub const LATEST_RELEASE_API: &'static str =
        "https://api.github.com/repos/hrastnik/lloyds-tracker/releases/latest";
    /// Ako odgovor nema `html_url`, vodi na zadnji release.
    pub const LATEST_RELEASE_PAGE: &'static str =
        "https://github.com/hrastnik/lloyds-tracker/releases/latest";

    /// Verzija ove aplikacije iz `Cargo.toml` (release CI u nju upiše verziju iz taga).
    /// Za razliku od macOS-a (`swift run` nema Info.plist) uvijek je poznata.
    pub fn current_version() -> &'static str {
        env!("CARGO_PKG_VERSION")
    }

    /// Zadnji objavljeni release — verzija bez "v" (tag `v1.7.0` → "1.7.0"). Blokira do
    /// 15 s, pa se zove s pomoćnog threada (vidi `main.rs`).
    pub fn fetch_latest() -> Result<AvailableUpdate, String> {
        #[derive(Deserialize)]
        struct Release {
            tag_name: String,
            html_url: Option<String>,
        }

        let agent: ureq::Agent = ureq::Agent::config_builder()
            .timeout_global(Some(Duration::from_secs(15)))
            .user_agent("LloydsTracker")
            .build()
            .into();
        let mut response = agent
            .get(Self::LATEST_RELEASE_API)
            .header("Accept", "application/vnd.github+json")
            .call()
            .map_err(|e| e.to_string())?;
        if response.status() != 200 {
            return Err(format!("HTTP {}", response.status()));
        }
        let body = response.body_mut().read_to_string().map_err(|e| e.to_string())?;
        let release: Release = serde_json::from_str(&body).map_err(|e| e.to_string())?;

        let tag = release.tag_name;
        let version = tag.strip_prefix('v').unwrap_or(&tag).to_string();
        Ok(AvailableUpdate {
            version,
            url: release.html_url.unwrap_or_else(|| Self::LATEST_RELEASE_PAGE.to_string()),
        })
    }

    /// Usporedba po brojevima ("1.10.0" je novija od "1.9.2"); dio koji nedostaje je 0.
    pub fn is_newer(a: &str, b: &str) -> bool {
        let (pa, pb) = (parts(a), parts(b));
        for i in 0..pa.len().max(pb.len()) {
            let x = pa.get(i).copied().unwrap_or(0);
            let y = pb.get(i).copied().unwrap_or(0);
            if x != y {
                return x > y;
            }
        }
        false
    }

    /// Release stranica u zadanom pregledniku (kao "Otvori folder s podacima").
    pub fn open_page(url: &str) {
        let _ = gtk4::gio::AppInfo::launch_default_for_uri(url, None::<&gtk4::gio::AppLaunchContext>);
    }
}

/// Svaki dio su samo vodeće znamenke ("2-beta" → 2), inače 0. Prazni dijelovi se
/// preskaču, kao Swiftov `split(separator:)`.
fn parts(version: &str) -> Vec<u64> {
    version
        .split('.')
        .filter(|p| !p.is_empty())
        .map(|p| {
            let digits: String = p.chars().take_while(char::is_ascii_digit).collect();
            digits.parse().unwrap_or(0)
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::UpdateChecker;

    #[test]
    fn is_newer_compares_numerically() {
        assert!(UpdateChecker::is_newer("1.10.0", "1.9.2"));
        assert!(UpdateChecker::is_newer("1.7.0", "1.6.0"));
        assert!(UpdateChecker::is_newer("2", "1.9.9"));
        assert!(UpdateChecker::is_newer("1.6.1", "1.6"));
        assert!(!UpdateChecker::is_newer("1.6.0", "1.6.0"));
        assert!(!UpdateChecker::is_newer("1.6", "1.6.0"));
        assert!(!UpdateChecker::is_newer("1.9.2", "1.10.0"));
        assert!(!UpdateChecker::is_newer("1.6.0", "1.7.0"));
        // Dio su samo vodeće znamenke.
        assert!(UpdateChecker::is_newer("1.7.0-beta", "1.6.9"));
        assert!(!UpdateChecker::is_newer("1.6.0-beta", "1.6.0"));
    }
}
