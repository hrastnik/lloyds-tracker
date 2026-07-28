import SwiftUI

struct MenuBarView: View {
    @ObservedObject var engine: TrackerEngine
    @Environment(\.openSettings) private var openSettings
    @Environment(\.openWindow) private var openWindow

    private let maxVisibleEntries = 8

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            header
            Divider().overlay(Color.white.opacity(0.1))
            statusSection
            Divider().overlay(Color.white.opacity(0.1))
            entriesSection
            Divider().overlay(Color.white.opacity(0.1))
            footer
        }
        .frame(width: 340)
        .background(Color.lloydsBlack)
        .preferredColorScheme(.dark)
        .onAppear {
            engine.openSummary = {
                NSApp.activate(ignoringOtherApps: true)
                openWindow(id: "summary")
            }
        }
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
            Text(Fmt.dayTitle.string(from: Date()))
                .font(.system(size: 10))
                .foregroundStyle(Color.lloydsGray.opacity(0.7))
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 12)
    }

    private var statusSection: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Circle()
                    .fill(statusColor)
                    .frame(width: 8, height: 8)
                Text(engine.statusText)
                    .font(.system(size: 12, weight: .semibold))
                    .foregroundStyle(.white)
                Spacer()
                if engine.isTracking, engine.pauseUntil == nil, let next = engine.nextPromptAt {
                    TimelineView(.periodic(from: .now, by: 1)) { context in
                        Text("prompt za \(Fmt.countdown(next.timeIntervalSince(context.date)))")
                            .font(.system(size: 11, design: .monospaced))
                            .foregroundStyle(Color.lloydsYellow)
                    }
                }
            }

            if let autoStop = engine.autoStopText {
                HStack(spacing: 5) {
                    Image(systemName: "moon.stars")
                        .font(.system(size: 9))
                    Text(autoStop)
                        .font(.system(size: 10))
                }
                .foregroundStyle(Color.lloydsGray.opacity(0.7))
            }

            if engine.isTracking {
                HStack(spacing: 8) {
                    if engine.pauseUntil == nil {
                        Menu {
                            Button("15 minuta") { engine.pause(minutes: 15) }
                            Button("30 minuta") { engine.pause(minutes: 30) }
                            Button("1 sat") { engine.pause(minutes: 60) }
                            Button("Do nastavka") { engine.pause(minutes: nil) }
                        } label: {
                            Label("Pauziraj", systemImage: "pause.fill")
                                .font(.system(size: 12, weight: .semibold))
                                .foregroundStyle(Color.lloydsGray)
                        }
                        .menuStyle(.borderlessButton)
                        .fixedSize()
                    } else {
                        Button {
                            engine.resume()
                        } label: {
                            pillLabel("Nastavi", systemImage: "play.fill", color: .lloydsYellow)
                        }
                        .buttonStyle(.plain)
                    }
                    Spacer()
                    Button {
                        engine.stop()
                    } label: {
                        pillLabel("Završi dan", systemImage: "stop.fill", color: stopRed)
                    }
                    .buttonStyle(.plain)
                }
            } else {
                Button {
                    engine.start()
                } label: {
                    HStack {
                        Image(systemName: "play.fill")
                        Text("Start — počni radni dan")
                            .font(.system(size: 12, weight: .bold))
                    }
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 8)
                    .background(RoundedRectangle(cornerRadius: 8).fill(Color.lloydsYellow))
                    .foregroundStyle(Color.lloydsBlack)
                }
                .buttonStyle(.plain)
            }
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 12)
    }

    private let stopRed = Color(red: 1.0, green: 0.36, blue: 0.31)

    /// Vidljivi "pill" gumb za crnu pozadinu — default macOS button style se ovdje
    /// renderira kao prazna crna ploha, pa gumbe stiliziramo ručno.
    private func pillLabel(_ title: String, systemImage: String, color: Color) -> some View {
        HStack(spacing: 5) {
            Image(systemName: systemImage)
                .font(.system(size: 10, weight: .bold))
            Text(title)
                .font(.system(size: 12, weight: .semibold))
        }
        .foregroundStyle(color)
        .padding(.horizontal, 12)
        .padding(.vertical, 6)
        .background(
            RoundedRectangle(cornerRadius: 8)
                .fill(color.opacity(0.12))
                .overlay(
                    RoundedRectangle(cornerRadius: 8)
                        .stroke(color.opacity(0.5), lineWidth: 1)
                )
        )
    }

    private var statusColor: Color {
        if !engine.isTracking { return Color.lloydsGray.opacity(0.5) }
        if engine.pauseUntil != nil { return .orange }
        if engine.awaitingReturnSince != nil { return .blue }
        return Color.lloydsYellow
    }

    private var entriesSection: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                Text("DANAS")
                    .font(.system(size: 10, weight: .heavy))
                    .tracking(1.5)
                    .foregroundStyle(Color.lloydsGray)
                Spacer()
                let total = Summarize.workTotal(engine.entries)
                if total > 0 {
                    Text("ukupno \(Fmt.dur(total))")
                        .font(.system(size: 10, weight: .semibold))
                        .foregroundStyle(Color.lloydsYellow)
                }
            }
            if engine.entries.isEmpty {
                Text("Još nema unosa.")
                    .font(.system(size: 11))
                    .foregroundStyle(Color.lloydsGray.opacity(0.6))
                    .padding(.vertical, 6)
            } else {
                // Bez ScrollView-a: MenuBarExtra prozor zna krivo izmjeriti fleksibilnu
                // visinu pa odreže sadržaj. Fiksan broj redova → točna visina.
                let hidden = engine.entries.count - maxVisibleEntries
                VStack(alignment: .leading, spacing: 4) {
                    ForEach(engine.entries.suffix(maxVisibleEntries).reversed()) { entry in
                        entryRow(entry)
                    }
                    if hidden > 0 {
                        Button {
                            NSApp.activate(ignoringOtherApps: true)
                            openWindow(id: "summary")
                        } label: {
                            Text("… i još \(hidden) ranijih — Pregled dana")
                                .font(.system(size: 10))
                                .foregroundStyle(Color.lloydsGray.opacity(0.7))
                                .underline()
                        }
                        .buttonStyle(.plain)
                        .padding(.top, 2)
                    }
                }
            }
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 10)
    }

    private func entryRow(_ entry: Entry) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 8) {
            Text("\(Fmt.hhmm(entry.start))–\(Fmt.hhmm(entry.end))")
                .font(.system(size: 10, design: .monospaced))
                .foregroundStyle(Color.lloydsGray.opacity(entry.kind == .pause ? 0.5 : 0.9))
            Text(entry.text)
                .font(.system(size: 11))
                .italic(entry.kind == .pause)
                .foregroundStyle(entry.kind == .pause ? Color.lloydsGray.opacity(0.5) : .white)
                .lineLimit(1)
            Spacer()
            Text(Fmt.dur(entry.duration))
                .font(.system(size: 10, design: .monospaced))
                .foregroundStyle(Color.lloydsGray.opacity(0.7))
        }
        .padding(.vertical, 2)
    }

    private var footer: some View {
        HStack {
            Button("Pregled dana") {
                NSApp.activate(ignoringOtherApps: true)
                openWindow(id: "summary")
            }
            Spacer()
            Button("Postavke…") {
                NSApp.activate(ignoringOtherApps: true)
                openSettings()
            }
            Spacer()
            Button("Izlaz") {
                NSApp.terminate(nil)
            }
        }
        .buttonStyle(.plain)
        .font(.system(size: 11))
        .foregroundStyle(Color.lloydsGray)
        .padding(.horizontal, 16)
        .padding(.vertical, 10)
    }
}
