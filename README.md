# Lloyds Tracker

Aplikacija za praćenje vremena na poslu u Lloyds Digital vizualnom stilu
(crna `#070707` + žuta `#FBDE07`). Sjedi u traci (menu bar / system tray) i u
zadanom intervalu (default 15 min) pita **"Na čemu radiš?"**. Odgovori se spremaju
lokalno kao JSON, a na kraju dana dobiješ grupirani pregled koji možeš kopirati ili
exportati u CSV (kronološki pregled može spojiti susjedne unose istog naziva u jedan). Ako
zaboraviš isključiti, tracking se **sam zaustavlja** u zadano vrijeme (default 16:00) —
minutu prije iskoči upozorenje s opcijom produženja samo za taj dan.

Ujutro te sam podsjeti: u zadano vrijeme početka radnog dana (default **8:30**) iskoči
pop-up, a ako je računalo tada spavalo — čim ga probudiš. Ako laptop otvoriš tek u 9:30,
nudi i **start od 8:30**, pa se jutro nadoknadi.

Dvije nativne verzije, isti JSON format podataka:

| Verzija | Tehnologija | Folder |
|---|---|---|
| **macOS** | Swift 6 / SwiftUI + AppKit (`MenuBarExtra`) | [`macos/`](macos/) |
| **Windows** | C# / .NET 8 + WinForms (`NotifyIcon`) | [`windows/`](windows/) |

## macOS

```sh
cd macos
./build.sh                                  # → macos/dist/LloydsTracker.app
cp -r dist/LloydsTracker.app /Applications/
```

Zahtjevi: macOS 14+, Swift 6 toolchain. Detalji: [macos/README.md](macos/README.md).

## Windows

```powershell
cd windows
.\build.ps1                                 # → windows\dist\LloydsTracker.exe
```

Zahtjevi: .NET 8 SDK (Windows). Jedan mali framework-dependent `.exe` (~2–3 MB) — na
računalu treba **.NET 8 Desktop Runtime**. Detalji: [windows/README.md](windows/README.md).

## Nova verzija / Windows build

WinForms je `net8.0-windows`, pa se Windows `.exe` **ne može buildati na Macu** — to radi
GitHub Actions:

| Trigger | Workflow | Rezultat |
|---|---|---|
| push na `main` koji dira `windows/**` | [windows.yml](.github/workflows/windows.yml) | `.exe` kao **build artifact** (traje 90 dana, treba GitHub login) |
| tag `vX.Y.Z` | [release.yml](.github/workflows/release.yml) | **GitHub Release** s priloženim `.exe` (verzija se uzima iz taga) |

Postupak za novu verziju:

```sh
# 1. bumpaj verziju na oba porta (drži ih usklađene):
#      macos/Support/Info.plist  → CFBundleShortVersionString (+ CFBundleVersion)
#      windows/LloydsTracker/LloydsTracker.csproj → <Version>
# 2. commit + push na main  (→ CI provjeri da se Windows verzija kompajlira)
git push origin main
# 3. tag = release s .exe-om
git tag v1.1.0 && git push origin v1.1.0
gh run watch                       # ili: gh release view v1.1.0
# 4. macOS build je lokalan:
cd macos && ./build.sh && cp -r dist/LloydsTracker.app /Applications/
```

## Podaci

Obje verzije spremaju iste, čitljive JSON datoteke (ISO 8601 UTC vremena, pa su
međusobno kompatibilne):

```
2026-07-15.json   # unosi po danu (start, end, text, kind)
history.json      # povijest unosa za pre-fill
settings.json     # postavke
```

- macOS: `~/Library/Application Support/LloydsTracker/`
- Windows: `%APPDATA%\LloydsTracker\`

CSV export: `start,end,minutes,text,kind`.
