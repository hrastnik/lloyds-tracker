import SwiftUI

struct SettingsView: View {
    @ObservedObject var engine: TrackerEngine

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

            Section("Odsutnost") {
                Toggle("Detekcija odsutnosti", isOn: $engine.settings.idleDetectionEnabled)
                Picker("Prag neaktivnosti", selection: $engine.settings.idleThresholdMinutes) {
                    ForEach([3, 5, 10, 15], id: \.self) { m in
                        Text("\(m) min").tag(m)
                    }
                }
                .disabled(!engine.settings.idleDetectionEnabled)
                Text("Ako je Mac zaključan ili nema aktivnosti dulje od praga, prompt se odgađa dok se ne vratiš, a odsutnost se bilježi kao pauza.")
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
