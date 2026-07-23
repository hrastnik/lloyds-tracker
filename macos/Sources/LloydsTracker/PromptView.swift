import SwiftUI

struct PromptView: View {
    let request: PromptRequest
    @ObservedObject var model: PromptModel
    let style: PromptStyle
    let history: [String]
    let onSubmit: ([PromptSegment]) -> Void
    let onSnooze: () -> Void
    let onLayoutChange: () -> Void

    /// Spojeni niz blokova s jednim opisom; identitet mu je vrijeme početka.
    private struct Segment: Identifiable {
        let start: Date
        let end: Date
        var id: Date { start }
    }

    private var periodStart: Date { model.start }
    /// Kraj perioda se uživo produžuje dok prompt čeka odgovor (skupno vrijeme).
    private var periodEnd: Date { max(model.end, model.start) }
    /// Unutarnje točke 5-min mreže na kojima se period može razdvojiti.
    private var boundaries: [Date] { Self.gridBoundaries(from: periodStart, to: periodEnd) }

    @State private var splitPoints: Set<Date> = []
    /// Tekst po segmentu, ključ = početak segmenta. Preživljava spajanje/razdvajanje.
    @State private var texts: [Date: String]
    @State private var drafts: [Date: String] = [:]
    @State private var historyIndices: [Date: Int] = [:]
    @FocusState private var focusedField: Date?

    init(
        request: PromptRequest,
        model: PromptModel,
        style: PromptStyle,
        history: [String],
        onSubmit: @escaping ([PromptSegment]) -> Void,
        onSnooze: @escaping () -> Void,
        onLayoutChange: @escaping () -> Void = {}
    ) {
        self.request = request
        self.model = model
        self.style = style
        self.history = history
        self.onSubmit = onSubmit
        self.onSnooze = onSnooze
        self.onLayoutChange = onLayoutChange
        _texts = State(initialValue: [model.start: history.first ?? ""])
    }

    private var prefill: String { history.first ?? "" }

    private var segments: [Segment] {
        var result: [Segment] = []
        var s = periodStart
        for p in boundaries where splitPoints.contains(p) {
            result.append(Segment(start: s, end: p))
            s = p
        }
        result.append(Segment(start: s, end: periodEnd))
        return result
    }

    private var timeRange: String {
        "\(Fmt.hhmm(periodStart)) – \(Fmt.hhmm(periodEnd))"
    }

    /// Točke 5-min mreže strogo unutar perioda (min. 2 min od rubova).
    /// Za jako duge periode mreža se prorjeđuje da ne bude više od 12 blokova.
    static func gridBoundaries(from start: Date, to end: Date) -> [Date] {
        guard end.timeIntervalSince(start) > 240,
              let hourStart = Calendar.current.dateInterval(of: .hour, for: start)?.start
        else { return [] }

        for stepMinutes in [5, 10, 15, 30, 60] {
            let step = TimeInterval(stepMinutes * 60)
            var t = hourStart
            while t <= start.addingTimeInterval(120) { t += step }
            var points: [Date] = []
            while t <= end.addingTimeInterval(-120) {
                points.append(t)
                t += step
            }
            if points.count <= 11 { return points }
        }
        return []
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

            if !boundaries.isEmpty {
                blockBar
            }

            VStack(alignment: .leading, spacing: 8) {
                ForEach(segments) { seg in
                    segmentRow(seg, big: big, single: segments.count == 1)
                }
            }

            HStack(spacing: 14) {
                hint("↑↓", "povijest")
                hint("⏎", "spremi")
                if segments.count == 1 {
                    if style == .floating && !prefill.isEmpty {
                        hint("esc", "isto kao zadnje")
                    }
                    if !boundaries.isEmpty {
                        hint("✂", "razbij period")
                    }
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
                focusedField = periodStart
            }
        }
        .onExitCommand {
            _ = handleEscape()
        }
    }

    // MARK: - Traka blokova

