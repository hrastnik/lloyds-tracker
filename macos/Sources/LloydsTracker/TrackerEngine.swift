import AppKit
import Combine
import ServiceManagement

@MainActor
final class TrackerEngine: ObservableObject {
    @Published var isTracking = false
    @Published var entries: [Entry] = []
    @Published var history: [String] = []
    @Published var pauseUntil: Date?
    @Published var nextPromptAt: Date?
    @Published var awaitingReturnSince: Date?
    @Published var launchAtLoginStatus: String?
    /// Vrijeme automatskog zaustavljanja za trenutnu sesiju (nil = isključeno).
    /// Produženja iz upozorenja mijenjaju samo ovo, ne i postavku.
    @Published var autoStopAt: Date?

    @Published var settings: AppSettings {
        didSet {
            Store.saveSettings(settings)
            settingsChanged(from: oldValue)
        }
    }

    private(set) var currentDayKey: String
    private var sessionStart: Date?
    private var lastCovered = Date()
    /// Kraj perioda vidljivog "običnog" prompta; produžuje se dok čeka odgovor.
    /// nil znači da nema prompta koji se smije produžiti (npr. pauza/kraj dana).
    private var activePromptEnd: Date?
    private var pausedSince: Date?
    private var isLocked = false
    private var lockedAt: Date?
    private var timer: Timer?
    private let prompt = PromptController()
    private let startupReminder = StartupReminderController()
    private let autoStopWarning = AutoStopWarningController()
    private var autoStopWarningShown = false
    /// Koliko prije automatskog zaustavljanja iskoči upozorenje.
    private let autoStopLead: TimeInterval = 60

    /// Postavlja se iz view sloja — otvara prozor "Pregled dana".
    var openSummary: () -> Void = {}

    var interval: TimeInterval { TimeInterval(settings.intervalMinutes * 60) }
    var isPromptVisible: Bool { prompt.isVisible }

    init() {
        settings = Store.loadSettings()
        history = Store.loadHistory()
        currentDayKey = Store.dayKey(Date())
        entries = Store.loadDay(currentDayKey)

        let dnc = DistributedNotificationCenter.default()
        dnc.addObserver(forName: .init("com.apple.screenIsLocked"), object: nil, queue: .main) { [weak self] _ in
            Task { @MainActor in
                self?.isLocked = true
                self?.lockedAt = Date()
            }
        }
        dnc.addObserver(forName: .init("com.apple.screenIsUnlocked"), object: nil, queue: .main) { [weak self] _ in
            Task { @MainActor in self?.isLocked = false }
        }

        let t = Timer(timeInterval: 1.0, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.tick() }
        }
        RunLoop.main.add(t, forMode: .common)
        timer = t

