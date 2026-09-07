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

/// Blago pojavljivanje pop-upa: fade + kratki pomak umjesto "upada" iz ničega.
///
/// Uz to, prozor **ne uzima tipkovnicu odmah**. Pop-up koji istog trenutka postane key
/// pokrade tipkanje u pola riječi — npr. iskoči dok pišeš u drugoj aplikaciji i ostatak
/// rečenice završi u polju prompta (pa i pregazi pre-fill). Zato se key status preuzima
/// tek nakon `keyDelay`, a i fokus na polje u view sloju čeka isto toliko.
enum PanelFade {
    static let duration: TimeInterval = 0.28
    /// Koliko pop-up čeka prije nego preuzme tipkovnicu.
    static let keyDelay: TimeInterval = 0.9

    /// Prozor mora već biti na svojoj konačnoj poziciji — animira se prema njoj.
    /// `slide` je početni odmak po Y (pozitivno = spušta se odozgo).
    /// `keyDelay: nil` znači "uzmi tipkovnicu odmah".
    @MainActor
    static func appear(_ window: NSWindow, slide: CGFloat = 10, keyDelay: TimeInterval? = keyDelay) {
        let target = window.frame
        window.alphaValue = 0
        window.setFrame(target.offsetBy(dx: 0, dy: slide), display: false)
        window.orderFrontRegardless()
        NSAnimationContext.runAnimationGroup { ctx in
            ctx.duration = duration
            ctx.timingFunction = CAMediaTimingFunction(name: .easeOut)
            window.animator().alphaValue = 1
            window.animator().setFrame(target, display: true)
        }
        guard let keyDelay else {
            window.makeKey()
            return
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + keyDelay) { [weak window] in
            guard let window, window.isVisible else { return }
            window.makeKey()
        }
    }
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
        onSubmit: @escaping (PromptResult) -> Void,
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
            onSubmit: { [weak self] result in
                self?.close()
                onSubmit(result)
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
            PanelFade.appear(panel)
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
            // Prompt se često otvori dok je ekran zaključan; AppKit prozor tad može
            // smjestiti na drugi ekran nego što je zatražen, pa nakon otključavanja
            // ostane u dimenzijama vanjskog monitora. Frame se zato postavlja izričito
            // i ponovno provjerava kad se ekrani slegnu.
            win.setFrame(screen.frame, display: true)
            // Preko cijelog ekrana pomak ne treba — samo fade.
            PanelFade.appear(win, slide: 0)
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

    /// Vraća već otvoreni prompt u prvi plan — "Zapiši sada" dok prompt visi.
    func focus() {
        window?.makeKeyAndOrderFront(nil)
    }

    func close() {
        window?.orderOut(nil)
        window = nil
        model = nil
    }
}
