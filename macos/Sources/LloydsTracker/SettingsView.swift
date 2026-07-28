import SwiftUI

struct SettingsView: View {
    @ObservedObject var engine: TrackerEngine

    /// DatePicker radi s Date-om, a postavka je sat+minuta — most između to dvoje.
    private var autoStopTime: Binding<Date> {
        Binding(
            get: {
                let cal = Calendar.current
                return cal.date(
                    bySettingHour: engine.settings.autoStopHour,
                    minute: engine.settings.autoStopMinute,
                    second: 0,
                    of: Date()
                ) ?? Date()
            },
            set: { newValue in
                let comps = Calendar.current.dateComponents([.hour, .minute], from: newValue)
                engine.settings.autoStopHour = comps.hour ?? 16
                engine.settings.autoStopMinute = comps.minute ?? 0
            }
        )
    }

    var body: some View {
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

            Section("Automatsko zaustavljanje") {
                Toggle("Zaustavi tracking u zadano vrijeme", isOn: $engine.settings.autoStopEnabled)
                DatePicker("Vrijeme", selection: autoStopTime, displayedComponents: .hourAndMinute)
                    .disabled(!engine.settings.autoStopEnabled)
                Text("Minutu prije iskoči upozorenje s opcijom produženja (+15 / +30 / +45 / +1 h) — produženje vrijedi samo za taj dan. Ako ne reagiraš, dan se sam zatvara u zadano vrijeme, pa tracking ne ostane pokrenut preko noći.")
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
                Text("Kad je uključeno, razdoblje bez aktivnosti (dulje od praga) odnosno sa zaključanim ekranom bilježi se kao pauza, a prompt se odgađa dok se ne vratiš. Ako je oboje isključeno, prompt te samo pita što si radio u tom periodu.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            Section("Povijest") {
                Picker("Broj zapamćenih unosa", selection: $engine.settings.historyLimit) {
                    ForEach([5, 10, 15, 25, 50], id: \.self) { n in
                        Text("\(n)").tag(n)
                    }
                }
            }

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
        .frame(width: 460)
        .fixedSize(horizontal: false, vertical: true)
        .tint(Color.lloydsYellow)
    }
}
