import SwiftUI

struct PromptView: View {
    let request: PromptRequest
    @ObservedObject var model: PromptModel
    let style: PromptStyle
    let history: [String]
    let onSubmit: (PromptResult) -> Void
    let onSnooze: () -> Void
    let onLayoutChange: () -> Void

    /// Jedan red prompta: blok (ili spojeni niz blokova) glavnog perioda, ili preskočeni
    /// period iz prijašnjeg prompta. Identitet mu je vrijeme početka.
    private struct Segment: Identifiable {
        let start: Date
        let end: Date
        /// Nije dio glavnog perioda — preskočen je i nosi se dalje.
        var carried = false
        var id: Date { start }
    }

    /// Ključ polja "nastavljam s" u `texts` — dijeli ga s opisima segmenata, pa listanje
    /// povijesti (↑/↓) radi i tamo. Nikad se ne poklapa s pravim vremenom bloka.
    private static let nextUpKey = Date.distantFuture

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
        onSubmit: @escaping (PromptResult) -> Void,
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
        // Pre-fill dobiva samo glavni period; preskočeni redovi ostaju prazni jer su
        // svjesno ostavljeni za kasnije — Enter ih ne smije napuniti zadnjim unosom.
        _texts = State(initialValue: [model.start: history.first ?? ""])
    }

    /// Degeneriran glavni period (npr. ručni prompt odmah nakon odgovora, ili dan zatvoren
    /// točno na granici) skriva se ako ima preskočenih redova — inače ostaje kao jedini red.
    private var showsMainPeriod: Bool { periodEnd > periodStart || request.carried.isEmpty }

    private var carriedSegments: [Segment] {
        request.carried
            .sorted { $0.start < $1.start }
            .map { Segment(start: $0.start, end: $0.end, carried: true) }
    }

    private var mainSegments: [Segment] {
        var result: [Segment] = []
        var s = periodStart
        for p in boundaries where splitPoints.contains(p) {
            result.append(Segment(start: s, end: p))
            s = p
        }
        result.append(Segment(start: s, end: periodEnd))
        return result
    }

    private var segments: [Segment] {
        carriedSegments + (showsMainPeriod ? mainSegments : [])
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
                    Text("Upiši što radiš i stisni ⏎ — ili preskoči (esc), pa te period čeka u sljedećem promptu.")
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
                // Uz preskočene redove period piše na svojoj sekciji, da se ne čita kao
                // da vrijedi za cijeli prompt.
                if showsMainPeriod, carriedSegments.isEmpty {
                    Text(timeRange)
                        .font(.system(size: big ? 13 : 11, weight: .medium, design: .monospaced))
                        .foregroundStyle(Color.lloydsGray)
                }
            }

            if let note = request.note {
                Text(note)
                    .font(.system(size: big ? 13 : 11))
                    .foregroundStyle(Color.lloydsYellow.opacity(0.9))
                    .fixedSize(horizontal: false, vertical: true)
            }

            // Preskočeni periodi iz prijašnjih promptova su svoja skupina, iznad crte —
            // traka blokova (`✂`) i pre-fill vrijede samo za glavni period pod njom.
            if !carriedSegments.isEmpty {
                VStack(alignment: .leading, spacing: 8) {
                    sectionLabel("PRESKOČENO PRIJE — POPUNI ILI OSTAVI ZA KASNIJE")
                    ForEach(carriedSegments) { seg in
                        segmentRow(seg, big: big, single: false)
                    }
                }
                Divider().overlay(Color.white.opacity(0.12))
            }

            if showsMainPeriod {
                if !carriedSegments.isEmpty {
                    sectionLabel("PERIOD \(Fmt.hhmm(periodStart))–\(Fmt.hhmm(periodEnd))")
                }
                if !boundaries.isEmpty {
                    blockBar
                }
                VStack(alignment: .leading, spacing: 8) {
                    // Nerazbijen period nosi vrijeme u naslovu (ili na sekciji), pa se u
                    // redu ne ponavlja.
                    ForEach(mainSegments) { seg in
                        segmentRow(seg, big: big, single: mainSegments.count == 1)
                    }
                }
            }

            if request.isManual {
                nextUpField(big: big)
            }

            // Dva reda: gore tipkovnica, dolje akcije. U jednom redu se na 420 px
            // natpisi lome u dva reda.
            VStack(alignment: .leading, spacing: 10) {
                HStack(spacing: 12) {
                    hint("↑↓", "povijest")
                    hint("⏎", "spremi")
                    hint("esc", "preskoči")
                    if mainSegments.count == 1, showsMainPeriod, !boundaries.isEmpty {
                        hint("✂", "razbij period")
                    }
                    Spacer(minLength: 0)
                }
                HStack(spacing: 10) {
                    Spacer(minLength: 0)
                    if request.allowSnooze {
                        Button("Odgodi 5 min", action: onSnooze)
                            .buttonStyle(.plain)
                            .font(.system(size: 11))
                            .foregroundStyle(Color.lloydsGray)
                            .underline()
                    }
                    Button(action: skip) {
                        Text("Preskoči")
                            .font(.system(size: 11, weight: .semibold))
                            .fixedSize()
                            .foregroundStyle(Color.lloydsGray)
                            .padding(.horizontal, 12)
                            .padding(.vertical, 5)
                            .background(
                                RoundedRectangle(cornerRadius: 6)
                                    .stroke(Color.white.opacity(0.22), lineWidth: 1)
                            )
                    }
                    .buttonStyle(.plain)
                    .help("Ne bilježi ništa — period se vraća u sljedeći prompt")
                }
            }
        }
        .padding(big ? 28 : 18)
        .frame(width: width)
        .onAppear {
            // Fokus čeka isto koliko i prozor prije nego uzme tipkovnicu — pop-up koji
            // iskoči dok tipkaš u drugoj aplikaciji ne smije presresti ostatak rečenice.
            DispatchQueue.main.asyncAfter(deadline: .now() + PanelFade.keyDelay + 0.05) {
                focusedField = segments.first?.start
            }
        }
        .onExitCommand(perform: skip)
    }

    // MARK: - Traka blokova

    private var blockBar: some View {
        let total = max(periodEnd.timeIntervalSince(periodStart), 1)
        let blocks = mainSegments
        return VStack(alignment: .leading, spacing: 3) {
            GeometryReader { geo in
                let w = geo.size.width
                ZStack(alignment: .topLeading) {
                    ForEach(blocks) { seg in
                        let x = CGFloat(seg.start.timeIntervalSince(periodStart) / total) * w
                        let sw = CGFloat(seg.end.timeIntervalSince(seg.start) / total) * w
                        RoundedRectangle(cornerRadius: 6, style: .continuous)
                            .fill(Color.lloydsYellow.opacity(focusedField == seg.start || blocks.count == 1 ? 0.85 : 0.4))
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
            focusedField = mainSegments.last(where: { $0.start <= b })?.start ?? periodStart
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
                HStack(spacing: 3) {
                    if seg.carried {
                        Image(systemName: "arrow.uturn.left")
                            .font(.system(size: big ? 9 : 8, weight: .bold))
                    }
                    Text("\(Fmt.hhmm(seg.start))–\(Fmt.hhmm(seg.end))")
                        .font(.system(size: big ? 12 : 10, weight: .semibold, design: .monospaced))
                }
                .foregroundStyle(rowTint(seg))
                .frame(width: big ? 96 : 84, alignment: .leading)
                .help(seg.carried ? "Preskočeni period iz prijašnjeg prompta" : "")
            }
            TextField(seg.carried ? "preskočeno — upiši ili ostavi prazno" : "npr. Projekt X — opis zadatka",
                      text: binding(for: seg.start))
                .textFieldStyle(.plain)
                .font(.system(size: big ? 17 : 14))
                .foregroundStyle(.white)
                .tint(Color.lloydsYellow)
                .padding(big ? 14 : 10)
                .background(
                    RoundedRectangle(cornerRadius: 10, style: .continuous)
                        .fill(Color.white.opacity(seg.carried ? 0.04 : 0.07))
                )
                .overlay(
                    RoundedRectangle(cornerRadius: 10, style: .continuous)
                        .stroke(Color.lloydsYellow.opacity(focusedField == seg.start ? 0.8 : 0.25), lineWidth: 1)
                )
                .focused($focusedField, equals: seg.start)
                .onSubmit(submit)
                .onKeyPress(.upArrow) { cycleHistory(older: true); return .handled }
                .onKeyPress(.downArrow) { cycleHistory(older: false); return .handled }
                .onKeyPress(.escape) { skip(); return .handled }
        }
    }

    private func sectionLabel(_ title: String) -> some View {
        Text(title)
            .font(.system(size: 9, weight: .heavy))
            .tracking(1.2)
            .foregroundStyle(Color.lloydsGray.opacity(0.6))
    }

    private func rowTint(_ seg: Segment) -> Color {
        if focusedField == seg.start { return Color.lloydsYellow }
        return seg.carried ? Color.lloydsGray.opacity(0.55) : Color.lloydsGray
    }

    /// Ručni prompt: čime korisnik nastavlja. Ne bilježi se kao unos — samo pre-fillava
    /// sljedeći prompt, pa se prebacivanje na drugi projekt zapiše u jednom koraku.
    private func nextUpField(big: Bool) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            sectionLabel("NASTAVLJAM S — NIJE OBAVEZNO")
            TextField("npr. Projekt B — hitni fix", text: binding(for: Self.nextUpKey))
                .textFieldStyle(.plain)
                .font(.system(size: big ? 15 : 13))
                .foregroundStyle(.white)
                .tint(Color.lloydsYellow)
                .padding(big ? 12 : 9)
                .background(
                    RoundedRectangle(cornerRadius: 10, style: .continuous)
                        .fill(Color.white.opacity(0.05))
                )
                .overlay(
                    RoundedRectangle(cornerRadius: 10, style: .continuous)
                        .stroke(Color.lloydsYellow.opacity(focusedField == Self.nextUpKey ? 0.8 : 0.18), lineWidth: 1)
                )
                .focused($focusedField, equals: Self.nextUpKey)
                .onSubmit(submit)
                .onKeyPress(.upArrow) { cycleHistory(older: true); return .handled }
                .onKeyPress(.downArrow) { cycleHistory(older: false); return .handled }
                .onKeyPress(.escape) { skip(); return .handled }
            Text("Sljedeći prompt kreće s ovim opisom.")
                .font(.system(size: 10))
                .foregroundStyle(Color.lloydsGray.opacity(0.5))
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
        .fixedSize()
        .foregroundStyle(Color.lloydsGray)
    }

    // MARK: - Akcije

    /// Odgovor nije obavezan: segmenti bez teksta su preskočeni i engine ih vraća u
    /// sljedeći prompt.
    private func submit() {
        onSubmit(result(skipAll: false))
    }

    private func skip() {
        onSubmit(result(skipAll: true))
    }

    private func result(skipAll: Bool) -> PromptResult {
        PromptResult(
            segments: segments.map {
                PromptSegment(start: $0.start, end: $0.end, text: skipAll ? "" : (texts[$0.start] ?? ""))
            },
            // "Nastavljam s" vrijedi i kad se period preskoči — prebacivanje na drugi
            // projekt je jedini razlog zašto je to polje tamo.
            nextUp: request.isManual ? texts[Self.nextUpKey] : nil
        )
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
