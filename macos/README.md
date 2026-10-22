# Lloyds Tracker

Nativna macOS menu bar aplikacija za praćenje vremena na poslu, u Lloyds Digital vizualnom stilu (crna `#070707` + žuta `#FBDE07`).

Sjedi u status baru i u zadanom intervalu (default 15 min) pita **"Na čemu radiš?"**. Odgovor
**nije obavezan** — prompt se može preskočiti, pa taj period čeka u sljedećem promptu. Odgovori
se spremaju lokalno kao JSON, a na kraju dana dobiješ grupirani pregled koji možeš kopirati ili
exportati u CSV — za lako prepisivanje u firmin online tool.

## Build i instalacija

```sh
./build.sh                                  # builda dist/LloydsTracker.app
cp -r dist/LloydsTracker.app /Applications/ # instalacija
open /Applications/LloydsTracker.app
```

Gotov build (Apple Silicon) je i uz svaki [release](https://github.com/hrastnik/lloyds-tracker/releases/latest)
kao `LloydsTracker-macOS-arm64.zip` — vidi [root README](../README.md#preuzimanje) za prvo
otvaranje nepotpisane aplikacije.

Za razvoj: `swift run` (radi i bez .app bundle-a, ali "launch at login" i provjera nove
verzije tada nisu dostupni — verzija se čita iz Info.plista).

Zahtjevi: macOS 14+, Xcode toolchain (Swift 6).

## Korištenje

1. Klik na ikonu sata u status baru → **Start — počni radni dan**.
2. Svakih 15 min iskoči prompt. Polje je **pre-fillano zadnjim unosom**:
   - `⏎` — spremi (ako radiš isto, samo stisni Enter)
   - `↑` / `↓` — listanje povijesti nedavnih unosa
   - `esc` / *Preskoči* — ne bilježi ništa, period se vraća u sljedeći prompt
   - *Odgodi 5 min* — snooze (samo floating stil)
3. **Zapiši sada** — ručni prompt kad god treba (npr. kod prebacivanja na drugi projekt).
4. **Pauziraj** (15/30/60 min ili do nastavka) — bez promptanja, vrijeme se bilježi kao pauza.
5. **Završi dan** → otvara se pregled dana s grupiranim vremenima, *Kopiraj pregled* ili *Export CSV*.

Pop-upi se pojavljuju **s fade-inom**, a tipkovnicu preuzimaju ~0.9 s nakon toga — prompt koji
iskoči dok pišeš u drugoj aplikaciji tako ne presretne ostatak rečenice (ni ne pregazi pre-fill).

### Preskakanje prompta

Odgovor nije obavezan. *Preskoči* (ili `esc`) ne bilježi ništa i **period se ne troši** — sam
iskoči u sljedećem promptu, produžen za novi interval: preskočiš 10:00–10:15, a u 10:30 te
prompt pita za 10:00–10:30 (i to se može razbiti na više unosa s `✂`). Isto vrijedi i za
pojedini blok razbijenog perioda — ostavi ga praznog i vratit će se.

Ako između preskočenog perioda i sljedećeg stane pauza (period nije više susjedan), preskočeno
se nosi kao **zasebni red** iznad crte (`↩ 09:45–10:15`) — popuni ga kad znaš ili ostavi dalje.
Zadnji prompt dana nosi i te redove; ono što se tamo preskoči više se ne bilježi.

### Ručni prompt — "Zapiši sada"

U meniju iz status bara, dok je tracking aktivan. Zapisuje period **od zadnjeg zapisa do sada**,
bez čekanja na interval — za trenutak kad usred projekta A uskoči hitan zadatak na projektu B.

Uz opis perioda ima i polje **„Nastavljam s”** (nije obavezno): taj tekst se ne bilježi kao
unos, nego postaje **pre-fill sljedećeg prompta** — pa te redovni prompt u zakazano vrijeme
pita za ostatak intervala već s opisom projekta B. Ritam promptanja se ne mijenja: sljedeći
prompt iskoči u svoje vrijeme (10:15, 10:30…) kao i inače.

### Prozori i Cmd+Tab

Aplikacija je menu bar app (`.accessory`) — bez ikone u Docku. Dok je otvoren **Pregled dana**
ili **Postavke**, prebacuje se u `.regular`, pa se pojavi u Docku i u **Cmd+Tab** prebacivaču
s brand ikonom; kad se prozori zatvore, vraća se u `.accessory`. Prompt i popover su
borderless prozori i ne mijenjaju to.

Ikona (`AppIcon.icns`) se generira kod builda iz istog koda kao pločica u traci
(`AppIcon.swift` + `Support/IconGen`), pa ne mogu razići.

### Pregled dana

Dva taba: **Grupirano** (zbrojeno po opisu) i **Kronološki** (unos po unos, s ispravkom,
dodavanjem i brisanjem).

Klik na bilo koji red u tabu **Kronološki** otvara **uređivač na mjestu reda**: vrijeme
*od*/*do* upisuje se rukom (`9:30`, `930`, `9`…; strelice ↑/↓ pomiču za 5 min), uz opis (s
izborom iz povijesti) i vrstu (rad/pauza). Trajanje se računa uživo, Enter sprema, Esc
odustaje, a **Obriši** je u istom uređivaču. Radi i za prijašnje dane (takav unos ne ide u
povijest, pa ne mijenja pre-fill sljedećeg prompta). Kod spojenog reda
(`2×`) promjena samo opisa ili vrste zadržava blokove, a promjena vremena ih **stopi u jedan
unos** — novi raspon nema stare granice blokova (uređivač na to i upozori).
Vrijeme koje nisi dirao ostaje točno kakvo je bilo (sa sekundama), pa ispravak samo opisa ne
pomiče unos. Kraj upisan prije početka znači **sutradan** (`23:30`–`00:30`, `…`–`00:00`), do
12 h trajanja — `10:00`–`9:00` je i dalje greška.

**Dodavanje unosa:** nezabilježeno vrijeme između unosa (≥ 1 min) prikazuje se kao
isprekidani red `+ 10:15–10:45 nezabilježeno · 30m` — klik ga popuni novim unosom. Gumb
**+ Dodaj unos** na dnu (i u praznom danu) predlaže raspon od kraja zadnjeg unosa (ili
početka radnog dana) u trajanju jednog intervala. Ako se novi ili ispravljeni unos preklapa
s drugima, **on ima prednost**: preklopljeni dio ostalih unosa se odreže (ili ih razdvoji
ili makne) — uređivač prije spremanja javi s koliko se unosa preklapa.
Unos ne može završiti u budućnosti (danas prijedlog ide najkasnije do sada). Ručni unos ima
prednost i pred promptom: vrijeme koje si danas već upisao rukom sljedeći prompt više ne pita,
a ako je prompt bio otvoren od prije, odgovor (kao i pauza ili odsutnost) popuni samo ono što
je ostalo nepokriveno — ništa se ne broji dvaput.

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

**Vikendom** (subota i nedjelja) se ne javlja nijedan od njih — opcija *Preskoči vikende*,
default uključena. Tracking se i vikendom može pokrenuti ručno iz menija.

**Nakon kraja radnog dana** — vremena *Automatskog zaustavljanja* (default 16:00) — također se
ne javlja nijedan: računalo upaljeno ili laptop probuđen navečer ne pita za početak dana. Bez
uključenog auto-stopa kraj radnog dana nije zadan, pa tog ograničenja nema.

Uz uključenu **nadoknadu** (default) pop-up, kad je radni dan već počeo, nudi *Start od
8:30*, a strelice ‹ › uz gumb pomiču početak za jedan interval (uz 15 min u 9:42: 8:30,
8:45 … 9:15, 9:30). Zadnji korak je početak trenutnog intervala — isto što i start od sada.
Dok pop-up stoji otvoren, ponuda prati sat (u 11:05 zadnji korak je 11:00), a već odabrano
vrijeme ostaje odabrano.
Nadoknada samo pomiče početak trackanja unatrag — prvi prompt onda pita za cijelo jutro
(8:30–9:45) i to razdoblje možeš razbiti na više unosa (`✂`).
Postojeći unosi se ne diraju: ako je jutro već djelomično zabilježeno, kreće se od kraja
zadnjeg unosa. Nadoknada se uvijek odnosi na **današnji** dan: pop-up čeka odgovor koliko
treba, pa se onaj koji ostane otvoren (preko noći ili od prije 8:30) u zadano vrijeme
osvježi — nadoknada nikad ne vuče početak na jučer.

### Odsutnost

Dvije neovisne opcije, **obje po defaultu isključene**:

- **Detekcija neaktivnosti (tipkovnica/miš)** — nema inputa dulje od praga (default 5 min).
- **Bilježi pauzu kad je ekran zaključan** — ekran je zaključan.

Kad je opcija uključena, prompt se odgađa dok se ne vratiš; po povratku te pita što si radio **prije** odsutnosti, a samo razdoblje odsutnosti se bilježi kao pauza. Isto vrijedi i kad odeš od već otvorenog prompta: ako sljedeća granica intervala zatekne odsutnost, prompt se ne produžuje nego zatvori, a po povratku te pita samo za vrijeme do odsutnosti. Pauza se zapisuje čim se vratiš i traje do povratka — vrijeme dok odgovaraš je već novi period. Ako povratni prompt umjesto odgovora zatvoriš sa *Završi dan* ili *Pauziraj*, odsutnost ostaje zapisana, a rad prije nje se pita kao zaseban red. Ako računalo zaspi odmah nakon zaključavanja (zatvoren poklopac), odsutnost počinje kad je zaspalo, a ne tek kad se probudi. Ako su obje isključene, prompt te u zakazano vrijeme samo pita što si radio.

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
Dok zadnji prompt čeka odgovor, meni (menu bar) umjesto *Pauziraj* / *Završi dan* nudi samo
*Otvori zadnji zapis* (status: „Kraj dana — čeka zadnji zapis").

#### Prompt koji je prenoćio

Zatvoriš laptop bez odgovora na prompt i otvoriš ga **sutra**: dan se ne nastavlja od jučer.
Čim se app probudi, period se **odreže na zadano vrijeme zaustavljanja** i dan se zatvara, pa
zadnji prompt pita za taj skraćeni period (i to piše u njemu) — a odgovor završi kod jučerašnjeg
datuma, s ispravnim vremenima. Produženja iz upozorenja (+15 / +30 / +45 / +1 h) se poštuju:
rez je na produženom vremenu. Pregled dana se nakon tog odgovora otvori na jučerašnjem danu
(kraj dana uvijek otvara pregled na dan koji je upravo zatvoren, i kad je prozor već otvoren).

Dok tracking ne radi, „danas" prati sat: nakon ponoći meni pokazuje današnje unose i
prije Starta. Sesija koja još traje (i zadnji prompt koji čeka odgovor) drži svoj dan do kraja.

Ograda vrijedi i kad je automatsko zaustavljanje **isključeno** — tada se koristi vrijeme iz
te sekcije, a ako je rad zabilježen i preko njega (npr. rad poslije ponoći), dan se zatvara u
ponoć.

Dan pokrenut tek **nakon** vremena zaustavljanja (start u 20:00 uz auto-stop u 16:00 →
zaustavljanje je zakazano za sutra) smije prijeći ponoć dok računalo radi. Ako je računalo
**spavalo** preko granice dana (≥ 30 min bez ticka, npr. poklopac zatvoren navečer), dan se
zatvara u trenutku kad je zaspalo, a zadnji prompt pita samo za period do tada.

### Nova verzija

Jednom dnevno (prvi put ~15 s nakon pokretanja) aplikacija pita GitHub za zadnji release i
usporedi ga sa svojom verzijom. Ako je izašla novija:

- jednom po verziji iskoči **pop-up** (*Preuzmi* / *Kasnije*) u gornjem desnom kutu — ne
  gura se preko prompta, podsjetnika ni upozorenja, nego pričeka da se maknu;
- na vrhu menija stoji žuta traka **Nova verzija X · Preuzmi** dok se ne instalira novija.

Oboje otvara release stranicu; ništa se ne skida ni ne instalira samo. Bez mreže se provjera
ponovi za sat. U *Postavke → Sustav* je trenutna verzija, prekidač *Provjeravaj nove
verzije* i gumb *Provjeri sada*. Koja je verzija već javljena pop-upom pamti se u
`settings.json` (`updateNotifiedVersion`).

## Postavke

Status bar ikona → *Postavke…* — tri taba: **Promptanje**, **Radni dan**, **Sustav**.

| Postavka | Default |
|---|---|
| Interval promptanja | 15 min (5–60) |
| Stil prompta | Floating panel / Cijeli ekran (preko svega) |
| Zvuk kod prompta | uključen |
| Podsjetnik na početak radnog dana + vrijeme | uključeno, 8:30 |
| Ponudi i nadoknadu od tog vremena | uključeno |
| Preskoči vikende (bez podsjetnika subotom i nedjeljom) | uključeno |
| Automatsko zaustavljanje + vrijeme | uključeno, 16:00 |
| Detekcija neaktivnosti (tipkovnica/miš) + prag | isključena, 5 min |
| Bilježi pauzu kad je ekran zaključan | isključeno |
| Broj zapamćenih unosa (povijest) | 15 |
| Pokreni kod prijave | isključeno (zahtijeva .app u /Applications) |
| Podsjetnik kod pokretanja (pop-up) | uključeno |
| Provjeravaj nove verzije | uključeno |
| Spoji susjedne unose istog naziva (toggle u *Pregled dana → Kronološki*) | uključeno |

## Podaci

Sve je lokalno, u čitljivom JSON formatu:

```
~/Library/Application Support/LloydsTracker/
├── 2026-07-15.json   # unosi po danu (start, end, text, kind)
├── history.json      # povijest unosa za pre-fill
└── settings.json     # postavke
```

Datoteka koja se ne da pročitati (krnja, ručno pokvarena) se ne prebriše: app je preimenuje
u npr. `2026-07-15.json.corrupt-20260715-143000` i nastavi kao da je nema (prazan dan /
default postavke), pa se original može ručno popraviti i vratiti.

CSV export format: `start,end,minutes,text,kind` (ISO 8601 vremena u UTC-u).

## Struktura koda

```
Sources/LloydsTracker/
├── App.swift              # MenuBarExtra + prozori (SwiftUI App)
├── TrackerEngine.swift    # stanje, timer, prompt logika, idle/pauze
├── PromptController.swift # NSPanel (floating) / NSWindow (fullscreen)
├── PromptView.swift       # UI prompta: povijest, pre-fill, preskakanje, "nastavljam s"
├── MenuBarView.swift      # popover iz status bara
├── SummaryView.swift      # pregled dana, ispravak/dodavanje/brisanje unosa, copy/CSV export
├── SettingsView.swift     # postavke
├── EntryEditView.swift    # uređivač unosa na mjestu reda (Pregled dana → Kronološki)
├── StartupReminder.swift  # pop-up podsjetnik (pokretanje + početak radnog dana)
├── AutoStopWarning.swift  # upozorenje 1 min prije auto-stopa + produženja
├── UpdateChecker.swift    # provjera nove verzije (GitHub releases/latest)
├── UpdatePopup.swift      # pop-up "Nova verzija" (jednom po verziji)
├── AppIcon.swift          # brand ikona (Dock/Cmd+Tab + izvor za AppIcon.icns)
├── Store.swift            # JSON pohrana
├── IdleMonitor.swift      # detekcija neaktivnosti (CGEventSource)
├── Models.swift           # Entry, AppSettings, grupiranje
└── Theme.swift            # Lloyds boje i formatiranje
```
