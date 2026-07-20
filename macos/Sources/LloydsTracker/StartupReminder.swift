import AppKit
import SwiftUI

/// Pop-up koji iskoči kod pokretanja aplikacije kao podsjetnik da se pokrene
/// radni dan — inače se lako zaboravi startati tracking.
@MainActor
final class StartupReminderController {
    private var window: NSWindow?

    var isVisible: Bool { window != nil }

    func show(
        dayTitle: String,
        onStart: @escaping () -> Void,
        onDismiss: @escaping () -> Void
    ) {
        guard window == nil else { return }

        let view = StartupReminderView(
            dayTitle: dayTitle,
            onStart: { [weak self] in
                self?.close()
                onStart()
            },
            onDismiss: { [weak self] in
                self?.close()
                onDismiss()
            }
        )
        let hosting = NSHostingView(rootView: view)
        let size = hosting.fittingSize

        // Isti floating panel kao kod prompta (gornji desni kut, iznad ostalih prozora).
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
    }

    func close() {
        window?.orderOut(nil)
        window = nil
    }
}

struct StartupReminderView: View {
    let dayTitle: String
    let onStart: () -> Void
    let onDismiss: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            HStack(spacing: 8) {
                Rectangle().fill(Color.lloydsYellow).frame(width: 18, height: 18)
                Text("LLOYDS")
                    .font(.system(size: 13, weight: .heavy))
                    .tracking(2)
                    .foregroundStyle(.white)
                Text("TRACKER")
                    .font(.system(size: 13, weight: .light))
                    .tracking(2)
                    .foregroundStyle(Color.lloydsGray)
                Spacer()
                Text(dayTitle)
                    .font(.system(size: 10))
                    .foregroundStyle(Color.lloydsGray.opacity(0.7))
            }

            VStack(alignment: .leading, spacing: 6) {
                Text("Novi radni dan?")
                    .font(.system(size: 20, weight: .heavy))
                    .foregroundStyle(.white)
                Text("Tracking još nije pokrenut. Klikni Start da počneš bilježiti vrijeme.")
                    .font(.system(size: 12))
                    .foregroundStyle(Color.lloydsGray)
                    .fixedSize(horizontal: false, vertical: true)
            }

            HStack(spacing: 10) {
                Button(action: onStart) {
                    HStack(spacing: 6) {
                        Image(systemName: "play.fill")
                            .font(.system(size: 11, weight: .bold))
                        Text("Start — počni radni dan")
                            .font(.system(size: 13, weight: .bold))
                    }
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 10)
                    .background(RoundedRectangle(cornerRadius: 10).fill(Color.lloydsYellow))
                    .foregroundStyle(Color.lloydsBlack)
                }
                .buttonStyle(.plain)

                Button(action: onDismiss) {
                    Text("Kasnije")
                        .font(.system(size: 13, weight: .semibold))
                        .foregroundStyle(Color.lloydsGray)
                        .padding(.vertical, 10)
                        .padding(.horizontal, 16)
                        .background(
                            RoundedRectangle(cornerRadius: 10)
                                .stroke(Color.white.opacity(0.2), lineWidth: 1)
                        )
                }
                .buttonStyle(.plain)
            }
        }
        .padding(22)
        .frame(width: 360)
        .background(
            RoundedRectangle(cornerRadius: 16, style: .continuous)
                .fill(Color.lloydsBlack)
        )
        .overlay(
            RoundedRectangle(cornerRadius: 16, style: .continuous)
                .stroke(Color.lloydsYellow.opacity(0.35), lineWidth: 1)
        )
        .preferredColorScheme(.dark)
        .onExitCommand(perform: onDismiss)
    }
}
