import AppKit
import SwiftUI

/// Borderless panel koji može postati key (potrebno za tipkanje).
final class KeyablePanel: NSPanel {
    override var canBecomeKey: Bool { true }
}

final class KeyableWindow: NSWindow {
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { true }
}

/// Promjenjivo stanje perioda dok je prompt vidljiv. Kad neodgovoren prompt "preživi"
/// granicu intervala, produžimo `end` (skupno vrijeme) umjesto da otvaramo novi prompt.
@MainActor
final class PromptModel: ObservableObject {
    let start: Date
    @Published var end: Date

    init(start: Date, end: Date) {
        self.start = start
        self.end = max(end, start)
    }
}

@MainActor
final class PromptController {
    private var window: NSWindow?
    private var model: PromptModel?
    private var style: PromptStyle = .floating
    private var screenParamsObserver: (any NSObjectProtocol)?
    private var unlockObserver: (any NSObjectProtocol)?

    var isVisible: Bool { window != nil }

    init() {
        // Raspored ekrana se može promijeniti dok prompt stoji otvoren (uspavan/odspojen
        // vanjski monitor, otključavanje laptopa) — fullscreen prozor tad ostane u
        // dimenzijama starog ekrana, pa ga treba ponovno prilijepiti na aktualni.
        screenParamsObserver = NotificationCenter.default.addObserver(
            forName: NSApplication.didChangeScreenParametersNotification,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            Task { @MainActor in self?.refitToScreenSoon() }
        }
        unlockObserver = DistributedNotificationCenter.default().addObserver(
            forName: .init("com.apple.screenIsUnlocked"),
            object: nil,
            queue: .main
        ) { [weak self] _ in
            Task { @MainActor in self?.refitToScreenSoon() }
        }
    }

    deinit {
        if let screenParamsObserver { NotificationCenter.default.removeObserver(screenParamsObserver) }
        if let unlockObserver { DistributedNotificationCenter.default().removeObserver(unlockObserver) }
    }

    /// Ekran na kojem prompt treba biti. `NSScreen.main` je nepouzdan dok je ekran
    /// zaključan ili je vanjski monitor uspavan (vrati ekran na kojem prozor neće
    /// završiti), pa prvo gledamo gdje je miš.
    static func promptScreen() -> NSScreen {
        let mouse = NSEvent.mouseLocation
        if let screen = NSScreen.screens.first(where: { NSMouseInRect(mouse, $0.frame, false) }) {
            return screen
        }
        return NSScreen.main ?? NSScreen.screens[0]
    }

    /// Nakon promjene ekrana koordinate se slegnu s malim zakašnjenjem.
    private func refitToScreenSoon() {
        guard window != nil else { return }
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.35) { [weak self] in
            Task { @MainActor in self?.refitToScreen() }
        }
    }

    /// Fullscreen prompt drži točno preko jednog ekrana; floating panel vrati unutar
    /// vidljivog okvira ako je ostao izvan ekrana.
    private func refitToScreen() {
        guard let window else { return }
        switch style {
        case .fullscreen:
            let screen = window.screen ?? Self.promptScreen()
            if window.frame != screen.frame {
                window.setFrame(screen.frame, display: true, animate: false)
            }
        case .floating:
            let visible = (window.screen ?? Self.promptScreen()).visibleFrame
            var frame = window.frame
            frame.size.width = min(frame.width, visible.width)
            frame.size.height = min(frame.height, visible.height)
            frame.origin.x = min(max(frame.minX, visible.minX), visible.maxX - frame.width)
            frame.origin.y = min(max(frame.minY, visible.minY), visible.maxY - frame.height)
            if frame != window.frame {
                window.setFrame(frame, display: true, animate: false)
            }
        }
    }

    func show(
        request: PromptRequest,
        style: PromptStyle,
        history: [String],
        onSubmit: @escaping ([PromptSegment]) -> Void,
        onSnooze: @escaping () -> Void
    ) {
        guard window == nil else { return }

        let model = PromptModel(start: request.start, end: request.end ?? Date())
        self.model = model
        self.style = style

        let view = PromptView(
            request: request,
            model: model,
            style: style,
            history: history,
            onSubmit: { [weak self] segments in
                self?.close()
                onSubmit(segments)
            },
            onSnooze: { [weak self] in
                self?.close()
                onSnooze()
            },
            onLayoutChange: { [weak self] in
                self?.resizeFloatingToFit()
            }
        )
        let hosting = NSHostingView(rootView: view)

        switch style {
        case .floating:
            let size = hosting.fittingSize
            let panel = KeyablePanel(
                contentRect: NSRect(origin: .zero, size: size),
                styleMask: [.borderless, .nonactivatingPanel],
                backing: .buffered,
                defer: false
            )
            panel.level = .floating
            panel.isOpaque = false
            panel.backgroundColor = .clear
            panel.hasShadow = true
            panel.isMovableByWindowBackground = true
            panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
            panel.contentView = hosting
            panel.setContentSize(size)
            let f = Self.promptScreen().visibleFrame
            panel.setFrameOrigin(NSPoint(x: f.maxX - size.width - 24, y: f.maxY - size.height - 24))
            panel.isReleasedWhenClosed = false
            panel.makeKeyAndOrderFront(nil)
            window = panel

        case .fullscreen:
            let screen = Self.promptScreen()
            let win = KeyableWindow(
                contentRect: screen.frame,
                styleMask: [.borderless],
                backing: .buffered,
                defer: false
            )
            win.level = .screenSaver
            win.isOpaque = true
            win.backgroundColor = .lloydsBlack
            win.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
            hosting.autoresizingMask = [.width, .height]
            win.contentView = hosting
            win.isReleasedWhenClosed = false
            NSApp.activate(ignoringOtherApps: true)
            win.makeKeyAndOrderFront(nil)
            // Prompt se često otvori dok je ekran zaključan; AppKit prozor tad može
            // smjestiti na drugi ekran nego što je zatražen, pa nakon otključavanja
            // ostane u dimenzijama vanjskog monitora. Frame se zato postavlja izričito
            // i ponovno provjerava kad se ekrani slegnu.
            win.setFrame(screen.frame, display: true)
            window = win
            refitToScreenSoon()
        }
    }

    /// Kad razdvajanje blokova promijeni visinu sadržaja, prilagodi floating panel
    /// (sidren za gornji rub, kao i početno pozicioniranje).
    private func resizeFloatingToFit() {
        guard let panel = window as? KeyablePanel, let content = panel.contentView else { return }
        let size = content.fittingSize
        guard size.height > 0, abs(size.height - panel.frame.height) > 0.5 else { return }
        var frame = panel.frame
        frame.origin.y = frame.maxY - size.height
        frame.size = size
        panel.setFrame(frame, display: true, animate: false)
    }

    /// Produži period vidljivog prompta do nove granice (skupno vrijeme). Bez treptanja —
    /// samo se ažurira model, a upisani tekst i podjele ostaju.
    func extend(to end: Date) {
        guard let model else { return }
        model.end = max(end, model.start)
        // Nove granice mogu promijeniti visinu — poravnaj floating panel.
        DispatchQueue.main.async { [weak self] in self?.resizeFloatingToFit() }
    }

    func close() {
        window?.orderOut(nil)
        window = nil
        model = nil
    }
}
