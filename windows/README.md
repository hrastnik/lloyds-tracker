# Lloyds Tracker — Windows

Nativni Windows tray port macOS menu bar aplikacije za praćenje vremena, u Lloyds Digital
vizualnom stilu (crna `#070707` + žuta `#FBDE07`). Ista funkcionalnost, isti JSON format
podataka kao macOS verzija.

Sjedi u system trayu (pored sata) i u zadanom intervalu (default 15 min) pita
**"Na čemu radiš?"**. Odgovor **nije obavezan** — prompt se može preskočiti, pa taj period
čeka u sljedećem promptu. Odgovori se spremaju lokalno kao JSON, a na kraju dana dobiješ
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
   - `Esc` / *Preskoči* — ne bilježi ništa, period se vraća u sljedeći prompt
   - `✂` na traci blokova — razbij period na više unosa
   - *Odgodi 5 min* — snooze (samo floating stil)
3. **Zapiši sada** — ručni prompt kad god treba (npr. kod prebacivanja na drugi projekt).
4. **Pauziraj** (15/30/60 min ili do nastavka) — vrijeme se bilježi kao pauza.
5. **Završi dan** → pregled dana (grupirano/kronološki), *Kopiraj pregled* ili *Export CSV*.

Pop-upi se pojavljuju **s fade-inom**, a tipkovnicu preuzimaju ~0.9 s nakon toga — prompt koji
iskoči dok pišeš u drugoj aplikaciji tako ne presretne ostatak rečenice (ni ne pregazi
pre-fill). Do tada prozor stoji vidljiv, ali neaktivan (`ShowWithoutActivation`).

### Preskakanje prompta

Odgovor nije obavezan. *Preskoči* (ili `Esc`) ne bilježi ništa i **period se ne troši** — sam
iskoči u sljedećem promptu, produžen za novi interval: preskočiš 10:00–10:15, a u 10:30 te
prompt pita za 10:00–10:30 (i to se može razbiti na više unosa s `✂`). Isto vrijedi i za
pojedini blok razbijenog perioda — ostavi ga praznog i vratit će se.

Ako između preskočenog perioda i sljedećeg stane pauza (period nije više susjedan), preskočeno
se nosi kao **zasebni red** iznad crte (`↩ 09:45–10:15`) — popuni ga kad znaš ili ostavi dalje.
Zadnji prompt dana nosi i te redove; ono što se tamo preskoči više se ne bilježi.

### Ručni prompt — "Zapiši sada"

U popoveru iz traya, dok je tracking aktivan. Zapisuje period **od zadnjeg zapisa do sada**,
bez čekanja na interval — za trenutak kad usred projekta A uskoči hitan zadatak na projektu B.

Uz opis perioda ima i polje **„Nastavljam s”** (nije obavezno): taj tekst se ne bilježi kao
unos, nego postaje **pre-fill sljedećeg prompta** — pa te redovni prompt u zakazano vrijeme
pita za ostatak intervala već s opisom projekta B. Ritam promptanja se ne mijenja: sljedeći
prompt iskoči u svoje vrijeme (10:15, 10:30…) kao i inače.

### Pregled dana

