import SwiftUI
import UniformTypeIdentifiers

struct SummaryView: View {
    @ObservedObject var engine: TrackerEngine

    @State private var date = Date()
    @State private var mode: Mode = .grouped
    @State private var copied = false
    /// Unos koji se uređuje ili dodaje u kronološkom pregledu (nil = uređivač zatvoren).
    @State private var draft: EntryDraft?

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
            if entries.isEmpty && draft == nil {
                Spacer()
                Text("Nema unosa za ovaj dan.")
                    .foregroundStyle(.secondary)
                addButton
                    .frame(width: 200)
                    .padding(.top, 10)
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
        // Uređivač pripada danu i prikazu u kojem je otvoren.
        .onChange(of: dayKey) { draft = nil }
        .onChange(of: mode) { if mode == .grouped { draft = nil } }
        .onChange(of: engine.settings.mergeAdjacentEntries) { draft = nil }
    }

    private var header: some View {
        VStack(spacing: 10) {
            HStack {
                dayButton("chevron.left", by: -1, help: "Prethodni dan")
                Spacer()
                Text(Fmt.dayTitle.string(from: date))
                    .font(.system(size: 15, weight: .heavy))
                    .foregroundStyle(.white)
                Spacer()
                dayButton("chevron.right", by: 1, help: "Sljedeći dan")
                    .disabled(Calendar.current.isDateInToday(date))
            }

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

            if mode == .chronological {
                HStack {
                    mergeToggle
                    Spacer()
                }
            }
        }
        .padding(16)
    }

    /// Strelica za promjenu dana — cijela pločica (ne samo glif) prima klik.
    private func dayButton(_ systemName: String, by days: Int, help: String) -> some View {
        HoverButton(cornerRadius: 8, hoverFill: Color.lloydsYellow.opacity(0.12)) {
            date = Calendar.current.date(byAdding: .day, value: days, to: date) ?? date
        } label: { _ in
            Image(systemName: systemName)
                .font(.system(size: 14, weight: .bold))
                .foregroundStyle(Color.lloydsYellow)
                .frame(width: 40, height: 32)
        }
        .help(help)
        .modifier(DimWhenDisabled())
    }

