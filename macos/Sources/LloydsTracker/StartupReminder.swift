import AppKit
import SwiftUI

/// Pop-up koji iskoči kod pokretanja aplikacije i u zadano vrijeme početka radnog dana
/// kao podsjetnik da se pokrene radni dan — inače se lako zaboravi startati tracking.
@MainActor
final class StartupReminderController {
    private var window: NSWindow?
    /// Kad je podsjetnik prikazan i s kojom ponudom nadoknade — po tome se vidi je li
    /// otvoreni prozor u međuvremenu zastario (prenoćio, ili je radni dan tek počeo).
    private(set) var shownAt: Date?
    private(set) var shownBackfillFrom: Date?

    var isVisible: Bool { window != nil }

    /// `startOptions` (od početka radnog dana koji je već prošao do početka trenutnog
    /// intervala, u koracima intervala) dodaje strelice ‹ › kojima se pomiče početak.
    /// `onStart` dobije odabrano vrijeme ili nil za start od sada.
    func show(
        dayTitle: String,
        startOptions: [Date],
        onStart: @escaping (Date?) -> Void,
        onDismiss: @escaping () -> Void
    ) {
        guard window == nil else { return }

        let view = StartupReminderView(
            dayTitle: dayTitle,
            startOptions: startOptions,
            onStart: { [weak self] from in
                self?.close()
                onStart(from)
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
        let f = PromptController.promptScreen().visibleFrame
        panel.setFrameOrigin(NSPoint(x: f.maxX - size.width - 24, y: f.maxY - size.height - 24))
        panel.isReleasedWhenClosed = false
        PanelFade.appear(panel)
        window = panel
        shownAt = Date()
        shownBackfillFrom = startOptions.first
    }

    func close() {
        window?.orderOut(nil)
        window = nil
        shownAt = nil
        shownBackfillFrom = nil
    }
}

struct StartupReminderView: View {
    let dayTitle: String
    /// Nije prazno → nudi se i nadoknada jutra: početak se strelicama pomiče od početka
    /// radnog dana do početka trenutnog intervala, u koracima intervala.
    let startOptions: [Date]
    /// Parametar je odabrano vrijeme ili nil za start od sada.
    let onStart: (Date?) -> Void
    let onDismiss: () -> Void

    /// Indeks u `startOptions`. Zadano je početak radnog dana — to je razlog zašto je
    /// pop-up uopće iskočio u zadano vrijeme.
    @State private var selection = 0

    private var selectedStart: Date? {
        startOptions.indices.contains(selection) ? startOptions[selection] : nil
    }

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
                Text(subtitle)
                    .font(.system(size: 12))
                    .foregroundStyle(Color.lloydsGray)
                    .fixedSize(horizontal: false, vertical: true)
            }

            HStack(spacing: 10) {
                // Vrijeme je u samom gumbu, a ‹ › ga pomiču za jedan interval — jedan klik
                // za zadano, bez zasebnog izbornika.
                HStack(spacing: 2) {
                    if !startOptions.isEmpty {
                        stepButton("chevron.left", enabled: selection > 0) { selection -= 1 }
                    }
                    Button { onStart(selectedStart) } label: {
                        HStack(spacing: 6) {
                            Image(systemName: "play.fill")
                                .font(.system(size: 11, weight: .bold))
                            Text(startTitle)
                                .font(.system(size: 13, weight: .bold))
                                .monospacedDigit()
                        }
                        .frame(maxWidth: .infinity)
                        .padding(.vertical, 10)
                        .background(RoundedRectangle(cornerRadius: 10).fill(Color.lloydsYellow))
                        .foregroundStyle(Color.lloydsBlack)
                    }
                    .buttonStyle(.plain)
                    if !startOptions.isEmpty {
                        stepButton("chevron.right", enabled: selection < startOptions.count - 1) { selection += 1 }
                    }
                }

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

    private var startTitle: String {
        selectedStart.map { "Start od \(Fmt.hhmm($0))" } ?? "Start — počni radni dan"
    }

    /// Strelica uz gumb Start — pomiče početak za jedan interval.
    private func stepButton(_ systemName: String, enabled: Bool, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: systemName)
                .font(.system(size: 12, weight: .heavy))
                .frame(width: 30)
                .padding(.vertical, 10)
                .background(RoundedRectangle(cornerRadius: 10).fill(Color.lloydsYellow.opacity(enabled ? 1 : 0.35)))
                .foregroundStyle(Color.lloydsBlack.opacity(enabled ? 1 : 0.5))
        }
        .buttonStyle(.plain)
        .disabled(!enabled)
    }

    private var subtitle: String {
        guard let from = startOptions.first else {
            return "Tracking još nije pokrenut. Klikni Start da počneš bilježiti vrijeme."
        }
        return "Radni dan počinje u \(Fmt.hhmm(from)), a tracking još nije pokrenut. Strelicama pomakni početak — prvi prompt te onda pita i za propušteno vrijeme."
    }
}
