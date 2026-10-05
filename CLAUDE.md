# CLAUDE.md

Time tracker u traci (menu bar / system tray), Lloyds Digital stil: crna `#070707`,
žuta `#FBDE07`, tekst UI-a i komunikacija na hrvatskom.

**Tri nativna porta istog proizvoda** — `macos/` (Swift 6 / SwiftUI + AppKit),
`windows/` (C# / .NET 8 + WinForms) i `linux/` (Rust / GTK4 + `ksni`). Windows i Linux su
namjerno *direktni portovi*: iste klase/moduli, ista imena, isti komentari po sekcijama
(`// MARK: -`), isti tekstovi u UI-u.

## Pravila kod izmjena

- **Svaka funkcionalna promjena ide na sva tri porta**, u istoj promjeni. Mapiranja su u
  [windows/README.md](windows/README.md) i [linux/README.md](linux/README.md) (uz popis
  onoga što je na Linuxu namjerno drukčije — pozicioniranje prozora, meni u traci).
- **`settings.json` / dnevne JSON datoteke dijele shemu** (ISO 8601 UTC). Novi ključ mora
  imati isto ime i isti default na sva tri porta. macOS `AppSettings.init(from:)` koristi
  `decodeIfPresent` (stari file ne smije pasti); Windows svojstva i Linux `AppSettings`
  polja drže **abecedno** jer macOS encoder piše `sortedKeys` (Linux dodatno traži
  `#[serde(rename_all = "camelCase")]` i `#[serde(default = "…")]` po polju).
- Linux `TrackerEngine` ne zove prozore izravno nego vraća `Effect`-e koje `main.rs`
  primijeni nakon što otpusti `RefCell` posudbu — inače callback iz prozora natrag u engine
  ruši program.
- README-e (root + sva tri porta) drži u skladu s postavkama i ponašanjem.

## Build / release

```sh
cd macos && ./build.sh && cp -r dist/LloydsTracker.app /Applications/   # macOS

# Linux se kompajlira i na Macu (GTK4 preko brewa) — provjeri prije pusha:
cd linux && PKG_CONFIG_PATH="$(brew --prefix)/lib/pkgconfig:$(brew --prefix)/share/pkgconfig" \
  cargo clippy -- -D warnings
```

Windows se **ne može buildati na Macu** (`net8.0-windows`) i `dotnet` nije instaliran —
push na `main` je jedina provjera da se C# kompajlira (`gh run list`). Linux tarball isto
nastaje tek na CI-u (`linux.yml`), ali se kod lokalno da provjeriti gore navedenim
clippyjem. Release s `.exe`-om, `.tar.gz`-om i macOS `.zip`-om (Apple Silicon, `macos.yml` /
`release.yml`) ide preko taga `vX.Y.Z`; cijeli postupak i bump verzija su u
[README.md](README.md#nova-verzija--build-na-ci-u). Aplikacije jednom dnevno provjeravaju
GitHub `releases/latest` (repo je javan), pa svaki objavljeni tag korisnicima javlja novu
verziju.

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
