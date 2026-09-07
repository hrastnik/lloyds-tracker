import SwiftUI

/// Ispravak postojećeg unosa iz kronološkog pregleda — opis, vrijeme i vrsta (rad/pauza).
/// Spojeni red (`2×`) se ispravlja kao jedna cjelina: promjena samo opisa/vrste zadržava
/// blokove, a promjena vremena ih stopi u jedan unos (novi raspon nema stare granice).
struct EntryEditView: View {
    let row: ChronoRow
    let history: [String]
    let onSave: (String, Date, Date, EntryKind) -> Void
    let onCancel: () -> Void

    @State private var text: String
    @State private var start: Date
    @State private var end: Date
    @State private var kind: EntryKind
    @FocusState private var textFocused: Bool

    init(
        row: ChronoRow,
        history: [String],
        onSave: @escaping (String, Date, Date, EntryKind) -> Void,
        onCancel: @escaping () -> Void
    ) {
        self.row = row
        self.history = history
        self.onSave = onSave
        self.onCancel = onCancel
        _text = State(initialValue: row.text)
        _start = State(initialValue: row.start)
        _end = State(initialValue: row.end)
        _kind = State(initialValue: row.kind)
    }

    private var trimmed: String { text.trimmingCharacters(in: .whitespacesAndNewlines) }
    private var isValid: Bool { !trimmed.isEmpty && end > start }

    /// Trajanje nakon ispravka — odmah je vidljivo koliko će unos nositi.
    private var duration: String { Fmt.dur(max(0, end.timeIntervalSince(start))) }

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            HStack(spacing: 8) {
                Rectangle().fill(Color.lloydsYellow).frame(width: 16, height: 16)
                Text("ISPRAVI UNOS")
                    .font(.system(size: 12, weight: .heavy))
                    .tracking(1.5)
                    .foregroundStyle(.white)
                Spacer()
                Text(Fmt.dayTitle.string(from: row.start))
                    .font(.system(size: 10))
                    .foregroundStyle(Color.lloydsGray.opacity(0.7))
            }

            VStack(alignment: .leading, spacing: 6) {
                label("OPIS")
                TextField("npr. Projekt X — opis zadatka", text: $text)
                    .textFieldStyle(.plain)
                    .font(.system(size: 14))
                    .foregroundStyle(.white)
                    .tint(Color.lloydsYellow)
                    .padding(10)
                    .background(
                        RoundedRectangle(cornerRadius: 10, style: .continuous)
                            .fill(Color.white.opacity(0.07))
                    )
                    .overlay(
                        RoundedRectangle(cornerRadius: 10, style: .continuous)
                            .stroke(Color.lloydsYellow.opacity(textFocused ? 0.8 : 0.25), lineWidth: 1)
                    )
                    .focused($textFocused)
                    .onSubmit { if isValid { save() } }
                if !history.isEmpty {
                    Menu("Iz povijesti…") {
                        ForEach(history, id: \.self) { item in
                            Button(item) { text = item }
                        }
                    }
                    .menuStyle(.borderlessButton)
                    .fixedSize()
                    .font(.system(size: 11))
                }
            }

            HStack(alignment: .bottom, spacing: 14) {
                VStack(alignment: .leading, spacing: 6) {
                    label("OD")
                    DatePicker("", selection: $start, displayedComponents: .hourAndMinute)
                        .labelsHidden()
                }
                VStack(alignment: .leading, spacing: 6) {
                    label("DO")
                    DatePicker("", selection: $end, displayedComponents: .hourAndMinute)
                        .labelsHidden()
                }
                VStack(alignment: .leading, spacing: 6) {
                    label("TRAJANJE")
                    Text(duration)
                        .font(.system(size: 13, weight: .bold, design: .monospaced))
                        .foregroundStyle(end > start ? Color.lloydsYellow : Color.orange)
                        .padding(.vertical, 3)
                }
                Spacer()
            }

            VStack(alignment: .leading, spacing: 6) {
                label("VRSTA")
                Picker("", selection: $kind) {
                    Text("Rad").tag(EntryKind.work)
                    Text("Pauza").tag(EntryKind.pause)
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .frame(width: 180)
            }

            if end <= start {
                Text("Kraj mora biti nakon početka.")
                    .font(.system(size: 11))
                    .foregroundStyle(.orange)
            } else if row.isMerged, timesChanged {
                Text("Promjena vremena stapa \(row.ids.count) bloka u jedan unos.")
                    .font(.system(size: 11))
                    .foregroundStyle(Color.lloydsYellow.opacity(0.8))
            }

            HStack(spacing: 10) {
                Spacer()
                Button(action: onCancel) {
                    Text("Odustani")
                        .font(.system(size: 13, weight: .semibold))
                        .foregroundStyle(Color.lloydsGray)
                        .padding(.vertical, 8)
                        .padding(.horizontal, 16)
                        .background(
                            RoundedRectangle(cornerRadius: 10)
                                .stroke(Color.white.opacity(0.2), lineWidth: 1)
                        )
                }
                .buttonStyle(.plain)
                .keyboardShortcut(.cancelAction)

                Button(action: save) {
                    Text("Spremi")
                        .font(.system(size: 13, weight: .bold))
                        .foregroundStyle(Color.lloydsBlack)
                        .padding(.vertical, 8)
                        .padding(.horizontal, 20)
                        .background(
                            RoundedRectangle(cornerRadius: 10)
                                .fill(Color.lloydsYellow.opacity(isValid ? 1 : 0.35))
                        )
                }
                .buttonStyle(.plain)
                .disabled(!isValid)
            }
        }
        .padding(22)
        .frame(width: 420)
        .background(Color.lloydsBlack)
        .preferredColorScheme(.dark)
        .tint(Color.lloydsYellow)
        .onAppear { textFocused = true }
    }

    private var timesChanged: Bool {
        abs(start.timeIntervalSince(row.start)) > 1 || abs(end.timeIntervalSince(row.end)) > 1
    }

    private func label(_ title: String) -> some View {
        Text(title)
            .font(.system(size: 9, weight: .heavy))
            .tracking(1.2)
            .foregroundStyle(Color.lloydsGray.opacity(0.6))
    }

    private func save() {
        guard isValid else { return }
        onSave(trimmed, start, end, kind)
    }
}
