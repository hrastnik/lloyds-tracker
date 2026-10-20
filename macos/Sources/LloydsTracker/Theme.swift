import SwiftUI

// Lloyds Digital brand — https://lloyds-digital.com/
extension Color {
    static let lloydsYellow = Color(red: 0xFB / 255, green: 0xDE / 255, blue: 0x07 / 255) // #FBDE07
    static let lloydsBlack = Color(red: 0x07 / 255, green: 0x07 / 255, blue: 0x07 / 255)  // #070707
    static let lloydsGray = Color(red: 0xC4 / 255, green: 0xC4 / 255, blue: 0xC4 / 255)   // #C4C4C4
}

extension NSColor {
    static let lloydsBlack = NSColor(red: 0x07 / 255, green: 0x07 / 255, blue: 0x07 / 255, alpha: 1)
    static let lloydsYellow = NSColor(red: 0xFB / 255, green: 0xDE / 255, blue: 0x07 / 255, alpha: 1)
}

enum Fmt {
    static let hr = Locale(identifier: "hr_HR")

    static let time: DateFormatter = {
        let f = DateFormatter()
        f.dateFormat = "HH:mm"
        f.locale = hr
        return f
    }()

    static let dayKey: DateFormatter = {
        let f = DateFormatter()
        f.dateFormat = "yyyy-MM-dd"
        f.locale = Locale(identifier: "en_US_POSIX")
        return f
    }()

    static let dayTitle: DateFormatter = {
        let f = DateFormatter()
        f.dateFormat = "EEEE, d.M.yyyy."
        f.locale = hr
        return f
    }()

    static func hhmm(_ d: Date) -> String { time.string(from: d) }

    static func dur(_ seconds: TimeInterval) -> String {
        let total = Int(seconds.rounded())
        let h = total / 3600
        let m = (total % 3600) / 60
        if h > 0 && m > 0 { return "\(h)h \(m)m" }
        if h > 0 { return "\(h)h" }
        return "\(m)m"
    }

    /// "1 blok", "2 bloka", "5 blokova", "21 blok", "12 blokova" — hrvatska množina.
    static func blocks(_ n: Int) -> String {
        let (last, lastTwo) = (n % 10, n % 100)
        if last == 1 && lastTwo != 11 { return "\(n) blok" }
        if (2...4).contains(last) && !(12...14).contains(lastTwo) { return "\(n) bloka" }
        return "\(n) blokova"
    }

    /// Vrijeme upisano rukom: "9", "930", "0930", "9:30", "9.30" ili "9,30".
    static func parseTime(_ input: String) -> (hour: Int, minute: Int)? {
        let s = input.trimmingCharacters(in: .whitespaces)
            .replacingOccurrences(of: ".", with: ":")
            .replacingOccurrences(of: ",", with: ":")
        var h: Int?
        var m: Int?
        if s.contains(":") {
            let parts = s.split(separator: ":", omittingEmptySubsequences: false)
            guard parts.count == 2, (1...2).contains(parts[0].count), parts[1].count == 2 else { return nil }
            h = Int(parts[0])
            m = Int(parts[1])
        } else {
            guard s.allSatisfy(\.isASCII), s.allSatisfy(\.isNumber) else { return nil }
            switch s.count {
            case 1, 2: h = Int(s); m = 0
            case 3, 4: h = Int(s.dropLast(2)); m = Int(s.suffix(2))
            default: return nil
            }
        }
        guard let hour = h, let minute = m, (0...23).contains(hour), (0...59).contains(minute) else { return nil }
        return (hour, minute)
    }

    /// Isti dan kao `day`, u zadani sat i minutu.
    static func at(_ day: Date, hour: Int, minute: Int) -> Date {
        Calendar.current.date(bySettingHour: hour, minute: minute, second: 0, of: day) ?? day
    }

    /// Najdulji unos koji smije prijeći ponoć kad je kraj upisan "prije" početka — 23:30–00:30
    /// je unos preko ponoći, a 10:00–9:00 tipfeler (ne unos od 23 sata).
    static let maxOvernightSpan: TimeInterval = 12 * 3600

