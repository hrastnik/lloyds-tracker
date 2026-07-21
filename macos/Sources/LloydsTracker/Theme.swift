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

    static func countdown(_ seconds: TimeInterval) -> String {
        let total = max(0, Int(seconds.rounded()))
        return String(format: "%02d:%02d", total / 60, total % 60)
    }
}
