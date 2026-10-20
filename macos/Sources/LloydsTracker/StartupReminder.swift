import AppKit
import SwiftUI

/// Pop-up koji iskoči kod pokretanja aplikacije i u zadano vrijeme početka radnog dana
/// kao podsjetnik da se pokrene radni dan — inače se lako zaboravi startati tracking.
@MainActor
final class StartupReminderController {
    private var window: NSWindow?
    private var model: StartupReminderModel?
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

        let model = StartupReminderModel(startOptions: startOptions)
        let view = StartupReminderView(
            dayTitle: dayTitle,
            model: model,
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
        self.model = model
        shownAt = Date()
        shownBackfillFrom = startOptions.first
    }

    /// Ponuda početaka vrijedi za interval u kojem je prozor prikazan — ako stoji satima,
    /// najkasniji početak zaostaje (prikazan u 8:40, u 11:00 i dalje nudi najviše 8:30).
    /// Engine mu na novom intervalu pošalje svježu ponudu; odabrano vrijeme ostaje odabrano
    /// ako je još u ponudi. Samo kad prozor već nudi nadoknadu i nova ponuda nije prazna —
    /// pojavu nadoknade i prenoćeni prozor rješava zamjena prozora u engineu.
    func updateStartOptions(_ options: [Date]) {
        guard let model, !model.startOptions.isEmpty, !options.isEmpty,
              options != model.startOptions else { return }
        let old = model.startOptions
        let selected = old.indices.contains(model.selection) ? old[model.selection] : nil
        model.selection = selected.flatMap { options.firstIndex(of: $0) }
            ?? min(model.selection, options.count - 1)
        model.startOptions = options
        shownBackfillFrom = options.first
    }

    func close() {
        window?.orderOut(nil)
        window = nil
        model = nil
        shownAt = nil
        shownBackfillFrom = nil
    }
}

/// Ponuda početaka i odabir u otvorenom podsjetniku — engine je osvježava kad počne novi
/// interval (vidi `StartupReminderController.updateStartOptions`).
@MainActor
final class StartupReminderModel: ObservableObject {
    @Published var startOptions: [Date]
    /// Indeks u `startOptions`. Zadano je početak radnog dana — to je razlog zašto je
    /// pop-up uopće iskočio u zadano vrijeme.
    @Published var selection = 0

    init(startOptions: [Date]) {
        self.startOptions = startOptions
    }
}

struct StartupReminderView: View {
    let dayTitle: String
    @ObservedObject var model: StartupReminderModel
    /// Parametar je odabrano vrijeme ili nil za start od sada.
    let onStart: (Date?) -> Void
    let onDismiss: () -> Void

    /// Nije prazno → nudi se i nadoknada jutra: početak se strelicama pomiče od početka
    /// radnog dana do početka trenutnog intervala, u koracima intervala.
    private var startOptions: [Date] { model.startOptions }

    private var selection: Int {
        get { model.selection }
        nonmutating set { model.selection = newValue }
    }

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
                // Isto kao header menija — dug naslov ponedjeljkom ne smije u dva reda.
                Spacer(minLength: 0)
                Text(dayTitle)
                    .font(.system(size: 10))
                    .foregroundStyle(Color.lloydsGray.opacity(0.7))
                    .lineLimit(1)
                    .minimumScaleFactor(0.85)
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
                        .clickArea(cornerRadius: 10)
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