        // Kratki delay da se app slegne (menu bar ikona, ekrani) prije pop-upa.
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.0) { [weak self] in
            Task { @MainActor in self?.showStartupReminderIfNeeded() }
        }
    }

    /// Pop-up podsjetnik na pokretanju — samo ako je uključen u postavkama i
    /// dan još nije pokrenut (da se ne zaboravi startati tracking).
    func showStartupReminderIfNeeded() {
        guard settings.showStartupReminder, !isTracking, !prompt.isVisible else { return }
        startupReminder.show(
            dayTitle: Fmt.dayTitle.string(from: Date()),
            onStart: { [weak self] in self?.start() },
            onDismiss: {}
        )
    }

    // MARK: - Kontrole

    func start() {
        let now = Date()
        currentDayKey = Store.dayKey(now)
        entries = Store.loadDay(currentDayKey)
        sessionStart = now

        // Trackaj od početka trenutnog intervala (npr. start u 9:56 uz 15 min → od 9:45).
        // Ako nakon početka intervala već postoji neki unos, kreni od početka trenutnog
        // 5-min bloka (i nikad prije kraja zadnjeg unosa) da ne nastane dupli zapis.
        let intervalStart = Self.gridFloor(now, step: interval)
        var coverFrom = intervalStart
        if let latestEnd = entries.map(\.end).max(), latestEnd > intervalStart {
            coverFrom = max(Self.gridFloor(now, step: 300), latestEnd)
        }
        lastCovered = min(coverFrom, now)
        pauseUntil = nil
        pausedSince = nil
        awaitingReturnSince = nil
        activePromptEnd = nil
        nextPromptAt = alignedNextPrompt(after: now)
        autoStopWarningShown = false
        autoStopAt = nextAutoStop(after: now)
        isTracking = true
    }

    func stop() {
        stop(at: Date())
    }

    /// `endTime` je kraj zadnjeg perioda — kod automatskog zaustavljanja to je zakazano
    /// vrijeme, a ne trenutak kad se odgovori na zadnji prompt (koji može biti i sutra).
    private func stop(at endTime: Date) {
        guard isTracking else { return }
        cancelAutoStop()
        if pauseUntil != nil {
            endManualPause(at: endTime)
        }
        prompt.close()
        activePromptEnd = nil

        let periodStart = lastCovered
        var periodEnd = endTime
        // Ako je korisnik odsutan (idle/zaključan ekran), odsutnost bilježimo kao pauzu,
        // a pitamo samo za rad do trenutka odsutnosti — inače bi cijela odsutnost
        // završila kao "rad".
        if let gapStart = awaitingReturnSince {
            awaitingReturnSince = nil
            let pauseStart = max(gapStart, periodStart)
            if endTime > pauseStart {
                entries.append(Entry(start: pauseStart, end: endTime, text: "Pauza (odsutnost)", kind: .pause))
            }
            periodEnd = pauseStart
            lastCovered = max(lastCovered, endTime)
        }

        if periodEnd.timeIntervalSince(periodStart) > 60 {
            show(PromptRequest(
                start: periodStart,
                end: periodEnd,
                isFinal: true,
                note: "Kraj dana — što si radio u zadnjem periodu?",
                allowSnooze: false
            ))
        } else {
            finalizeStop()
        }
    }

    private func finalizeStop() {
        isTracking = false
        cancelAutoStop()
        nextPromptAt = nil
        sessionStart = nil
        awaitingReturnSince = nil
        persistDay()
        openSummary()
    }

    func pause(minutes: Int?) {
        guard isTracking, pauseUntil == nil else { return }
        let now = Date()
        pausedSince = now
        if let minutes {
            pauseUntil = now.addingTimeInterval(TimeInterval(minutes * 60))
        } else {
            pauseUntil = .distantFuture // do kraja dana / dok se ručno ne nastavi
        }
        prompt.close()
        activePromptEnd = nil
        if now.timeIntervalSince(lastCovered) > 60 {
            show(PromptRequest(
                start: lastCovered,
                end: now,
                note: "Prije pauze — na čemu si radio?",
                allowSnooze: false
            ))
        }
    }

    func resume() {
        guard pauseUntil != nil else { return }
        endManualPause(at: Date())
    }

    func snooze(minutes: Int) {
        prompt.close()
        activePromptEnd = nil
        // Zaokruži na 5-min mrežu da periodi (i trajanja) ostanu poravnati.
        let target = Self.snapToGrid(Date().addingTimeInterval(TimeInterval(minutes * 60)))
        nextPromptAt = max(target, Date().addingTimeInterval(60))
    }

    /// Najbliža točka 5-minutne mreže (npr. 10:17:40 → 10:20).
    private static func snapToGrid(_ date: Date) -> Date {
        let cal = Calendar.current
        guard let hourStart = cal.dateInterval(of: .hour, for: date)?.start else { return date }
        let step: TimeInterval = 300
        return hourStart.addingTimeInterval((date.timeIntervalSince(hourStart) / step).rounded() * step)
    }

    /// Početak bloka mreže u kojem se `date` nalazi (npr. 9:56 uz step 900 → 9:45).
    private static func gridFloor(_ date: Date, step: TimeInterval) -> Date {
        let cal = Calendar.current
        guard let hourStart = cal.dateInterval(of: .hour, for: date)?.start else { return date }
        let elapsed = date.timeIntervalSince(hourStart)
        return hourStart.addingTimeInterval(floor(elapsed / step) * step)
    }

    func deleteEntry(id: UUID, dayKey: String) {
        if dayKey == currentDayKey {
            entries.removeAll { $0.id == id }
            persistDay()
        } else {
            var day = Store.loadDay(dayKey)
            day.removeAll { $0.id == id }
            Store.saveDay(dayKey, entries: day)
            objectWillChange.send()
        }
    }

    /// Sljedeći prompt poravnat s početkom sata (npr. 15 min → :00, :15, :30, :45).
    /// Interval koji ne dijeli sat (20, 45) resetira se na svakom punom satu.
    private func alignedNextPrompt(after date: Date) -> Date {
        let cal = Calendar.current
        guard let hourStart = cal.dateInterval(of: .hour, for: date)?.start else {
            return date.addingTimeInterval(interval)
        }
        let elapsed = date.timeIntervalSince(hourStart)
        let next = hourStart.addingTimeInterval((floor(elapsed / interval) + 1) * interval)
        let nextHour = hourStart.addingTimeInterval(3600)
        return min(next, nextHour)
    }

    // MARK: - Automatsko zaustavljanje

    /// Sljedeće zaustavljanje u zadano vrijeme dana; ako je to vrijeme danas već prošlo,
    /// zakazuje se za sutra (npr. start u 20:00 uz auto-stop 16:00).
    private func nextAutoStop(after date: Date) -> Date? {
        guard settings.autoStopEnabled else { return nil }
        let cal = Calendar.current
        var comps = cal.dateComponents([.year, .month, .day], from: date)
        comps.hour = settings.autoStopHour
        comps.minute = settings.autoStopMinute
        comps.second = 0
        guard let target = cal.date(from: comps) else { return nil }
        if target > date { return target }
        return cal.date(byAdding: .day, value: 1, to: target) ?? target.addingTimeInterval(86_400)
    }

    private func cancelAutoStop() {
        autoStopAt = nil
        autoStopWarningShown = false
        autoStopWarning.close()
    }

    /// Produži današnje zaustavljanje — računa se od zakazanog vremena (16:00 + 30 → 16:30).
    /// Postavka se ne mijenja, pa sutra opet vrijedi zadano vrijeme.
    func extendAutoStop(minutes: Int) {
        guard let current = autoStopAt else { return }
        autoStopWarning.close()
        autoStopWarningShown = false
        autoStopAt = max(current, Date()).addingTimeInterval(TimeInterval(minutes * 60))
    }

    private func showAutoStopWarning(stopAt: Date) {
        autoStopWarningShown = true
        if settings.soundEnabled {
            NSSound(named: "Glass")?.play()
        }
        autoStopWarning.show(
            stopAt: stopAt,
            lead: autoStopLead,
            onExtend: { [weak self] minutes in self?.extendAutoStop(minutes: minutes) },
            onStopNow: { [weak self] in self?.stop() },
            onDismiss: {}
        )
    }

    // MARK: - Tick petlja

    private func tick() {
        guard isTracking else { return }
        let now = Date()

        // Prije svega ostalog — auto-stop vrijedi i kad je pauzirano ili se čeka povratak.
        if settings.autoStopEnabled, let stopAt = autoStopAt {
            if now >= stopAt {
                stop(at: stopAt)
                return
            }
            if !autoStopWarningShown, stopAt.timeIntervalSince(now) <= autoStopLead {
                showAutoStopWarning(stopAt: stopAt)
            }
        }

        if let until = pauseUntil {
            if now >= until { endManualPause(at: now) }
            return
        }

        if let gapStart = awaitingReturnSince {
            if !isLocked && IdleMonitor.idleSeconds() < 5 {
                handleReturn(gapStart: gapStart, now: now)
            }
            return
        }

        if prompt.isVisible {
            // Neodgovoren prompt "preživio" je granicu intervala — ne otvaramo drugi
            // prompt, nego produžimo period na postojećem (skupno vrijeme).
            if let next = nextPromptAt, now >= next, activePromptEnd != nil {
                extendActivePrompt(to: next)
            }
            return
        }
        if let next = nextPromptAt, now >= next {
            attemptPrompt(now: now)
        }
    }

    private func attemptPrompt(now: Date) {
        let idle = IdleMonitor.idleSeconds()
        if settings.lockPauseEnabled && isLocked {
            awaitingReturnSince = max(lockedAt ?? now, lastCovered)
        } else if settings.idleDetectionEnabled && idle >= TimeInterval(settings.idleThresholdMinutes * 60) {
            awaitingReturnSince = max(now.addingTimeInterval(-idle), lastCovered)
        } else {
            // Kraj perioda je zakazano (poravnato) vrijeme prompta, ne trenutak odgovora —
            // tako su unosi uvijek točno na 5-min mreži, a kašnjenje odgovora se
            // prelijeva u sljedeći period.
            let end = nextPromptAt ?? now
            activePromptEnd = end
            show(PromptRequest(
                start: lastCovered,
                end: end,
                allowSnooze: settings.promptStyle == .floating
            ))
            // Iduća granica na kojoj će se ovaj prompt produžiti (a ne otvoriti novi).
            nextPromptAt = alignedNextPrompt(after: end)
        }
    }

    /// Produži vidljivi prompt do nove granice intervala i pomakni sljedeću granicu.
    private func extendActivePrompt(to boundary: Date) {
        activePromptEnd = boundary
        prompt.extend(to: boundary)
        nextPromptAt = alignedNextPrompt(after: boundary)
    }

    private func handleReturn(gapStart: Date, now: Date) {
        awaitingReturnSince = nil
        let gapMinutes = max(1, Int(now.timeIntervalSince(gapStart) / 60))

        if gapStart.timeIntervalSince(lastCovered) > 60 {
            show(PromptRequest(
                start: lastCovered,
                end: gapStart,
                pauseAfter: .init(start: gapStart, reason: "Pauza (odsutnost)"),
                note: "Bio si odsutan ~\(gapMinutes) min — to razdoblje bit će označeno kao pauza.",
                allowSnooze: false
            ))
        } else {
            if now > gapStart {
                entries.append(Entry(start: max(gapStart, lastCovered), end: now, text: "Pauza (odsutnost)", kind: .pause))
                persistDay()
            }
            lastCovered = now
            nextPromptAt = alignedNextPrompt(after: now)
        }
    }

    private func endManualPause(at now: Date) {
        if let since = pausedSince, now > since {
            entries.append(Entry(start: max(since, lastCovered), end: now, text: "Pauza", kind: .pause))
        }
        pausedSince = nil
        pauseUntil = nil
        lastCovered = max(lastCovered, now)
        nextPromptAt = alignedNextPrompt(after: now)
        persistDay()
    }

    // MARK: - Prompt

    private func show(_ request: PromptRequest) {
        if settings.soundEnabled {
            NSSound(named: "Glass")?.play()
        }
        prompt.show(
            request: request,
            style: settings.promptStyle,
            history: history
        ) { [weak self] segments in
            self?.handleSubmit(request, segments: segments)
        } onSnooze: { [weak self] in
            self?.snooze(minutes: 5)
        }
    }

    private func handleSubmit(_ request: PromptRequest, segments: [PromptSegment]) {
        let now = Date()
        // Produženi kraj (ako je prompt čekao preko granica) ima prednost nad izvornim.
        let effectiveEnd = activePromptEnd ?? request.end
        activePromptEnd = nil
        var coveredEnd = request.start
        for seg in segments where seg.end.timeIntervalSince(seg.start) > 5 {
            entries.append(Entry(start: seg.start, end: seg.end, text: seg.text, kind: .work))
            // Kronološki redoslijed → zadnji segment završi kao history.first (prefill za idući prompt).
            pushHistory(seg.text)
            coveredEnd = max(coveredEnd, seg.end)
        }
        if let pending = request.pauseAfter, now > pending.start {
            entries.append(Entry(start: pending.start, end: now, text: pending.reason, kind: .pause))
            lastCovered = max(lastCovered, now)
        } else {
            lastCovered = max(lastCovered, effectiveEnd ?? coveredEnd)
        }
        persistDay()

        if request.isFinal {
            finalizeStop()
        } else if pauseUntil == nil {
            nextPromptAt = alignedNextPrompt(after: max(now, lastCovered))
        }
    }

    private func pushHistory(_ text: String) {
        let t = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !t.isEmpty else { return }
        history.removeAll { $0 == t }
        history.insert(t, at: 0)
        if history.count > settings.historyLimit {
            history = Array(history.prefix(settings.historyLimit))
        }
        Store.saveHistory(history)
    }

    private func persistDay() {
        entries.sort { $0.start < $1.start }
        Store.saveDay(currentDayKey, entries: entries)
    }

    // MARK: - Settings

    private func settingsChanged(from old: AppSettings) {
        if old.intervalMinutes != settings.intervalMinutes, isTracking, pauseUntil == nil {
            nextPromptAt = alignedNextPrompt(after: Date())
        }
        if old.historyLimit != settings.historyLimit, history.count > settings.historyLimit {
            history = Array(history.prefix(settings.historyLimit))
            Store.saveHistory(history)
        }
        if old.launchAtLogin != settings.launchAtLogin {
            applyLaunchAtLogin()
        }
        // Promjena vremena/uključenosti poništava eventualno današnje produženje.
        if old.autoStopEnabled != settings.autoStopEnabled
            || old.autoStopHour != settings.autoStopHour
            || old.autoStopMinute != settings.autoStopMinute {
            autoStopWarning.close()
            autoStopWarningShown = false
            autoStopAt = isTracking ? nextAutoStop(after: Date()) : nil
        }
    }

    private func applyLaunchAtLogin() {
        guard Bundle.main.bundleIdentifier != nil else {
            launchAtLoginStatus = "Radi samo iz .app bundle-a — buildaj s ./build.sh i pokreni iz /Applications."
            return
        }
        do {
            if settings.launchAtLogin {
                try SMAppService.mainApp.register()
            } else {
                try SMAppService.mainApp.unregister()
            }
            launchAtLoginStatus = nil
        } catch {
            launchAtLoginStatus = "Greška: \(error.localizedDescription)"
        }
    }

    // MARK: - Pomoćno za UI

    var menuIcon: String {
        if !isTracking { return "clock" }
        if pauseUntil != nil { return "pause.circle.fill" }
        if awaitingReturnSince != nil { return "moon.zzz.fill" }
        return "clock.fill"
    }

    var statusText: String {
        if !isTracking { return "Nije pokrenuto" }
        if let until = pauseUntil {
            return until == .distantFuture
                ? "Pauzirano do nastavka"
                : "Pauzirano do \(Fmt.hhmm(until))"
        }
        if let since = awaitingReturnSince {
            return "Odsutan od \(Fmt.hhmm(since)) — čekam povratak"
        }
        return "Trackam"
    }

    /// "Auto-stop u 16:00" — nil kad je isključeno ili kad tracking nije aktivan.
    var autoStopText: String? {
        guard isTracking, let at = autoStopAt else { return nil }
        let today = Calendar.current.isDate(at, inSameDayAs: Date())
        return today ? "Auto-stop u \(Fmt.hhmm(at))" : "Auto-stop sutra u \(Fmt.hhmm(at))"
    }
}
