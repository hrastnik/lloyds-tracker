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

### Odsutnost

Dvije neovisne opcije, **obje po defaultu isključene**:

- **Detekcija neaktivnosti (tipkovnica/miš)** — nema inputa dulje od praga (default 5 min).
- **Bilježi pauzu kad je ekran zaključan** — ekran je zaključan.

Kad je opcija uključena, prompt se odgađa dok se ne vratiš; po povratku te pita što si radio **prije** odsutnosti, a samo razdoblje odsutnosti se bilježi kao pauza. Ako su obje isključene, prompt te u zakazano vrijeme samo pita što si radio.

## Postavke

Status bar ikona → *Postavke…*

| Postavka | Default |
|---|---|
| Interval promptanja | 15 min (5–60) |
| Stil prompta | Floating panel / Cijeli ekran (obavezan odgovor) |
| Zvuk kod prompta | uključen |
| Detekcija neaktivnosti (tipkovnica/miš) + prag | isključena, 5 min |
| Bilježi pauzu kad je ekran zaključan | isključeno |
| Broj zapamćenih unosa (povijest) | 15 |
| Pokreni kod prijave | isključeno (zahtijeva .app u /Applications) |

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
├── Store.swift            # JSON pohrana
├── IdleMonitor.swift      # detekcija neaktivnosti (CGEventSource)
├── Models.swift           # Entry, AppSettings, grupiranje
└── Theme.swift            # Lloyds boje i formatiranje
```
