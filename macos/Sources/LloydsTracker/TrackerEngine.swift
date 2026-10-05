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
    /// Preskočeni periodi koji nisu susjedni sljedećem promptu (npr. pauza između) —
    /// nose se dalje kao zasebni redovi dok se ne popune ili dan ne završi.
    private var carriedSpans: [PromptSpan] = []
    /// Dan je zatvoren, a zadnji prompt još čeka odgovor — ništa se više ne zakazuje i
    /// dan se ne smije zatvarati drugi put (inače bi tick vrtio isti prompt u krug).
    private var awaitingFinalAnswer = false
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
    /// Dan (dayKey) za koji je podsjetnik na početak radnog dana odrađen — prikazan ili
    /// je dan u međuvremenu pokrenut. Drži se u memoriji: kod restarta aplikacije ulogu
    /// ionako preuzima podsjetnik kod pokretanja.
    private var workdayReminderDayKey: String?

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
        guard settings.showStartupReminder, !isTracking, !prompt.isVisible,
              !isSkippedWeekend(Date()) else { return }
        presentStartReminder(now: Date())
    }

    // MARK: - Kontrole

    /// `backfillFrom` (iz podsjetnika na početak radnog dana) pomiče početak trackanja
    /// unatrag — prvi prompt onda pita za cijelo jutro, npr. 8:30–9:45.
    func start(from backfillFrom: Date? = nil) {
        let now = Date()
        currentDayKey = Store.dayKey(now)
        entries = Store.loadDay(currentDayKey)
        sessionStart = now
        workdayReminderDayKey = currentDayKey
        startupReminder.close()

        // Nadoknada vrijedi samo unutar današnjeg dana — sigurnosna ograda da početak nikad
        // ne padne u jučer (prvi prompt bi onda pitao za period od 25 h).
        let backfill = backfillFrom.flatMap { Store.dayKey($0) == currentDayKey ? $0 : nil }

        // Trackaj od početka trenutnog intervala (npr. start u 9:56 uz 15 min → od 9:45).
        // Ako nakon toga već postoji neki unos, kreni od kraja zadnjeg unosa — kod
        // nadoknade da se popuni ostatak jutra, inače od početka trenutnog 5-min bloka
        // (da ne nastane dupli zapis).
        var coverFrom = backfill ?? Self.gridFloor(now, step: interval)
        if let latestEnd = entries.map(\.end).max(), latestEnd > coverFrom {
            coverFrom = backfill == nil ? max(Self.gridFloor(now, step: 300), latestEnd) : latestEnd
        }
        lastCovered = min(coverFrom, now)
        pauseUntil = nil
        pausedSince = nil
        awaitingReturnSince = nil
        activePromptEnd = nil
        carriedSpans = []
        awaitingFinalAnswer = false
        nextPromptAt = alignedNextPrompt(after: now)
        autoStopWarningShown = false
        autoStopAt = nextAutoStop(after: now)
        isTracking = true
    }

    func stop() {
        let now = Date()
        // Zaustavljanje u zadnjoj minuti prije auto-stopa (klik u upozorenju ili u meniju)
        // bilježi period do zakazanog vremena — inače dan završi minutu prije postavke
        // (npr. 16:59:46 umjesto 17:00).
        if let scheduled = autoStopAt, scheduled > now, scheduled.timeIntervalSince(now) <= autoStopLead {
            stop(at: scheduled)
        } else {
            stop(at: now)
        }
    }

    /// `endTime` je kraj zadnjeg perioda — kod automatskog zaustavljanja to je zakazano
    /// vrijeme, a ne trenutak kad se odgovori na zadnji prompt (koji može biti i sutra).
    private func stop(at endTime: Date) {
        guard isTracking, !awaitingFinalAnswer else { return }
        let now = Date()
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

        let hasPeriod = periodEnd.timeIntervalSince(periodStart) > 60
        if hasPeriod || !carriedSpans.isEmpty {
            // Prompt koji je prenoćio (laptop zatvoren) odgovara se sutra, a period je
            // odrezan na kraj svog dana — to treba i pisati, da unos ne izgleda pogrešno.
            let overnight = Store.dayKey(endTime) != Store.dayKey(now)
            awaitingFinalAnswer = true
            show(PromptRequest(
                start: periodStart,
                // Prompt koji visi samo zbog preskočenih redova nema svoj period.
                end: hasPeriod ? periodEnd : periodStart,
                isFinal: true,
                note: overnight
                    ? "Prompt je prenoćio — period je odrezan na kraj radnog dana (\(Fmt.hhmm(endTime)))."
                    : "Kraj dana — što si radio u zadnjem periodu?",
                allowSnooze: false
            ))
        } else {
            finalizeStop()
        }
    }

    private func finalizeStop() {
        isTracking = false
        awaitingFinalAnswer = false
        carriedSpans = []
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
        let hasPeriod = now.timeIntervalSince(lastCovered) > 60
        if hasPeriod || !carriedSpans.isEmpty {
            show(PromptRequest(
                start: lastCovered,
                end: hasPeriod ? now : lastCovered,
                note: "Prije pauze — na čemu si radio?",
                allowSnooze: false
            ))
        }
    }

    func resume() {
        guard pauseUntil != nil else { return }
        endManualPause(at: Date())
    }

    /// Ručno pokrenut prompt ("Zapiši sada") — pita za period od zadnjeg zapisa do sada i
    /// nudi polje "nastavljam s", pa sljedeći prompt kreće s tim pre-fillom. Ritam
    /// promptanja ostaje netaknut: `nextPromptAt` se ne pomiče, a ako prompt dočeka
    /// granicu intervala, period mu se samo produži (kao i običnom promptu).
    func manualPrompt() {
        guard isTracking, !awaitingFinalAnswer, pauseUntil == nil, awaitingReturnSince == nil else { return }
        guard !prompt.isVisible else {
            prompt.focus()
            return
        }
        let now = Date()
        activePromptEnd = now
        show(PromptRequest(
            start: lastCovered,
            end: now,
            isManual: true,
            note: "Ručni zapis — spremi period do sada, pa (ako želiš) upiši čime nastavljaš.",
            allowSnooze: false
        ))
    }

    /// Je li "Zapiši sada" trenutno smisleno (meni ga inače skriva).
    var canPromptNow: Bool {
        isTracking && !awaitingFinalAnswer && pauseUntil == nil && awaitingReturnSince == nil
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

    /// Briše jedan ili više unosa — spojeni red u kronološkom pregledu pokriva više unosa.
    func deleteEntries(ids: [UUID], dayKey: String) {
        let set = Set(ids)
        if dayKey == currentDayKey {
            entries.removeAll { set.contains($0.id) }
            persistDay()
        } else {
            var day = Store.loadDay(dayKey)
            day.removeAll { set.contains($0.id) }
            Store.saveDay(dayKey, entries: day)
            objectWillChange.send()
        }
    }

    /// Ispravlja unos(e) iza jednog reda kronološkog pregleda. Ako su vremena ostala ista,
    /// mijenja se samo opis i vrsta svih blokova reda (spojeni red ostaje spojen). Ako su
    /// vremena promijenjena, red postaje **jedan** unos — novi raspon se ne može smisleno
    /// razdijeliti na stare granice blokova.
    func updateEntries(
        row: ChronoRow,
        dayKey: String,
        text: String,
        start: Date,
        end: Date,
        kind: EntryKind
    ) {
        let clean = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !clean.isEmpty, end > start else { return }
        let ids = Set(row.ids)
        let timesChanged = abs(start.timeIntervalSince(row.start)) > 1
            || abs(end.timeIntervalSince(row.end)) > 1

        func apply(to day: inout [Entry]) {
            if timesChanged {
                day.removeAll { ids.contains($0.id) }
                day.append(Entry(id: row.ids[0], start: start, end: end, text: clean, kind: kind))
            } else {
                for i in day.indices where ids.contains(day[i].id) {
                    day[i].text = clean
                    day[i].kind = kind
                }
            }
            day.sort { $0.start < $1.start }
        }

        if dayKey == currentDayKey {
            apply(to: &entries)
            Store.saveDay(currentDayKey, entries: entries)
        } else {
            var day = Store.loadDay(dayKey)
            apply(to: &day)
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

    /// Kraj radnog dana za sesiju koja je "prenoćila" — nil dok je sesija još u svom danu.
    ///
    /// Laptop se zatvori s otvorenim promptom, a odgovor dođe sutra: bez ograde bi taj
    /// period tekao cijelu noć i završio kao višesatni unos od jučer. Rez je vrijeme iz
    /// sekcije "Automatsko zaustavljanje" na dan sesije, a ako je rad zabilježen i dalje
    /// od njega (produženja iz upozorenja, ili je auto-stop isključen) — to se poštuje.
    private func overnightCutoff(now: Date) -> Date? {
        guard Store.dayKey(now) != currentDayKey else { return nil }
        // Sesija koja legitimno prelazi u novi dan (start u 20:00 uz auto-stop u 16:00 →
        // zaustavljanje je zakazano za sutra) se ne prekida.
        if settings.autoStopEnabled, let stopAt = autoStopAt, stopAt > now { return nil }
        guard let sessionDay = Fmt.dayKey.date(from: currentDayKey) else { return nil }
        let configured = Calendar.current.date(
            bySettingHour: min(max(settings.autoStopHour, 0), 23),
            minute: min(max(settings.autoStopMinute, 0), 59),
            second: 0,
            of: sessionDay
        ) ?? sessionDay
        // Rad zabilježen i preko vremena iz postavki (auto-stop isključen, rad poslije
        // ponoći) ne pomiče se unatrag — takav dan se zatvara na svojoj granici, u ponoć.
        guard configured > lastCovered else {
            let dayEnd = Calendar.current.date(
                byAdding: .day, value: 1, to: Calendar.current.startOfDay(for: sessionDay)
            ) ?? sessionDay
            return max(dayEnd, lastCovered)
        }
        return configured
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

    // MARK: - Početak radnog dana

    /// Početak radnog dana na dan `date` (nil kad je podsjetnik isključen).
    private func workdayStart(on date: Date) -> Date? {
        guard settings.workdayStartEnabled else { return nil }
        return Calendar.current.date(
            bySettingHour: min(max(settings.workdayStartHour, 0), 23),
            minute: min(max(settings.workdayStartMinute, 0), 59),
            second: 0,
            of: date
        )
    }

    /// Subota ili nedjelja uz uključeno "Preskoči vikende" — tad ne iskače nijedan
    /// podsjetnik. Namjerno fiksno sub/ned, a ne `isDateInWeekend` (ovisi o regiji), da se
    /// sva tri porta ponašaju isto.
    private func isSkippedWeekend(_ date: Date) -> Bool {
        guard settings.skipWeekendReminders else { return false }
        let weekday = Calendar.current.component(.weekday, from: date) // 1 = nedjelja, 7 = subota
        return weekday == 1 || weekday == 7
    }

    /// Vrijeme od kojeg podsjetnik nudi nadoknadu ("Start od 8:30") — nil kad je opcija
    /// isključena, kad radni dan još nije počeo ili kad je razmak premali da bi se
    /// nadoknada uopće razlikovala od starta od sada.
    private func backfillStart(now: Date) -> Date? {
        guard settings.workdayStartBackfillEnabled,
              let start = workdayStart(on: now),
              now.timeIntervalSince(start) >= 300 else { return nil }
        return start
    }

    /// Podsjetnik u zadano vrijeme: iskoči kad radni dan počne, a ako je računalo tada
    /// spavalo — čim se probudi i otključa (timer se nakon buđenja nastavi vrtjeti, pa ga
    /// uhvati prvi idući tick). Javlja se jednom dnevno.
    private func checkWorkdayStart(now: Date) {
        guard settings.workdayStartEnabled, !isTracking, !isLocked, !prompt.isVisible,
              !isSkippedWeekend(now),
              let start = workdayStart(on: now), now >= start else { return }

        // Otvoreni podsjetnik čeka odgovor koliko treba, pa može biti od jučer (prenoćio)
        // ili od prije početka radnog dana, kad nadoknada još nije bila u ponudi. Nosi
        // zastarjeli naslov i ponudu, a blokira i današnji podsjetnik — zamijenimo ga
        // svježim; ako nije zastario, pustimo ga na miru.
        var playSound = settings.soundEnabled
        if startupReminder.isVisible {
            let shownAt = startupReminder.shownAt ?? .distantPast
            let fromPreviousDay = Store.dayKey(shownAt) != Store.dayKey(now)
            let backfillAppeared = startupReminder.shownBackfillFrom == nil && backfillStart(now: now) != nil
            guard fromPreviousDay || backfillAppeared else { return }
            // Prozor od danas je već na ekranu i zvuk je uz njega odsvirao — mijenja mu se
            // samo ponuda, pa ide bez zvuka.
            if !fromPreviousDay { playSound = false }
            startupReminder.close()
        } else if workdayReminderDayKey == Store.dayKey(now) {
            return
        }
        if playSound {
            // Za razliku od podsjetnika na pokretanju, ovaj lako iskoči dok nisi za
            // ekranom (npr. čim se laptop probudi), pa ga prati i zvuk.
            NSSound(named: "Glass")?.play()
        }
        presentStartReminder(now: now)
    }

    /// Zajednički pop-up za oba podsjetnika (pokretanje aplikacije i početak radnog dana).
    private func presentStartReminder(now: Date) {
        // Podsjetnik prikazan prije početka radnog dana ne troši današnji termin — u
        // zadano vrijeme svejedno iskoči (npr. pokretanje u 7:00, radni dan u 8:30).
        if let start = workdayStart(on: now), now >= start {
            workdayReminderDayKey = Store.dayKey(now)
        }
        startupReminder.show(
            dayTitle: Fmt.dayTitle.string(from: now),
            backfillFrom: backfillStart(now: now),
            // Vrijeme nadoknade se računa u trenutku klika, a ne prikaza — inače bi
            // podsjetnik koji je prenoćio startao dan od jučerašnjeg početka.
            onStart: { [weak self] useBackfill in
                guard let self else { return }
                self.start(from: useBackfill ? self.backfillStart(now: Date()) : nil)
            },
            onDismiss: {}
        )
    }

    // MARK: - Tick petlja

    private func tick() {
        let now = Date()
        checkWorkdayStart(now: now)
        // Dan je zatvoren i čeka se odgovor na zadnji prompt — ništa se više ne zakazuje.
        guard isTracking, !awaitingFinalAnswer else { return }

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

        // Sigurnosna ograda za sesiju koja je prenoćila (laptop zatvoren, često s otvorenim
        // promptom): period se ne smije razvući u novi dan.
        if let cutoff = overnightCutoff(now: now) {
            stop(at: cutoff)
            return
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
        if let since = pausedSince {
            // Ako je prompt prije pauze preskočen, taj period nije susjedan onome što
            // slijedi (pauza je između) — nosi se dalje kao zasebni red.
            carry(PromptSpan(start: lastCovered, end: since))
            if now > since {
                entries.append(Entry(start: max(since, lastCovered), end: now, text: "Pauza", kind: .pause))
            }
        }
        pausedSince = nil
        pauseUntil = nil
        lastCovered = max(lastCovered, now)
        nextPromptAt = alignedNextPrompt(after: now)
        persistDay()
    }

    // MARK: - Prompt

    private func show(_ request: PromptRequest) {
        var request = request
        // Preskočeni periodi idu uz svaki prompt. Oni koji su susjedni glavnom periodu
        // stapaju se u njega (jedan period koji se može razbiti `✂`), ostali se prikazuju
        // kao zasebni redovi iznad.
        var carried = carriedSpans
        while let last = carried.last, abs(request.start.timeIntervalSince(last.end)) <= 1 {
            request.start = last.start
            // Prompt bez vlastitog perioda (samo preskočeni redovi) dobiva kraj stopljenog
            // perioda — inače bi ostao degeneriran i to vrijeme bi propalo.
            request.end = max(request.end ?? last.end, last.end)
            carried.removeLast()
        }
        request.carried = carried

        if settings.soundEnabled {
            NSSound(named: "Glass")?.play()
        }
        let submitted = request
        prompt.show(
            request: request,
            style: settings.promptStyle,
            history: history
        ) { [weak self] result in
            self?.handleSubmit(submitted, result: result)
        } onSnooze: { [weak self] in
            self?.snooze(minutes: 5)
        }
    }

    /// Doda preskočeni period u popis koji se nosi u sljedeći prompt; susjedni se spajaju.
    private func carry(_ span: PromptSpan) {
        guard span.duration > 60 else { return }
        if let last = carriedSpans.last, abs(span.start.timeIntervalSince(last.end)) <= 1 {
            carriedSpans[carriedSpans.count - 1].end = max(last.end, span.end)
        } else {
            carriedSpans.append(span)
        }
    }

    /// Segment bez teksta je **preskočen** — ne bilježi se. Ako je na kraju perioda,
    /// `lastCovered` ostaje gdje je bio, pa isti period sam iskoči u sljedećem promptu
    /// (produžen za novi interval). Preskočeni period kojem iza slijedi zabilježeno
    /// vrijeme nosi se dalje kao zasebni red.
    private func handleSubmit(_ request: PromptRequest, result: PromptResult) {
        let now = Date()
        // Produženi kraj (ako je prompt čekao preko granica) ima prednost nad izvornim.
        let effectiveEnd = activePromptEnd ?? request.end
        activePromptEnd = nil
        // Svi prikazani preskočeni periodi vraćeni su u odgovoru — popis se gradi ispočetka.
        carriedSpans = []

        let segments = result.segments.sorted { $0.start < $1.start }
        let texts = segments.map { $0.text.trimmingCharacters(in: .whitespacesAndNewlines) }

        // Rep praznih segmenata glavnog perioda samo "otkriva" vrijeme natrag.
        var coveredEnd = effectiveEnd ?? segments.last?.end ?? request.start
        var lastFilled = segments.count - 1
        while lastFilled >= 0, texts[lastFilled].isEmpty, segments[lastFilled].start >= request.start {
            coveredEnd = segments[lastFilled].start
            lastFilled -= 1
        }

        for (i, seg) in segments.enumerated() where seg.end.timeIntervalSince(seg.start) > 5 {
            if texts[i].isEmpty {
                if i <= lastFilled { carry(PromptSpan(start: seg.start, end: seg.end)) }
            } else {
                entries.append(Entry(start: seg.start, end: seg.end, text: texts[i], kind: .work))
                // Kronološki redoslijed → zadnji segment završi kao history.first (prefill za idući prompt).
                pushHistory(texts[i])
            }
        }
        // "Nastavljam s" iz ručnog prompta se ne bilježi kao unos, samo ide u povijest —
        // time postaje pre-fill sljedećeg prompta.
        if let nextUp = result.nextUp?.trimmingCharacters(in: .whitespacesAndNewlines), !nextUp.isEmpty {
            pushHistory(nextUp)
        }

        if let pending = request.pauseAfter, now > pending.start {
            // Odsutnost ide u zapis kao pauza; preskočeni rad prije nje nije susjedan
            // onome što slijedi, pa se nosi dalje zasebno.
            carry(PromptSpan(start: coveredEnd, end: min(pending.start, effectiveEnd ?? pending.start)))
            entries.append(Entry(start: pending.start, end: now, text: pending.reason, kind: .pause))
            lastCovered = max(lastCovered, now)
        } else {
            lastCovered = max(lastCovered, coveredEnd)
        }
        persistDay()

        if request.isFinal {
            // Dan je zatvoren — preskočene periode se više nema kad pitati.
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
        if old.workdayStartEnabled != settings.workdayStartEnabled
            || old.workdayStartHour != settings.workdayStartHour
            || old.workdayStartMinute != settings.workdayStartMinute
            || old.skipWeekendReminders != settings.skipWeekendReminders {
            // Novo vrijeme vrijedi od idućeg početka radnog dana: ako je današnji već
            // prošao, danas se više ne javlja — inače bi pop-up iskočio čim se u
            // postavkama namjesti raniji sat (ili vikendom isključi "Preskoči vikende").
            let now = Date()
            let started = workdayStart(on: now).map { now >= $0 } ?? false
            workdayReminderDayKey = started ? Store.dayKey(now) : nil
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