Svaki red u tabu **Kronološki** ima `✎` — **Ispravi unos**: opis (uz padajući izbor iz
povijesti), vrijeme *od*/*do* i vrsta (rad/pauza). Radi i za prijašnje dane. Kod spojenog reda
(`2×`) promjena samo opisa ili vrste zadržava blokove, a promjena vremena ih **stopi u jedan
unos** — novi raspon nema stare granice blokova (prozor na to i upozori).

U tabu **Kronološki** opcija **Spoji susjedne unose istog naziva** (uključena po defaultu)
prikazuje niz susjednih unosa istog opisa kao jedan — `Mamic web 14:45–15:00` +
`Mamic web 15:00–15:15` postaje `Mamic web 14:45–15:15`, s oznakom koliko je blokova
spojeno (`2×`). Spajaju se samo neposredni susjedi (jedan završava kad drugi počinje), pa
pauza ili drugi opis između prekida niz. Brisanje spojenog reda briše sve njegove blokove.
Postavka se pamti.

### Početak radnog dana

Podsjetnik da se pokrene tracking javlja se na dva načina:

- **Kod pokretanja aplikacije** — pop-up čim se app digne (npr. nakon paljenja računala).
- **U zadano vrijeme početka radnog dana** (default **8:30**) — isti pop-up iskoči u to
  vrijeme, a ako je računalo tada spavalo, čim ga probudiš i otključaš. Laptop koji se ne
  gasi tako više ne ostane bez podsjetnika. Javlja se jednom dnevno; *Kasnije* ga zatvara
  do sutra, a pokretanje dana iz popovera ga također preskače.

**Vikendom** (subota i nedjelja) se ne javlja nijedan od njih — opcija *Preskoči vikende*,
default uključena. Tracking se i vikendom može pokrenuti ručno iz popovera.

Uz uključenu **nadoknadu** (default) pop-up nudi izbor kad je radni dan već počeo: *Start od
8:30* ili *Počni tek od sada*. Nadoknada samo pomiče početak trackanja unatrag — prvi prompt
onda pita za cijelo jutro (8:30–9:45) i to razdoblje možeš razbiti na više unosa (`✂`).
Postojeći unosi se ne diraju: ako je jutro već djelomično zabilježeno, kreće se od kraja
zadnjeg unosa. Nadoknada se uvijek odnosi na **današnji** dan: pop-up čeka odgovor koliko
treba, pa se onaj koji ostane otvoren (preko noći ili od prije 8:30) u zadano vrijeme
osvježi — *Start od 8:30* nikad ne vuče početak na jučer.

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

Zaustavljanje u toj zadnjoj minuti (*Zaustavi sad* ili *Završi dan* iz popovera) bilježi zadnji
period **do zakazanog vremena** — dan uvijek završi na 17:00, a ne na 16:59.

Ako se ne reagira, dan se zatvara: zadnji period završava u zakazano vrijeme (ne u trenutak
kad se odgovori na zadnji prompt), pa unosi ostaju ispravni i kad se odgovori sljedeći dan.
Ako je u tom trenutku aktivna odsutnost (idle/zaključan ekran), to razdoblje se bilježi kao
pauza, a pita se samo za rad prije odsutnosti.

#### Prompt koji je prenoćio

Zatvoriš laptop bez odgovora na prompt i otvoriš ga **sutra**: dan se ne nastavlja od jučer.
Čim se app probudi, period se **odreže na zadano vrijeme zaustavljanja** i dan se zatvara, pa
zadnji prompt pita za taj skraćeni period (i to piše u njemu) — a odgovor završi kod jučerašnjeg
datuma, s ispravnim vremenima. Produženja iz upozorenja (+15 / +30 / +45 / +1 h) se poštuju:
rez je na produženom vremenu.

Ograda vrijedi i kad je automatsko zaustavljanje **isključeno** — tada se koristi vrijeme iz
te sekcije, a ako je rad zabilježen i preko njega (npr. rad poslije ponoći), dan se zatvara u
ponoć.

## Postavke

Tray → *Postavke…* — tri taba: **Promptanje**, **Radni dan**, **Sustav**.

| Postavka | Default |
|---|---|
| Interval promptanja | 15 min (5–60) |
| Stil prompta | Floating panel / Cijeli ekran (preko svega) |
| Zvuk kod prompta | uključen |
| Podsjetnik na početak radnog dana + vrijeme | uključeno, 8:30 (minute u koraku od 5) |
| Ponudi i nadoknadu od tog vremena | uključeno |
| Preskoči vikende (bez podsjetnika subotom i nedjeljom) | uključeno |
| Automatsko zaustavljanje + vrijeme | uključeno, 16:00 (minute u koraku od 5) |
| Detekcija neaktivnosti (tipkovnica/miš) + prag | isključena, 5 min |
| Bilježi pauzu kad je ekran zaključan | isključeno |
| Broj zapamćenih unosa (povijest) | 15 |
| Pokreni kod prijave (autostart) | isključeno |
| Podsjetnik kod pokretanja (pop-up) | uključeno |
| Spoji susjedne unose istog naziva (toggle u *Pregled dana → Kronološki*) | uključeno |

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
├── TrayIconFactory.cs        # GDI brand pločica: tray ikone po stanju + ikona prozora
├── MenuBarPopover.cs         # popover iz traya (status, kontrole, unosi, footer)
├── TrackerEngine.cs          # stanje, 1s timer, prompt logika, idle/pauze, autostart
├── PromptForm.cs             # floating panel / fullscreen prompt + text polja
├── BlockBarControl.cs        # traka za razbijanje perioda + PromptGeometry
├── SummaryForm.cs            # pregled dana, copy/CSV export, ispravak/brisanje
├── SettingsForm.cs           # postavke
├── EntryEditForm.cs          # prozor za ispravak unosa (Pregled dana → Kronološki)
├── StartupReminder.cs        # pop-up podsjetnik (pokretanje + početak radnog dana)
├── AutoStopWarning.cs        # upozorenje 1 min prije auto-stopa + produženja
├── IdleMonitor.cs            # GetLastInputInfo
├── SessionMonitor.cs         # SessionSwitch (lock/unlock)
├── LaunchAtLogin.cs          # registry Run key
├── Store.cs                  # JSON pohrana (+ konverteri za macOS schema)
├── Models.cs                 # Entry, AppSettings, PromptRequest, grupiranje
├── UiKit.cs                  # brand fontovi, FlatButton, TrackedLabel, CardPanel, PanelFade
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
| `AppIcon.icns` + `.accessory`/`.regular` (Cmd+Tab) | `Form.Icon` (prozori su ionako u Alt+Tab) |
| `NSSound("Glass")` | `SystemSounds.Asterisk` |
| `PanelFade` (`NSAnimationContext` + odgođeni `makeKey`) | `PanelFade` (timer nad `Form.Opacity` + odgođeni `Activate()`) |
| `.sheet` (ispravak unosa) | modalni `Form` (`ShowDialog`) |
| SwiftUI `DatePicker(.hourAndMinute)` | dva dark drop-downa (sat + minuta) |
| `.help(…)` tooltip | `ToolTip.SetToolTip` |
```
