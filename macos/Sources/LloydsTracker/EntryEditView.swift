import SwiftUI

/// Što se uređuje u kronološkom pregledu — postojeći red ili novi unos. Vremena se drže
/// kao upisani tekst ("9:30"), a tumače tek kod provjere i spremanja.
struct EntryDraft: Equatable {
    enum Target: Hashable {
        case existing(ChronoRow)
        /// Novi unos; `gapStart` je rupa iz koje je pokrenut (nil = gumb "Dodaj unos").
        case new(gapStart: Date?)
    }

    var target: Target
    var text: String
    var start: String
    var end: String
    var kind: EntryKind
    /// Točna vremena s kojima je uređivač otvoren (sa sekundama) — neizmijenjeno polje
    /// vraća njih, a ne punu minutu (vidi `Fmt.editRange`).
    var originalStart: Date
    var originalEnd: Date

    static func edit(_ row: ChronoRow) -> EntryDraft {
        EntryDraft(target: .existing(row), text: row.text,
                   start: Fmt.hhmm(row.start), end: Fmt.hhmm(row.end), kind: row.kind,
                   originalStart: row.start, originalEnd: row.end)
    }

    static func new(start: Date, end: Date, gapStart: Date?) -> EntryDraft {
        EntryDraft(target: .new(gapStart: gapStart), text: "",
                   start: Fmt.hhmm(start), end: Fmt.hhmm(end), kind: .work,
                   originalStart: start, originalEnd: end)
    }

    var row: ChronoRow? {
        if case .existing(let r) = target { return r }
        return nil
    }
}

/// Uređivač unosa unutar kronološkog pregleda — otvara se na mjestu reda (ispravak),
/// rupe ili gumba "Dodaj unos" (novi unos). Spojeni red (`2×`) se ispravlja kao jedna
/// cjelina: promjena samo opisa/vrste zadržava blokove, a promjena vremena ih stopi u
/// jedan unos (novi raspon nema stare granice). Preklopljeni dio drugih unosa se reže.
struct EntryEditor: View {
    @Binding var draft: EntryDraft
    /// Dan pregleda — upisana vremena se tumače unutar njega.
    let day: Date
    let entries: [Entry]
    let history: [String]
    let onSave: (String, Date, Date, EntryKind) -> Void
    let onDelete: (() -> Void)?
    let onCancel: () -> Void

    private enum Field: Hashable { case text, start, end }
    @FocusState private var focus: Field?

    private var trimmed: String { draft.text.trimmingCharacters(in: .whitespacesAndNewlines) }

    private var dates: (start: Date?, end: Date?) {
        Fmt.editRange(start: draft.start, end: draft.end, day: day,
                      original: (draft.originalStart, draft.originalEnd))
    }

    private var startDate: Date? { dates.start }
    private var endDate: Date? { dates.end }

    private var range: (start: Date, end: Date)? {
        guard let s = startDate, let e = endDate, e > s else { return nil }
        return (s, e)
    }

    /// Unos ne smije završiti u budućnosti — sljedeći prompt bi inače pitao za vrijeme
    /// koje je već "pokriveno" (i jedno od toga bi se izgubilo).
    private var endsInFuture: Bool { (range?.end ?? .distantPast) > Date() }

    private var isValid: Bool { !trimmed.isEmpty && range != nil && !endsInFuture }

    private var timesChanged: Bool {
        guard let row = draft.row else { return true }
        guard let s = startDate, let e = endDate else { return true }
        return abs(s.timeIntervalSince(row.start)) > 1 || abs(e.timeIntervalSince(row.end)) > 1
    }

    /// Unosi koje će spremanje skratiti ili maknuti.
    private var overlaps: [Entry] {
        guard let r = range, timesChanged else { return [] }
        return Summarize.overlapping(entries, start: r.start, end: r.end, excluding: Set(draft.row?.ids ?? []))
    }

