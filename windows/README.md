# Lloyds Tracker — Windows

Nativni Windows tray port macOS menu bar aplikacije za praćenje vremena, u Lloyds Digital
vizualnom stilu (crna `#070707` + žuta `#FBDE07`). Ista funkcionalnost, isti JSON format
podataka kao macOS verzija.

Sjedi u system trayu (pored sata) i u zadanom intervalu (default 15 min) pita
**"Na čemu radiš?"**. Odgovori se spremaju lokalno kao JSON, a na kraju dana dobiješ
grupirani pregled koji možeš kopirati ili exportati u CSV.

## Tehnologija

- **C# / .NET 8 + WinForms** — najlakši od .NET UI stackova (tanki sloj nad Win32/GDI),
  najbrži boot i najmanja potrošnja memorije. Sistem tray (`NotifyIcon`) je ugrađen.
- Build je jedan mali **framework-dependent `.exe`** (~2–3 MB). Runtime (WinForms + ICU)
  dolazi s računala, pa treba jednom instalirati **.NET 8 Desktop Runtime**:
  `winget install Microsoft.DotNet.DesktopRuntime.8` (Windows 11 ga ne uključuje; ako fali,
  `.exe` sam ponudi link za download). Self-contained varijanta ne treba runtime, ali je
  ~59 MB jer bundla cijeli runtime — pa je izabran mali framework-dependent build.

## Build i pokretanje