    /// Tumači vremena iz uređivača unosa ("9:30") za dan pregleda `day`:
    /// - vrijeme koje se nije promijenilo (isti HH:mm kao u `original`) vraća originalni
    ///   trenutak, sa sekundama — inače bi ispravak samo opisa pomaknuo unos za par sekundi
    ///   (pa spojio blokove i rezao susjede);
    /// - unos koji je već iza ponoći (u fileu prethodnog dana) i dalje se tumači iza ponoći;
    /// - kraj koji nije nakon početka je sutradan (…–00:00, 23:30–00:30), do `maxOvernightSpan`.
    static func editRange(
        start startText: String,
        end endText: String,
        day: Date,
        original: (start: Date, end: Date)?
    ) -> (start: Date?, end: Date?) {
        let cal = Calendar.current
        let dayStart = cal.startOfDay(for: day)
        let nextDay = cal.date(byAdding: .day, value: 1, to: dayStart) ?? dayStart.addingTimeInterval(24 * 3600)

        func unchanged(_ t: (hour: Int, minute: Int), _ date: Date) -> Bool {
            let c = cal.dateComponents([.hour, .minute], from: date)
            return c.hour == t.hour && c.minute == t.minute
        }

        var start: Date?
        if let t = parseTime(startText) {
            if let o = original, unchanged(t, o.start) {
                start = o.start
            } else {
                let sameDay = at(dayStart, hour: t.hour, minute: t.minute)
                // Unos koji seže iza ponoći: bliži od dva kandidata originalnom početku
                // (00:10 za unos 23:30–00:30 je sutradan, 23:45 isti dan).
                if let o = original, o.end > nextDay {
                    let following = at(nextDay, hour: t.hour, minute: t.minute)
                    start = abs(following.timeIntervalSince(o.start)) < abs(sameDay.timeIntervalSince(o.start))
                        ? following : sameDay
                } else {
                    start = sameDay
                }
            }
        }

        var end: Date?
        if let t = parseTime(endText) {
            if let o = original, unchanged(t, o.end) {
                end = o.end
            } else {
                let base = cal.startOfDay(for: start ?? dayStart)
                var e = at(base, hour: t.hour, minute: t.minute)
                if let s = start, e <= s,
                   let next = cal.date(byAdding: .day, value: 1, to: base) {
                    let rolled = at(next, hour: t.hour, minute: t.minute)
                    if rolled.timeIntervalSince(s) <= maxOvernightSpan { e = rolled }
                }
                end = e
            }
        }
        return (start, end)
    }

    static func countdown(_ seconds: TimeInterval) -> String {
        let total = max(0, Int(seconds.rounded()))
        return String(format: "%02d:%02d", total / 60, total % 60)
    }
}

// MARK: - Površina za klik

extension View {
    /// Cijeli okvir gumba prima klik. `.plain` gumb inače reagira samo na glif/tekst i
    /// ispunjenu podlogu — prazan prostor unutar obruba (`.stroke`) ili oko ikone ne.
    func clickArea(cornerRadius: CGFloat = 0) -> some View {
        contentShape(RoundedRectangle(cornerRadius: cornerRadius))
    }
}

/// Gumb bez okvira s hover podlogom i jasnom površinom za klik (strelice dana, redovi
/// kronološkog pregleda). Label dobije `hovering` da može i sam reagirati.
struct HoverButton<Content: View>: View {
    var cornerRadius: CGFloat = 6
    var baseFill: Color = .clear
    var hoverFill: Color = Color.white.opacity(0.08)
    let action: () -> Void
    @ViewBuilder let label: (_ hovering: Bool) -> Content

    @State private var hovering = false
    @Environment(\.isEnabled) private var isEnabled

    var body: some View {
        let active = hovering && isEnabled
        Button(action: action) {
            label(active)
                .background(RoundedRectangle(cornerRadius: cornerRadius).fill(active ? hoverFill : baseFill))
                .clickArea(cornerRadius: cornerRadius)
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
    }
}
