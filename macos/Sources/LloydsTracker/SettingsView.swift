import SwiftUI

/// Postavke u tabovima (nativni macOS pattern). Sve u jednoj listi je preraslo visinu
/// ekrana, pa je razbijeno u tri kratke grupe; prozor ima fiksnu visinu, a pojedini tab
/// scrolla ako je duži — nikad se ne odreže.
struct SettingsView: View {
    @ObservedObject var engine: TrackerEngine

    /// Visina odabrana da najviši tab ("Radni dan") stane bez scrollanja.
    private let contentHeight: CGFloat = 670

    var body: some View {
        TabView {
            promptTab
                .tabItem { Label("Promptanje", systemImage: "bell.badge") }
            dayTab
                .tabItem { Label("Radni dan", systemImage: "clock.arrow.circlepath") }
            systemTab
                .tabItem { Label("Sustav", systemImage: "gearshape") }
        }
        .frame(width: 480, height: contentHeight)
        .tint(Color.lloydsYellow)
    }

    // MARK: - Tabovi

    var promptTab: some View {
        Form {
            Section("Promptanje") {
                Picker("Interval", selection: $engine.settings.intervalMinutes) {
                    ForEach([5, 10, 15, 20, 30, 45, 60], id: \.self) { m in
                        Text("\(m) min").tag(m)
                    }
                }
                Picker("Stil prompta", selection: $engine.settings.promptStyle) {
                    ForEach(PromptStyle.allCases) { style in
                        Text(style.label).tag(style)
                    }
                }
                Toggle("Zvuk kod prompta", isOn: $engine.settings.soundEnabled)
            }

            Section("Povijest") {
                Picker("Broj zapamćenih unosa", selection: $engine.settings.historyLimit) {
                    ForEach([5, 10, 15, 25, 50], id: \.self) { n in
                        Text("\(n)").tag(n)
                    }
                }
                Text("Koliko se nedavnih unosa pamti za pre-fill i listanje (↑/↓) u promptu.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
    }

    var dayTab: some View {
        Form {
            Section("Početak radnog dana") {
                Toggle("Podsjetnik u zadano vrijeme", isOn: $engine.settings.workdayStartEnabled)
                DatePicker("Vrijeme", selection: workdayStartTime, displayedComponents: .hourAndMinute)
                    .disabled(!engine.settings.workdayStartEnabled)
                Toggle("Ponudi i nadoknadu od tog vremena", isOn: $engine.settings.workdayStartBackfillEnabled)
                    .disabled(!engine.settings.workdayStartEnabled)
                Text("Pop-up iskoči u zadano vrijeme, a ako je računalo tada spavalo — čim ga probudiš (npr. u 9:30). Uz nadoknadu nudi i start od zadanog vremena, pa te prvi prompt pita i za jutro.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            Section("Automatsko zaustavljanje") {
                Toggle("Zaustavi tracking u zadano vrijeme", isOn: $engine.settings.autoStopEnabled)
                DatePicker("Vrijeme", selection: autoStopTime, displayedComponents: .hourAndMinute)
                    .disabled(!engine.settings.autoStopEnabled)
                Text("Minutu prije iskoči upozorenje s produženjem (+15 / +30 / +45 / +1 h), koje vrijedi samo za taj dan. Bez reakcije dan se sam zatvara — pa tracking ne ostane pokrenut preko noći.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            Section("Odsutnost") {
                Toggle("Detekcija neaktivnosti (tipkovnica/miš)", isOn: $engine.settings.idleDetectionEnabled)
                Picker("Prag neaktivnosti", selection: $engine.settings.idleThresholdMinutes) {
                    ForEach([3, 5, 10, 15], id: \.self) { m in
                        Text("\(m) min").tag(m)
                    }
                }
                .disabled(!engine.settings.idleDetectionEnabled)
                Toggle("Bilježi pauzu kad je ekran zaključan", isOn: $engine.settings.lockPauseEnabled)
                Text("Uključeno: razdoblje odsutnosti se bilježi kao pauza, a prompt čeka da se vratiš. Isključeno: prompt te u zakazano vrijeme samo pita što si radio.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
    }

    var systemTab: some View {
        Form {
            Section("Sustav") {
                Toggle("Pokreni kod prijave (launch at login)", isOn: $engine.settings.launchAtLogin)
                if let status = engine.launchAtLoginStatus {
                    Text(status)
                        .font(.caption)
                        .foregroundStyle(.orange)
                }
                Toggle("Podsjetnik kod pokretanja (pop-up)", isOn: $engine.settings.showStartupReminder)
                Text("Kad se app pokrene, iskoči pop-up da te podsjeti da pokreneš radni dan ako tracking još nije aktivan.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            Section("Podaci") {
                LabeledContent("Lokacija") {
                    Text(Store.directory.path)
                        .font(.system(size: 11, design: .monospaced))
                        .textSelection(.enabled)
                        .lineLimit(2)
                }
                Button("Otvori folder s podacima") {
                    NSWorkspace.shared.open(Store.directory)
                }
            }
        }
        .formStyle(.grouped)
    }

    // MARK: - Pomoćno

    private var autoStopTime: Binding<Date> {
        timeBinding(hour: \.autoStopHour, minute: \.autoStopMinute)
    }

    private var workdayStartTime: Binding<Date> {
        timeBinding(hour: \.workdayStartHour, minute: \.workdayStartMinute)
    }

    /// DatePicker radi s Date-om, a postavke su sat+minuta — most između to dvoje.
    private func timeBinding(
        hour: WritableKeyPath<AppSettings, Int>,
        minute: WritableKeyPath<AppSettings, Int>
    ) -> Binding<Date> {
        Binding(
            get: {
                let cal = Calendar.current
                return cal.date(
                    bySettingHour: engine.settings[keyPath: hour],
                    minute: engine.settings[keyPath: minute],
                    second: 0,
                    of: Date()
                ) ?? Date()
            },
            set: { newValue in
                let comps = Calendar.current.dateComponents([.hour, .minute], from: newValue)
                engine.settings[keyPath: hour] = comps.hour ?? 0
                engine.settings[keyPath: minute] = comps.minute ?? 0
            }
        )
    }
}