    private var notice: (text: String, warn: Bool)? {
        if startDate == nil || endDate == nil { return ("Vrijeme upiši kao 9:30 ili 0930.", true) }
        if range == nil { return ("Kraj mora biti nakon početka.", true) }
        if endsInFuture { return ("Kraj ne može biti u budućnosti (sad je \(Fmt.hhmm(Date()))).", true) }
        var parts: [String] = []
        if let row = draft.row, row.isMerged, timesChanged {
            parts.append("Promjena vremena stapa \(Fmt.blocks(row.ids.count)) u jedan unos.")
        }
        let n = overlaps.count
        if n > 0 {
            parts.append("Preklapa se s \(n) \(n == 1 ? "unosom" : "unosa") — ovaj unos zamjenjuje preklopljeni dio.")
        }
        if let r = range, !Calendar.current.isDate(r.end.addingTimeInterval(-1), inSameDayAs: r.start) {
            parts.append("Završava iza ponoći (\(Fmt.hhmm(r.end)) sutradan).")
        }
        return parts.isEmpty ? nil : (parts.joined(separator: " "), false)
    }

    /// Trajanje nakon izmjene — odmah je vidljivo koliko će unos nositi.
    private var duration: String {
        guard let s = startDate, let e = endDate else { return "—" }
        return Fmt.dur(max(0, e.timeIntervalSince(s)))
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 8) {
                label("OD")
                timeField(text: $draft.start, field: .start)
                label("DO")
                timeField(text: $draft.end, field: .end)
                Text(duration)
                    .font(.system(size: 12, weight: .bold, design: .monospaced))
                    .foregroundStyle(range != nil ? Color.lloydsYellow : Color.orange)
                    .frame(minWidth: 56, alignment: .leading)
                Spacer(minLength: 0)
                Picker("", selection: $draft.kind) {
                    Text("Rad").tag(EntryKind.work)
                    Text("Pauza").tag(EntryKind.pause)
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .frame(width: 130)
            }

            HStack(spacing: 8) {
                TextField("Opis — npr. Projekt X — opis zadatka", text: $draft.text)
                    .textFieldStyle(.plain)
                    .font(.system(size: 13))
                    .foregroundStyle(.white)
                    .padding(.vertical, 7)
                    .padding(.horizontal, 9)
                    .background(RoundedRectangle(cornerRadius: 7).fill(Color.white.opacity(0.07)))
                    .overlay(
                        RoundedRectangle(cornerRadius: 7)
                            .stroke(Color.lloydsYellow.opacity(focus == .text ? 0.8 : 0.25), lineWidth: 1)
                    )
                    .focused($focus, equals: .text)
                    .onSubmit(save)
                if !history.isEmpty {
                    Menu("Povijest") {
                        ForEach(history, id: \.self) { item in
                            Button(item) { draft.text = item }
                        }
                    }
                    .menuStyle(.borderlessButton)
                    .fixedSize()
                    .font(.system(size: 11))
                    .help("Opis iz povijesti")
                }
            }

            if let notice {
                Text(notice.text)
                    .font(.system(size: 11))
                    .foregroundStyle(notice.warn ? Color.orange : Color.lloydsYellow.opacity(0.85))
                    .fixedSize(horizontal: false, vertical: true)
            }

            HStack(spacing: 8) {
                if let onDelete {
                    Button(action: onDelete) {
                        HStack(spacing: 5) {
                            Image(systemName: "trash")
                                .font(.system(size: 10, weight: .bold))
                            Text(draft.row?.isMerged == true ? "Obriši (\(Fmt.blocks(draft.row!.ids.count)))" : "Obriši")
                                .font(.system(size: 12, weight: .semibold))
                        }
                        .foregroundStyle(Self.deleteRed)
                        .padding(.vertical, 6)
                        .padding(.horizontal, 10)
                        .background(RoundedRectangle(cornerRadius: 8).fill(Self.deleteRed.opacity(0.1)))
                        .clickArea(cornerRadius: 8)
                    }
                    .buttonStyle(.plain)
                }
                Spacer()
                Button(action: onCancel) {
                    Text("Odustani")
                        .font(.system(size: 12, weight: .semibold))
                        .foregroundStyle(Color.lloydsGray)
                        .padding(.vertical, 6)
                        .padding(.horizontal, 14)
                        .background(
                            RoundedRectangle(cornerRadius: 8)
                                .stroke(Color.white.opacity(0.2), lineWidth: 1)
                        )
                        .clickArea(cornerRadius: 8)
                }
                .buttonStyle(.plain)
                .keyboardShortcut(.cancelAction)

                Button(action: save) {
                    Text(draft.row == nil ? "Dodaj" : "Spremi")
                        .font(.system(size: 12, weight: .bold))
                        .foregroundStyle(Color.lloydsBlack)
                        .padding(.vertical, 6)
                        .padding(.horizontal, 18)
                        .background(
                            RoundedRectangle(cornerRadius: 8)
                                .fill(Color.lloydsYellow.opacity(isValid ? 1 : 0.35))
                        )
                        .clickArea(cornerRadius: 8)
                }
                .buttonStyle(.plain)
                .disabled(!isValid)
            }
        }
        .padding(12)
        .background(RoundedRectangle(cornerRadius: 8).fill(Color.white.opacity(0.06)))
        .overlay(RoundedRectangle(cornerRadius: 8).stroke(Color.lloydsYellow.opacity(0.5), lineWidth: 1))
        .onAppear { focus = .text }
        .onChange(of: focus) { old, _ in
            // Napušteno polje vremena se uredi u HH:mm ("930" → "09:30").
            if old == .start { draft.start = normalized(draft.start) }
            if old == .end { draft.end = normalized(draft.end) }
        }
    }

    private static let deleteRed = Color(red: 1.0, green: 0.36, blue: 0.31)

    private func label(_ title: String) -> some View {
        Text(title)
            .font(.system(size: 9, weight: .heavy))
            .tracking(1.2)
            .foregroundStyle(Color.lloydsGray.opacity(0.6))
    }

    /// Polje za vrijeme: upisuje se rukom ("930", "9:30"), a strelice ↑/↓ pomiču za 5 min.
    private func timeField(text: Binding<String>, field: Field) -> some View {
        let ok = Fmt.parseTime(text.wrappedValue) != nil
        return TextField("hh:mm", text: text)
            .textFieldStyle(.plain)
            .font(.system(size: 13, weight: .semibold, design: .monospaced))
            .multilineTextAlignment(.center)
            .foregroundStyle(.white)
            .frame(width: 50)
            .padding(.vertical, 6)
            .padding(.horizontal, 6)
            .background(RoundedRectangle(cornerRadius: 7).fill(Color.white.opacity(0.07)))
            .overlay(
                RoundedRectangle(cornerRadius: 7)
                    .stroke(ok ? Color.lloydsYellow.opacity(focus == field ? 0.8 : 0.25) : Color.orange, lineWidth: 1)
            )
            .focused($focus, equals: field)
            .onSubmit(save)
            .onKeyPress(.upArrow) { step(text, by: 5); return .handled }
            .onKeyPress(.downArrow) { step(text, by: -5); return .handled }
            .help("Upiši npr. 9:30 ili 0930 — strelice ↑/↓ pomiču za 5 minuta")
    }

    private func step(_ text: Binding<String>, by minutes: Int) {
        guard let t = Fmt.parseTime(text.wrappedValue) else { return }
        let total = min(max(t.hour * 60 + t.minute + minutes, 0), 23 * 60 + 59)
        text.wrappedValue = String(format: "%02d:%02d", total / 60, total % 60)
    }

    private func normalized(_ input: String) -> String {
        guard let t = Fmt.parseTime(input) else { return input }
        return String(format: "%02d:%02d", t.hour, t.minute)
    }

    private func save() {
        guard isValid, let r = range else { return }
        onSave(trimmed, r.start, r.end, draft.kind)
    }
}
