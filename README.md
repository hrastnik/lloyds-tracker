# Lloyds Tracker

Aplikacija za praćenje vremena na poslu u Lloyds Digital vizualnom stilu
(crna `#070707` + žuta `#FBDE07`). Sjedi u traci (menu bar / system tray) i u
zadanom intervalu (default 15 min) pita **"Na čemu radiš?"**. Odgovor nije obavezan —
prompt se može **preskočiti**, pa taj period čeka u sljedećem promptu; a *Zapiši sada*
otvara prompt ručno, kad god treba. Odgovori se spremaju
lokalno kao JSON, a na kraju dana dobiješ grupirani pregled koji možeš kopirati ili
exportati u CSV (kronološki pregled može spojiti susjedne unose istog naziva u jedan, a
svaki se unos može i ispraviti). Ako
zaboraviš isključiti, tracking se **sam zaustavlja** u zadano vrijeme (default 16:00) —
minutu prije iskoči upozorenje s opcijom produženja samo za taj dan. Prompt koji je
prenoćio (laptop zatvoren bez odgovora) ne razvuče period u novi dan — odreže se na to
vrijeme zaustavljanja.

Ujutro te sam podsjeti: u zadano vrijeme početka radnog dana (default **8:30**) iskoči
pop-up, a ako je računalo tada spavalo — čim ga probudiš. Ako laptop otvoriš tek u 9:30,
nudi i **start od 8:30**, pa se jutro nadoknadi.

Tri nativne verzije, isti JSON format podataka:

| Verzija | Tehnologija | Folder |
|---|---|---|
| **macOS** | Swift 6 / SwiftUI + AppKit (`MenuBarExtra`) | [`macos/`](macos/) |
| **Windows** | C# / .NET 8 + WinForms (`NotifyIcon`) | [`windows/`](windows/) |
| **Linux** | Rust / GTK4 + `ksni` (StatusNotifierItem) | [`linux/`](linux/) |

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

## Linux

```sh
cd linux
./build.sh                                  # → linux/dist/lloyds-tracker-linux-x86_64.tar.gz
cd dist && tar -xzf lloyds-tracker-linux-x86_64.tar.gz && ./lloyds-tracker-linux-x86_64/install.sh
```

Zahtjevi: GTK 4.10+ i StatusNotifierItem host u traci (KDE ima ugrađeno, **GNOME treba
AppIndicator ekstenziju**). Za build: Rust stable + `libgtk-4-dev`. Detalji:
[linux/README.md](linux/README.md).

## Nova verzija / Windows i Linux build

WinForms je `net8.0-windows`, a GTK4 build traži Linux — pa se ni `.exe` ni Linux tarball
**ne mogu buildati na Macu**. To radi GitHub Actions:

| Trigger | Workflow | Rezultat |
|---|---|---|
| push na `main` koji dira `windows/**` | [windows.yml](.github/workflows/windows.yml) | `.exe` kao **build artifact** (traje 90 dana, treba GitHub login) |
| push na `main` koji dira `linux/**` | [linux.yml](.github/workflows/linux.yml) | `clippy -D warnings` + `.tar.gz` kao **build artifact** |
| tag `vX.Y.Z` | [release.yml](.github/workflows/release.yml) | **GitHub Release** s priloženim `.exe` i `.tar.gz` (verzija se uzima iz taga) |

Postupak za novu verziju:

```sh
# 1. bumpaj verziju na sva tri porta (drži ih usklađene):
#      macos/Support/Info.plist  → CFBundleShortVersionString (+ CFBundleVersion)
#      windows/LloydsTracker/LloydsTracker.csproj → <Version>
#      linux/Cargo.toml → version
# 2. commit + push na main  (→ CI provjeri da se Windows i Linux verzija kompajliraju)
git push origin main
# 3. tag = release s .exe-om i .tar.gz-om
git tag v1.1.0 && git push origin v1.1.0
gh run watch                       # ili: gh release view v1.1.0
# 4. macOS build je lokalan:
cd macos && ./build.sh && cp -r dist/LloydsTracker.app /Applications/
```

## Podaci

Sve tri verzije spremaju iste, čitljive JSON datoteke (ISO 8601 UTC vremena, pa su
međusobno kompatibilne):

```
2026-07-15.json   # unosi po danu (start, end, text, kind)
history.json      # povijest unosa za pre-fill
settings.json     # postavke
```

- macOS: `~/Library/Application Support/LloydsTracker/`
- Windows: `%APPDATA%\LloydsTracker\`
- Linux: `~/.local/share/LloydsTracker/`

CSV export: `start,end,minutes,text,kind`.
