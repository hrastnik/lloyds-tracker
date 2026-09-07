# Lloyds Tracker — Linux

Nativni Linux tray port macOS menu bar aplikacije za praćenje vremena, u Lloyds Digital
vizualnom stilu (crna `#070707` + žuta `#FBDE07`). Ista funkcionalnost, isti JSON format
podataka kao macOS i Windows verzija.

Sjedi u traci i u zadanom intervalu (default 15 min) pita **"Na čemu radiš?"**. Odgovor
**nije obavezan** — prompt se može preskočiti, pa taj period čeka u sljedećem promptu.
Odgovori se spremaju lokalno kao JSON, a na kraju dana dobiješ grupirani pregled koji možeš
kopirati ili exportati u CSV.

## Tehnologija

- **Rust + GTK4** (`gtk4-rs`) — nativni toolkit na GNOME-u i dostupan svugdje; binary se
  dinamički linka na sistemski GTK, pa ostaje mali (release build je reda veličine 1–2 MB;
  točna veličina je u CI artifactu).
- **Traka: `ksni`** (čisti Rust, preko `zbus`) — GTK4 **nema** podršku za tray ikonu, pa
  ikona ide preko [StatusNotifierItem](https://www.freedesktop.org/wiki/Specifications/StatusNotifierItem/)
  spec-a (KDE/freedesktop). Meni crta desktop (host), a ne aplikacija.
- **DBus (`zbus`)** za detekciju neaktivnosti i zaključanog ekrana.

### Preduvjeti za pokretanje

| Što | Zašto | Instalacija |
|---|---|---|
| GTK 4.10+ (`libgtk-4-1`) | UI | dolazi s GNOME/KDE distribucijama |
| StatusNotifierItem host | ikona u traci | KDE Plasma ima ugrađeno; **GNOME treba** [AppIndicator ekstenziju](https://extensions.gnome.org/extension/615/appindicator-support/) |
| `canberra-gtk-play` (ili `paplay` / `pw-play` / `aplay`) | zvuk kod prompta | opcionalno — bez njega je prompt tih |

Bez SNI hosta aplikacija i dalje radi (promptovi, podsjetnici, auto-stop), samo nema ikone
u traci — pa se pregled i postavke otvaraju ponovnim pokretanjem `lloyds-tracker`.

## Build i instalacija

Za build treba Rust toolchain (stable) i GTK4 dev paket:

```sh
sudo apt install libgtk-4-dev build-essential     # Debian/Ubuntu
sudo dnf install gtk4-devel                       # Fedora
sudo pacman -S gtk4                               # Arch
```

```sh
cd linux
./build.sh                    # → linux/dist/lloyds-tracker-linux-x86_64.tar.gz
cd dist && tar -xzf lloyds-tracker-linux-x86_64.tar.gz
./lloyds-tracker-linux-x86_64/install.sh
```

`install.sh` instalira u home (bez roota): binary u `~/.local/bin`, `.desktop` u
`~/.local/share/applications`, ikonu u `~/.local/share/icons`. Deinstalacija:
`install.sh --uninstall` (podaci ostaju).

Za razvoj: `cargo run` iz `linux/` foldera.

## Korištenje

Isto kao macOS i Windows verzija:

1. **Klik** na ikonu u traci → meni → **Start — počni radni dan**.
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
pre-fill). Do tada prozor tipke **odbacuje** (v. [Što je namjerno drukčije](#što-je-namjerno-drukčije)).

Meni u traci prikazuje status, sljedeći prompt (**vrijeme**, npr. `Sljedeći prompt u 10:15`,
a ne živo odbrojavanje kao macOS popover — meni crta host preko DBus-a, pa bi sekundno
osvježavanje bilo promet bez koristi), ukupno vrijeme i zadnjih 8 unosa.

### Preskakanje prompta

Odgovor nije obavezan. *Preskoči* (ili `Esc`) ne bilježi ništa i **period se ne troši** — sam
iskoči u sljedećem promptu, produžen za novi interval: preskočiš 10:00–10:15, a u 10:30 te
prompt pita za 10:00–10:30 (i to se može razbiti na više unosa s `✂`). Isto vrijedi i za
pojedini blok razbijenog perioda — ostavi ga praznog i vratit će se.

Ako između preskočenog perioda i sljedećeg stane pauza (period nije više susjedan), preskočeno
se nosi kao **zasebni red** iznad crte (`↩ 09:45–10:15`) — popuni ga kad znaš ili ostavi dalje.
Zadnji prompt dana nosi i te redove; ono što se tamo preskoči više se ne bilježi.

### Ručni prompt — "Zapiši sada"

U meniju u traci, dok je tracking aktivan. Zapisuje period **od zadnjeg zapisa do sada**, bez
čekanja na interval — za trenutak kad usred projekta A uskoči hitan zadatak na projektu B.

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
  vrijeme, a ako je računalo tada spavalo, čim ga probudiš. Javlja se jednom dnevno;
  *Kasnije* ga zatvara do sutra, a pokretanje dana iz menija ga također preskače.

Uz uključenu **nadoknadu** (default) pop-up nudi izbor kad je radni dan već počeo: *Start od
8:30* ili *Počni tek od sada*. Nadoknada samo pomiče početak trackanja unatrag — prvi prompt
onda pita za cijelo jutro (8:30–9:45) i to razdoblje možeš razbiti na više unosa (`✂`).
Postojeći unosi se ne diraju: ako je jutro već djelomično zabilježeno, kreće se od kraja
zadnjeg unosa. Nadoknada se uvijek odnosi na **današnji** dan.

### Odsutnost

Dvije neovisne opcije, **obje po defaultu isključene**:

- **Detekcija neaktivnosti (tipkovnica/miš)** — nema inputa dulje od praga (default 5 min).
- **Bilježi pauzu kad je ekran zaključan**.

Kad je opcija uključena, prompt se odgađa dok se ne vratiš; po povratku te pita što si radio
prije odsutnosti, a samo razdoblje odsutnosti se bilježi kao pauza. Ako su obje isključene,
prompt te u zakazano vrijeme samo pita što si radio.

Oboje ovisi o desktopu, jer Wayland namjerno nema globalni input hook. Redom se traži prvi
dostupan servis, a **što je stvarno pronađeno piše u postavkama** (tab *Radni dan*):

| | GNOME | KDE Plasma | fallback |
|---|---|---|---|
| Neaktivnost | `org.gnome.Mutter.IdleMonitor` | `org.freedesktop.ScreenSaver.GetSessionIdleTime` | nema (opcija nema efekta) |
| Zaključan ekran | `org.gnome.ScreenSaver` | `org.freedesktop.ScreenSaver` | `logind` `LockedHint` |

### Automatsko zaustavljanje

Da tracking ne ostane pokrenut preko noći, dan se **sam zatvara u zadano vrijeme**
(default **16:00**, mijenja se u postavkama).

Minutu prije iskoči upozorenje — s odbrojavanjem i trakom koja se prazni — i nudi produženje
**+15 / +30 / +45 / +1 h** (na svakom gumbu piše do kada), plus *Zaustavi sad*. Produženje
vrijedi **samo za taj dan**; postavka ostaje nepromijenjena.

Zaustavljanje u toj zadnjoj minuti bilježi zadnji period **do zakazanog vremena** — dan
uvijek završi na 16:00, a ne na 15:59. Ako se ne reagira, dan se zatvara sam: zadnji period
završava u zakazano vrijeme, pa unosi ostaju ispravni i kad se odgovori sljedeći dan.

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

Traka → *Postavke…* — tri taba: **Promptanje**, **Radni dan**, **Sustav**.

| Postavka | Default |
|---|---|
| Interval promptanja | 15 min (5–60) |
| Stil prompta | Floating panel / Cijeli ekran (preko svega) |
| Zvuk kod prompta | uključen |
| Podsjetnik na početak radnog dana + vrijeme | uključeno, 8:30 |
| Ponudi i nadoknadu od tog vremena | uključeno |
| Automatsko zaustavljanje + vrijeme | uključeno, 16:00 |
| Detekcija neaktivnosti (tipkovnica/miš) + prag | isključena, 5 min |
| Bilježi pauzu kad je ekran zaključan | isključeno |
| Broj zapamćenih unosa (povijest) | 15 |
| Pokreni kod prijave (autostart) | isključeno |
| Podsjetnik kod pokretanja (pop-up) | uključeno |
| Spoji susjedne unose istog naziva (toggle u *Pregled dana → Kronološki*) | uključeno |

**Pokreni kod prijave** piše/briše `~/.config/autostart/lloyds-tracker.desktop` (XDG
autostart, poštuju ga GNOME i KDE). `Exec` pokazuje na trenutno pokrenuti binary, pa radi i
kad app nije instalirana u `~/.local/bin`. Kod pokretanja se stanje uskladi s postavkom —
ako je `settings.json` prenesen s drugog računala, `.desktop` se sam napiše.

## Podaci

Sve je lokalno, u istom čitljivom JSON formatu kao macOS i Windows verzija:

```
~/.local/share/LloydsTracker/          # ili $XDG_DATA_HOME/LloydsTracker
├── 2026-07-15.json   # unosi po danu (start, end, text, kind)
├── history.json      # povijest unosa za pre-fill
└── settings.json     # postavke
```

Vremena su ISO 8601 (UTC, npr. `2026-07-15T07:45:00Z`), a ključevi u datotekama idu
abecedno — kao što ih piše macOS `sortedKeys` encoder — pa su datoteke izravno
kompatibilne. CSV export: `start,end,minutes,text,kind`.

## Struktura koda

```
linux/src/
├── main.rs             # App: engine + prozori + traka, 1s tick, red efekata
├── engine.rs           # stanje, prompt logika, idle/pauze, auto-stop, Effect
├── tray.rs             # ksni Tray: meni, snapshot, naredbe prema aplikaciji
├── icon.rs             # cairo brand pločica: ikone u traci po stanju
├── prompt.rs           # floating panel / fullscreen prompt + traka blokova
├── summary.rs          # pregled dana, copy/CSV export, ispravak/brisanje
├── entry_edit.rs       # prozor za ispravak unosa (Pregled dana → Kronološki)
├── settings_window.rs  # postavke (3 taba)
├── startup_reminder.rs # pop-up podsjetnik (pokretanje + početak radnog dana)
├── auto_stop.rs        # upozorenje 1 min prije auto-stopa + produženja
├── idle.rs             # DBus: Mutter IdleMonitor / ScreenSaver
├── session.rs          # DBus: zaključan ekran (GNOME / freedesktop / logind)
├── autostart.rs        # XDG autostart .desktop
├── sound.rs            # canberra-gtk-play / paplay / pw-play / aplay
├── store.rs            # JSON pohrana (atomski zapis)
├── models.rs           # Entry, AppSettings, PromptRequest, grupiranje
├── ui.rs               # graditelji widgeta (label, button, hbox…) + PanelFade
└── theme.rs            # Lloyds boje, hr formatiranje, CSS
```

## Mapiranje macOS → Linux

| macOS (SwiftUI/AppKit) | Linux (Rust/GTK4) |
|---|---|
| `MenuBarExtra` + popover | `ksni` StatusNotifierItem + DBus meni (`tray.rs`) |
| `NSPanel` (floating) / borderless `NSWindow` (fullscreen) | `gtk::Window` (`decorated(false)`) / `fullscreen()` |
| `ObservableObject` + `@Published` | `Rc<RefCell<TrackerEngine>>` + red `Effect`-a |
| `CGEventSource.secondsSinceLastEventType` | `org.gnome.Mutter.IdleMonitor` / `org.freedesktop.ScreenSaver` |
| `com.apple.screenIsLocked` notifikacije | `ScreenSaver.GetActive` / logind `LockedHint` (polling u ticku) |
| `SMAppService` (launch at login) | `~/.config/autostart/*.desktop` |
| `~/Library/Application Support/LloydsTracker` | `~/.local/share/LloydsTracker` |
| SF Symbols (clock/pause/moon) | cairo-crtane ikone (`icon.rs`) |
| `NSSound("Glass")` | `canberra-gtk-play -i message` (+ fallbackovi) |
| `NSPasteboard` | `gtk::Widget::clipboard()` |
| `NSSavePanel` | `gtk::FileDialog` |
| `PanelFade` (`NSAnimationContext` + odgođeni `makeKey`) | `ui::PanelFade` (`set_opacity` u `glib::timeout` + odgođeno hvatanje tipki) |
| `.sheet` (ispravak unosa) | modalni `gtk::Window` (`transient_for`) |
| SwiftUI `DatePicker(.hourAndMinute)` | dva `SpinButton`-a (sat + minuta) |
| `.help(…)` tooltip | `set_tooltip_text` |

### Što je namjerno drukčije

- **Pozicija prozora.** GTK4 nema API za pozicioniranje prozora ni za "always on top"
  (Wayland to prepušta compositoru), pa floating prompt ne sjeda u gornji desni kut nego
  ga smješta window manager. **Fullscreen stil radi identično na sva tri porta** i zato je
  preporučen na Linuxu.
- **Meni u traci** je host-renderiran (DBus), pa nema živog odbrojavanja ni brand stila —
  prikazuje vrijeme sljedećeg prompta i zadnjih 8 unosa kao neaktivne stavke.
- **Odgoda tipkovnice.** Da pop-up ne presretne rečenicu koju pišeš u drugoj aplikaciji,
  macOS prozor tek nakon ~0.9 s postane *key* — do tada tipke normalno idu prethodnoj
  aplikaciji. GTK4 nema pandan `makeKey`-u i ne može vratiti fokus onome od koga ga je
  compositor uzeo, pa Linux port tipke u tom međuvremenu **odbacuje** (hvata ih u capture
  fazi) i tek onda fokusira polje. Efekt je isti tamo gdje je važan — ništa ne završi u
  promptu — ali te tipke su izgubljene, a ne isporučene prethodnoj aplikaciji.
- **Cmd+Tab pandan** ne postoji: aplikacija je tray-only i prozori se pojave u pregledu
  prozora samo dok su otvoreni.
