import SwiftUI

final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationDidFinishLaunching(_ notification: Notification) {
        // Menu bar aplikacija — bez ikone u Docku (i kad se pokreće izvan .app bundle-a).
        NSApp.setActivationPolicy(.accessory)

        // Cijeli UI je tamni (lloydsBlack pozadine). AppKit kontrole poput
        // borderless Menu-a ("Pauziraj") prate appearance prozora, a ne SwiftUI
        // .preferredColorScheme — pa im tekst na light Macu ispadne crn i nevidljiv.
        // Forsiramo dark appearance globalno da se boje razriješe za tamnu podlogu.
        NSApp.appearance = NSAppearance(named: .darkAqua)
    }
}

@main
struct LloydsTrackerApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate
    @StateObject private var engine = TrackerEngine()

    var body: some Scene {
        MenuBarExtra {
            MenuBarView(engine: engine)
        } label: {
            MenuBarLabel(engine: engine)
        }
        .menuBarExtraStyle(.window)

        Window("Pregled dana", id: "summary") {
            SummaryView(engine: engine)
        }
        .windowResizability(.contentMinSize)
        .defaultSize(width: 560, height: 520)

        Settings {
            SettingsView(engine: engine)
        }
    }
}

struct MenuBarLabel: View {
    @ObservedObject var engine: TrackerEngine

    var body: some View {
        // Non-template image so the brand color survives in the menu bar — a bare
        // SF Symbol would be tinted monochrome (and a white glyph is invisible on a
        // light menu bar). Mirrors the Windows TrayIconFactory tile.
        Image(nsImage: MenuBarIcon.image(for: engine.menuIcon))
    }
}

/// Draws the menu bar glyph as the Lloyds brand tile: a yellow rounded square with
/// a black SF Symbol on top. Rendered non-template and resolution-independent, so it
/// stays crisp on Retina and legible on both a light and a dark menu bar. The
/// counterpart of the Windows `TrayIconFactory`.
enum MenuBarIcon {
    static func image(for symbolName: String) -> NSImage {
        let side: CGFloat = 18
        let image = NSImage(size: NSSize(width: side, height: side), flipped: false) { rect in
            let tile = NSBezierPath(
                roundedRect: rect.insetBy(dx: 0.5, dy: 0.5),
                xRadius: 4, yRadius: 4)
            NSColor.lloydsYellow.setFill()
            tile.fill()

            let config = NSImage.SymbolConfiguration(pointSize: 11, weight: .semibold)
            guard let symbol = NSImage(systemSymbolName: symbolName, accessibilityDescription: nil)?
                .withSymbolConfiguration(config) else { return true }
            // SF Symbols are template images — draw() renders them black, which is
            // exactly what we want on the yellow tile.
            let size = symbol.size
            let origin = NSPoint(x: (rect.width - size.width) / 2,
                                 y: (rect.height - size.height) / 2)
            symbol.draw(in: NSRect(origin: origin, size: size))
            return true
        }
        image.isTemplate = false
        return image
    }
}