    /// Spajanje susjednih unosa je postavka (pamti se), ali se toggle-a ovdje jer vrijedi
    /// samo za kronološki prikaz.
    private var mergeToggle: some View {
        let on = engine.settings.mergeAdjacentEntries
        return Button {
            engine.settings.mergeAdjacentEntries.toggle()
        } label: {
            HStack(spacing: 6) {
                Image(systemName: on ? "checkmark.square.fill" : "square")
                    .font(.system(size: 12, weight: .bold))
                Text("Spoji susjedne unose istog naziva")
                    .font(.system(size: 11, weight: .medium))
            }
            .foregroundStyle(on ? Color.lloydsYellow : Color.lloydsGray)
            .padding(.horizontal, 8)
            .padding(.vertical, 4)
            .background(
                RoundedRectangle(cornerRadius: 6)
                    .fill(Color.white.opacity(on ? 0.06 : 0.03))
            )
            .clickArea(cornerRadius: 6)
        }
        .buttonStyle(.plain)
        .help("14:45–15:00 + 15:00–15:15 istog naziva prikazuje se kao 14:45–15:15")
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
                    let rows = Summarize.chronology(entries, merging: engine.settings.mergeAdjacentEntries)
                    ForEach(Summarize.timeline(rows)) { item in
                        switch item {
                        case .row(let row):
                            if draft?.row?.id == row.id {
                                editor
                            } else {
                                chronoRow(row)
                            }
                        case .gap(let start, let end):
                            if draft?.target == .new(gapStart: start) {
                                editor
                            } else {
                                gapRow(start, end)
                            }
                        }
                    }
                    if draft?.target == .new(gapStart: nil) {
                        editor
                    } else {
                        addButton
                            .padding(.top, 4)
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

    /// Cijeli red je gumb — klik otvara uređivač na mjestu reda.
    private func chronoRow(_ row: ChronoRow) -> some View {
        HoverButton(baseFill: Color.white.opacity(0.03), hoverFill: Color.white.opacity(0.08)) {
            draft = .edit(row)
        } label: { hovering in
            HStack(alignment: .firstTextBaseline, spacing: 10) {
                Text("\(Fmt.hhmm(row.start))–\(Fmt.hhmm(row.end))")
                    .font(.system(size: 11, design: .monospaced))
                    .foregroundStyle(Color.lloydsGray)
                Text(row.text)
                    .font(.system(size: 12))
                    .italic(row.kind == .pause)
                    .foregroundStyle(row.kind == .pause ? Color.lloydsGray.opacity(0.6) : .white)
                    .lineLimit(1)
                Spacer()
                if row.isMerged {
                    Text("\(row.ids.count)×")
                        .font(.system(size: 10, weight: .bold, design: .monospaced))
                        .foregroundStyle(Color.lloydsYellow.opacity(0.6))
                }
                Text(Fmt.dur(row.duration))
                    .font(.system(size: 11, design: .monospaced))
                    .foregroundStyle(Color.lloydsGray.opacity(0.7))
                Image(systemName: "pencil")
                    .font(.system(size: 11, weight: .semibold))
                    .foregroundStyle(hovering ? Color.lloydsYellow : Color.lloydsGray.opacity(0.35))
                    .frame(width: 14)
            }
            .padding(.vertical, 7)
            .padding(.horizontal, 10)
        }
        .help(row.isMerged ? "Uredi unos (\(row.ids.count) bloka)" : "Uredi unos")
    }

    /// Nezabilježeno vrijeme između dva unosa — klik ga popuni novim unosom.
    private func gapRow(_ start: Date, _ end: Date) -> some View {
        HoverButton(hoverFill: Color.lloydsYellow.opacity(0.08)) {
            draft = .new(start: start, end: end, gapStart: start)
        } label: { hovering in
            HStack(spacing: 10) {
                Image(systemName: "plus")
                    .font(.system(size: 9, weight: .heavy))
                Text("\(Fmt.hhmm(start))–\(Fmt.hhmm(end))")
                    .font(.system(size: 10, design: .monospaced))
                Text("nezabilježeno · \(Fmt.dur(end.timeIntervalSince(start)))")
                    .font(.system(size: 10))
                Spacer()
                if hovering {
                    Text("Dodaj unos")
                        .font(.system(size: 10, weight: .semibold))
                }
            }
            .foregroundStyle(hovering ? Color.lloydsYellow : Color.lloydsGray.opacity(0.55))
            .padding(.vertical, 5)
            .padding(.horizontal, 10)
            .overlay(
                RoundedRectangle(cornerRadius: 6)
                    .strokeBorder(style: StrokeStyle(lineWidth: 1, dash: [3, 3]))
                    .foregroundStyle(Color.white.opacity(hovering ? 0.25 : 0.12))
            )
        }
        .help("Dodaj unos za \(Fmt.hhmm(start))–\(Fmt.hhmm(end))")
    }

    /// Novi unos na kraju dana (od kraja zadnjeg unosa) — i iz praznog dana.
    private var addButton: some View {
        HoverButton(hoverFill: Color.lloydsYellow.opacity(0.12)) {
            let r = Summarize.suggestedRange(entries, day: date, settings: engine.settings)
            mode = .chronological
            draft = .new(start: r.start, end: r.end, gapStart: nil)
        } label: { _ in
            HStack(spacing: 6) {
                Image(systemName: "plus")
                    .font(.system(size: 10, weight: .heavy))
                Text("Dodaj unos")
                    .font(.system(size: 12, weight: .semibold))
            }
            .foregroundStyle(Color.lloydsYellow)
            .frame(maxWidth: .infinity)
            .padding(.vertical, 7)
            .overlay(
                RoundedRectangle(cornerRadius: 6)
                    .stroke(Color.lloydsYellow.opacity(0.4), lineWidth: 1)
            )
        }
    }

    @ViewBuilder
    private var editor: some View {
        if let current = draft {
            EntryEditor(
                draft: Binding(get: { draft ?? current }, set: { draft = $0 }),
                day: date,
                entries: entries,
                history: engine.history,
                onSave: { text, start, end, kind in
                    if let row = current.row {
                        engine.updateEntries(row: row, dayKey: dayKey, text: text, start: start, end: end, kind: kind)
                    } else {
                        engine.addEntry(dayKey: dayKey, text: text, start: start, end: end, kind: kind)
                    }
                    draft = nil
                },
                onDelete: current.row.map { row in
                    {
                        engine.deleteEntries(ids: row.ids, dayKey: dayKey)
                        draft = nil
                    }
                },
                onCancel: { draft = nil }
            )
            .id(current.target)
        }
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

            Button {
                NSWorkspace.shared.open(Store.directory)
            } label: {
                Text("Otvori folder s podacima")
                    .padding(.vertical, 5)
                    .padding(.horizontal, 6)
                    .clickArea()
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

/// Onemogućena strelica (npr. "sljedeći dan" na današnjem danu) je vidno prigušena.
private struct DimWhenDisabled: ViewModifier {
    @Environment(\.isEnabled) private var isEnabled
    func body(content: Content) -> some View { content.opacity(isEnabled ? 1 : 0.3) }
}
