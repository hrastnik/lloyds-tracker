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
    /// Novija verzija na GitHubu (nil = nema je ili provjera nije rađena).
    @Published var availableUpdate: AvailableUpdate?
    /// Ishod zadnje provjere nove verzije, za prikaz u postavkama.
    @Published var updateStatus: String?

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
    /// Odsutnost čiji povratni prompt (rad prije odsutnosti) još nije odgovoren — ako ga
    /// zatvori Stop ili Pauza, rad prije odsutnosti se nosi dalje, a period kreće od povratka.
    private var pendingReturn: PromptRequest.PendingPause?
    /// Dan je zatvoren, a zadnji prompt još čeka odgovor — ništa se više ne zakazuje i
    /// dan se ne smije zatvarati drugi put (inače bi tick vrtio isti prompt u krug).
    @Published private(set) var awaitingFinalAnswer = false
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
    /// Vrijeme zadnjeg ticka — velika rupa do sljedećeg znači da je računalo spavalo.
    private var lastTickAt: Date?
    /// Rupa između tickova nakon koje smatramo da je računalo spavalo (poklopac zatvoren).
    private let sleepGap: TimeInterval = 30 * 60
    /// Tick je svake sekunde — veća rupa znači da je računalo u međuvremenu spavalo.
    /// Zaključavanje javljeno tek nakon takve rupe (poklopac zatvoren odmah nakon
    /// zaključavanja, notifikacija stigne kod buđenja) dogodilo se najkasnije kod zadnjeg
    /// ticka prije spavanja.
    private let lockTickGap: TimeInterval = 5
    /// Dan (dayKey) za koji je podsjetnik na početak radnog dana odrađen — prikazan ili
    /// je dan u međuvremenu pokrenut. Drži se u memoriji: kod restarta aplikacije ulogu
    /// ionako preuzima podsjetnik kod pokretanja.
    private var workdayReminderDayKey: String?
    private let updatePopup = UpdatePopupController()
    /// Prva provjera nove verzije malo nakon pokretanja (da se app slegne), dalje jednom
    /// dnevno; nakon neuspjele (nema mreže) opet za sat.
    private var nextUpdateCheckAt = Date().addingTimeInterval(15)
    private var updateCheckInFlight = false
    /// Nova verzija čiji pop-up čeka da se makne prompt/podsjetnik.
    private var pendingUpdatePopup: AvailableUpdate?
    /// Kraj perioda zadnjeg prompta dok dan čeka zadnji odgovor — sprema se uz sesiju.
    private var finalEnd: Date?
    /// Zadnje stanje zapisano u `session.json` (nil = datoteke nema).
    private var savedSession: SavedSession?
    /// Prekinuta sesija je obnovljena — do tad tick ne radi ništa, da ne prebriše
    /// `session.json` niti ponudi podsjetnik prije nastavka.
    private var launched = false

    /// Postavlja se iz view sloja — otvara prozor "Pregled dana".
    var openSummary: () -> Void = {}
    /// Dan na koji engine otvara "Pregled dana" (kraj dana) — nakon zadnjeg prompta koji je
    /// prenoćio to je jučer, ne današnji prazan dan. `SummaryView` ga preuzme i vrati na nil.
    @Published var summaryDayRequest: Date?

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
                guard let self else { return }
                let now = Date()
                self.isLocked = true
                if let lastTickAt = self.lastTickAt, now.timeIntervalSince(lastTickAt) > self.lockTickGap {
                    self.lockedAt = lastTickAt
                } else {
                    self.lockedAt = now
                }
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
            Task { @MainActor in self?.launch() }
        }
    }

    /// Nakon pokretanja: nastavi trackanje koje je prekinulo gašenje računala, a ako ga
    /// nema — podsjetnik na početak dana.
    private func launch() {
        launched = true
        restoreSession(now: Date())
        showStartupReminderIfNeeded()
    }

    /// Pop-up podsjetnik na pokretanju — samo ako je uključen u postavkama, dan još nije
    /// pokrenut (da se ne zaboravi startati tracking) i radno vrijeme traje. Prije početka
    /// radnog dana šuti — javi se u zadano vrijeme (`checkWorkdayStart`).
    func showStartupReminderIfNeeded() {
        let now = Date()
        guard settings.showStartupReminder, !isTracking, !prompt.isVisible,
              !isSkippedWeekend(now), !isBeforeWorkday(now), !isAfterWorkday(now) else { return }
        presentStartReminder(now: now)
    }

    // MARK: - Kontrole

    /// `backfillFrom` (iz podsjetnika na početak radnog dana) pomiče početak trackanja
    /// unatrag — prompt odmah pita za završene blokove jutra, npr. 8:30–9:45 kod starta u
    /// 9:52 (tekući blok 9:45–10:00 ide u sljedeći prompt).
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
        pendingReturn = nil
        awaitingFinalAnswer = false
        finalEnd = nil
        nextPromptAt = alignedNextPrompt(after: now)
        autoStopWarningShown = false
        autoStopAt = nextAutoStop(after: now)
        isTracking = true
        persistSession()
        if backfill != nil {
            promptPastBlocks(now: now, note: "Nadoknada — na čemu si radio od \(Fmt.hhmm(lastCovered))?")
        }
    }

    /// Odmah pita za završene blokove od `lastCovered` do početka tekućeg bloka (nadoknada
    /// nakon starta od ranijeg vremena, nastavak nakon gašenja računala). Tekući blok ide u
    /// sljedeći prompt — ako ovaj dočeka njegovu granicu, period mu se samo produži.
    private func promptPastBlocks(now: Date, note: String) {
        let blockStart = Self.gridFloor(now, step: interval)
        guard blockStart.timeIntervalSince(lastCovered) > 60 else { return }
        activePromptEnd = blockStart
        show(PromptRequest(start: lastCovered, end: blockStart, note: note, allowSnooze: false))
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
    /// `note` zamjenjuje zadanu poruku zadnjeg prompta.
    private func stop(at endTime: Date, note: String? = nil) {
        guard isTracking, !awaitingFinalAnswer else { return }
        let now = Date()
        cancelAutoStop()
        if pauseUntil != nil {
            endManualPause(at: endTime)
        }
        prompt.close()
        activePromptEnd = nil
        settlePendingReturn()

        let periodStart = lastCovered
        var periodEnd = endTime
        // Ako je korisnik odsutan (idle/zaključan ekran), odsutnost bilježimo kao pauzu,
        // a pitamo samo za rad do trenutka odsutnosti — inače bi cijela odsutnost
        // završila kao "rad".
        if let gapStart = awaitingReturnSince {
            awaitingReturnSince = nil
            let pauseStart = max(gapStart, periodStart)
            if endTime > pauseStart {
                record(start: pauseStart, end: endTime, text: "Pauza (odsutnost)", kind: .pause)
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
            finalEnd = endTime
            show(PromptRequest(
                start: periodStart,
                // Prompt koji visi samo zbog preskočenih redova nema svoj period.
                end: hasPeriod ? periodEnd : periodStart,
                isFinal: true,
                note: note ?? (overnight
                    ? "Prompt je prenoćio — period je odrezan na kraj radnog dana (\(Fmt.hhmm(endTime)))."
                    : "Kraj dana — što si radio u zadnjem periodu?"),
                allowSnooze: false
            ))
        } else {
            finalizeStop()
        }
    }

    private func finalizeStop() {
        isTracking = false
        awaitingFinalAnswer = false
        finalEnd = nil
        carriedSpans = []
        pendingReturn = nil
        cancelAutoStop()
        nextPromptAt = nil
        sessionStart = nil
        awaitingReturnSince = nil
        persistDay()
        persistSession()
        summaryDayRequest = Fmt.dayKey.date(from: currentDayKey) ?? Date()
        openSummary()
    }

    func pause(minutes: Int?) {
        // Dok čeka zadnji odgovor dan je već zatvoren — pauza bi zatvorila zadnji prompt
        // i ostavila `awaitingFinalAnswer` zauvijek uključen (tracking zaglavi).
        guard isTracking, !awaitingFinalAnswer, pauseUntil == nil else { return }
        let now = Date()
        pausedSince = now
        if let minutes {
            pauseUntil = now.addingTimeInterval(TimeInterval(minutes * 60))
        } else {
            pauseUntil = .distantFuture // do kraja dana / dok se ručno ne nastavi
        }
        prompt.close()
        activePromptEnd = nil
        settlePendingReturn()
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

    /// Vraća zadnji prompt u prvi plan — "Završi dan" u meniju dok dan čeka zadnji odgovor.
    func focusFinalPrompt() {
        guard awaitingFinalAnswer, prompt.isVisible else { return }
        NSApp.activate(ignoringOtherApps: true)
        prompt.focus()
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
    /// razdijeliti na stare granice blokova — a preklopljeni dio drugih unosa se reže.
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
                Summarize.carve(&day, start: start, end: end)
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
            absorbManualEntries()
        } else {
            var day = Store.loadDay(dayKey)
            apply(to: &day)
            Store.saveDay(dayKey, entries: day)
            objectWillChange.send()
        }
        // Povijest (prefill idućeg prompta) prati samo tekući dan — ispravak jučerašnjeg
        // unosa ne smije postati prijedlog za ono na čemu radiš sada.
        if kind == .work, clean != row.text, dayKey == currentDayKey { pushHistory(clean) }
    }

    /// Ručno dodan unos iz kronološkog pregleda (npr. popunjavanje rupe). Preklopljeni
    /// dio postojećih unosa se reže — novi unos ima prednost.
    func addEntry(dayKey: String, text: String, start: Date, end: Date, kind: EntryKind) {
        let clean = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !clean.isEmpty, end > start else { return }

        func apply(to day: inout [Entry]) {
            Summarize.carve(&day, start: start, end: end)
            day.append(Entry(start: start, end: end, text: clean, kind: kind))
            day.sort { $0.start < $1.start }
        }

        if dayKey == currentDayKey {
            apply(to: &entries)
            Store.saveDay(currentDayKey, entries: entries)
            absorbManualEntries()
        } else {
            var day = Store.loadDay(dayKey)
            apply(to: &day)
            Store.saveDay(dayKey, entries: day)
            objectWillChange.send()
        }
        if kind == .work, dayKey == currentDayKey { pushHistory(clean) }
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
        // zaustavljanje je zakazano za sutra) se ne prekida — dok računalo radi; spavanje
        // preko ponoći hvata `tick` (`sleepGap`).
        if settings.autoStopEnabled, let stopAt = autoStopAt, stopAt > now { return nil }
        return sessionDayCutoff()
    }

    /// Kraj radnog dana na dan sesije — vidi `overnightCutoff`; koristi ga i sesija koju je
    /// prekinulo gašenje računala.
    private func sessionDayCutoff() -> Date? {
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
        updatePopup.close()
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

    /// Radni dan je gotov — prošlo je vrijeme automatskog zaustavljanja (kraj radnog dana).
    /// Tad ne iskače nijedan podsjetnik: paljenje računala ili buđenje laptopa navečer nije
    /// početak radnog dana. Bez auto-stopa kraj nije zadan, a auto-stop prije početka
    /// radnog dana (rad preko ponoći) ne omeđuje dan — u oba slučaja nema ograničenja.
    private func isAfterWorkday(_ date: Date) -> Bool {
        guard settings.autoStopEnabled, workdayBounded else { return false }
        let comps = Calendar.current.dateComponents([.hour, .minute], from: date)
        return (comps.hour ?? 0) * 60 + (comps.minute ?? 0) >= autoStopMinuteOfDay
    }

    /// Radni dan još nije počeo — paljenje računala u 7:45 uz početak u 8:30 ne nudi start,
    /// podsjetnik iskoči tek u 8:30. Isto kao kod `isAfterWorkday`: rad preko ponoći (auto-stop
    /// prije početka radnog dana) nije omeđen.
    private func isBeforeWorkday(_ date: Date) -> Bool {
        guard let start = workdayStart(on: date) else { return false }
        if settings.autoStopEnabled, !workdayBounded { return false }
        return date < start
    }

    /// Kraj radnog dana (auto-stop) je iza početka — dan se ne proteže preko ponoći.
    private var workdayBounded: Bool {
        let start = min(max(settings.workdayStartHour, 0), 23) * 60 + min(max(settings.workdayStartMinute, 0), 59)
        return autoStopMinuteOfDay > start
    }

    private var autoStopMinuteOfDay: Int {
        min(max(settings.autoStopHour, 0), 23) * 60 + min(max(settings.autoStopMinute, 0), 59)
    }

    /// Vrijeme od kojeg podsjetnik nudi nadoknadu ("Start od 8:30") — nil kad je opcija
    /// isključena, kad radni dan još nije počeo ili kad još nije prošao ni jedan cijeli
    /// interval, pa se nadoknada ne bi razlikovala od starta od sada. Ono što je danas već
    /// zapisano (npr. jutro prije ručnog Stopa) se ne nudi ponovno — tad je početak kraj
    /// zadnjeg unosa.
    private func backfillStart(now: Date) -> Date? {
        guard settings.workdayStartBackfillEnabled, let workday = workdayStart(on: now) else { return nil }
        let recorded = currentDayKey == Store.dayKey(now) ? entries.map(\.end).max() : nil
        let start = max(workday, recorded ?? workday)
        return start < Self.gridFloor(now, step: interval) ? start : nil
    }

    /// Vremena kroz koja ‹ › u podsjetniku pomiču početak: od početka radnog dana pa po
    /// mreži intervala, a zadnje je početak trenutnog intervala — od tamo kreće i start
    /// bez nadoknade, pa zasebni "od sada" ne treba (u 9:42 uz 15 min: 8:30 … 9:15, 9:30).
    /// Prazno → nema nadoknade.
    private func backfillOptions(now: Date) -> [Date] {
        guard let start = backfillStart(now: now) else { return [] }
        let current = Self.gridFloor(now, step: interval)
        var options = [start]
        // Početak izvan mreže (kraj zadnjeg unosa u 12:03) — dalje se ide po mreži (12:15 …).
        var t = Self.gridFloor(start, step: interval).addingTimeInterval(interval)
        while t < current {
            options.append(t)
            t = t.addingTimeInterval(interval)
        }
        options.append(current)
        return options
    }

    /// Podsjetnik u zadano vrijeme: iskoči kad radni dan počne, a ako je računalo tada
    /// spavalo — čim se probudi i otključa (timer se nakon buđenja nastavi vrtjeti, pa ga
    /// uhvati prvi idući tick). Javlja se jednom dnevno.
    private func checkWorkdayStart(now: Date) {
        guard settings.workdayStartEnabled, !isTracking, !isLocked, !prompt.isVisible,
              !isSkippedWeekend(now), !isAfterWorkday(now),
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

    /// Otvoren podsjetnik s nadoknadom dobiva svježu ponudu početaka kad počne novi interval
    /// — vidi `StartupReminderController.updateStartOptions`. Prenoćeni prozor nosi jučerašnji
    /// naslov, pa ga se ne dira (zamijeni ga `checkWorkdayStart`).
    private func refreshStartReminderOptions(now: Date) {
        guard startupReminder.isVisible, startupReminder.shownBackfillFrom != nil,
              let shownAt = startupReminder.shownAt,
              Store.dayKey(shownAt) == Store.dayKey(now) else { return }
        startupReminder.updateStartOptions(backfillOptions(now: now))
    }

    /// Zajednički pop-up za oba podsjetnika (pokretanje aplikacije i početak radnog dana).
    private func presentStartReminder(now: Date) {
        // Podsjetnik prikazan prije početka radnog dana ne troši današnji termin — u
        // zadano vrijeme svejedno iskoči (npr. pokretanje u 7:00, radni dan u 8:30).
        if let start = workdayStart(on: now), now >= start {
            workdayReminderDayKey = Store.dayKey(now)
        }
        updatePopup.close()
        startupReminder.show(
            dayTitle: Fmt.dayTitle.string(from: now),
            startOptions: backfillOptions(now: now),
            // Ako je prozor prenoćio, odabrano vrijeme je jučerašnje — start() ga odbaci i
            // kreće od sada, pa dan nikad ne počne jučer.
            onStart: { [weak self] from in self?.start(from: from) },
            onDismiss: { [weak self] in self?.startReminderDismissed() }
        )
    }

    /// "Kasnije" — ako je radni dan već počeo, današnji podsjetnik je time odrađen. Inače bi
    /// podsjetnik otvoren prije početka radnog dana (pokretanje u 7:50, radni dan u 8:00) i
    /// odbijen nakon njega odmah iskočio opet, sa zvukom.
    private func startReminderDismissed() {
        let now = Date()
        if let start = workdayStart(on: now), now >= start {
            workdayReminderDayKey = Store.dayKey(now)
        }
    }

    // MARK: - Prekinuta sesija

    /// Stanje trackanja u `session.json` — piše se samo kad se promijeni, a briše kad
    /// tracking stane.
    private func persistSession() {
        let current = isTracking ? SavedSession(
            autoStopAt: autoStopAt,
            carried: carriedSpans,
            dayKey: currentDayKey,
            finalEnd: awaitingFinalAnswer ? finalEnd : nil,
            lastCovered: lastCovered
        ) : nil
        guard current != savedSession else { return }
        savedSession = current
        Store.saveSession(current)
    }

    /// Trackanje koje je prekinulo gašenje računala (ili izlaz iz aplikacije). Ako radno
    /// vrijeme istog dana još traje, tracking se sam nastavi i odmah pita za propušteno. Ako
    /// je kraj radnog dana u međuvremenu prošao, dan se zatvori na kraju radnog dana, a
    /// zadnji prompt pita za blokove od zadnjeg zapisa — kao da je računalo bilo upaljeno.
    private func restoreSession(now: Date) {
        guard let saved = Store.loadSession() else { return }
        // Sesija "iz budućnosti" (sat je u međuvremenu vraćen unatrag) se ne nastavlja.
        guard saved.dayKey <= Store.dayKey(now), saved.lastCovered <= now else {
            Store.saveSession(nil)
            return
        }
        savedSession = saved
        currentDayKey = saved.dayKey
        entries = Store.loadDay(saved.dayKey)
        lastCovered = saved.lastCovered
        carriedSpans = saved.carried
        autoStopAt = saved.autoStopAt
        sessionStart = saved.lastCovered
        isTracking = true

        if let end = saved.finalEnd {
            // Dan je bio zatvoren, a zadnji prompt bez odgovora — pita se opet.
            stop(at: end)
            return
        }
        let offNote: (Date) -> String = { end in
            let day = Store.dayKey(end) == Store.dayKey(now) ? "" : " (\(Fmt.dayTitle.string(from: end)))"
            return "Računalo je bilo ugašeno — tracking je zaustavljen u \(Fmt.hhmm(end))\(day). Što si radio od zadnjeg zapisa?"
        }
        if saved.dayKey != Store.dayKey(now) {
            // Sesija od nekog prošlog dana: kraj je njeno zakazano zaustavljanje ako je
            // padalo u taj dan, inače kraj radnog dana iz postavki (kao kod prenoćenog prompta).
            let end = saved.autoStopAt.flatMap { Store.dayKey($0) == saved.dayKey ? $0 : nil }
                ?? sessionDayCutoff() ?? lastCovered
            stop(at: end, note: offNote(end))
            return
        }
        if let stopAt = autoStopAt, now >= stopAt {
            stop(at: stopAt, note: offNote(stopAt))
            return
        }
        // Radno vrijeme još traje — tracking ide dalje.
        workdayReminderDayKey = currentDayKey
        nextPromptAt = alignedNextPrompt(after: now)
        promptPastBlocks(
            now: now,
            note: "Računalo je bilo ugašeno — tracking se nastavlja. Na čemu si radio od \(Fmt.hhmm(lastCovered))?"
        )
    }

    // MARK: - Nova verzija

    private func checkForUpdateIfDue(now: Date) {
        guard settings.updateCheckEnabled, !updateCheckInFlight, now >= nextUpdateCheckAt else { return }
        checkForUpdate()
    }

    /// Pita GitHub za zadnji release — iz tick petlje i ručno ("Provjeri sada" u postavkama).
    func checkForUpdate() {
        guard !updateCheckInFlight else { return }
        guard let current = UpdateChecker.currentVersion else {
            updateStatus = "Verzija nije poznata (pokrenuto izvan .app bundle-a)."
            nextUpdateCheckAt = .distantFuture
            return
        }
        updateCheckInFlight = true
        updateStatus = "Provjeravam…"
        Task { @MainActor in
            defer { updateCheckInFlight = false }
            do {
                let latest = try await UpdateChecker.fetchLatest()
                nextUpdateCheckAt = Date().addingTimeInterval(24 * 3600)
                handleLatest(latest, current: current)
            } catch {
                nextUpdateCheckAt = Date().addingTimeInterval(3600)
                // Provjera je isključena dok je zahtjev bio u tijeku.
                guard settings.updateCheckEnabled else { return }
                updateStatus = "Provjera nije uspjela (nema mreže?). Pokušat ću opet za sat."
            }
        }
    }

    private func handleLatest(_ latest: AvailableUpdate, current: String) {
        // Provjera je isključena dok je zahtjev bio u tijeku.
        guard settings.updateCheckEnabled else { return }
        guard UpdateChecker.isNewer(latest.version, than: current) else {
            availableUpdate = nil
            updateStatus = "Imaš najnoviju verziju."
            return
        }
        availableUpdate = latest
        updateStatus = "Dostupna je verzija \(latest.version)."
        if settings.updateNotifiedVersion != latest.version {
            pendingUpdatePopup = latest
            showPendingUpdatePopup()
        }
    }

    /// Pop-up se ne gura preko prompta, podsjetnika ni upozorenja (svi su u istom kutu),
    /// a ni na zaključan ekran — čeka da se maknu, tick ga onda pokuša opet.
    private func showPendingUpdatePopup() {
        guard let update = pendingUpdatePopup, !isLocked, !prompt.isVisible,
              !startupReminder.isVisible, !autoStopWarning.isVisible else { return }
        pendingUpdatePopup = nil
        settings.updateNotifiedVersion = update.version
        updatePopup.show(update: update, currentVersion: UpdateChecker.currentVersion ?? "") { [weak self] in
            self?.openUpdatePage()
        }
    }

    /// Release stranica nove verzije (s menija i iz pop-upa).
    func openUpdatePage() {
        NSWorkspace.shared.open(availableUpdate?.url ?? UpdateChecker.latestReleasePage)
    }

    // MARK: - Tick petlja

    private func tick() {
        guard launched else { return }
        let now = Date()
        let previousTick = lastTickAt
        lastTickAt = now
        persistSession()
        checkWorkdayStart(now: now)
        refreshStartReminderOptions(now: now)
        checkForUpdateIfDue(now: now)
        showPendingUpdatePopup()
        rollOverIdleDay(now: now)
        // Dan je zatvoren i čeka se odgovor na zadnji prompt — ništa se više ne zakazuje.
        guard isTracking, !awaitingFinalAnswer else { return }

        // Sesija pokrenuta nakon vremena zaustavljanja (zaustavljanje je zakazano tek za
        // neki od idućih dana) smije prijeći ponoć samo dok računalo radi. Ako je spavalo
        // preko granice dana (poklopac zatvoren navečer), dan se zatvara u trenutku kad je
        // zaspalo — inače bi cijela noć (i jutro do sutrašnjeg zaustavljanja) bila rad
        // u jučerašnjem danu.
        if let previousTick, now.timeIntervalSince(previousTick) >= sleepGap,
           Store.dayKey(now) != currentDayKey,
           let stopAt = autoStopAt, Store.dayKey(stopAt) != currentDayKey {
            stop(at: max(previousTick, lastCovered))
            return
        }

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
                if let since = absenceStart(now: now) {
                    // Korisnik je otišao od otvorenog prompta (npr. ručak) — odsutnost se ne
                    // smije pripojiti periodu kao rad. Prompt se zatvori i čeka se povratak,
                    // kao da je granica zatekla zaključan ekran: na povratku iskoči prompt do
                    // početka odsutnosti, a odsutnost ide kao pauza.
                    prompt.close()
                    activePromptEnd = nil
                    awaitingReturnSince = max(since, lastCovered)
                } else {
                    extendActivePrompt(to: next)
                }
            }
            return
        }
        if let next = nextPromptAt, now >= next {
            attemptPrompt(now: now)
        }
    }

    /// Bez trackanja "danas" prati sat — inače bi meni nakon ponoći pokazivao jučerašnje unose
    /// sve do Starta. Sesija (i zadnji prompt koji još čeka odgovor) drži svoj dan do kraja.
    private func rollOverIdleDay(now: Date) {
        let today = Store.dayKey(now)
        guard !isTracking, today != currentDayKey else { return }
        currentDayKey = today
        entries = Store.loadDay(today)
    }

    /// Početak odsutnosti ako korisnik trenutno nije za računalom (zaključan ekran ili idle
    /// iznad praga, prema postavkama), inače nil.
    private func absenceStart(now: Date) -> Date? {
        if settings.lockPauseEnabled && isLocked { return lockedAt ?? now }
        let idle = IdleMonitor.idleSeconds()
        if settings.idleDetectionEnabled && idle >= TimeInterval(settings.idleThresholdMinutes * 60) {
            return now.addingTimeInterval(-idle)
        }
        return nil
    }

    private func attemptPrompt(now: Date) {
        if let since = absenceStart(now: now) {
            awaitingReturnSince = max(since, lastCovered)
        } else {
            // Kraj perioda je zakazano (poravnato) vrijeme prompta, ne trenutak odgovora —
            // tako su unosi uvijek točno na 5-min mreži, a kašnjenje odgovora se
            // prelijeva u sljedeći period.
            let end = nextPromptAt ?? now
            // Ručni unos je pokrio (skoro) cijeli period — nema se što pitati, ostatak ide
            // u period sljedeće granice.
            if end.timeIntervalSince(lastCovered) < 60, carriedSpans.isEmpty {
                nextPromptAt = alignedNextPrompt(after: end)
                return
            }
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
            // Odsutnost se zapisuje odmah (pauza do povratka), a prompt pita samo za rad
            // prije nje — vrijeme od povratka do odgovora je već novi period.
            if now > gapStart {
                record(start: gapStart, end: now, text: "Pauza (odsutnost)", kind: .pause)
                persistDay()
            }
            let pending = PromptRequest.PendingPause(start: gapStart, end: max(now, gapStart))
            pendingReturn = pending
            show(PromptRequest(
                start: lastCovered,
                end: gapStart,
                pauseAfter: pending,
                note: "Bio si odsutan ~\(gapMinutes) min — to razdoblje je označeno kao pauza.",
                allowSnooze: false
            ))
        } else {
            if now > gapStart {
                record(start: max(gapStart, lastCovered), end: now, text: "Pauza (odsutnost)", kind: .pause)
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
                record(start: max(since, lastCovered), end: now, text: "Pauza", kind: .pause)
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
        // Isti kut ekrana — prompt ima prednost, obavijest o verziji ostaje u meniju.
        updatePopup.close()
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
    /// Dio koji je u međuvremenu popunjen ručnim unosom se više ne pita.
    private func carry(_ span: PromptSpan) {
        for part in Summarize.uncovered(entries, start: span.start, end: span.end)
        where part.end.timeIntervalSince(part.start) > 60 {
            if let last = carriedSpans.last, abs(part.start.timeIntervalSince(last.end)) <= 1 {
                carriedSpans[carriedSpans.count - 1].end = max(last.end, part.end)
            } else {
                carriedSpans.append(PromptSpan(start: part.start, end: part.end))
            }
        }
    }

    /// Automatski unos (odgovor na prompt, pauza, odsutnost) — upisuje se samo u vrijeme
    /// koje još ne pokriva neki unos, pa ručni unos iz pregleda dana ima prednost.
    private func record(start: Date, end: Date, text: String, kind: EntryKind) {
        for part in Summarize.uncovered(entries, start: start, end: end)
        where part.end.timeIntervalSince(part.start) > 5 {
            entries.append(Entry(start: part.start, end: part.end, text: text, kind: kind))
        }
    }

    /// Povratni prompt zatvoren je bez odgovora (Stop, Pauza, auto-stop). Pauza za odsutnost
    /// je već zapisana; rad prije nje nosi se dalje kao zaseban red, a period kreće od
    /// povratka — inače bi odsutnost završila u sljedećem promptu kao rad.
    private func settlePendingReturn() {
        guard let pending = pendingReturn else { return }
        pendingReturn = nil
        carry(PromptSpan(start: lastCovered, end: pending.start))
        lastCovered = max(lastCovered, pending.end)
    }

    /// Nakon ručnog unosa/ispravka u današnjem danu: vrijeme koje je sad pokriveno ne
    /// pita se ponovno. `lastCovered` preskoči unose koji se nastavljaju na njega (najviše
    /// do sada), a preneseni preskočeni periodi se skrate za popunjeni dio.
    private func absorbManualEntries() {
        guard isTracking else { return }
        let now = Date()
        var advanced = true
        while advanced {
            advanced = false
            for e in entries where e.start.timeIntervalSince(lastCovered) <= 1 {
                let end = min(e.end, now)
                if end > lastCovered {
                    lastCovered = end
                    advanced = true
                }
            }
        }
        let spans = carriedSpans
        carriedSpans = []
        spans.forEach(carry)
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
                record(start: seg.start, end: seg.end, text: texts[i], kind: .work)
                // Kronološki redoslijed → zadnji segment završi kao history.first (prefill za idući prompt).
                pushHistory(texts[i])
            }
        }
        // "Nastavljam s" iz ručnog prompta se ne bilježi kao unos, samo ide u povijest —
        // time postaje pre-fill sljedećeg prompta.
        if let nextUp = result.nextUp?.trimmingCharacters(in: .whitespacesAndNewlines), !nextUp.isEmpty {
            pushHistory(nextUp)
        }

        if let pending = request.pauseAfter {
            // Pauza za odsutnost je zapisana kod povratka; preskočeni rad prije nje nije
            // susjedan onome što slijedi, pa se nosi dalje zasebno. Sljedeći period kreće od
            // povratka.
            pendingReturn = nil
            carry(PromptSpan(start: coveredEnd, end: min(pending.start, effectiveEnd ?? pending.start)))
            lastCovered = max(lastCovered, pending.end)
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
        if old.updateCheckEnabled != settings.updateCheckEnabled {
            if settings.updateCheckEnabled {
                nextUpdateCheckAt = Date()
            } else {
                availableUpdate = nil
                pendingUpdatePopup = nil
                updateStatus = nil
                updatePopup.close()
            }
        }
        if old.workdayStartEnabled != settings.workdayStartEnabled
            || old.workdayStartHour != settings.workdayStartHour
            || old.workdayStartMinute != settings.workdayStartMinute
            || old.skipWeekendReminders != settings.skipWeekendReminders
            || old.autoStopEnabled != settings.autoStopEnabled
            || old.autoStopHour != settings.autoStopHour
            || old.autoStopMinute != settings.autoStopMinute {
            // Novo vrijeme vrijedi od idućeg početka radnog dana: ako je današnji već
            // prošao, danas se više ne javlja — inače bi pop-up iskočio čim se u
            // postavkama namjesti raniji sat (ili vikendom isključi "Preskoči vikende",
            // ili se navečer auto-stop pomakne kasnije, pa kraj radnog dana više nije prošao).
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
        if awaitingFinalAnswer { return "Kraj dana — čeka zadnji zapis" }
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
