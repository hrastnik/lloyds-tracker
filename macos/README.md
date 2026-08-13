# Lloyds Tracker

Nativna macOS menu bar aplikacija za praćenje vremena na poslu, u Lloyds Digital vizualnom stilu (crna `#070707` + žuta `#FBDE07`).

Sjedi u status baru i u zadanom intervalu (default 15 min) pita **"Na čemu radiš?"**. Odgovori se spremaju lokalno kao JSON, a na kraju dana dobiješ grupirani pregled koji možeš kopirati ili exportati u CSV — za lako prepisivanje u firmin online tool.

## Build i instalacija

```sh
./build.sh                                  # builda dist/LloydsTracker.app
cp -r dist/LloydsTracker.app /Applications/ # instalacija
open /Applications/LloydsTracker.app
```

Za razvoj: `swift run` (radi i bez .app bundle-a, ali "launch at login" tada nije dostupan).

Zahtjevi: macOS 14+, Xcode toolchain (Swift 6).

## Korištenje

1. Klik na ikonu sata u status baru → **Start — počni radni dan**.
2. Svakih 15 min iskoči prompt. Polje je **pre-fillano zadnjim unosom**:
   - `⏎` — spremi (ako radiš isto, samo stisni Enter)
   - `↑` / `↓` — listanje povijesti nedavnih unosa
   - `esc` — spremi isto kao zadnji put (samo floating stil)
   - *Odgodi 5 min* — snooze (samo floating stil)
3. **Pauziraj** (15/30/60 min ili do nastavka) — bez promptanja, vrijeme se bilježi kao pauza.
4. **Završi dan** → otvara se pregled dana s grupiranim vremenima, *Kopiraj pregled* ili *Export CSV*.

### Prozori i Cmd+Tab

Aplikacija je menu bar app (`.accessory`) — bez ikone u Docku. Dok je otvoren **Pregled dana**
ili **Postavke**, prebacuje se u `.regular`, pa se pojavi u Docku i u **Cmd+Tab** prebacivaču
s brand ikonom; kad se prozori zatvore, vraća se u `.accessory`. Prompt i popover su
borderless prozori i ne mijenjaju to.

Ikona (`AppIcon.icns`) se generira kod builda iz istog koda kao pločica u traci
(`AppIcon.swift` + `Support/IconGen`), pa ne mogu razići.

### Pregled dana

Dva taba: **Grupirano** (zbrojeno po opisu) i **Kronološki** (unos po unos, s brisanjem).

U kronološkom tabu opcija **Spoji susjedne unose istog naziva** (uključena po defaultu)
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
  do sutra, a pokretanje dana iz menija ga također preskače.

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
- **Bilježi pauzu kad je ekran zaključan** — ekran je zaključan.

Kad je opcija uključena, prompt se odgađa dok se ne vratiš; po povratku te pita što si radio **prije** odsutnosti, a samo razdoblje odsutnosti se bilježi kao pauza. Ako su obje isključene, prompt te u zakazano vrijeme samo pita što si radio.

### Automatsko zaustavljanje

Da tracking ne ostane pokrenut preko noći, dan se **sam zatvara u zadano vrijeme**
(default **16:00**, mijenja se u postavkama).

Minutu prije iskoči upozorenje u donjem desnom kutu — s odbrojavanjem i trakom koja se
prazni — i nudi produženje **+15 / +30 / +45 / +1 h** (na svakom gumbu piše do kada), plus
*Zaustavi sad*. Produženje vrijedi **samo za taj dan**; postavka ostaje nepromijenjena, a
minutu prije novog vremena upozorenje se ponovi — pa nema načina da dan ostane otvoren
slučajno.

Zaustavljanje u toj zadnjoj minuti (*Zaustavi sad* ili *Završi dan* iz menija) bilježi zadnji
period **do zakazanog vremena** — dan uvijek završi na 17:00, a ne na 16:59.

Ako se ne reagira, dan se zatvara: zadnji period završava u zakazano vrijeme (ne u trenutak
kad se odgovori na zadnji prompt), pa unosi ostaju ispravni i kad se odgovori sljedeći dan.
Ako je u tom trenutku aktivna odsutnost (idle/zaključan ekran), to razdoblje se bilježi kao
pauza, a pita se samo za rad prije odsutnosti.

## Postavke

Status bar ikona → *Postavke…* — tri taba: **Promptanje**, **Radni dan**, **Sustav**.

| Postavka | Default |
|---|---|
| Interval promptanja | 15 min (5–60) |
| Stil prompta | Floating panel / Cijeli ekran (obavezan odgovor) |
| Zvuk kod prompta | uključen |
| Podsjetnik na početak radnog dana + vrijeme | uključeno, 8:30 |
| Ponudi i nadoknadu od tog vremena | uključeno |
| Automatsko zaustavljanje + vrijeme | uključeno, 16:00 |
| Detekcija neaktivnosti (tipkovnica/miš) + prag | isključena, 5 min |
| Bilježi pauzu kad je ekran zaključan | isključeno |
| Broj zapamćenih unosa (povijest) | 15 |
| Pokreni kod prijave | isključeno (zahtijeva .app u /Applications) |
| Spoji susjedne unose istog naziva (toggle u *Pregled dana → Kronološki*) | uključeno |

## Podaci

Sve je lokalno, u čitljivom JSON formatu:

```
~/Library/Application Support/LloydsTracker/
├── 2026-07-15.json   # unosi po danu (start, end, text, kind)
├── history.json      # povijest unosa za pre-fill
└── settings.json     # postavke
```

CSV export format: `start,end,minutes,text,kind` (ISO 8601 vremena).

## Struktura koda

```
Sources/LloydsTracker/
├── App.swift              # MenuBarExtra + prozori (SwiftUI App)
├── TrackerEngine.swift    # stanje, timer, prompt logika, idle/pauze
├── PromptController.swift # NSPanel (floating) / NSWindow (fullscreen)
├── PromptView.swift       # UI prompta s povijesti i pre-fillom
├── MenuBarView.swift      # popover iz status bara
├── SummaryView.swift      # pregled dana, copy/CSV export
├── SettingsView.swift     # postavke
├── StartupReminder.swift  # pop-up podsjetnik (pokretanje + početak radnog dana)
├── AutoStopWarning.swift  # upozorenje 1 min prije auto-stopa + produženja
├── AppIcon.swift          # brand ikona (Dock/Cmd+Tab + izvor za AppIcon.icns)
├── Store.swift            # JSON pohrana
├── IdleMonitor.swift      # detekcija neaktivnosti (CGEventSource)
├── Models.swift           # Entry, AppSettings, grupiranje
└── Theme.swift            # Lloyds boje i formatiranje
```
