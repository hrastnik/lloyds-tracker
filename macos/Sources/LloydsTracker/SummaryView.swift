import SwiftUI
import UniformTypeIdentifiers

struct SummaryView: View {
    @ObservedObject var engine: TrackerEngine

    @State private var date = Date()
    @State private var mode: Mode = .grouped
    @State private var copied = false

    enum Mode: String, CaseIterable, Identifiable {
        case grouped = "Grupirano"
        case chronological = "Kronološki"
        var id: String { rawValue }
    }

    private var dayKey: String { Store.dayKey(date) }

    private var entries: [Entry] {
        dayKey == engine.currentDayKey ? engine.entries : Store.loadDay(dayKey)
    }

    var body: some View {
        VStack(spacing: 0) {
            header
            Divider()
            if entries.isEmpty {
                Spacer()
                Text("Nema unosa za ovaj dan.")
                    .foregroundStyle(.secondary)
                Spacer()
            } else {
                content
            }
            Divider()
            footer
        }
        .frame(minWidth: 520, minHeight: 440)
        .background(Color.lloydsBlack)
        .preferredColorScheme(.dark)
        .tint(Color.lloydsYellow)
    }

    private var header: some View {
        VStack(spacing: 10) {
            HStack {
                Button {
                    date = Calendar.current.date(byAdding: .day, value: -1, to: date) ?? date
                } label: { Image(systemName: "chevron.left") }

                Spacer()
                Text(Fmt.dayTitle.string(from: date))
                    .font(.system(size: 15, weight: .heavy))
                    .foregroundStyle(.white)
                Spacer()

                Button {
                    date = Calendar.current.date(byAdding: .day, value: 1, to: date) ?? date
                } label: { Image(systemName: "chevron.right") }
                .disabled(Calendar.current.isDateInToday(date))
            }
            .buttonStyle(.plain)
            .foregroundStyle(Color.lloydsYellow)

            HStack(spacing: 16) {
                badge("RAD", Fmt.dur(Summarize.workTotal(entries)), .lloydsYellow)
                badge("PAUZE", Fmt.dur(Summarize.pauseTotal(entries)), .lloydsGray)
                Spacer()
                Picker("", selection: $mode) {
                    ForEach(Mode.allCases) { m in Text(m.rawValue).tag(m) }
                }
                .pickerStyle(.segmented)
                .frame(width: 200)
            }
        }
        .padding(16)
    }

    private func badge(_ label: String, _ value: String, _ color: Color) -> some View {
        HStack(spacing: 6) {
            Text(label)
                .font(.system(size: 9, weight: .heavy))
                .tracking(1)
                .foregroundStyle(color.opacity(0.7))
            Text(value)
                .font(.system(size: 13, weight: .bold, design: .monospaced))
                .foregroundStyle(color)
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 5)
        .background(RoundedRectangle(cornerRadius: 6).fill(Color.white.opacity(0.06)))
    }

    private var content: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 8) {
                switch mode {
                case .grouped:
                    ForEach(Summarize.groups(from: entries)) { group in
                        groupRow(group)
                    }
                case .chronological:
                    ForEach(entries) { entry in
                        chronoRow(entry)
                    }
                }
            }
            .padding(16)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    private func groupRow(_ group: GroupSummary) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack(alignment: .firstTextBaseline) {
                Text(Fmt.dur(group.total))
                    .font(.system(size: 13, weight: .bold, design: .monospaced))
                    .foregroundStyle(Color.lloydsYellow)
                    .frame(width: 70, alignment: .leading)
                Text(group.text)
                    .font(.system(size: 13, weight: .semibold))
                    .foregroundStyle(.white)
                Spacer()
            }
            Text(group.ranges.map { "\(Fmt.hhmm($0.start))–\(Fmt.hhmm($0.end))" }.joined(separator: " · "))
                .font(.system(size: 11, design: .monospaced))
                .foregroundStyle(Color.lloydsGray.opacity(0.7))
                .padding(.leading, 70)
        }
        .padding(.vertical, 6)
        .padding(.horizontal, 10)
        .background(RoundedRectangle(cornerRadius: 8).fill(Color.white.opacity(0.04)))
    }

    private func chronoRow(_ entry: Entry) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 10) {
            Text("\(Fmt.hhmm(entry.start))–\(Fmt.hhmm(entry.end))")
                .font(.system(size: 11, design: .monospaced))
                .foregroundStyle(Color.lloydsGray)
            Text(entry.text)
                .font(.system(size: 12))
                .italic(entry.kind == .pause)
                .foregroundStyle(entry.kind == .pause ? Color.lloydsGray.opacity(0.6) : .white)
            Spacer()
            Text(Fmt.dur(entry.duration))
                .font(.system(size: 11, design: .monospaced))
                .foregroundStyle(Color.lloydsGray.opacity(0.7))
            Button {
                engine.deleteEntry(id: entry.id, dayKey: dayKey)
            } label: {
                Image(systemName: "trash")
                    .font(.system(size: 10))
                    .foregroundStyle(Color.lloydsGray.opacity(0.5))
            }
            .buttonStyle(.plain)
            .help("Obriši unos")
        }
        .padding(.vertical, 4)
        .padding(.horizontal, 10)
        .background(RoundedRectangle(cornerRadius: 6).fill(Color.white.opacity(0.03)))
    }

    private var footer: some View {
        HStack {
            Button {
                let text = Summarize.clipboardText(for: date, entries: entries)
                NSPasteboard.general.clearContents()
                NSPasteboard.general.setString(text, forType: .string)
                copied = true
                DispatchQueue.main.asyncAfter(deadline: .now() + 2) { copied = false }
            } label: {
                Label(copied ? "Kopirano ✓" : "Kopiraj pregled", systemImage: "doc.on.doc")
            }

            Button {
                exportCSV()
            } label: {
                Label("Export CSV…", systemImage: "square.and.arrow.up")
            }

            Spacer()

            Button("Otvori folder s podacima") {
                NSWorkspace.shared.open(Store.directory)
            }
            .buttonStyle(.plain)
            .font(.system(size: 11))
            .foregroundStyle(Color.lloydsGray)
        }
        .controlSize(.regular)
        .padding(12)
    }

    private func exportCSV() {
        let panel = NSSavePanel()
        panel.nameFieldStringValue = "lloyds-tracker-\(dayKey).csv"
        panel.allowedContentTypes = [.commaSeparatedText]
        NSApp.activate(ignoringOtherApps: true)
        if panel.runModal() == .OK, let url = panel.url {
            try? Summarize.csv(entries: entries).write(to: url, atomically: true, encoding: .utf8)
        }
    }
}
