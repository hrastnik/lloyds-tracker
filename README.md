# Lloyds Tracker

Aplikacija za praćenje vremena na poslu u Lloyds Digital vizualnom stilu
(crna `#070707` + žuta `#FBDE07`). Sjedi u traci (menu bar / system tray) i u
zadanom intervalu (default 15 min) pita **"Na čemu radiš?"**. Odgovori se spremaju
lokalno kao JSON, a na kraju dana dobiješ grupirani pregled koji možeš kopirati ili
exportati u CSV.

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

Zahtjevi: .NET 8 SDK (Windows). Jedan self-contained `.exe`, bez runtime instalacije.
Detalji: [windows/README.md](windows/README.md).

Windows verziju automatski builda **GitHub Actions CI** ([.github/workflows/windows.yml](.github/workflows/windows.yml)) —
gotov `.exe` je dostupan kao build artifact na svakom pushu.

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
