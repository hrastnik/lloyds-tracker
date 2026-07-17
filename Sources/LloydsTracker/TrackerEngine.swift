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

    @Published var settings: AppSettings {
        didSet {
            Store.saveSettings(settings)
            settingsChanged(from: oldValue)
        }
    }

    private(set) var currentDayKey: String
    private var sessionStart: Date?
    private var lastCovered = Date()
    private var pausedSince: Date?
    private var isLocked = false
    private var lockedAt: Date?
    private var timer: Timer?
    private let prompt = PromptController()

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
    }

    // MARK: - Kontrole

    func start() {
        let now = Date()
        currentDayKey = Store.dayKey(now)
        entries = Store.loadDay(currentDayKey)
        sessionStart = now
        lastCovered = now
        pauseUntil = nil
        pausedSince = nil
        awaitingReturnSince = nil
        nextPromptAt = alignedNextPrompt(after: now)
        isTracking = true
    }

    func stop() {
        guard isTracking else { return }
        let now = Date()
        if pauseUntil != nil {
            endManualPause(at: now)
        }
        prompt.close()
        if now.timeIntervalSince(lastCovered) > 60 {
            show(PromptRequest(
                start: lastCovered,
                end: now,
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
        nextPromptAt = Date().addingTimeInterval(TimeInterval(minutes * 60))
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

    // MARK: - Tick petlja

    private func tick() {
        guard isTracking else { return }
        let now = Date()

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

        guard !prompt.isVisible else { return }
        if let next = nextPromptAt, now >= next {
            attemptPrompt(now: now)
        }
    }

    private func attemptPrompt(now: Date) {
        let idle = IdleMonitor.idleSeconds()
        if isLocked {
            awaitingReturnSince = max(lockedAt ?? now, lastCovered)
        } else if settings.idleDetectionEnabled && idle >= TimeInterval(settings.idleThresholdMinutes * 60) {
            awaitingReturnSince = max(now.addingTimeInterval(-idle), lastCovered)
        } else {
            show(PromptRequest(
                start: lastCovered,
                end: nil,
                allowSnooze: settings.promptStyle == .floating
            ))
        }
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
        ) { [weak self] text in
            self?.handleSubmit(request, text: text)
        } onSnooze: { [weak self] in
            self?.snooze(minutes: 5)
        }
    }

    private func handleSubmit(_ request: PromptRequest, text: String) {
        let now = Date()
        let end = request.end ?? now
        if end.timeIntervalSince(request.start) > 5 {
            entries.append(Entry(start: request.start, end: end, text: text, kind: .work))
        }
        if let pending = request.pauseAfter, now > pending.start {
            entries.append(Entry(start: pending.start, end: now, text: pending.reason, kind: .pause))
            lastCovered = max(lastCovered, now)
        } else {
            lastCovered = max(lastCovered, end)
        }
        pushHistory(text)
        persistDay()

        if request.isFinal {
            finalizeStop()
        } else if pauseUntil == nil {
            nextPromptAt = alignedNextPrompt(after: now)
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
            nextPromptAt = max(Date().addingTimeInterval(5), alignedNextPrompt(after: Date()))
        }
        if old.historyLimit != settings.historyLimit, history.count > settings.historyLimit {
            history = Array(history.prefix(settings.historyLimit))
            Store.saveHistory(history)
        }
        if old.launchAtLogin != settings.launchAtLogin {
            applyLaunchAtLogin()
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
}
