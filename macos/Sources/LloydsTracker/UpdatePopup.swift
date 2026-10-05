import AppKit
import SwiftUI

/// Pop-up koji jednom po verziji javi da je izašla nova verzija. Poslije toga obavijest
/// ostaje samo u meniju.
@MainActor
final class UpdatePopupController {
    private var window: NSWindow?

    var isVisible: Bool { window != nil }

    func show(update: AvailableUpdate, currentVersion: String, onDownload: @escaping () -> Void) {
        guard window == nil else { return }

        let view = UpdatePopupView(
            update: update,
            currentVersion: currentVersion,
            onDownload: { [weak self] in
                self?.close()
                onDownload()
            },
            onDismiss: { [weak self] in self?.close() }
        )
        let hosting = NSHostingView(rootView: view)
        let size = hosting.fittingSize

        // Isti floating panel kao podsjetnik (gornji desni kut, iznad ostalih prozora).
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
        let f = PromptController.promptScreen().visibleFrame
        panel.setFrameOrigin(NSPoint(x: f.maxX - size.width - 24, y: f.maxY - size.height - 24))
        panel.isReleasedWhenClosed = false
        PanelFade.appear(panel)
        window = panel
    }

    func close() {
        window?.orderOut(nil)
        window = nil
    }
}

struct UpdatePopupView: View {
    let update: AvailableUpdate
    let currentVersion: String
    let onDownload: () -> Void
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
            }

            VStack(alignment: .leading, spacing: 6) {
                Text("Nova verzija \(update.version)")
                    .font(.system(size: 20, weight: .heavy))
                    .foregroundStyle(.white)
                Text("Imaš verziju \(currentVersion). Novu preuzmi s GitHuba i zamijeni postojeću aplikaciju. Poveznica ostaje i u meniju.")
                    .font(.system(size: 12))
                    .foregroundStyle(Color.lloydsGray)
                    .fixedSize(horizontal: false, vertical: true)
            }

            HStack(spacing: 10) {
                Button(action: onDownload) {
                    HStack(spacing: 6) {
                        Image(systemName: "arrow.down.circle.fill")
                            .font(.system(size: 12, weight: .bold))
                        Text("Preuzmi")
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
