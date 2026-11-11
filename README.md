<div align="center">

<img src="docs/images/logo.png" alt="Windows DaSi Tool" width="120">

# Windows DaSi Tool

**Datensicherung und Wiederherstellung von Windows-Benutzerprofilen — portabel, schnell, ohne Installation.**

[![Version](https://img.shields.io/badge/Version-1.2.1-blue)](https://github.com/Tinnitus97/Windows.DaSi.Tool/releases)
[![Platform](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4)](https://github.com/Tinnitus97/Windows.DaSi.Tool)
[![.NET](https://img.shields.io/badge/.NET-10%20LTS-512BD4)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/badge/Lizenz-MIT-green)](LICENSE)

</div>

---

Entwickelt für IT-Administratoren und Power-User. Das Tool vereint die Robustheit
von **Robocopy** mit einer modernen, zweisprachigen Oberfläche und sichert in einem
Durchgang Benutzerprofile, Browserdaten, installierte Programme, WLAN-Zugänge,
Drucker und das Hintergrundbild — und stellt sie auf dem neuen Rechner wieder her.
Auch die Desktop-Symbole kommen wieder an ihre Stelle.

<div align="center">
<img src="docs/images/hauptfenster.png" alt="Hauptfenster des Windows DaSi Tool nach einer Sicherung" width="850">
</div>

---

## Was das Tool sichert

| Bereich | Inhalt |
| --- | --- |
| **Windows-Benutzerprofil** | Dokumente, Bilder, Desktop, Downloads und alles Weitere im Profil, auch versteckte und Systemdateien. Zwischenspeicher, Temp-Ordner, die Registry-Dateien (`NTUSER.DAT`) und Windows-Altlasten (Verknüpfungspunkte wie *Anwendungsdaten*) bleiben draußen. Zeigt ein Ordner als Verknüpfung aus dem Profil hinaus (z. B. `Dokumente\Spiele` → `D:\Spiele`), steht das als Warnung im Protokoll. **AppData** und **Cloud-Ordner** wahlweise, auch einzeln ohne den Rest des Profils — Vorgabe: aus |
| **Browser-Profile** | Firefox, Edge, Chrome, Brave und Opera — inklusive Lesezeichen, Verlauf und Einstellungen. Bei Opera jede Fassung, die beim Benutzer eingerichtet ist: Opera, Opera GX, Beta, Developer, Air … |
| **E-Mail** | Thunderbird-Profil mit Konten und lokalen Ordnern |
| **Programme** | Export der installierten Software als Winget-Paketliste, Wiederherstellung mit Auswahldialog. Programme ohne Winget-Paket landen in einer eigenen Liste |
| **Netzwerk** | Export und Import gespeicherter WLAN-Profile inklusive Zugangsdaten |
| **Drucker** | Warteschlangen, Treiber, Ports und Einstellungen über PrintBRM in einer Datei |
| **Hintergrundbild** | Sichert das Desktop-Hintergrundbild und stellt es an derselben Stelle im gewählten Profil wieder her, auch wenn der Benutzer dort anders heißt |
| **Desktop-Layout** | Wahlweise die Positionen der Desktop-Symbole (wie DesktopOK, beim Wiederherstellen automatisch zurückgesetzt) oder ein Foto des Desktops, ein Bild pro Monitor |

> **WLAN-Schlüssel liegen im Klartext in der Sicherung.** Damit sie auf einem anderen
> Rechner funktionieren, exportiert das Tool sie mit `key=clear` — verschlüsselt wären
> sie an den alten Rechner gebunden. Das Backup-Ziel deshalb wie ein
> Passwortverzeichnis behandeln.

---

## Highlights

### Ein Bereich, zwei Richtungen

Sichern und Wiederherstellen teilen sich eine Ansicht. Der Umschalter oben wechselt
die Richtung, die Beschriftungen passen sich automatisch an — aus *Programme
exportieren* wird *Programme installieren*, aus *Desktop-Layout sichern* wird
*Desktop-Layout wiederherstellen*. Die Auswahl bleibt beim Umschalten erhalten, soweit
die neue Richtung sie zulässt (siehe nächster Abschnitt).

<div align="center">
<img src="docs/images/modus-umschalter.png" alt="Die Auswahl im Modus Sichern und im Modus Wiederherstellen, nicht verfügbare Punkte ausgegraut" width="640">
</div>

### Nur wählbar, was es auch gibt

Punkte, die nichts zu tun hätten, sind **ausgegraut** und lassen sich nicht ankreuzen:

- **Sichern:** Eine Programmsicherung (Firefox, Edge, Chrome, Brave, Opera,
  Thunderbird) ist gesperrt, wenn das Programm beim gewählten Benutzer nicht
  eingerichtet ist, also kein Profil hat. Oben im Bild: Brave.
- **Wiederherstellen:** Jeder Punkt ist gesperrt, den die gewählte Sicherung nicht
  enthält. Unten im Bild: Gesichert wurden Profil samt AppData, Firefox, Edge,
  Opera und die Programmliste, alles andere ist grau.

Fährt man mit der Maus über einen grauen Punkt, nennt der Tooltip den Grund, etwa
*Chrome ist bei diesem Benutzer nicht eingerichtet - kein Profil unter …* oder *Die
gewählte Sicherung enthält kein Chrome-Profil.* Ist statt einer Sicherung der Ordner
darüber gewählt (`D:\DaSi` statt `D:\DaSi\DASI_…`), sagt der Tooltip genau das. Wird ein
Punkt durch einen Wechsel von Benutzer, Sicherung oder Modus gesperrt, verliert er
auch seinen Haken. Solange noch kein vorhandenes Profil bzw. kein vorhandener Ordner
gewählt ist, bleibt alles frei.

### Opera in jeder Fassung

Opera gibt es als Opera, Opera GX, Beta, Developer, Air … Jede Fassung legt ihr Profil
in einen eigenen Ordner unter `AppData\Roaming\Opera Software`. Das Tool erkennt
jede davon an ihren Profildateien, auch eine künftige Fassung. In der Sicherung liegt
jede in einem eigenen Unterordner von `Opera-Profil`, beim Wiederherstellen kommt
jede an ihren Platz zurück. Welche Fassungen gefunden wurden, zeigt der Tooltip der
Opera-Kachel. Fehlt eine Fassung auf dem neuen Rechner, installiert das Tool Opera
und Opera GX per Winget nach. Für die übrigen steht ein Hinweis im Protokoll, das
Profil wird trotzdem zurückgeschrieben.

### AppData und Cloud-Ordner: bewusst entscheiden

Beide gehören zum Benutzerprofil, werden aber **nur auf ausdrücklichen Wunsch**
mitgenommen — die Haken sitzen direkt in der Profilkachel. Sie gehen auch **ohne** das
Profil: Dann sichert bzw. stellt das Tool nur AppData bzw. nur die Cloud-Ordner wieder
her. Abgelegt werden sie an derselben Stelle wie bei einer Profilsicherung
(`Benutzerprofil\AppData`, `Benutzerprofil\OneDrive` …), sodass jede spätere
Wiederherstellung sie findet.

- **AppData** enthält Programmeinstellungen, aber auch maschinengebundene Lizenzen,
  Gerätekennungen und Anmeldesitzungen. Vor dem *Zurückschreiben* fragt das Tool
  deshalb nach. Zwischenspeicher wie `Temp`, `INetCache` und die Browser-Caches
  bleiben auch mit Haken draußen.
  Programme wie Discord, Teams oder Browser schreiben ständig in AppData, viele
  Dateien sind deshalb gesperrt oder verschwinden während der Sicherung. Jede Datei
  bekommt genau einen Versuch. Statt drei Protokollzeilen je Datei steht am Ende
  eine kurze Zusammenfassung je Fehlerart im Protokoll. Die vollständige Liste liegt
  als `AppData-nicht-gesichert.txt` im Sicherungsordner. Sind nur solche gesperrten
  oder verschwundenen Dateien betroffen, gilt die Sicherung als gelungen. Echte
  Fehler wie *Zugriff verweigert* oder ein volles Ziellaufwerk bleiben Fehler.
- **Cloud-Ordner** erkennt das Tool selbst: OneDrive (auch mit angehängtem
  Firmennamen wie *OneDrive - Muster GmbH*), Dropbox, Google Drive, iCloud, STRATO
  HiDrive, Nextcloud, ownCloud, MagentaCLOUD, pCloud, MEGA, Box, Tresorit, Seafile,
  Proton Drive, Jottacloud und Sync.com. Wer sie mitsichern will, bekommt vorher die
  gefundenen Anbieter genannt und eine Warnung: Dateien, die nur online liegen, werden
  dabei vollständig heruntergeladen.

Beide Schalter wirken in beide Richtungen: Ohne Haken wird AppData bzw. ein
Cloud-Ordner auch dann nicht zurückgeschrieben, wenn die Sicherung ihn enthält.

**Cloud-Ordner landen beim Zurückschreiben neben dem Original**, nicht darin:

| In der Sicherung | Wird zurückgeschrieben nach |
| --- | --- |
| `C:\Users\<Name>\OneDrive` | `C:\Users\<Name>\OneDrive_wiederhergestellt` |
| `C:\Users\<Name>\OneDrive - Muster GmbH` | `C:\Users\<Name>\OneDrive - Muster GmbH_wiederhergestellt` |
| `C:\Users\<Name>\HiDrive` | `C:\Users\<Name>\HiDrive_wiederhergestellt` |

Im echten Cloud-Ordner arbeitet der Cloud-Client: Er hielte jede zurückgeschriebene
Datei für geändert und lüde sie erneut hoch, in geteilten Ordnern entstünden
Dubletten und Konflikte. Daneben abgelegt lässt sich in Ruhe vergleichen und von
Hand übernehmen, was noch fehlt. Bei einer späteren Sicherung gilt eine solche
`…_wiederhergestellt`-Kopie als gewöhnlicher Ordner und wird mitgesichert — sie wird
ja nicht synchronisiert.

<div align="center">
<img src="docs/images/cloud-rueckfrage.png" alt="Rückfrage mit den erkannten Cloud-Anbietern" width="440">
</div>

### Auf einen anderen Rechner, zu einem anderen Benutzer

Gesichert wird zum Beispiel der Benutzer *Besitzer*, auf dem neuen Rechner heißt er
*Anwender*. Zurückgeschrieben wird immer in das Profil, das im Feld
*Benutzerprofil* gewählt ist, also nach `C:\Users\Anwender`:

- Ein Profil, das es dort nicht gibt, legt das Tool nicht an, sondern bricht mit
  einem Hinweis ab. Ein neuer Benutzer muss sich vorher einmal angemeldet haben,
  erst dann legt Windows sein Profil an.
- Pfade aus der Sicherung (etwa der des Hintergrundbilds) werden ins gewählte
  Profil übertragen: `C:\Users\Besitzer\Pictures\…` wird zu `C:\Users\Anwender\Pictures\…`.
- Die zurückgeschriebenen Dateien erben die Zugriffsrechte des Zielprofils. Sie
  gehören damit dem neuen Benutzer, nicht dem alten.
- Die Registry-Dateien des Zielbenutzers (`NTUSER.DAT` …) werden nie überschrieben.

### Desktop-Layout: Wo lag was?

Nach einer Neuinstallation ist der Desktop leer, und kaum jemand weiß noch, wo seine
Verknüpfungen lagen. Der Punkt **Desktop-Layout** bietet dafür zwei Wege, gewählt
über die beiden Knöpfe in der Kachel:

**Symbolpositionen** (wie *DesktopOK*): Das Tool merkt sich, welches Symbol wo liegt,
und setzt beim Wiederherstellen jedes wieder an seine Stelle — automatisch.

- Gelesen und gesetzt wird über die Schnittstelle, die der Explorer für seinen
  Desktop selbst anbietet. Zugeordnet wird über den Namen im Desktop-Ordner
  (`Rechnung.pdf`, beim Papierkorb seine Kennung), also unabhängig von der Sprache:
  Der „Papierkorb“ findet auch den „Recycle Bin“.
- Ist der Desktop auf dem neuen Rechner anders groß (anderer Monitor, andere
  Auflösung), werden die Stellen im selben Verhältnis umgerechnet. Was am rechten
  Rand lag, liegt wieder am rechten Rand.
- *Symbole automatisch anordnen* wird dafür ausgeschaltet — sonst reihte Windows die
  Symbole sofort wieder neu. *Am Raster ausrichten* wird so eingestellt, wie es beim
  Sichern war. War beim Sichern *automatisch anordnen* an, ist es danach wieder an.
- Symbole, die es auf dem neuen Desktop (noch) nicht gibt, nennt das Protokoll. Am
  besten läuft der Punkt deshalb zusammen mit Benutzerprofil und Programmen — er
  kommt in der Warteschlange ohnehin zuletzt.
- Gespeichert wird `Desktop-Symbole\Symbolpositionen.json` im Backup-Ziel.

**Screenshot**: Das Tool fotografiert den Desktop, und zwar **ein Bild pro Monitor**:
alle im selben Moment, jedes in Größe und voller Auflösung seines Monitors, auch bei
skalierten Bildschirmen. Damit die Symbole frei liegen, legt es dafür für einen Moment
alle Programmfenster beiseite und holt sie danach **genau so zurück, wie sie waren**:
in derselben Reihenfolge, maximierte wieder maximiert, das zuletzt aktive Fenster
wieder vorn. Die Bilder landen im Ordner `Desktop-Screenshot`:

| Monitore | Bilder |
| --- | --- |
| einer | `Bildschirm-1.png` |
| mehrere | `Bildschirm-1-Hauptbildschirm.png`, `Bildschirm-2.png` … |

Die Nummern folgen Windows (`\\.\DISPLAY2` → `Bildschirm-2.png`) und stimmen meist mit
denen unter *Einstellungen → Anzeige → Identifizieren* überein. Beim
**Wiederherstellen** öffnet das Tool jedes Bild in einem eigenen Fenster — zum
Nachsehen und Anordnen von Hand.

Beim Wiederherstellen ist nur wählbar, was in der Sicherung liegt: Enthält sie nur
Symbolpositionen, ist *Screenshot* ausgegraut und umgekehrt.

> Beides betrifft den Desktop, der gerade zu sehen ist. Ist ein anderer Benutzer
> angemeldet als der, dessen Profil gewählt ist — etwa der Techniker —, tut das Tool
> nichts und sagt das im Protokoll: Es erwischte den falschen Desktop.

### Speicherprüfung mit Ampel

Noch vor dem Start schätzt das Tool die Datenmenge — mit genau denselben
Ausschlüssen wie beim Kopieren — und vergleicht sie mit dem freien Platz am Ziel,
inklusive 10 % Reserve:

| Anzeige | Bedeutung |
| --- | --- |
| 🟢 grün | passt, mit Angabe, wie viel danach frei bleibt |
| 🟡 gelb | passt, aber knapp |
| 🔴 rot | reicht nicht — mit Angabe, wie viel fehlt |

Bei eingeschalteter *Auto-Berechnung* rechnet das Tool nach jeder Änderung der
Auswahl neu. Eine laufende Berechnung wird dabei abgebrochen und mit dem neuen Stand
wiederholt.

### Winget: erst prüfen, dann arbeiten

Vor jedem Export und jedem Import prüft das Tool, ob winget **startet** *und* ob
seine **Paketquelle antwortet** — „vorhanden“ heißt noch nicht „funktionsfähig“.
Fehlt etwas, kommt die Meldung vor der ersten Kopie und nicht nach zwanzig Minuten
Profilsicherung, mit dem konkreten Weg zur Behebung. Stehen weitere Aktionen an,
lässt sich der winget-Schritt überspringen, ohne den Rest zu verlieren.

<div align="center">
<img src="docs/images/winget-pruefung.png" alt="Hinweis, dass winget nicht einsatzbereit ist, mit Lösungsweg" width="440">
</div>

Beim **Export** wird die Liste um Zusatz- und Laufzeitpakete bereinigt (etwa die
Visual-C++-Laufzeiten); Programme, die es bei Winget nicht gibt — Firmensoftware,
Eigenentwicklungen —, landen in `Winget\Nicht-zugeordnete-Programme.txt`. Eine
vorhandene Liste wird erst ersetzt, wenn die neue vollständig ist.

Beim **Wiederherstellen** wird gezielt ausgewählt, was installiert werden soll:

<div align="center">
<img src="docs/images/winget-auswahl.png" alt="Auswahldialog für die Winget-Wiederherstellung" width="460">
</div>

### Ein eigener Ordner je Sicherung

Als Ziel genügt das Laufwerk: Wer `G:\` wählt, sieht im Feld sofort einen eigenen
Ordner mit Rechnername und Datum —

```
G:\DASI_DESKTOP-7K4Q2M_10_11_2026
```

So liegen mehrere Rechner sauber nebeneinander, statt sich im Wurzelverzeichnis
gegenseitig zu überschreiben. Gesichert wird genau dorthin, wo das Feld zeigt;
angelegt wird der Ordner beim Start. Der Schalter **Eigener Ordner** (Vorgabe: an)
lässt sich abschalten — dann zeigt das Feld wieder `G:\`, und die Sicherung geht
direkt dorthin.

**Eine vorhandene Sicherung wird aktualisiert**, statt daneben einen weiteren Ordner
anzulegen: Ist das Gewählte selbst schon eine Sicherung (ein `DASI_…`-Ordner oder ein
Ordner mit Sicherungsinhalt), geht die neue Sicherung genau dort hinein. Ebenso bei
einer zweiten Sicherung am selben Tag — der Ordnername ist derselbe. Das Profil wird
dabei abgeglichen: Was es auf dem Rechner nicht mehr gibt, verschwindet auch aus der
Sicherung. Stammt der Ordner erkennbar von einem **anderen Rechner**
(`DASI_ALTPC_…`), fragt das Tool vorher nach. Beim **Wiederherstellen** wirkt der
Schalter nicht; dort zeigt das Feld genau den gewählten Ordner.

### Schnellauswahl der Benutzerprofile

Alle echten Profile unter `C:\Users` stehen im Dropdown bereit. System- und
Dienstprofile werden automatisch herausgefiltert, sodass nur relevante Konten
erscheinen.

### Zweisprachig und mit Hell-/Dunkelmodus

Die gesamte Oberfläche inklusive Aktivitätsprotokoll lässt sich zwischen
**Deutsch** und **Englisch** umschalten. Sprache und Farbschema werden beim Start
aus den Windows-Einstellungen übernommen und lassen sich jederzeit über das
Sonne-/Mond-Symbol und das Sprach-Dropdown ändern. Das Farbschema gilt auch für
alle Dialoge, etwa Rückfragen und die Winget-Auswahl.

<div align="center">
<img src="docs/images/hell-englisch.png" alt="Hauptfenster im hellen Design auf Englisch, nach einer Wiederherstellung" width="850">
</div>

### Nachvollziehbares Protokoll und ehrliche Bilanz

Jeder Schritt wird in Echtzeit protokolliert. Das Protokoll scrollt automatisch mit,
hält aber an, sobald man zum Nachlesen nach oben scrollt. Mit **Log: an** erscheint
zusätzlich die vollständige Robocopy-Ausgabe Datei für Datei.

Am Ende steht eine Bilanz — *4 von 4 erfolgreich, 0 Fehler* —, und dieselbe Übersicht
landet als Datei im Backup-Ziel: `Sicherung-Protokoll_<Rechner>_<Datum>.txt` bzw.
`Wiederherstellung-Protokoll_…`. Eine Aktion zählt nur dann als erfolgreich, wenn sie
ohne einen einzigen Fehler durchlief.

### Für lange Sicherungen gerüstet

- Läuft ein Notebook im **Akkubetrieb**, warnt das Tool vor dem Start und fragt nach.
- Während des Laufs geht der Rechner **nicht in den Standby**, und das Tool arbeitet
  mit erhöhter Priorität. Beides wird danach vollständig zurückgesetzt — an den
  Energieeinstellungen von Windows ändert sich nichts.
- Browser und Thunderbird werden vor dem Kopieren **automatisch geschlossen**, damit
  keine Datei mitten im Schreiben gesichert wird.

---

## Selbstaktualisierung

Beim Start holt das Programm **eine** Datei:

```
https://raw.githubusercontent.com/Tinnitus97/Windows.DaSi.Tool/HEAD/update.json
```

Gibt es eine neuere Fassung, erscheint über dem Protokoll ein gelber Streifen
mit zwei Schaltflächen — **Was ist neu?** und **Jetzt aktualisieren**. Gibt es
nichts, bleibt er unsichtbar.

**Heruntergeladen und ersetzt wird nichts ohne Rückfrage.** Vor dem Austausch
wird die **SHA256-Summe** der geladenen Datei mit der Angabe aus `update.json`
verglichen; stimmt sie nicht, wird die Datei verworfen.

Eine laufende EXE kann sich unter Windows nicht selbst überschreiben. Deshalb
schreibt das Programm ein kleines Skript nach `%TEMP%\WindowsDaSiTool-Update`,
das die Datei ersetzt, sobald sich das Programm beendet hat, und die neue Fassung
startet. Schlägt das fehl — fehlende Schreibrechte, oder das Programm läuft anderswo
aus demselben Ordner —, öffnet sich eine Erklärung mit beiden Pfaden im Editor, und
die bisherige Fassung startet wieder. Umlaute im Benutzernamen (`C:\Users\Jürgen`)
sind kein Hindernis.

> **Umstieg von 1.0.x auf 1.1.0:** Diesen einen Austausch erledigt noch das
> Austauschskript der alten Fassung. Das Programm braucht dabei rund eine Minute, bis
> es neu startet. Bei einem **Umlaut im Benutzernamen** scheitert das alte Skript —
> dort die neue EXE einmal von Hand aus den
> [Releases](https://github.com/Tinnitus97/Windows.DaSi.Tool/releases) holen. Ab 1.1.0
> ist beides behoben.

Für ein internes Spiegelverzeichnis lässt sich in `Update-Url.txt` **neben der
EXE** eine abweichende Adresse hinterlegen — eine Zeile, vollständige
`https://…`-Adresse.

> Warum nicht `api.github.com`? Die Schnittstelle erlaubt ohne Anmeldung nur 60
> Abrufe je Stunde und IP-Adresse — in einer Firma sitzen alle Rechner hinter
> derselben Adresse. `raw.githubusercontent.com` kennt diese Grenze nicht.

Wie eine neue Fassung veröffentlicht wird — im Browser, in GitHub Desktop oder
auf der Kommandozeile — steht in **[docs/VEROEFFENTLICHEN.md](docs/VEROEFFENTLICHEN.md)**.

---

## Systemanforderungen

| Anforderung | Details |
| --- | --- |
| **Betriebssystem** | Windows 10 oder Windows 11 (64 Bit) |
| **Berechtigungen** | Administratorrechte — die UAC-Abfrage erfolgt automatisch beim Start |
| **Dateisystem** | Das Backup-Ziel muss auf einem **NTFS**-Laufwerk liegen (Robocopy-Voraussetzung) |
| **.NET** | Nicht erforderlich — die Laufzeit ist in der EXE enthalten |
| **Winget** | Nur für *Programme exportieren/installieren*: der *App-Installer* aus dem Microsoft Store (unter Windows 11 vorinstalliert) |

---

## Nutzung

1. Aktuelle `WindowsDaSiTool.exe` aus dem [Releases-Bereich](https://github.com/Tinnitus97/Windows.DaSi.Tool/releases) herunterladen.
2. EXE starten und die UAC-Abfrage mit **Ja** bestätigen.
3. Über die **Schnellauswahl** das Benutzerprofil wählen (oder per `...` manuell suchen).
4. Das **Backup-Ziel** festlegen — ein Ordner auf einem NTFS-Laufwerk. Mit *Eigener
   Ordner: AN* genügt das Laufwerk (`G:\`) — das Feld zeigt dann sofort `G:\DASI_…`.
   Eine vorhandene Sicherung als Ziel wird aktualisiert.
   Zum Wiederherstellen den gewünschten `DASI_…`-Ordner wählen.
5. Modus **Sichern** oder **Wiederherstellen** wählen und die gewünschten Punkte ankreuzen.
   *AppData* und *Cloud-Anbieter* nur bei Bedarf dazunehmen. Ausgegraute Punkte gibt es
   beim Benutzer bzw. in der Sicherung nicht — der Tooltip nennt den Grund.
6. Auf die **Speicherzeile** achten: Grün heißt, der Platz reicht.
7. **Ausgewählte Aktionen starten** — der Fortschritt erscheint im Protokoll, die
   Bilanz am Ende zusätzlich als Datei im Backup-Ziel.

> **Hinweis zu Passwörtern:** Gespeicherte Browser-Passwörter sind im Profil-Backup
> verschlüsselt enthalten und funktionieren nach der Wiederherstellung auf demselben
> Rechner und Benutzerkonto wieder. Für einen Wechsel auf einen anderen Rechner nutze
> den Export-Dialog des jeweiligen Browsers (z. B. `edge://settings/passwords`).

---

## Technische Details

| | |
| --- | --- |
| **Sprache** | C# |
| **UI-Framework** | [Avalonia](https://avaloniaui.net/) 12.1 |
| **Zielplattform** | .NET 10 (LTS), `win-x64`, self-contained, festgelegt in `global.json` |
| **Dateioperationen** | Robocopy mit Spiegelung, Ausschlusslisten und Multithreading |
| **Prozess-Handling** | Browser und Thunderbird werden vor dem Backup automatisch geschlossen, um Dateikonflikte zu vermeiden |
| **Fehlerbehandlung** | Unerwartete Fehler landen in `%USERPROFILE%\Desktop\WindowsDaSiTool_Absturz.log` |
| **Prüfungen** | `tests/DaSiTests` — Ausschlusslisten, Cloud-Erkennung, Opera-Fassungen, gesperrte Punkte, Sicherungsordner, Symbolpositionen, Bilanz, Update-Logik. Läuft bei jedem Push unter Linux und Windows; unter Windows werden dabei das Austauschskript der Selbstaktualisierung echt ausgeführt und ein Desktop-Symbol am echten Explorer-Desktop versetzt und zurückgesetzt |

### Selbst bauen

Voraussetzung ist das [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

> **Warum .NET 10 und nicht das neuere .NET 11?** Mit .NET 11 gebaut (Version
> 1.2.0) startete das Tool auf Windows-10-Rechnern nicht. Das Projekt ist deshalb
> in `global.json` auf .NET 10 (LTS) festgelegt. Ein daneben installiertes
> .NET 11 SDK stört nicht, `dotnet` nimmt hier trotzdem das 10er-SDK. Die
> wöchentliche Prüfung respektiert die Festlegung und meldet sich erst, wenn
> der Support für .NET 10 ausläuft.

```powershell
git clone https://github.com/Tinnitus97/Windows.DaSi.Tool.git
cd Windows.DaSi.Tool
.\build.ps1
```

Die fertige EXE liegt anschließend im Ordner `publish`.

| Parameter | Wirkung |
| --- | --- |
| `.\build.ps1` | Self-contained Build (Standard) — läuft ohne installiertes .NET |
| `.\build.ps1 -Mode framework` | Deutlich kleinere EXE, setzt .NET 10 Desktop Runtime voraus |
| `.\build.ps1 -Sign` | Signiert die EXE, um SmartScreen-Warnungen zu reduzieren |

> **Fehler `MSB4236 … "Microsoft.NET.SDK.WorkloadAutoImportPropsLocator" wurde nicht gefunden`?**
> Dann kann die .NET-Installation des Rechners ihre Workloads nicht auflösen.
> `build.ps1` erkennt das und baut ohne Workload-Auflösung weiter. Das Projekt
> braucht keine Workloads, die EXE ist bytegenau dieselbe. Wer `dotnet` direkt
> aufruft (etwa für die Prüfungen unten), setzt vorher in derselben PowerShell
> `$env:MSBuildEnableWorkloadResolver = 'false'`. Dauerhaft beheben lässt es sich
> durch Reparieren oder Neuinstallieren des .NET SDK.

Die Prüfungen laufen ohne Oberfläche und auf jedem System:

```powershell
dotnet run --project tests/DaSiTests -c Release
```

---

## Hinweis zu SmartScreen

Die EXE ist nicht mit einem kostenpflichtigen Zertifikat signiert. Windows zeigt
beim ersten Start daher möglicherweise eine SmartScreen-Warnung. Über
**Weitere Informationen → Trotzdem ausführen** lässt sie sich starten. Wer die
Warnung vermeiden möchte, kann das Projekt selbst bauen und mit einem eigenen
Zertifikat signieren.

---

## Changelog

Alle Änderungen sind in der [CHANGELOG.md](CHANGELOG.md) dokumentiert.

---

## Lizenz

Dieses Projekt steht unter der [MIT-Lizenz](LICENSE).
