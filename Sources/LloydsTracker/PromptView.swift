import SwiftUI

struct PromptView: View {
    let request: PromptRequest
    let style: PromptStyle
    let history: [String]
    let onSubmit: (String) -> Void
    let onSnooze: () -> Void

    @State private var text: String
    @State private var draft: String
    @State private var historyIndex: Int?
    @FocusState private var focused: Bool

    init(
        request: PromptRequest,
        style: PromptStyle,
        history: [String],
        onSubmit: @escaping (String) -> Void,
        onSnooze: @escaping () -> Void
    ) {
        self.request = request
        self.style = style
        self.history = history
        self.onSubmit = onSubmit
        self.onSnooze = onSnooze
        let prefill = history.first ?? ""
        _text = State(initialValue: prefill)
        _draft = State(initialValue: prefill)
    }

    private var prefill: String { history.first ?? "" }

    private var timeRange: String {
        let end = request.end ?? Date()
        return "\(Fmt.hhmm(request.start)) – \(Fmt.hhmm(end))"
    }

    var body: some View {
        switch style {
        case .floating:
            card(width: 420, big: false)
                .background(
                    RoundedRectangle(cornerRadius: 16, style: .continuous)
                        .fill(Color.lloydsBlack)
                )
                .overlay(
                    RoundedRectangle(cornerRadius: 16, style: .continuous)
                        .stroke(Color.lloydsYellow.opacity(0.35), lineWidth: 1)
                )
                .preferredColorScheme(.dark)

        case .fullscreen:
            ZStack {
                Color.lloydsBlack.ignoresSafeArea()
                VStack(spacing: 28) {
                    HStack(spacing: 8) {
                        Rectangle().fill(Color.lloydsYellow).frame(width: 26, height: 26)
                        Text("LLOYDS TRACKER")
                            .font(.system(size: 13, weight: .heavy))
                            .tracking(3)
                            .foregroundStyle(Color.lloydsGray)
                    }
                    card(width: 560, big: true)
                        .background(
                            RoundedRectangle(cornerRadius: 20, style: .continuous)
                                .fill(Color.white.opacity(0.04))
                        )
                        .overlay(
                            RoundedRectangle(cornerRadius: 20, style: .continuous)
                                .stroke(Color.lloydsYellow.opacity(0.3), lineWidth: 1)
                        )
                    Text("Odgovor je obavezan — upiši što radiš i stisni Enter.")
                        .font(.system(size: 12))
                        .foregroundStyle(Color.lloydsGray.opacity(0.7))
                }
            }
            .preferredColorScheme(.dark)
        }
    }

    private func card(width: CGFloat, big: Bool) -> some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack {
                Circle().fill(Color.lloydsYellow).frame(width: 9, height: 9)
                Text("NA ČEMU RADIŠ?")
                    .font(.system(size: big ? 20 : 13, weight: .heavy))
                    .tracking(1.5)
                    .foregroundStyle(.white)
                Spacer()
                Text(timeRange)
                    .font(.system(size: big ? 13 : 11, weight: .medium, design: .monospaced))
                    .foregroundStyle(Color.lloydsGray)
            }

            if let note = request.note {
                Text(note)
                    .font(.system(size: big ? 13 : 11))
                    .foregroundStyle(Color.lloydsYellow.opacity(0.9))
            }

            TextField("npr. Projekt X — opis zadatka", text: $text)
                .textFieldStyle(.plain)
                .font(.system(size: big ? 17 : 14))
                .foregroundStyle(.white)
                .tint(Color.lloydsYellow)
                .padding(big ? 14 : 10)
                .background(
                    RoundedRectangle(cornerRadius: 10, style: .continuous)
                        .fill(Color.white.opacity(0.07))
                )
                .overlay(
                    RoundedRectangle(cornerRadius: 10, style: .continuous)
                        .stroke(Color.lloydsYellow.opacity(focused ? 0.8 : 0.25), lineWidth: 1)
                )
                .focused($focused)
                .onSubmit(submit)
                .onKeyPress(.upArrow) { cycleHistory(older: true); return .handled }
                .onKeyPress(.downArrow) { cycleHistory(older: false); return .handled }
                .onKeyPress(.escape) { handleEscape() }

            HStack(spacing: 14) {
                hint("↑↓", "povijest")
                hint("⏎", "spremi")
                if style == .floating && !prefill.isEmpty {
                    hint("esc", "isto kao zadnje")
                }
                Spacer()
                if request.allowSnooze {
                    Button("Odgodi 5 min", action: onSnooze)
                        .buttonStyle(.plain)
                        .font(.system(size: 11))
                        .foregroundStyle(Color.lloydsGray)
                        .underline()
                }
            }
        }
        .padding(big ? 28 : 18)
        .frame(width: width)
        .onAppear {
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.15) {
                focused = true
            }
        }
        .onExitCommand {
            _ = handleEscape()
        }
    }

    private func hint(_ key: String, _ label: String) -> some View {
        HStack(spacing: 4) {
            Text(key)
                .font(.system(size: 10, weight: .bold, design: .monospaced))
                .padding(.horizontal, 5)
                .padding(.vertical, 2)
                .background(RoundedRectangle(cornerRadius: 4).fill(Color.white.opacity(0.12)))
            Text(label)
                .font(.system(size: 10))
        }
        .foregroundStyle(Color.lloydsGray)
    }

    private func submit() {
        let t = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !t.isEmpty else { return }
        onSubmit(t)
    }

    private func handleEscape() -> KeyPress.Result {
        if style == .floating, !prefill.isEmpty {
            onSubmit(prefill)
            return .handled
        }
        return .handled // fullscreen: esc ne radi ništa (non-skippable)
    }

    private func cycleHistory(older: Bool) {
        guard !history.isEmpty else { return }
        if historyIndex == nil { draft = text }
        var idx = historyIndex ?? -1
        idx += older ? 1 : -1
        if idx < 0 {
            historyIndex = nil
            text = draft
            return
        }
        idx = min(idx, history.count - 1)
        historyIndex = idx
        text = history[idx]
    }
}