Za **build**: [.NET 8 SDK](https://dotnet.microsoft.com/download) na Windowsima.
Za **pokretanje** (na bilo kojem računalu): [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0/runtime)
(`winget install Microsoft.DotNet.DesktopRuntime.8`).

```powershell
cd windows
.\build.ps1                 # → windows\dist\LloydsTracker.exe
.\dist\LloydsTracker.exe
```

Za razvoj: `dotnet run --project LloydsTracker` iz `windows/` foldera.

> **Napomena:** WinForms je Windows-only (`net8.0-windows`), pa se build i pokretanje rade
> na Windows računalu — ne na macOS-u.

## Korištenje

Isto kao macOS verzija:

1. **Lijevi klik** na tray ikonu → popover → **Start — počni radni dan**.
   (Desni klik → brzi meni: Pregled dana / Postavke / Izlaz.)
2. Svakih 15 min iskoči prompt, pre-fillan zadnjim unosom:
   - `Enter` — spremi
   - `↑` / `↓` — listanje povijesti
   - `Esc` — spremi isto kao zadnji put (samo floating stil)
   - `✂` na traci blokova — razbij period na više unosa
   - *Odgodi 5 min* — snooze (samo floating stil)
3. **Pauziraj** (15/30/60 min ili do nastavka) — vrijeme se bilježi kao pauza.
4. **Završi dan** → pregled dana (grupirano/kronološki), *Kopiraj pregled* ili *Export CSV*.

### Odsutnost

Dvije neovisne opcije, **obje po defaultu isključene**:

- **Detekcija neaktivnosti (tipkovnica/miš)** — nema inputa dulje od praga (default 5 min).
- **Bilježi pauzu kad je ekran zaključan** — računalo zaključano (`Win+L`).

Kad je opcija uključena, prompt se odgađa dok se ne vratiš; po povratku te pita što si radio
prije odsutnosti, a samo razdoblje odsutnosti se bilježi kao pauza. Ako su obje isključene,
prompt te u zakazano vrijeme samo pita što si radio.

- Neaktivnost: Win32 `GetLastInputInfo`.
- Zaključavanje ekrana: `SystemEvents.SessionSwitch` (lock/unlock).

### Automatsko zaustavljanje

Da tracking ne ostane pokrenut preko noći, dan se **sam zatvara u zadano vrijeme**
(default **16:00**, mijenja se u postavkama).

Minutu prije iskoči upozorenje u donjem desnom kutu — s odbrojavanjem i trakom koja se
prazni — i nudi produženje **+15 / +30 / +45 / +1 h** (na svakom gumbu piše do kada), plus
*Zaustavi sad*. Produženje vrijedi **samo za taj dan**; postavka ostaje nepromijenjena, a
minutu prije novog vremena upozorenje se ponovi.

Ako se ne reagira, dan se zatvara: zadnji period završava u zakazano vrijeme (ne u trenutak
kad se odgovori na zadnji prompt), pa unosi ostaju ispravni i kad se odgovori sljedeći dan.
Ako je u tom trenutku aktivna odsutnost (idle/zaključan ekran), to razdoblje se bilježi kao
pauza, a pita se samo za rad prije odsutnosti.

## Postavke

Tray → *Postavke…* — tri taba: **Promptanje**, **Radni dan**, **Sustav**.

| Postavka | Default |
|---|---|
| Interval promptanja | 15 min (5–60) |
| Stil prompta | Floating panel / Cijeli ekran (obavezan odgovor) |
| Zvuk kod prompta | uključen |
| Automatsko zaustavljanje + vrijeme | uključeno, 16:00 (minute u koraku od 5) |
| Detekcija neaktivnosti (tipkovnica/miš) + prag | isključena, 5 min |
| Bilježi pauzu kad je ekran zaključan | isključeno |
| Broj zapamćenih unosa (povijest) | 15 |
| Pokreni kod prijave (autostart) | isključeno |
| Podsjetnik kod pokretanja (pop-up) | uključeno |

**Pokreni kod prijave** upisuje/briše vrijednost u
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run` (bez admin prava).

## Podaci

Sve je lokalno, u istom čitljivom JSON formatu kao macOS verzija:

```
%APPDATA%\LloydsTracker\
├── 2026-07-15.json   # unosi po danu (start, end, text, kind)
├── history.json      # povijest unosa za pre-fill
└── settings.json     # postavke
```

Vremena su ISO 8601 (UTC, npr. `2026-07-15T07:45:00Z`), pa su datoteke kompatibilne s
macOS verzijom. CSV export: `start,end,minutes,text,kind`.

## Struktura koda

```
windows/LloydsTracker/
├── Program.cs                # entry point: single-instance mutex + tray context
├── TrayApplicationContext.cs # NotifyIcon, ikone stanja, prozori
├── MenuBarPopover.cs         # popover iz traya (status, kontrole, unosi, footer)
├── TrackerEngine.cs          # stanje, 1s timer, prompt logika, idle/pauze, autostart
├── PromptForm.cs             # floating panel / fullscreen prompt + text polja
├── BlockBarControl.cs        # traka za razbijanje perioda + PromptGeometry
├── SummaryForm.cs            # pregled dana, copy/CSV export, brisanje
├── SettingsForm.cs           # postavke
├── StartupReminder.cs        # pop-up podsjetnik na pokretanju
├── AutoStopWarning.cs        # upozorenje 1 min prije auto-stopa + produženja
├── IdleMonitor.cs            # GetLastInputInfo
├── SessionMonitor.cs         # SessionSwitch (lock/unlock)
├── LaunchAtLogin.cs          # registry Run key
├── Store.cs                  # JSON pohrana (+ konverteri za macOS schema)
├── Models.cs                 # Entry, AppSettings, PromptRequest, grupiranje
├── UiKit.cs                  # brand fontovi, FlatButton, TrackedLabel, CardPanel
└── Theme.cs                  # Lloyds boje i hr-HR formatiranje
```

## Mapiranje macOS → Windows

| macOS (SwiftUI/AppKit) | Windows (WinForms) |
|---|---|
| `MenuBarExtra` | `NotifyIcon` + custom popover Form |
| `NSPanel` (floating) / borderless `NSWindow` (fullscreen) | borderless `Form` (TopMost) |
| `CGEventSource.secondsSinceLastEventType` | `GetLastInputInfo` |
| `com.apple.screenIsLocked` notifikacije | `SystemEvents.SessionSwitch` |
| `SMAppService` (launch at login) | `HKCU\...\Run` registry |
| `~/Library/Application Support/LloydsTracker` | `%APPDATA%\LloydsTracker` |
| SF Symbols (clock/pause/moon) | GDI-crtane tray ikone po stanju |
| `NSSound("Glass")` | `SystemSounds.Asterisk` |
```
