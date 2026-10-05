import Foundation

/// Nova verzija objavljena na GitHubu.
struct AvailableUpdate: Equatable {
    var version: String
    /// Release stranica s .zip / .exe / .tar.gz za preuzimanje.
    var url: URL
}

/// Provjera nove verzije preko GitHub API-ja (zadnji release). Samo javlja — ništa ne
/// skida ni ne instalira; korisnik preuzima s release stranice.
enum UpdateChecker {
    static let latestReleaseAPI = URL(string: "https://api.github.com/repos/hrastnik/lloyds-tracker/releases/latest")!
    /// Ako odgovor nema `html_url`, vodi na zadnji release.
    static let latestReleasePage = URL(string: "https://github.com/hrastnik/lloyds-tracker/releases/latest")!

    /// Verzija ove aplikacije iz Info.plista; nil kad se pokreće izvan .app bundle-a
    /// (`swift run`), pa se tad i ne provjerava.
    static var currentVersion: String? {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String
    }

    /// Zadnji objavljeni release — verzija bez "v" (tag `v1.7.0` → "1.7.0").
    static func fetchLatest() async throws -> AvailableUpdate {
        var request = URLRequest(url: latestReleaseAPI, timeoutInterval: 15)
        request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        request.setValue("LloydsTracker", forHTTPHeaderField: "User-Agent")
        let (data, response) = try await URLSession.shared.data(for: request)
        guard (response as? HTTPURLResponse)?.statusCode == 200 else {
            throw URLError(.badServerResponse)
        }
        struct Release: Decodable {
            let tag_name: String
            let html_url: URL?
        }
        let release = try JSONDecoder().decode(Release.self, from: data)
        let tag = release.tag_name
        let version = tag.hasPrefix("v") ? String(tag.dropFirst()) : tag
        return AvailableUpdate(version: version, url: release.html_url ?? latestReleasePage)
    }

    /// Usporedba po brojevima ("1.10.0" je novija od "1.9.2"); dio koji nedostaje je 0.
    static func isNewer(_ a: String, than b: String) -> Bool {
        let pa = parts(a), pb = parts(b)
        for i in 0..<max(pa.count, pb.count) {
            let x = i < pa.count ? pa[i] : 0
            let y = i < pb.count ? pb[i] : 0
            if x != y { return x > y }
        }
        return false
    }

    private static func parts(_ version: String) -> [Int] {
        version.split(separator: ".").map { Int($0.prefix { $0.isNumber }) ?? 0 }
    }
}
