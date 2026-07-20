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
    var idleDetectionEnabled: Bool = true
    var idleThresholdMinutes: Int = 5
    var historyLimit: Int = 15
    var launchAtLogin: Bool = false
    var showStartupReminder: Bool = true
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

enum Summarize {
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
