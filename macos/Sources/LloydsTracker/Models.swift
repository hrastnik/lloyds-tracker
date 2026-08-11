import Foundation

enum EntryKind: String, Codable {
    case work
    case pause
}

struct Entry: Codable, Identifiable, Equatable {
    var id = UUID()
    var start: Date
    var end: Date
    var text: String
    var kind: EntryKind = .work

    var duration: TimeInterval { end.timeIntervalSince(start) }
}

enum PromptStyle: String, Codable, CaseIterable, Identifiable {
    case floating
    case fullscreen

    var id: String { rawValue }

    var label: String {
        switch self {
        case .floating: return "Floating panel (kut ekrana)"
        case .fullscreen: return "Cijeli ekran (obavezan odgovor)"
        }
    }
}

struct AppSettings: Codable, Equatable {
    var intervalMinutes: Int = 15
    var promptStyle: PromptStyle = .floating
    var soundEnabled: Bool = true
    /// Neaktivnost tipkovnice/miša dulje od praga → razdoblje se bilježi kao pauza.
    var idleDetectionEnabled: Bool = false
    var idleThresholdMinutes: Int = 5
    /// Zaključan ekran → razdoblje odsutnosti se bilježi kao pauza.
    var lockPauseEnabled: Bool = false
    var historyLimit: Int = 15
    /// Kronološki pregled dana: susjedni unosi istog naziva (jedan završava kad drugi
    /// počinje) prikazuju se kao jedan unos.
    var mergeAdjacentEntries: Bool = true
    var launchAtLogin: Bool = false
    var showStartupReminder: Bool = true
    /// Automatsko zaustavljanje trackinga u zadano vrijeme — da tracking ne ostane
    /// pokrenut preko noći. Minutu prije iskoči upozorenje s opcijom produženja.
    var autoStopEnabled: Bool = true
    var autoStopHour: Int = 16
    var autoStopMinute: Int = 0
    /// Podsjetnik na početak radnog dana — iskoči u zadano vrijeme, a ako je računalo
    /// tad spavalo, čim se probudi. Laptop koji se ne gasi inače ostane bez podsjetnika,
    /// jer se onaj kod pokretanja aplikacije javlja samo kod paljenja računala.
    var workdayStartEnabled: Bool = true
    var workdayStartHour: Int = 8
    var workdayStartMinute: Int = 30
    /// Podsjetnik nudi i start od početka radnog dana — otvaranje laptopa u 9:30 se
    /// tako može upisati kao rad od 8:30 (jutro se nadoknadi).
    var workdayStartBackfillEnabled: Bool = true

    init() {}

    /// Ručni decode s `decodeIfPresent` da stari `settings.json` (bez novih ključeva)
    /// ne padne cijeli na default — nedostajuća polja samo preuzmu svoj default.
    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        let d = AppSettings()
        intervalMinutes = try c.decodeIfPresent(Int.self, forKey: .intervalMinutes) ?? d.intervalMinutes
        promptStyle = try c.decodeIfPresent(PromptStyle.self, forKey: .promptStyle) ?? d.promptStyle
        soundEnabled = try c.decodeIfPresent(Bool.self, forKey: .soundEnabled) ?? d.soundEnabled
        idleDetectionEnabled = try c.decodeIfPresent(Bool.self, forKey: .idleDetectionEnabled) ?? d.idleDetectionEnabled
        idleThresholdMinutes = try c.decodeIfPresent(Int.self, forKey: .idleThresholdMinutes) ?? d.idleThresholdMinutes
        lockPauseEnabled = try c.decodeIfPresent(Bool.self, forKey: .lockPauseEnabled) ?? d.lockPauseEnabled
        historyLimit = try c.decodeIfPresent(Int.self, forKey: .historyLimit) ?? d.historyLimit
        mergeAdjacentEntries = try c.decodeIfPresent(Bool.self, forKey: .mergeAdjacentEntries) ?? d.mergeAdjacentEntries
        launchAtLogin = try c.decodeIfPresent(Bool.self, forKey: .launchAtLogin) ?? d.launchAtLogin
        showStartupReminder = try c.decodeIfPresent(Bool.self, forKey: .showStartupReminder) ?? d.showStartupReminder
        autoStopEnabled = try c.decodeIfPresent(Bool.self, forKey: .autoStopEnabled) ?? d.autoStopEnabled
        autoStopHour = try c.decodeIfPresent(Int.self, forKey: .autoStopHour) ?? d.autoStopHour
        autoStopMinute = try c.decodeIfPresent(Int.self, forKey: .autoStopMinute) ?? d.autoStopMinute
        workdayStartEnabled = try c.decodeIfPresent(Bool.self, forKey: .workdayStartEnabled) ?? d.workdayStartEnabled
        workdayStartHour = try c.decodeIfPresent(Int.self, forKey: .workdayStartHour) ?? d.workdayStartHour
        workdayStartMinute = try c.decodeIfPresent(Int.self, forKey: .workdayStartMinute) ?? d.workdayStartMinute
        workdayStartBackfillEnabled = try c.decodeIfPresent(Bool.self, forKey: .workdayStartBackfillEnabled) ?? d.workdayStartBackfillEnabled
    }
}