    private var blockBar: some View {
        let total = max(periodEnd.timeIntervalSince(periodStart), 1)
        return VStack(alignment: .leading, spacing: 3) {
            GeometryReader { geo in
                let w = geo.size.width
                ZStack(alignment: .topLeading) {
                    ForEach(segments) { seg in
                        let x = CGFloat(seg.start.timeIntervalSince(periodStart) / total) * w
                        let sw = CGFloat(seg.end.timeIntervalSince(seg.start) / total) * w
                        RoundedRectangle(cornerRadius: 6, style: .continuous)
                            .fill(Color.lloydsYellow.opacity(focusedField == seg.start || segments.count == 1 ? 0.85 : 0.4))
                            .frame(width: max(6, sw - 4), height: 20)
                            .offset(x: x + 2)
                            .onTapGesture { focusedField = seg.start }
                    }
                    ForEach(boundaries, id: \.self) { b in
                        let x = CGFloat(b.timeIntervalSince(periodStart) / total) * w
                        splitHandle(b)
                            .offset(x: x - 10, y: 0)
                    }
                }
                .animation(.easeOut(duration: 0.15), value: splitPoints)
            }
            .frame(height: 20)

            GeometryReader { geo in
                let w = geo.size.width
                ZStack(alignment: .topLeading) {
                    ForEach(boundaries, id: \.self) { b in
                        let x = CGFloat(b.timeIntervalSince(periodStart) / total) * w
                        Text(Fmt.hhmm(b))
                            .font(.system(size: 9, design: .monospaced))
                            .foregroundStyle(splitPoints.contains(b) ? Color.lloydsYellow : Color.lloydsGray.opacity(0.55))
                            .frame(width: 40)
                            .offset(x: x - 20)
                    }
                }
            }
            .frame(height: 11)
        }
    }

    private func splitHandle(_ b: Date) -> some View {
        let isSplit = splitPoints.contains(b)
        return Button {
            toggleSplit(b)
        } label: {
            ZStack {
                Circle()
                    .fill(Color.lloydsBlack)
                    .overlay(Circle().stroke(isSplit ? Color.lloydsYellow : Color.white.opacity(0.35), lineWidth: 1))
                Image(systemName: isSplit ? "xmark" : "scissors")
                    .font(.system(size: 8, weight: .bold))
                    .foregroundStyle(isSplit ? Color.lloydsYellow : Color.lloydsGray)
            }
            .frame(width: 20, height: 20)
            .contentShape(Circle())
        }
        .buttonStyle(.plain)
        .help(isSplit ? "Spoji blokove" : "Razdvoji u \(Fmt.hhmm(b))")
    }

    private func toggleSplit(_ b: Date) {
        if splitPoints.contains(b) {
            splitPoints.remove(b)
            focusedField = segments.last(where: { $0.start <= b })?.start ?? periodStart
        } else {
            splitPoints.insert(b)
            if texts[b] == nil { texts[b] = "" }
            focusedField = b
        }
        DispatchQueue.main.async { onLayoutChange() }
    }

    // MARK: - Redovi segmenata

    private func segmentRow(_ seg: Segment, big: Bool, single: Bool) -> some View {
        HStack(spacing: 8) {
            if !single {
                Text("\(Fmt.hhmm(seg.start))–\(Fmt.hhmm(seg.end))")
                    .font(.system(size: big ? 12 : 10, weight: .semibold, design: .monospaced))
                    .foregroundStyle(focusedField == seg.start ? Color.lloydsYellow : Color.lloydsGray)
                    .frame(width: big ? 96 : 80, alignment: .leading)
            }
            TextField("npr. Projekt X — opis zadatka", text: binding(for: seg.start))
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
                        .stroke(Color.lloydsYellow.opacity(focusedField == seg.start ? 0.8 : 0.25), lineWidth: 1)
                )
                .focused($focusedField, equals: seg.start)
                .onSubmit(submit)
                .onKeyPress(.upArrow) { cycleHistory(older: true); return .handled }
                .onKeyPress(.downArrow) { cycleHistory(older: false); return .handled }
                .onKeyPress(.escape) { handleEscape() }
        }
    }

    private func binding(for key: Date) -> Binding<String> {
        Binding(
            get: { texts[key] ?? "" },
            set: { texts[key] = $0 }
        )
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

    // MARK: - Akcije

    private func submit() {
        var out: [PromptSegment] = []
        for seg in segments {
            let t = (texts[seg.start] ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
            guard !t.isEmpty else {
                focusedField = seg.start
                return
            }
            out.append(PromptSegment(start: seg.start, end: seg.end, text: t))
        }
        guard !out.isEmpty else { return }
        onSubmit(out)
    }

    private func handleEscape() -> KeyPress.Result {
        if style == .floating, segments.count == 1, !prefill.isEmpty {
            onSubmit([PromptSegment(start: periodStart, end: periodEnd, text: prefill)])
            return .handled
        }
        return .handled // fullscreen ili razdvojeno: esc ne radi ništa
    }

    private func cycleHistory(older: Bool) {
        guard let key = focusedField, !history.isEmpty else { return }
        if historyIndices[key] == nil { drafts[key] = texts[key] ?? "" }
        var idx = historyIndices[key] ?? -1
        idx += older ? 1 : -1
        if idx < 0 {
            historyIndices[key] = nil
            texts[key] = drafts[key] ?? ""
            return
        }
        idx = min(idx, history.count - 1)
        historyIndices[key] = idx
        texts[key] = history[idx]
    }
}
