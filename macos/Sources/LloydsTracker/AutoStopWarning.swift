import AppKit
import SwiftUI

/// Upozorenje minutu prije automatskog zaustavljanja radnog dana. Daje priliku da se
/// dan produži (samo za danas); ako se ne reagira, engine zatvori dan sam — tako
/// tracking ne ostane pokrenut preko noći.
@MainActor
final class AutoStopWarningController {
    private var window: NSWindow?

    var isVisible: Bool { window != nil }

    func show(
        stopAt: Date,
        lead: TimeInterval,
        onExtend: @escaping (Int) -> Void,
        onStopNow: @escaping () -> Void,
        onDismiss: @escaping () -> Void
    ) {
        guard window == nil else { return }

        let view = AutoStopWarningView(
            stopAt: stopAt,
            lead: lead,
            onExtend: { [weak self] minutes in
                self?.close()
                onExtend(minutes)
            },
            onStopNow: { [weak self] in
                self?.close()
                onStopNow()
            },
            onDismiss: { [weak self] in
                self?.close()
                onDismiss()
            }
        )
        let hosting = NSHostingView(rootView: view)
        let size = hosting.fittingSize

        // Isti floating panel kao prompt, ali u donjem desnom kutu — prompt sjedi u
        // gornjem desnom, pa se ne prekrivaju ako oba budu vidljiva.
        let panel = KeyablePanel(
            contentRect: NSRect(origin: .zero, size: size),
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false
        )
        // Iznad svega (uklj. fullscreen prompt na .screenSaver razini) — upozorenje traje
        // minutu, pa ne smije ostati skriveno ispod drugog prozora.
        panel.level = .screenSaver
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = true
        panel.isMovableByWindowBackground = true
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        panel.contentView = hosting
        panel.setContentSize(size)
        let f = PromptController.promptScreen().visibleFrame
        panel.setFrameOrigin(NSPoint(x: f.maxX - size.width - 24, y: f.minY + 24))
        panel.isReleasedWhenClosed = false
        // Sjedi u donjem kutu, pa se "diže" odozdo.
        PanelFade.appear(panel, slide: -10)
        window = panel
    }

    func close() {
        window?.orderOut(nil)
        window = nil
    }
}

struct AutoStopWarningView: View {
    let stopAt: Date
    let lead: TimeInterval
    let onExtend: (Int) -> Void
    let onStopNow: () -> Void
    let onDismiss: () -> Void

    /// Produženja se računaju od zakazanog vremena (16:00 + 30 min → 16:30).
    private let options = [15, 30, 45, 60]

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            header
            countdownBar

            VStack(alignment: .leading, spacing: 6) {
                Text("Zaustavljam tracking")
                    .font(.system(size: 20, weight: .heavy))
                    .foregroundStyle(.white)
                Text("Radni dan se automatski zatvara u \(Fmt.hhmm(stopAt)). Ako još radiš, produži — inače dobiješ pregled dana i tracking se zaustavlja.")
                    .font(.system(size: 12))
                    .foregroundStyle(Color.lloydsGray)
                    .fixedSize(horizontal: false, vertical: true)
            }

            VStack(alignment: .leading, spacing: 8) {
                Text("PRODUŽI — SAMO ZA DANAS")
                    .font(.system(size: 10, weight: .heavy))
                    .tracking(1.5)
                    .foregroundStyle(Color.lloydsGray.opacity(0.8))
                HStack(spacing: 8) {
                    ForEach(options, id: \.self) { minutes in
                        Button {
                            onExtend(minutes)
                        } label: {
                            VStack(spacing: 2) {
                                Text(Self.extendLabel(minutes))
                                    .font(.system(size: 13, weight: .bold))
                                    .foregroundStyle(Color.lloydsYellow)
                                Text(Fmt.hhmm(stopAt.addingTimeInterval(TimeInterval(minutes * 60))))
                                    .font(.system(size: 10, design: .monospaced))
                                    .foregroundStyle(Color.lloydsYellow.opacity(0.6))
                            }
                            .frame(maxWidth: .infinity)
                            .padding(.vertical, 8)
                            .background(
                                RoundedRectangle(cornerRadius: 8)
                                    .fill(Color.lloydsYellow.opacity(0.12))
                                    .overlay(
                                        RoundedRectangle(cornerRadius: 8)
                                            .stroke(Color.lloydsYellow.opacity(0.5), lineWidth: 1)
                                    )
                            )
                        }
                        .buttonStyle(.plain)
                        .help("Produži do \(Fmt.hhmm(stopAt.addingTimeInterval(TimeInterval(minutes * 60))))")
                    }
                }
            }

            Button(action: onStopNow) {
                HStack(spacing: 6) {
                    Image(systemName: "stop.fill")
                        .font(.system(size: 10, weight: .bold))
                    // Period se i tako bilježi do zakazanog vremena, pa to piše na gumbu.
                    Text("Zaustavi sad — bilježi do \(Fmt.hhmm(stopAt))")
                        .font(.system(size: 13, weight: .semibold))
                }
                .frame(maxWidth: .infinity)
                .padding(.vertical, 9)
                .foregroundStyle(Color.lloydsGray)
                .background(
                    RoundedRectangle(cornerRadius: 8)
                        .stroke(Color.white.opacity(0.2), lineWidth: 1)
                )
            }
            .buttonStyle(.plain)
        }
        .padding(22)
        .frame(width: 380)
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

    static func extendLabel(_ minutes: Int) -> String {
        minutes >= 60 ? "+1 h" : "+\(minutes) min"
    }

    private var header: some View {
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
            TimelineView(.periodic(from: .now, by: 1)) { context in
                Text(Fmt.countdown(stopAt.timeIntervalSince(context.date)))
                    .font(.system(size: 13, weight: .bold, design: .monospaced))
                    .foregroundStyle(Color.lloydsYellow)
                    .monospacedDigit()
            }
        }
    }

    /// Traka koja se prazni do zaustavljanja — odmah je vidljivo koliko vremena ostaje.
    private var countdownBar: some View {
        TimelineView(.periodic(from: .now, by: 0.25)) { context in
            let remaining = max(0, min(lead, stopAt.timeIntervalSince(context.date)))
            GeometryReader { geo in
                ZStack(alignment: .leading) {
                    Capsule().fill(Color.white.opacity(0.12))
                    Capsule()
                        .fill(Color.lloydsYellow)
                        .frame(width: geo.size.width * (lead > 0 ? remaining / lead : 0))
                }
            }
            .frame(height: 4)
        }
        .frame(height: 4)
    }
}
