# Changelog

Alle nennenswerten Änderungen an diesem Projekt werden hier dokumentiert.

Das Format orientiert sich an [Keep a Changelog](https://keepachangelog.com/de/1.1.0/),
die Versionierung folgt [Semantic Versioning](https://semver.org/lang/de/).

---

## [Unveröffentlicht]

### Behoben

- **Wiederherstellen zu einem anderen Benutzer legte ein fremdes Profil an.**
  Gesichert „Besitzer“, zurück zu „Anwender“: Das Hintergrundbild wurde unter
  seinem alten Pfad abgelegt. Dabei entstand `C:\Users\Besitzer`, ein Profil, das
  es auf dem neuen Rechner nicht gibt. Jetzt kommt es an dieselbe Stelle im
  gewählten Profil (`C:\Users\Anwender\…`). Beim Sichern wird dafür das Profil mit
  vermerkt, ältere Sicherungen werden am Pfad `C:\Users\<Name>\…` erkannt.
- **Zielprofil, das es nicht gibt, wurde angelegt.** Vor dem Start prüft das Tool
  jetzt, ob das gewählte Profil existiert, und bricht sonst mit einem Hinweis ab.
- **Zurückgeschriebene Dateien bekamen fremde Zugriffsrechte.** Statt `/COPYALL`
  jetzt `/COPY:DAT`: Die Dateien erben die Rechte des Zielprofils. Die
  Registry-Dateien des Zielbenutzers (`NTUSER.DAT` …) werden nie überschrieben, auch
  nicht aus älteren Sicherungen.
- **Profilsicherung ließ versteckte und Systemdateien ohne Meldung aus.** Der
  Schalter `/XA:SH` war für die Registry-Dateien gedacht, traf aber jede versteckte
  oder Systemdatei, etwa die `desktop.ini` angepasster Ordner. Jetzt werden nur die
  Registry-Dateien gezielt ausgelassen.
- **Gesperrte Unterpunkte sahen aus wie freie.** *AppData* und *Cloud-Anbieter* in der
  Profilkachel wurden bei gesperrter Oberfläche nicht blasser dargestellt. Jetzt
  sind sie wie die Kacheln deutlich ausgegraut.

### Hinzugefügt

- **Opera-Sicherung, jede Fassung.** Neue Kachel *Opera-Profil (auch GX)* direkt nach
  den anderen Browsern. Gesichert wird jede Fassung, die beim Benutzer eingerichtet
  ist (Opera, Opera GX, Beta, Developer, Air …). Erkannt wird jede an ihren
  Profildateien, nicht an einer festen Liste. Jede liegt in einem eigenen
  Unterordner von `Opera-Profil` und kommt beim Wiederherstellen an ihren Platz
  zurück. Ein vorhandenes Profil wird dabei beiseitegelegt, nicht gelöscht. Fehlt
  Opera oder Opera GX, wird es per Winget nachinstalliert.
- **Nicht verfügbare Punkte sind ausgegraut.** Beim Sichern sind die
  Programmsicherungen gesperrt, wenn das Programm beim gewählten Benutzer nicht
  eingerichtet ist. Beim Wiederherstellen ist jeder Punkt gesperrt, den die gewählte
  Sicherung nicht enthält. Der Tooltip nennt den Grund. Ist der Ordner über den
  Sicherungen gewählt statt einer Sicherung, sagt er das. Gesperrte Punkte verlieren
  ihren Haken.
- **Warnung bei Verknüpfungen aus dem Profil hinaus.** Ist ein Ordner in Wahrheit
  eine Verknüpfung auf einen Ort außerhalb des Profils (z. B. `Dokumente\Spiele` →
  `D:\Spiele`), wird ihr Inhalt nicht mitgesichert. Das steht jetzt vor dem Kopieren
  als Warnung im Protokoll. Bisher fehlte so ein Ordner ohne jeden Hinweis.

---

## [1.2.1] - 2026-11-11

### Behoben

- **Startete nicht unter Windows 10.** Version 1.2.0 war mit .NET 11 gebaut und
  ließ sich auf Windows-10-Rechnern nicht starten. Das Tool läuft wieder auf
  **.NET 10 (LTS)**. Die EXE bringt ihre Laufzeit weiterhin selbst mit, auf dem
  Zielrechner muss nichts installiert werden.

### Geändert

- **.NET-Version festgelegt.** `global.json` legt das SDK auf .NET 10 fest
  (`rollForward: latestFeature`, also neueste 10er-Fassung, nie 11). Zielframework,
  Workflows und `build.ps1` folgen. Die wöchentliche Prüfung respektiert die
  Festlegung: Ein neueres .NET meldet sie nur als Hinweis. Ein halbes Jahr vor dem
  Supportende von .NET 10 warnt sie, danach schlägt sie fehl.

---

## [1.2.0] - 2026-11-10

### Hinzugefügt

- **Desktop-Screenshot.** Beim Sichern fotografiert das Tool den Desktop samt
  Symbolen, **ein Bild pro Monitor** (`Bildschirm-1.png`, `Bildschirm-2.png` …).
  Jedes Bild hat genau die Größe und volle Auflösung seines Monitors, auch bei
  Skalierung. Alle Monitore werden im selben Moment aufgenommen, die Nummern folgen
  Windows, und bei mehreren Monitoren trägt das Bild des Hauptbildschirms das im Namen.
  Für die Aufnahme werden alle Programmfenster kurz beiseitegelegt und danach genau so
  zurückgeholt, wie sie waren: in derselben Reihenfolge, maximierte wieder maximiert,
  das aktive Fenster wieder vorn. Beim Wiederherstellen öffnet derselbe Punkt jedes
  Bild in einem eigenen Fenster, damit sich die Symbole von Hand wieder anordnen
  lassen. Ist ein anderer Benutzer angemeldet als der des gesicherten Profils, wird
  kein Foto gemacht, denn es zeigte den falschen Desktop.
- **Eigener Ordner je Sicherung** (abschaltbar, Vorgabe: an). Als Ziel genügt das
  Laufwerk: Nach der Auswahl von `G:\` zeigt das Feld sofort
  `G:\DASI_<Rechnername>_TT_MM_JJJJ_HH_MM`, und genau dorthin wird gesichert.
  Mehrere Rechner und Stände liegen so nebeneinander, statt sich zu überschreiben.
  Profil, winget-Liste, Desktop-Foto und Protokolldatei landen alle in diesem
  Ordner. Ist das Gewählte schon ein `DASI_…`-Ordner, kommt der neue daneben — in
  eine bestehende Sicherung wird nie hineingeschrieben. Beim Wiederherstellen ist der
  Schalter gesperrt.

### Geändert

- **AppData und Cloud-Ordner gehen auch ohne das Benutzerprofil.** Bisher waren die
  beiden Haken an das Profil gekoppelt und ohne es gesperrt. Jetzt lassen sie sich
  einzeln sichern und wiederherstellen; sie heißen deshalb *AppData sichern* und
  *Cloud-Anbieter sichern* (bzw. *… wiederherstellen*). Abgelegt wird an derselben
  Stelle wie bei einer Profilsicherung.
- **Cloud-Ordner werden neben das Original zurückgeschrieben.** Mit dem Haken
  *Cloud-Anbieter wiederherstellen* landet zum Beispiel `OneDrive` jetzt in
  `OneDrive_wiederhergestellt` (ebenso `HiDrive_wiederhergestellt`,
  `Dropbox_wiederhergestellt` …) statt im laufenden Cloud-Ordner. Dort hielte der
  Cloud-Client jede Datei für geändert und lüde sie erneut hoch, in geteilten Ordnern
  mit Dubletten und Konflikten. Solche Kopien gelten bei späteren Sicherungen als
  gewöhnliche Ordner und werden mitgesichert.
- **Auf .NET 11 umgestellt.** Die EXE bringt ihre Laufzeit weiterhin selbst mit;
  auf dem Zielrechner muss nichts installiert werden. Zum Selbstbauen wird jetzt
  das .NET 11 SDK gebraucht (`build.ps1` prüft das und nennt den Download).

### Behoben

- **AppData-Sicherung: Protokoll ging in Meldungen unter, Bilanz zeigte Fehler.**
  Gesperrte oder während der Sicherung gelöschte Dateien (Discord, Teams, Browser …)
  meldete robocopy einzeln mit je drei Zeilen. Dazu kam „maximale Anzahl von
  Wiederholungsversuchen überschritten“, obwohl jede Datei ohnehin nur einen Versuch
  bekommt. Schon eine solche Datei ließ die ganze Aktion als Fehler zählen. Jetzt
  steht im Protokoll eine Zeile je Fehlerart (z. B. `1.000 × Fehler 32: … von einem
  anderen Prozess verwendet`). Die vollständige Liste liegt als
  `AppData-nicht-gesichert.txt` im Sicherungsordner, und die Aktion gilt als gelungen.
  *Zugriff verweigert*, ein volles Ziel und Fehler außerhalb von AppData bleiben
  Fehler und stehen wie bisher im Protokoll.
- **`build.ps1` scheiterte auf manchen Rechnern schon beim Restore** mit
  `MSB4236 … "Microsoft.NET.SDK.WorkloadAutoImportPropsLocator" wurde nicht gefunden`,
  weil die .NET-Installation dort ihre Workloads nicht auflösen kann. Das Skript
  erkennt genau diesen Fehler und baut ohne Workload-Auflösung weiter. Das Projekt
  braucht keine Workloads, die EXE ist bytegenau dieselbe. Die Einstellung gilt nur
  für diesen Lauf.

---

## [1.1.0] - 2026-11-05

### Hinzugefügt

- **Winget-Bereitschaftsprüfung.** Vor jedem Export und jedem Import wird
  geprüft, ob winget startet *und* seine Paketquelle antwortet. Fehlt eines
  von beidem, sagt das Tool vorher Bescheid und nennt den Weg zur Behebung —
  statt mitten in der Warteschlange abzubrechen. Stehen andere Aktionen an,
  lässt sich der winget-Schritt überspringen, ohne den Rest zu verlieren.
- **AppData optional mitsichern** (Vorgabe: aus). Zwischenspeicher wie
  `Temp`, `INetCache` und die Browser-Caches bleiben dabei ausgenommen. Vor
  dem *Wiederherstellen* kommt eine Rückfrage mit Warnung, weil in AppData
  maschinengebundene Angaben liegen.
- **Cloud-Anbieter optional mitsichern** (Vorgabe: aus), mit Warnung zu
  Laufzeit und möglichen Konflikten — in beide Richtungen.
- **Cloud-Ordner werden selbst erkannt**: OneDrive (auch mit angehängtem
  Firmennamen), Dropbox, Google Drive, iCloud, STRATO HiDrive, Nextcloud,
  ownCloud, MagentaCLOUD, pCloud, MEGA, Box, Tresorit, Seafile, Proton
  Drive, Jottacloud und Sync.com.

### Geändert

- **Avalonia 12.1.5** (bisher 12.1.3) — Avalonia, Avalonia.Desktop,
  Avalonia.Fonts.Inter und Avalonia.Themes.Fluent; dazu
  `Tmds.DBus.Protocol` 0.95.2 (bisher 0.95.1).
- Die Kacheln füllen den verfügbaren Platz der linken Spalte aus. Das
  Benutzerprofil steht über die volle Breite und trägt seine beiden
  Zusatzoptionen untereinander bei sich, bündig mit der rechten
  Kachelspalte. Alles ist beim Start ohne Rollbalken sichtbar — auch dann,
  wenn die Zeile der Speicherprüfung eingeblendet wird.
- Beide Zusatzoptionen wirken auch beim Wiederherstellen: Ohne Haken bleibt
  AppData bzw. der Cloud-Ordner aus dem Rückschreiben heraus, selbst wenn die
  Sicherung ihn enthält.

### Behoben

- **Selbstaktualisierung bei Umlaut im Benutzernamen.** Das Austauschskript
  lag unter `C:\Users\<Name>` und enthielt die Pfade als UTF-8 mit BOM;
  cmd liest Batchdateien aber in der OEM-Codepage. Aus jedem Umlaut wurde
  Zeichensalat, der Austausch scheiterte — in einem Fenster, das nie
  erschien, mit einem `pause`, das ewig wartete. Jetzt: Pfade als
  Argumente, reines ASCII, im Fehlerfall eine Erklärung im Editor und die
  bisherige Fassung startet wieder. Außerdem wartete jedes Update rund
  60 Sekunden, weil die Warteschleife nie vorzeitig ausstieg; jetzt dient
  die Dateisperre als Signal. Unter echtem Windows geprüft.
- **Bilanz und Protokolldatei meldeten Fehlschläge als Erfolg.** Gezählt
  wurden nur Abstürze, nicht die `[FEHLER]`-Zeilen der Aktionen.
- Ein Backup-Ziel wie `C:\Users\Maximilian\Backup` galt beim Profil `Max`
  als „im Quellverzeichnis“ und wurde verweigert.
- Ließ sich ein Browserprofil beim Wiederherstellen nicht beiseitelegen,
  wurde es rekursiv gelöscht. Jetzt bleibt es unverändert, mit Meldung.
- Der Winget-Export löschte die alte Programmliste, bevor feststand, ob
  eine neue entsteht. Die Liste ist jetzt außerdem lesbar (`Notepad++`
  statt `Notepad\u002B\u002B`), und die Import-Bilanz trägt `[WARNUNG]`
  statt `[ERFOLG]`, sobald ein Programm scheitert.
- Die Speicherprüfung verwarf Neuberechnungen, die während einer laufenden
  Berechnung angestoßen wurden, und schätzte beim Wiederherstellen AppData
  und Cloud-Ordner mit ein, obwohl sie ausgespart werden.
- Speicherzeile und Update-Hinweis wechseln die Sprache jetzt sofort mit.
- WLAN-Export/-Import und die Einzelinstallation per winget meldeten
  Erfolg, auch wenn das Werkzeug dahinter scheiterte.
- Das Hintergrundbild wurde aus der ersten statt der passenden Datei
  wiederhergestellt.

---

## [1.0.8] - 2026-10-11

### Geändert

- **Avalonia 12.1.3** (bisher 12.1.2) — Avalonia, Avalonia.Desktop,
  Avalonia.Fonts.Inter und Avalonia.Themes.Fluent.
- Pakete und GitHub Actions halten sich über Dependabot selbst auf Stand;
  alle Workflows auf denselben, neuesten Stand gezogen.

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

[1.2.1]: https://github.com/Tinnitus97/Windows.DaSi.Tool/releases/tag/1.2.1
[1.2.0]: https://github.com/Tinnitus97/Windows.DaSi.Tool/releases/tag/1.2.0
[1.1.0]: https://github.com/Tinnitus97/Windows.DaSi.Tool/releases/tag/1.1.0
[1.0.8]: https://github.com/Tinnitus97/Windows.DaSi.Tool/releases/tag/1.0.8
[1.0.7]: https://github.com/Tinnitus97/Windows.DaSi.Tool/releases/tag/1.0.7
[1.0.6]: https://github.com/Tinnitus97/Windows.DaSi.Tool/releases/tag/1.0.6
[1.0.5]: https://github.com/Tinnitus97/Windows.DaSi.Tool/releases/tag/v1.0.5
[1.0.0]: https://github.com/Tinnitus97/Windows.DaSi.Tool/releases/tag/v1.0.0
