# CLAUDE.md

Time tracker u traci (menu bar / system tray), Lloyds Digital stil: crna `#070707`,
žuta `#FBDE07`, tekst UI-a i komunikacija na hrvatskom.

**Dva nativna porta istog proizvoda** — `macos/` (Swift 6 / SwiftUI + AppKit) i
`windows/` (C# / .NET 8 + WinForms). Windows je namjerno *direktan port*: iste klase, ista
imena, isti komentari po sekcijama (`// MARK: -`), isti tekstovi u UI-u.

## Pravila kod izmjena

- **Svaka funkcionalna promjena ide na oba porta**, u istoj promjeni. Mapiranje
  macOS → Windows je u [windows/README.md](windows/README.md).
- **`settings.json` / dnevne JSON datoteke dijele shemu** (ISO 8601 UTC). Novi ključ mora
  imati isto ime i isti default na oba porta. macOS `AppSettings.init(from:)` koristi
  `decodeIfPresent` (stari file ne smije pasti); Windows svojstva drži **abecedno** jer
  macOS encoder piše `sortedKeys`.
- README-e (root + oba porta) drži u skladu s postavkama i ponašanjem.

## Build / release

```sh
cd macos && ./build.sh && cp -r dist/LloydsTracker.app /Applications/   # macOS
```

Windows se **ne može buildati na Macu** (`net8.0-windows`) i `dotnet` nije instaliran —
push na `main` je jedina provjera da se C# kompajlira (`gh run list`). Release s `.exe`-om
ide preko taga `vX.Y.Z`; cijeli postupak i bump verzija su u [README.md](README.md#nova-verzija--windows-build).

## Kod pokretanja aplikacije

Pokretanje lokalno je OK, ali:

- `Store.directory` ide preko Foundation `applicationSupportDirectory` i **ignorira
  `HOME=`** — svaki harness sagrađen iz ovih sourcea piše u prave podatke u
  `~/Library/Application Support/LloydsTracker/`.
- Svaki prozor (prompt, startup reminder, auto-stop upozorenje) je iznad svega, uklj.
  videopozive. Test procese ubijaj s `pgrep -f … | kill -9` — `kill %1` u
  neinteraktivnom shellu ne radi.
- Za provjeru samo izgleda: mali zasebni SwiftPM target s view fileom + `Theme.swift`
  (bez `TrackerEngine`, pa nema pisanja u `Store`). `NSHostingView.fittingSize` daje
  točne visine za dimenzioniranje prozora.
