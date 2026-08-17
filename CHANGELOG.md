# Changelog

Alle nennenswerten Änderungen an diesem Projekt werden hier dokumentiert.

Das Format orientiert sich an [Keep a Changelog](https://keepachangelog.com/de/1.1.0/),
die Versionierung folgt [Semantic Versioning](https://semver.org/lang/de/).

---

## [1.0.7] - 2026-08-17

### Behoben

- **Beim Beenden erschien „Es läuft gerade eine Aktion!", obwohl nichts lief.**
  Das Fenster hat vor dem Schließen `UiEnabled` befragt — also den Schalter, der
  die Oberfläche sperrt. Gesperrt wird sie aber auch während des Selbstupdates.
  Nach „Jetzt aktualisieren" schließt sich das Programm selbst, damit das
  Austauschskript die EXE ersetzen kann; genau dabei kam die Rückfrage nach
  einem harten Abbruch. Ein „Nein" hielt das Fenster offen, während das Skript
  schon auf das Ende des Vorgangs wartete — das Update lief ins Nichts.

  Es gibt jetzt eine eigene Eigenschaft `IsActionRunning`, die ausschließlich
  während der Warteschlange gesetzt ist. Nur sie entscheidet über die Rückfrage.

---

## [1.0.6] - 2026-08-17

### Hinzugefügt

- **Selbstaktualisierung über GitHub.** Beim Start holt das Programm eine
  einzige Datei:

  ```
  https://raw.githubusercontent.com/Tinnitus97/Windows.DaSi.Tool/HEAD/update.json
  ```

  Gibt es eine neuere Fassung, erscheint der gewohnte gelbe Streifen — jetzt
  aber mit zwei Schaltflächen: **Was ist neu?** öffnet die Veröffentlichung im
  Browser, **Jetzt aktualisieren** erledigt den Rest.

- **Programm aktualisieren.** Lädt die neue EXE, prüft ihre **SHA256-Summe**
  gegen die Angabe in `update.json` und tauscht sie aus. Stimmt die Summe
  nicht, wird die Datei verworfen und nichts ersetzt.

  Weil eine laufende EXE sich unter Windows nicht selbst überschreiben kann,
  erledigt das ein kleines Skript, das auf das Ende des Vorgangs wartet, die
  Datei ersetzt und danach neu startet. Es liegt in einem eigenen Ordner
  (`%TEMP%\WindowsDaSiTool-Update`). Schlägt das Ersetzen fehl — fehlende
  Schreibrechte, oder das Programm läuft an einem anderen Arbeitsplatz aus
  demselben Ordner —, bleibt ein Fenster mit dem Grund und dem Pfad zur neuen
  Fassung stehen.

- **Heruntergeladen und ersetzt wird nichts ohne Rückfrage.** Der Dialog nennt
  Versionsnummer und Zielpfad.

- **Veröffentlichung über GitHub Actions.** `release.yml` baut die EXE, bildet
  die Prüfsumme, hängt beides an eine Veröffentlichung und schreibt
  `update.json` fort. Auslösen wahlweise über ein Etikett (`v1.0.6`) oder per
  Knopfdruck unter *Actions → Veröffentlichen → Run workflow*. `build.yml`
  prüft bei jedem Push, ob der Quelltext noch übersetzt und die Prüfungen
  durchlaufen.

### Geändert

- **Der Update-Check läuft nicht mehr über das alte Repository**
  `backup_my_windows_Updater/newversion.txt`. Diese Datei enthielt nur eine
  nackte Versionsnummer — es gab keine Adresse zum Herunterladen und keine
  Prüfsumme, der Streifen konnte deshalb nur die Projektseite öffnen.
- Der Klick auf die Streifenfläche öffnet nichts mehr von selbst; beide
  Schaltflächen sagen ausdrücklich, was sie tun.

### Entfernt

- `SystemHelpers.CheckForUpdate` — durch `UpdateService` ersetzt.

### Behoben

- `SystemHelpers.GetApplicationDirectory` fehlte in diesem Projekt (es gab die
  Methode nur in den Schwesterprogrammen) — der Build brach mit `CS0117` ab.
  Jetzt vorhanden, mit derselben Behandlung für SingleFile-EXEs: `ProcessPath`
  statt `AppContext.BaseDirectory`, weil letzteres bei einer gepackten EXE ins
  Entpackverzeichnis unter `%TEMP%` zeigt.

---

## [1.0.5] - 2026-08-02

### Hinzugefügt

- **Drucker und Treiber**: Sicherung und Wiederherstellung von Druckwarteschlangen,
  Treibern, Ports und Einstellungen über das Windows-Werkzeug PrintBRM.
- **Leistungsmodus**: Während eines Backups wird der Standby unterdrückt und
  die Priorität angehoben. Alle Änderungen werden nach Abschluss vollständig
  zurückgesetzt und verändern keine globalen Energieplan-Einstellungen.
- **Akku-Warnung**: Läuft ein Notebook im Akkubetrieb, erscheint vor dem Start
  eine Rückfrage mit der Empfehlung, das Ladegerät anzuschließen. Die Warnung
  wird zusätzlich ins Protokoll geschrieben.
- **Log leeren**: Neuer Knopf neben dem Protokoll-Titel.
- **Leistungshinweis**: Beim Einschalten des detaillierten Logs erscheint ein
  Hinweis auf die mögliche Geschwindigkeitseinbuße - als Meldung und im Log.
- **Speicherübersicht**: Zeigt den freien Platz am Backup-Ziel an und schätzt die Datenmenge ALLER ausgewählten Punkte (Benutzerprofil, Browser, Mail). Eine Ampel warnt farblich und per Popup, wenn der Platz (inkl. 10% Reserve) knapp wird oder nicht reicht. Die Berechnung läuft optional automatisch (abschaltbar).
- **Hintergrundbild**: Sichert das aktuelle Desktop-Hintergrundbild und stellt
  es wieder her. Liegt das Bild schon am Originalpfad, wird es nur gesetzt statt
  kopiert; sonst wird es an den Originalpfad zurückgelegt und als Hintergrund gesetzt.
- **Höhere Robocopy-Priorität**: Der Kopiervorgang selbst wird höher
  priorisiert (wirksam vor allem bei vielen kleinen Dateien).

### Geändert

- **Echte Umlaute**: Die deutsche Oberfläche verwendet jetzt durchgängig
  echte Umlaute (ae/oe/ue/ss wurden ersetzt).
- **Dynamische Konsolen-Codepage**: Die Kodierung für die Ausgabe von robocopy
  und PrintBRM wird jetzt automatisch aus der OEM-Codepage des Systems ermittelt
  (statt fest 850), mit 850 als Rückfallwert.
- **Log-Beschriftung**: "Logging: Minimal / AUS" heißt jetzt "Log: aus" bzw.
  "Log: an".
- **Fenstergröße**: Auf 1200x740 angepasst, damit alle Auswahlpunkte samt
  Start-Knopf sichtbar sind.
- **Avalonia 12.1.1**: Aktualisierung auf die neueste Version.
- **Ordner-Dialog**: Startet in einem garantiert existierenden Ordner, um einen
  Absturz beim Aufbau der Liste zuletzt besuchter Orte zu vermeiden.

### Behoben

- **Absturz beim Ordner-Auswählen**: Der Dialog konnte mit "Directory must exist"
  abstürzen, wenn die Verlaufsliste einen nicht mehr existierenden Pfad enthielt
  (abgezogener USB-Stick, getrenntes Netzlaufwerk). Der Aufruf ist jetzt
  abgesichert und startet in einem gültigen Ordner.
- **Drucker-Sicherung (0x8007007b)**: PrintBRM scheiterte an der Pfadübergabe.
  Läuft jetzt über einen Temp-Ordner mit bloßem Dateinamen.
- **Sonderzeichen**: Umlaute in der PrintBRM- und Robocopy-Ausgabe werden korrekt
  dargestellt.

### Entfernt

- **System.Text.Encoding.CodePages**: Ab .NET 10 im Framework enthalten, die
  separate Abhängigkeit entfällt (Warnung NU1510).
- Native Debug-Symbole (`.pdb`) werden nicht mehr mit ausgeliefert; die EXE wird
  zusätzlich komprimiert.

---

## [1.0.0] - 2026-07-23

Erste stabile Version. Vollständige Neuentwicklung des ursprünglichen
PowerShell/WPF-Skripts als native C#-Anwendung mit Avalonia.

### Hinzugefügt

- **Native Anwendung**: Komplette Portierung von PowerShell/WPF nach C# mit
  Avalonia. Ausgeliefert als eigenständige EXE ohne Installation.
- **Umschaltbarer Modus**: Sichern und Wiederherstellen sind zu einem Bereich
  mit Segment-Umschalter zusammengefasst. Die Beschriftungen passen sich dem
  gewählten Modus an (z.B. "Programme exportieren" / "Programme installieren").
- **Schnellauswahl für Benutzerprofile**: Dropdown mit allen echten Profilen
  unter `C:\Users`. System- und Dienstprofile werden herausgefiltert.
- **Zweisprachigkeit (Deutsch / Englisch)**: Die gesamte Oberfläche inklusive
  Aktivitäts-Protokoll und Dialoge lässt sich live umschalten. Die Sprache
  wird beim Start aus der Windows-Anzeigesprache übernommen (Fallback: Deutsch).
- **Hell- und Dunkelmodus**: Umschaltbar über ein Sonne-/Mond-Symbol. Beim Start
  wird der in Windows eingestellte Modus übernommen.
- **Intelligentes Auto-Scrolling im Protokoll**: Das Log folgt automatisch neuen
  Zeilen. Scrollt man manuell nach oben, pausiert das Nachspringen; unten
  angekommen läuft es automatisch weiter.
- **Winget-Integration**: Nativer Export der installierten Programme über
  `winget export`, mit Auswahldialog beim Wiederherstellen. Zusatz- und
  Laufzeitpakete werden automatisch herausgefiltert.
- **WLAN-Profile**: Export und Import der gespeicherten Netzwerkprofile.
- **Drucker und Treiber**: Sicherung und Wiederherstellung von Druckwarteschlangen,
  Treibern, Ports und Einstellungen über das Windows-Werkzeug PrintBRM.
- **Browser- und Mailprofile**: Sicherung und Wiederherstellung von Firefox,
  Edge, Chrome, Brave und Thunderbird.
- **Leistungsmodus**: Während eines Backups wird der Standby unterdrückt
  und die Prozesspriorität angehoben. Beides wird nach Abschluss vollständig
  zurückgesetzt und verändert keine globalen Energieplan-Einstellungen.
- **Absturzprotokoll**: Unerwartete Fehler werden in
  `%USERPROFILE%\Desktop\WindowsDaSiTool_Absturz.log` festgehalten.

### Geändert

- **.NET 10**: Umstellung von .NET 8 auf die aktuelle LTS-Version
  (Support bis November 2028).
- **Avalonia 12.1.0**: Aktualisierung auf die neueste Version des UI-Frameworks.
- **Alle NuGet-Pakete aktualisiert**: CommunityToolkit.Mvvm auf 8.4.2,
  Tmds.DBus.Protocol auf 0.94.2.
- **Ordner-Dialog** startet jetzt bei "Dieser PC" statt in einem festen Ordner.
- **Oberfläche überarbeitet**: Gruppierung in abgesetzte Karten, einheitliche
  Feldhöhen, kompaktere Fenstergröße (1200x700) für kleinere Auflösungen.
- **Kleinere EXE**: Die eingebettete Laufzeit wird komprimiert
  (`EnableCompressionInSingleFile`).

### Behoben

- **Absturz beim Ordner-Auswählen**: Der Windows-Ordner-Dialog konnte mit
  "Directory must exist" abstürzen. Der Aufruf ist jetzt abgesichert und
  beendet die Anwendung im Fehlerfall nicht mehr.
- **Codepage 850**: Umlaute in der Robocopy-Ausgabe werden korrekt dargestellt.
- **Fenster wuchs mit langem Protokoll**: Die Fenstergröße bleibt jetzt stabil,
  das Protokoll scrollt intern.
- **Winget im erhöhten Kontext**: Robuste Auflösung von `winget.exe` inklusive
  Selbstregistrierung des App-Installer-Pakets, falls nötig.
- **Lokalisierte Programmnamen** (z.B. "Mozilla Firefox (x64 de)") werden beim
  Winget-Export korrekt zugeordnet.

### Sicherheit

- `Tmds.DBus.Protocol` auf 0.94.2 angehoben. Behebt CVE-2026-39959 (HIGH), die
  über eine transitive Abhängigkeit hereinkam.
- `System.Text.Encoding.CodePages` entfernt: Ab .NET 10 im Framework enthalten,
  die separate Abhängigkeit entfällt.
- Native Debug-Symbole (`.pdb`) werden nicht mehr mit ausgeliefert.

---

[1.0.7]: https://github.com/Tinnitus97/Windows.DaSi.Tool/releases/tag/1.0.7
[1.0.6]: https://github.com/Tinnitus97/Windows.DaSi.Tool/releases/tag/1.0.6
[1.0.5]: https://github.com/Tinnitus97/Windows.DaSi.Tool/releases/tag/v1.0.5
[1.0.0]: https://github.com/Tinnitus97/Windows.DaSi.Tool/releases/tag/v1.0.0
