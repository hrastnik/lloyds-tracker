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
