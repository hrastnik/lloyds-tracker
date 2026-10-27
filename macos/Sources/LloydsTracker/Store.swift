import Foundation

/// JSON pohrana u ~/Library/Application Support/LloydsTracker/
enum Store {
    static var directory: URL {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        let dir = base.appendingPathComponent("LloydsTracker", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }

    private static let encoder: JSONEncoder = {
        let e = JSONEncoder()
        e.dateEncodingStrategy = .iso8601
        e.outputFormatting = [.prettyPrinted, .sortedKeys]
        return e
    }()

    private static let decoder: JSONDecoder = {
        let d = JSONDecoder()
        d.dateDecodingStrategy = .iso8601
        return d
    }()

    static func dayKey(_ date: Date) -> String { Fmt.dayKey.string(from: date) }

    static func dayURL(_ key: String) -> URL {
        directory.appendingPathComponent("\(key).json")
    }

    static func loadDay(_ key: String) -> [Entry] {
        guard let entries = load([Entry].self, from: dayURL(key)) else { return [] }
        return entries.sorted { $0.start < $1.start }
    }

    static func saveDay(_ key: String, entries: [Entry]) {
        let sorted = entries.sorted { $0.start < $1.start }
        guard let data = try? encoder.encode(sorted) else { return }
        try? data.write(to: dayURL(key), options: .atomic)
    }

    // MARK: Settings

    private static var settingsURL: URL { directory.appendingPathComponent("settings.json") }

    static func loadSettings() -> AppSettings {
        load(AppSettings.self, from: settingsURL) ?? AppSettings()
    }

    static func saveSettings(_ settings: AppSettings) {
        guard let data = try? encoder.encode(settings) else { return }
        try? data.write(to: settingsURL, options: .atomic)
    }

    // MARK: History

    private static var historyURL: URL { directory.appendingPathComponent("history.json") }

    static func loadHistory() -> [String] {
        load([String].self, from: historyURL) ?? []
    }

    static func saveHistory(_ history: [String]) {
        guard let data = try? encoder.encode(history) else { return }
        try? data.write(to: historyURL, options: .atomic)
    }

    // MARK: Sesija

    private static var sessionURL: URL { directory.appendingPathComponent("session.json") }

    static func loadSession() -> SavedSession? {
        load(SavedSession.self, from: sessionURL)
    }

    /// nil briše datoteku — tracking nije u tijeku.
    static func saveSession(_ session: SavedSession?) {
        guard let session else {
            try? FileManager.default.removeItem(at: sessionURL)
            return
        }
        guard let data = try? encoder.encode(session) else { return }
        try? data.write(to: sessionURL, options: .atomic)
    }

    // MARK: Pokvarene datoteke

    /// Nepostojeća datoteka → nil. Datoteka koja se ne da pročitati ili dekodirati (krnja,
    /// ručno pokvarena, novija shema) se makne u stranu prije nego što vrati nil — inače bi je
    /// sljedeći zapis prebrisao praznim danom / defaultnim postavkama.
    private static func load<T: Decodable>(_ type: T.Type, from url: URL) -> T? {
        guard FileManager.default.fileExists(atPath: url.path) else { return nil }
        if let data = try? Data(contentsOf: url), let value = try? decoder.decode(T.self, from: data) {
            return value
        }
        quarantine(url)
        return nil
    }

    private static let corruptStamp: DateFormatter = {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.dateFormat = "yyyyMMdd-HHmmss"
        return f
    }()

    /// `2026-07-20.json` → `2026-07-20.json.corrupt-20260720-143000` (ne završava na
    /// `.json`, pa je app više ne čita ni ne prepisuje).
    private static func quarantine(_ url: URL) {
        let target = url.deletingLastPathComponent()
            .appendingPathComponent("\(url.lastPathComponent).corrupt-\(corruptStamp.string(from: Date()))")
        do {
            try FileManager.default.moveItem(at: url, to: target)
            NSLog("LloydsTracker: neispravan %@ premješten u %@", url.lastPathComponent, target.lastPathComponent)
        } catch {
            NSLog("LloydsTracker: neispravan %@ se ne da premjestiti: %@", url.lastPathComponent, "\(error)")
        }
    }
}