/// Jedan blok (ili spojeni niz blokova) unutar prompt perioda, s pripadajućim opisom.
struct PromptSegment {
    var start: Date
    var end: Date
    var text: String
}

struct PromptRequest {
    struct PendingPause {
        var start: Date
        var reason: String
    }

    var start: Date
    /// Fiksni kraj perioda; nil znači "do trenutka odgovora".
    var end: Date?
    var pauseAfter: PendingPause?
    var isFinal = false
    var note: String?
    var allowSnooze = true
}

// MARK: - Dnevni pregled (grupiranje)

struct GroupSummary: Identifiable {
    var id: String { text }
    var text: String
    var total: TimeInterval
    var ranges: [(start: Date, end: Date)]
}

/// Red kronološkog pregleda — jedan unos ili niz spojenih susjednih unosa istog naziva.
struct ChronoRow: Identifiable {
    var ids: [UUID]
    var start: Date
    var end: Date
    var text: String
    var kind: EntryKind

    var id: UUID { ids[0] }
    var duration: TimeInterval { end.timeIntervalSince(start) }
    var isMerged: Bool { ids.count > 1 }
}

enum Summarize {
    /// Kronološki popis unosa. Uz `merging` susjedni unosi istog naziva i vrste, gdje
    /// jedan završava kad drugi počinje, čine jedan red (14:45–15:00 + 15:00–15:15 →
    /// 14:45–15:15). Spajaju se samo neposredni susjedi, pa pauza ili drugi opis između
    /// prekida niz.
    static func chronology(_ entries: [Entry], merging: Bool) -> [ChronoRow] {
        var rows: [ChronoRow] = []
        for e in entries.sorted(by: { $0.start < $1.start }) {
            let text = e.text.trimmingCharacters(in: .whitespacesAndNewlines)
            if merging, var last = rows.last,
               last.kind == e.kind,
               last.text == text,
               abs(e.start.timeIntervalSince(last.end)) <= 1 {
                last.ids.append(e.id)
                last.end = max(last.end, e.end)
                rows[rows.count - 1] = last
            } else {
                rows.append(ChronoRow(ids: [e.id], start: e.start, end: e.end, text: text, kind: e.kind))
            }
        }
        return rows
    }

    /// Grupira work unose po tekstu, spaja susjedne intervale istog teksta.
    static func groups(from entries: [Entry]) -> [GroupSummary] {
        let work = entries.filter { $0.kind == .work }.sorted { $0.start < $1.start }
        var byText: [String: GroupSummary] = [:]
        var order: [String] = []

        for e in work {
            let key = e.text.trimmingCharacters(in: .whitespacesAndNewlines)
            if var g = byText[key] {
                if let last = g.ranges.last, e.start.timeIntervalSince(last.end) < 90 {
                    g.ranges[g.ranges.count - 1].end = max(last.end, e.end)
                } else {
                    g.ranges.append((e.start, e.end))
                }
                g.total += e.duration
                byText[key] = g
            } else {
                byText[key] = GroupSummary(text: key, total: e.duration, ranges: [(e.start, e.end)])
                order.append(key)
            }
        }
        return order.compactMap { byText[$0] }.sorted { $0.total > $1.total }
    }

    static func workTotal(_ entries: [Entry]) -> TimeInterval {
        entries.filter { $0.kind == .work }.reduce(0) { $0 + $1.duration }
    }

    static func pauseTotal(_ entries: [Entry]) -> TimeInterval {
        entries.filter { $0.kind == .pause }.reduce(0) { $0 + $1.duration }
    }

    static func clipboardText(for date: Date, entries: [Entry]) -> String {
        var out = "LLOYDS TRACKER — \(Fmt.dayTitle.string(from: date))\n"
        out += "Ukupno rad: \(Fmt.dur(workTotal(entries)))"
        let pauses = pauseTotal(entries)
        if pauses > 0 { out += " · Pauze: \(Fmt.dur(pauses))" }
        out += "\n\n"
        for g in groups(from: entries) {
            out += "\(Fmt.dur(g.total)) — \(g.text)\n"
            let ranges = g.ranges.map { "\(Fmt.hhmm($0.start))–\(Fmt.hhmm($0.end))" }.joined(separator: " · ")
            out += "    \(ranges)\n"
        }
        return out
    }

    static func csv(entries: [Entry]) -> String {
        let iso = ISO8601DateFormatter()
        var out = "start,end,minutes,text,kind\n"
        for e in entries.sorted(by: { $0.start < $1.start }) {
            let text = "\"" + e.text.replacingOccurrences(of: "\"", with: "\"\"") + "\""
            let minutes = Int((e.duration / 60).rounded())
            out += "\(iso.string(from: e.start)),\(iso.string(from: e.end)),\(minutes),\(text),\(e.kind.rawValue)\n"
        }
        return out
    }
}
