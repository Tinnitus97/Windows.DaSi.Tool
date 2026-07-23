# Changelog

Alle nennenswerten Aenderungen an diesem Projekt werden hier dokumentiert.

Das Format orientiert sich an [Keep a Changelog](https://keepachangelog.com/de/1.1.0/),
die Versionierung folgt [Semantic Versioning](https://semver.org/lang/de/).

---

## [1.0.0] - 2026-07-23

Erste stabile Version. Vollstaendige Neuentwicklung des urspruenglichen
PowerShell/WPF-Skripts als native C#-Anwendung mit Avalonia.

### Hinzugefuegt

- **Native Anwendung**: Komplette Portierung von PowerShell/WPF nach C# mit
  Avalonia. Ausgeliefert als eigenstaendige EXE ohne Installation.
- **Umschaltbarer Modus**: Sichern und Wiederherstellen sind zu einem Bereich
  mit Segment-Umschalter zusammengefasst. Die Beschriftungen passen sich dem
  gewaehlten Modus an (z.B. "Programme exportieren" / "Programme installieren").
- **Schnellauswahl fuer Benutzerprofile**: Dropdown mit allen echten Profilen
  unter `C:\Users`. System- und Dienstprofile werden herausgefiltert.
- **Zweisprachigkeit (Deutsch / Englisch)**: Die gesamte Oberflaeche inklusive
  Aktivitaets-Protokoll und Dialoge laesst sich live umschalten. Die Sprache
  wird beim Start aus der Windows-Anzeigesprache uebernommen (Fallback: Deutsch).
- **Hell- und Dunkelmodus**: Umschaltbar ueber ein Sonne-/Mond-Symbol. Beim Start
  wird der in Windows eingestellte Modus uebernommen.
- **Intelligentes Auto-Scrolling im Protokoll**: Das Log folgt automatisch neuen
  Zeilen. Scrollt man manuell nach oben, pausiert das Nachspringen; unten
  angekommen laeuft es automatisch weiter.
- **Winget-Integration**: Nativer Export der installierten Programme ueber
  `winget export`, mit Auswahldialog beim Wiederherstellen. Zusatz- und
  Laufzeitpakete werden automatisch herausgefiltert.
- **WLAN-Profile**: Export und Import der gespeicherten Netzwerkprofile.
- **Browser- und Mailprofile**: Sicherung und Wiederherstellung von Firefox,
  Edge, Chrome, Brave und Thunderbird.
- **Absturzprotokoll**: Unerwartete Fehler werden in
  `%USERPROFILE%\Desktop\WindowsDaSiTool_Absturz.log` festgehalten.

### Geaendert

- **.NET 10**: Umstellung von .NET 8 auf die aktuelle LTS-Version
  (Support bis November 2028).
- **Avalonia 12.1.0**: Aktualisierung auf die neueste Version des UI-Frameworks.
- **Alle NuGet-Pakete aktualisiert**: CommunityToolkit.Mvvm auf 8.4.2,
  Tmds.DBus.Protocol auf 0.94.2.
- **Ordner-Dialog** startet jetzt bei "Dieser PC" statt in einem festen Ordner.
- **Oberflaeche ueberarbeitet**: Gruppierung in abgesetzte Karten, einheitliche
  Feldhoehen, kompaktere Fenstergroesse (1200x700) fuer kleinere Aufloesungen.
- **Kleinere EXE**: Die eingebettete Laufzeit wird komprimiert
  (`EnableCompressionInSingleFile`).

### Behoben

- **Absturz beim Ordner-Auswaehlen**: Der Windows-Ordner-Dialog konnte mit
  "Directory must exist" abstuerzen. Der Aufruf ist jetzt abgesichert und
  beendet die Anwendung im Fehlerfall nicht mehr.
- **Codepage 850**: Umlaute in der Robocopy-Ausgabe werden korrekt dargestellt.
- **Fenster wuchs mit langem Protokoll**: Die Fenstergroesse bleibt jetzt stabil,
  das Protokoll scrollt intern.
- **Winget im erhoehten Kontext**: Robuste Aufloesung von `winget.exe` inklusive
  Selbstregistrierung des App-Installer-Pakets, falls noetig.
- **Lokalisierte Programmnamen** (z.B. "Mozilla Firefox (x64 de)") werden beim
  Winget-Export korrekt zugeordnet.

### Sicherheit

- `Tmds.DBus.Protocol` auf 0.94.2 angehoben. Behebt CVE-2026-39959 (HIGH), die
  ueber eine transitive Abhaengigkeit hereinkam.
- `System.Text.Encoding.CodePages` entfernt: Ab .NET 10 im Framework enthalten,
  die separate Abhaengigkeit entfaellt.
- Native Debug-Symbole (`.pdb`) werden nicht mehr mit ausgeliefert.

---

[1.0.0]: https://github.com/Tinnitus97/backup_my_windows/releases/tag/v1.0.0
