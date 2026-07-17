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

@MainActor
final class PromptController {
    private var window: NSWindow?

    var isVisible: Bool { window != nil }

    func show(
        request: PromptRequest,
        style: PromptStyle,
        history: [String],
        onSubmit: @escaping ([PromptSegment]) -> Void,
        onSnooze: @escaping () -> Void
    ) {
        guard window == nil else { return }

        let view = PromptView(
            request: request,
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
            if let screen = NSScreen.main {
                let f = screen.visibleFrame
                panel.setFrameOrigin(NSPoint(x: f.maxX - size.width - 24, y: f.maxY - size.height - 24))
            }
            panel.isReleasedWhenClosed = false
            panel.makeKeyAndOrderFront(nil)
            window = panel

        case .fullscreen:
            let screen = NSScreen.main ?? NSScreen.screens[0]
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
            window = win
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

    func close() {
        window?.orderOut(nil)
        window = nil
    }
}
