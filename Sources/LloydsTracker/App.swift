import SwiftUI

final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationDidFinishLaunching(_ notification: Notification) {
        // Menu bar aplikacija — bez ikone u Docku (i kad se pokreće izvan .app bundle-a).
        NSApp.setActivationPolicy(.accessory)
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
        Image(systemName: engine.menuIcon)
    }
}
