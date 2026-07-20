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
        guard let data = try? Data(contentsOf: dayURL(key)),
              let entries = try? decoder.decode([Entry].self, from: data) else { return [] }
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
        guard let data = try? Data(contentsOf: settingsURL),
              let s = try? decoder.decode(AppSettings.self, from: data) else { return AppSettings() }
        return s
    }

    static func saveSettings(_ settings: AppSettings) {
        guard let data = try? encoder.encode(settings) else { return }
        try? data.write(to: settingsURL, options: .atomic)
    }

    // MARK: History

    private static var historyURL: URL { directory.appendingPathComponent("history.json") }

    static func loadHistory() -> [String] {
        guard let data = try? Data(contentsOf: historyURL),
              let h = try? decoder.decode([String].self, from: data) else { return [] }
        return h
    }

    static func saveHistory(_ history: [String]) {
        guard let data = try? encoder.encode(history) else { return }
        try? data.write(to: historyURL, options: .atomic)
    }
}
