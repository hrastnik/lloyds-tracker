import CoreGraphics
import Foundation

enum IdleMonitor {
    /// Sekunde od zadnjeg korisničkog inputa (tipkovnica, miš, scroll).
    static func idleSeconds() -> TimeInterval {
        let types: [CGEventType] = [.keyDown, .mouseMoved, .leftMouseDown, .rightMouseDown, .otherMouseDown, .scrollWheel]
        let times = types.map {
            CGEventSource.secondsSinceLastEventType(.combinedSessionState, eventType: $0)
        }
        return times.min() ?? 0
    }
}
