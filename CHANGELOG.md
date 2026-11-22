# Changelog

Alle nennenswerten Änderungen an diesem Projekt werden hier dokumentiert.

Das Format orientiert sich an [Keep a Changelog](https://keepachangelog.com/de/1.1.0/),
die Versionierung folgt [Semantic Versioning](https://semver.org/lang/de/).

---

## [Unveröffentlicht]

### Neu

- **Taskleisten-Symbole.** Die Option *Symbolpositionen* der Kachel *Desktop-Layout*
  heißt jetzt **Symbole + Taskleiste**. Sie sichert zusätzlich die an die Taskleiste
  angehefteten Symbole: die Verknüpfungen aus `User Pinned\TaskBar` und ihre
  Reihenfolge samt angehefteter Store-Apps (Registry-Schlüssel `Taskband`). Abgelegt
  wird das unter `Desktop-Symbole\Taskleiste\`. Beim Wiederherstellen heftet das Tool
  sie wieder an und startet dafür den Explorer kurz neu, denn er liest die Taskleiste
  nur beim Start. Erst danach setzt es die Desktop-Symbole an ihre Stelle. Windows
  startet die Shell normalerweise selbst wieder. Tut es das nicht, startet das Tool sie.

- **„Design sichern / wiederherstellen“ statt „Hintergrundbild“.** Der Punkt sichert
  weiterhin den Hintergrund (Bild oder Volltonfarbe). Dazu kommen die
  Design-Einstellungen von Windows (*Personalisierung*). Alles liegt in **einem**
  Ordner `Design`: `Design.json` mit Hintergrund und Einstellungen, bei einem Bild
  die Bilddatei daneben. Sicherungen im alten Format (Ordner `Hintergrundbild`)
  lassen sich weiter wiederherstellen. Eine Sicherung im neuen Format können
  Fassungen bis 1.2.6 nicht mehr zurückschreiben. Gesichert werden:
  - *Hell/Dunkel* für Windows und für Apps, *Transparenzeffekte*;
  - *Akzentfarbe*: die Farbe selbst, „automatisch aus dem Hintergrund“ und ob sie
    auf Start, Taskleiste und Titelleisten erscheint;
  - *Mauszeiger*: Schema, Größe und die einzelnen Zeiger. Zeiger aus einem eigenen
    Schema, deren Datei es auf dem neuen Rechner nicht gibt, werden ausgelassen;
  - *Taskleiste*: Ausrichtung, Suche, Taskansicht, Gruppieren, mehrere Bildschirme;
  - *Start*: Layout, zuletzt geöffnete Elemente und Empfehlungen.

  Gesichert wird eine feste Liste, nicht der ganze Registry-Zweig. Daneben liegen
  rechnergebundene Werte (Bildschirmpositionen, Kennungen), und manche Werte schützt
  Windows inzwischen (Widgets-Knopf). Gelesen und gesetzt wird beim angemeldeten
  Benutzer, auch wenn das Tool unter einem anderen Admin-Konto läuft. Ist ein
  anderes Profil gewählt als das des angemeldeten Benutzers, bleibt das Design mit
  einer Fehlermeldung außen vor. Hell/Dunkel und Transparenz gelten sofort, die Akzentfarbe auf
  Titelleisten und Taskleiste teils erst nach dem nächsten Anmelden. Das Protokoll
  fasst zusammen, was gesichert bzw. übernommen wurde, etwa „Windows dunkel, Apps
  dunkel, Transparenz an, Akzentfarbe #0063B1, Taskleiste links“. Ältere Sicherungen
  mit nur dem Hintergrund lassen sich weiter wiederherstellen.

- **Volltonfarbe als Hintergrund.** Ist unter *Personalisierung > Hintergrund*
  statt eines Bildes eine Volltonfarbe eingestellt, sichert das Tool jetzt die Farbe.
  Bisher meldete es nur „kein aktuelles Hintergrundbild gefunden“. Beim
  Wiederherstellen setzt es die Farbe wieder, sofort sichtbar und dauerhaft, und die
  Einstellungen-App zeigt wieder „Volltonfarbe“. Im Protokoll steht die Farbe als
  RGB-Wert und Hexcode, etwa `RGB 0, 99, 177 (#0063B1)`.
- **Hintergrundbild samt Darstellung.** Zum Bild werden jetzt auch *Füllen*,
  *Anpassen*, *Strecken*, *Kacheln* usw. und die Randfarbe gesichert und
  wiederhergestellt. Bei einer Diashow oder Windows-Blickpunkt ohne festes Bild sagt
  das Protokoll das jetzt ausdrücklich.

- **WLAN-Profile nur, wenn der PC WLAN hat.** Ohne WLAN-Adapter (oder mit
  deaktiviertem) ist der Punkt jetzt ausgegraut, wie die übrigen Kacheln, die nichts
  zu tun hätten. Der Tooltip nennt den Grund. Beim Wiederherstellen ist er nur frei,
  wenn die Sicherung WLAN-Profile enthält und dieser PC einen Adapter hat, in den sie
  sich einspielen lassen. Bisher scheiterte `netsh wlan` erst während des Laufs.

### Behoben

- **Drucker wiederherstellen meldete Erfolg, obwohl PrintBrm scheiterte.**
  PrintBrm endet auch bei manchem Fehlschlag mit dem Rückgabewert 0 – etwa bei
  einer unbrauchbaren Sicherungsdatei („The following error occurred:
  0x80070057. The parameter is incorrect.“). Das Tool schrieb dann „[ERFOLG]
  Drucker wiederhergestellt“ ins Protokoll und in die Bilanz. Jetzt zählt auch
  der Fehlercode in der Ausgabe von PrintBrm; das Protokoll nennt ihn samt
  Erklärung. Aufgefallen durch die neue Prüfung des Sicherungsdienstes.

- **Hintergrund nach einem Neustart wieder schwarz bzw. weg.** Nach dem
  Wiederherstellen war der Hintergrund zu sehen. Nach einem Neustart von Windows
  bzw. der nächsten Anmeldung war er schwarz bzw. wieder der alte. Der
  Explorer-Neustart für die Taskleiste ist es nicht: Er liest den Hintergrund
  nicht neu ein, das ist unter Windows geprüft. Ursachen und Abhilfe:
  - **Hauptursache: der Designdienst von Windows.** Er hält das aktuelle Design im
    Speicher und schreibt es beim Neustart des Explorers und beim Abmelden zurück,
    in die Registry und in `Custom.theme`. In einer Windows-11-VM schrieb das Tool
    genau dieselben Werte wie die Einstellungen-App, belegt per Vergleich der
    Registry vorher und nachher. Trotzdem war die Farbe nach dem Explorer-Neustart
    wieder `0 0 0`: Die Einstellungen-App ändert das Design im Dienst, das Tool
    änderte nur die Dateien. Jetzt lässt das Tool **Windows selbst das Design
    anwenden**, wie bei einem Doppelklick auf eine `.theme`-Datei:
    - Aus der aktuellen Design-Datei baut es `DaSi-Wiederherstellung-<Zeitpunkt>.theme`
      mit Hintergrund, Hell/Dunkel, Akzentfarbe und Mauszeigern aus der Sicherung.
      Sie ist danach unter *Personalisierung > Designs* auswählbar. Jede Anwendung
      bekommt einen eigenen Namen, denn ein schon aktives Design wendet Windows nicht
      noch einmal an. Frühere DaSi-Designs räumt das Tool danach weg.
    - Angewendet wird sie als der angemeldete Benutzer: Eine kurzlebige geplante
      Aufgabe startet `explorer.exe` mit der Datei, wie in CLInstall. Das gilt auch,
      wenn das Tool unter einem anderen Konto läuft (Administratorschutz „ADMIN_…“
      oder fremde Anmeldedaten).
    - Bei einer Volltonfarbe bleibt in der Datei ein Bild eingetragen, wie bei der
      Einstellungen-App. Ein leeres oder ein Blickpunkt-Bild deutet Windows 11 als
      Windows-Blickpunkt. Danach schaltet das Tool auf die Farbe um, über dieselbe
      Schnittstelle wie die Einstellungen-App (`IDesktopWallpaper`).
    - Erst danach trägt es die übrigen Einstellungen ein. Hätten Hell/Dunkel vorher
      schon in der Registry gestanden, sähe Windows keinen Wechsel und sagte den
      Fenstern nicht Bescheid.
    - Klappt das Anwenden nicht, setzt das Tool alles wie bisher direkt und sagt im
      Protokoll, dass der alte Hintergrund nach einem Neustart zurückkommen kann.
  - **Explorer-Neustart vor dem Design.** In einer Windows-11-VM (Canary) holt ein
    hart beendeter Explorer beim Start den zuletzt von ihm gespeicherten
    Hintergrund zurück. Das gilt auch für eine über die Einstellungen gewählte
    Farbe. Das Tool beendet den Explorer für die Taskleiste. Beim Wiederherstellen
    kommen *Symbole + Taskleiste* deshalb jetzt vor dem Design dran, danach wird der
    Explorer nicht mehr neu gestartet. Setzt ein Explorer-Neustart den Hintergrund
    trotzdem zurück, wendet das Tool das Design erneut über Windows an, auch ohne
    Design-Wiederherstellung im selben Lauf.
  - **Die Design-Datei von Windows.** Windows führt das aktuelle
    Design zusätzlich in einer Datei, meist `…\AppData\Local\Microsoft\Windows\Themes\Custom.theme`.
    Beim Anmelden übernimmt es Werte daraus wieder in die Registry. In einer
    Windows-11-VM stand dort `Background=0 0 0`: Die wiederhergestellte Volltonfarbe
    war vor dem Neustart da, danach wieder schwarz. Eine über die Einstellungen
    gewählte Farbe blieb erhalten, denn die Einstellungen-App schreibt auch in diese
    Datei. Jetzt trägt das Tool dort ebenfalls ein, was es setzt:
    - beim Hintergrund das Bild samt Darstellung bzw. die Volltonfarbe (das Bild
      bleibt dabei stehen, wie bei der Einstellungen-App), und eine Diashow fällt weg;
    - bei den Design-Einstellungen Hell/Dunkel, Akzentfarbe, „automatisch“ und die
      Mauszeiger, soweit die Datei sie führt.

    Die Kodierung der Datei (meist UTF-16) bleibt erhalten, geschrieben wird über
    eine Zwischendatei. Angefasst wird nur eine Datei im Profil des Benutzers, nie
    ein Windows-Design unter `C:\Windows`.
  - Das Tool trug den Hintergrund in die Registry des Kontos ein, **unter dem es
    läuft**. Gibt jemand bei der Admin-Abfrage die Daten eines anderen Kontos ein,
    ist das nicht der angemeldete Benutzer. Sofort zu sehen war der Hintergrund
    trotzdem, dauerhaft aber nicht. Jetzt schreibt das Tool Bild, Darstellung, Farbe
    und Art direkt beim angemeldeten Benutzer, wie schon die Design-Einstellungen.
    Den Windows-Aufruf nutzt es nur noch für die sofortige Anzeige. Dazu verwirft es
    Windows' Kopie des alten Hintergrunds in diesem Profil, damit Windows bei der
    nächsten Anmeldung das neue Bild einliest. Danach prüft es,
    ob der Hintergrund wirklich eingetragen ist. Ist er es nicht, meldet das
    Protokoll einen Fehler statt „gesetzt“. Läuft das Tool unter einem anderen Konto,
    steht das als Hinweis im Protokoll. Auch das Sichern liest jetzt beim
    angemeldeten Benutzer und nicht beim Admin-Konto.
  - Windows 11 kann sich für jeden virtuellen Desktop einen eigenen Hintergrund
    merken. Solche Einträge gleicht das Tool jetzt an, damit kein alter Hintergrund
    zurückkommt.
  - Zeigte der alte Rechner ein Bild aus Windows' Zwischenspeicher
    (`Themes\TranscodedWallpaper`, `CachedFiles`), schrieb das Tool es dorthin zurück
    und trug es als Hintergrund ein. Diese Dateien überschreibt bzw. löscht Windows
    selbst. Jetzt wird so ein Bild als `Hintergrundbild.jpg` (passende Endung)
    gesichert und unter *Bilder* abgelegt. Ältere Sicherungen werden beim
    Wiederherstellen genauso behandelt. Ist das eingetragene Bild gelöscht, zeigt
    Windows es aber noch, wird jetzt das gezeigte Bild gesichert.
  - Mauszeiger übernimmt Windows beim fremden Konto erst nach der nächsten Anmeldung.
    Bisher lud es dabei die Zeiger des Admin-Kontos.
  - **Kontrolle danach.** Ein paar Sekunden nach dem Setzen und noch einmal nach dem
    Explorer-Neustart für die Taskleiste sieht das Tool nach, ob Registry und
    Desktop noch zeigen, was gesetzt wurde. Dafür bildet es das Desktop-Fenster
    selbst ab, auch unter anderen Fenstern. Hat Windows den Hintergrund geändert,
    steht im Protokoll, was anders ist und was es sein könnte: eine Richtlinie,
    Windows-Blickpunkt oder eine Diashow. Dann setzt das Tool ihn einmal neu. Eine
    Richtlinie, die den Hintergrund festlegt, meldet es schon vorher als Warnung.
    Unter Windows 11 25H2 und Windows Server geprüft: Hier blieben Volltonfarbe
    und Bild auch nach dem Explorer-Neustart stehen.

- **Chrome war nach der Wiederherstellung leer**, obwohl die Sicherung vollständig
  war. Von Hand bei geschlossenem Chrome in den Profilordner kopiert, war alles da.
  Wahrscheinliche Ursache: Chrome lief während des Kopierens. Der Installer startet
  Chrome nach der Installation manchmal von selbst, ein paar Sekunden später. Dann
  legt Chrome ein frisches Profil an und schreibt beim Beenden „Local State“ und
  „Preferences“ über die zurückgeschriebenen. Für alle Browser und Thunderbird gilt
  jetzt:
  - Nach einer frischen Installation wird kurz abgewartet, ob der Installer das
    Programm startet. Danach wird es beendet, und das Tool **wartet, bis es wirklich
    weg ist**, statt pauschal zwei Sekunden.
  - Startet es während des Kopierens trotzdem, wird es beendet und das Profil
    **noch einmal** zurückgeschrieben.
  - Danach prüft das Tool, ob die Dateien, an denen das Profil hängt (`Local State`,
    `Preferences`, `Bookmarks`, `History`, bei Firefox `profiles.ini`, `prefs.js` …),
    so im Ziel stehen wie in der Sicherung. Das Protokoll nennt die
    wiederhergestellten Profile (`Default, Profile 1`) – oder als Fehler die
    abweichenden Dateien.
  - Lässt sich der Browser nicht beenden, wird das Profil nicht zurückgeschrieben,
    sondern eine Meldung samt Abhilfe ausgegeben.
  - Chrome, das nur für den Benutzer installiert ist, gilt als installiert. Bisher
    wurde dann zusätzlich eine zweite Chrome-Installation für alle Benutzer angestoßen.
- **Profil mit AppData zurückschreiben endete weiter mit „Robocopy Fehlercode: 11“.**
  Das Protokoll mit 1.2.6 zeigte vier Gründe:
  - **Das Tool selbst lief vom Desktop des Zielprofils.** In der Sicherung lag eine
    ältere `WindowsDaSiTool.exe` an derselben Stelle. Sie wird jetzt ausgelassen,
    statt am Ende als Fehler 32 zu scheitern.
  - **30 App-Aliase in `AppData\Local\Microsoft\WindowsApps`** (Fehler 1920). Das
    sind Verweise, die Windows für installierte Apps anlegt, keine Dateien. Sie
    bleiben jetzt beim Sichern und beim Zurückschreiben draußen.
  - **Fehler 1224**: eine Datei, die ein laufendes Programm in den Speicher
    eingeblendet hat. Gilt jetzt wie Fehler 32 als „gesperrt“.
  - **Verschränkte Meldungen.** robocopy kopiert mit 32 Threads gleichzeitig, und
    deren Meldungen schieben sich ineinander. Zeilen landeten dadurch im Protokoll
    statt in der Zusammenfassung. Sie werden jetzt der richtigen Datei zugeordnet.
- **Fehlende Desktop-Symbole: Hinweis auf die Herkunft.** Verknüpfungen, die nicht
  im Desktop-Ordner des gesicherten Profils lagen, stammen meist vom öffentlichen
  Desktop für alle Benutzer und erscheinen erst mit ihrem Programm. Das Protokoll
  nennt sie getrennt und rät, *Desktop-Layout wiederherstellen* danach noch einmal
  auszuführen.

### Geändert

- **Prüfungen nur noch unter Windows.** Das Programm läuft ausschließlich unter
  Windows; der zusätzliche Linux-Lauf in `build.yml` prüfte nur dasselbe noch
  einmal und ist entfallen. Alle Prüfungen laufen weiterhin bei jedem Push auf
  dem Windows-Läufer – dort auch die, die echtes Windows brauchen. Das
  Prüfprojekt zielt deshalb auf `net10.0-windows`; die Warnungen CA1416
  („nur unter Windows unterstützt“) sind damit weg, und jede Warnung gilt in
  den Prüfprojekten als Fehler.
- **Jede Funktion hat eine Prüfung.** Neu geprüft werden der Sicherungsdienst
  (jede Aktion der Warteschlange an einem Testprofil mit echtem robocopy, netsh
  und PrintBrm; Design, Desktop-Foto, Symbole und Taskleiste am echten
  Desktop), winget (Bereitschaft, Reparatur, Export, Import – gegen eine
  Attrappe, die antwortet wie winget), Prozessstart, Systemhelfer,
  Leistungsmodus, Update und das Nachinstallieren von winget über einen eigenen
  Webserver. Dazu kommt die Oberfläche (`tests/DaSiGuiTests`): das echte
  Hauptfenster ohne Bildschirm, bedient mit Mausklicks – Pfade, Moduswechsel,
  gesperrte Kacheln, jede Rückfrage, die Warteschlange, Update-Streifen,
  Winget-Auswahl, Schließen mit laufender Aktion – und ein echter Start der
  EXE. Die CI misst mit coverlet, welche Funktionen die Prüfungen ausführen;
  `tools/abdeckung.py` meldet jede, die keine Prüfung erreicht.
- **Toter Code entfernt:** ein „Beenden“-Befehl ohne Knopf, ein zweiter
  Sprachumschalter neben der Auswahlliste, drei nicht angezeigte Beschriftungen
  und eine nicht benutzte Eigenschaft im Sicherungsdienst.

---

## [1.2.6] - 2026-11-18

### Behoben

- **Nach dem Wiederherstellen mit AppData scheiterte jede winget-Installation**
  mit `0x8A150009` („Cannot write to index; it is a higher schema version“) –
  Firefox und alle zehn Programme des Winget-Imports. Ursache: Die
  Profil-Wiederherstellung kopierte den Zustand des App-Installers aus AppData mit,
  samt der Datenbanken einer neueren winget-Fassung vom alten Rechner. Damit kommt
  das ältere winget eines frisch eingerichteten Rechners nicht zurecht. Dieser
  rechnergebundene Zustand bleibt jetzt in beide Richtungen draußen, auch beim
  Zurückschreiben älterer Sicherungen, die ihn noch enthalten. Das gilt ebenso für
  den der Windows-Shell (Suche, Widgets).
- **winget repariert sich, wenn seine Paketquelle kaputt ist.** Scheitert eine
  Installation nicht am Paket, sondern an winget selbst (`0x8A150009`, beschädigter
  Index, beschädigte Quellen), setzt das Tool die Quellen zurück und versucht es
  erneut. Hilft das nicht, aktualisiert es den App-Installer von GitHub. Bleibt
  winget unbrauchbar, bricht der Import mit einer Meldung samt Abhilfe ab, statt
  dieselbe Meldung für jedes Programm zu wiederholen.
- **Die Fehlermeldung von winget ist lesbar.** Statt `ExitCode -1978335223. -` und
  einer Zeile voller Leerzeichen steht jetzt der Wert, wie ihn die winget-Doku nennt
  (`0x8A150009`), dazu eine Erklärung und die eigentliche Meldung. Der
  Fortschrittskreisel und die Balken fallen weg.
- **Browser werden über denselben winget-Weg nachinstalliert** wie beim
  Winget-Import, mit derselben Reparatur. Bisher lief dafür einfach „winget“ aus
  dem Suchpfad. Liegen mehrere App-Installer-Fassungen auf dem Rechner, nimmt das
  Tool die neueste: Sortiert wurde bisher als Text, und „1.9“ stand vor „1.26“.
- **Profil mit AppData zurückschreiben endete mit „Robocopy Fehlercode: 11“.**
  Edge lief im Hintergrund, seine Dateien und die der Windows-Suche waren gesperrt,
  und jede davon stand mit drei Zeilen im Protokoll, 126 Stück. Jetzt werden vor dem
  Zurückschreiben von AppData die Browser und Mailprogramme geschlossen (wie bei den
  einzelnen Browser-Kacheln). Die Grafik-Zwischenspeicher der Browser bleiben
  draußen. Was trotzdem gesperrt ist, fasst das Tool zusammen wie beim Sichern: eine
  Zeile je Fehlerart, die vollständige Liste als
  `AppData-nicht-wiederhergestellt.txt` im Sicherungsordner. Die Aktion gilt dann als
  gelungen.
- **„Dieser PC“ und der Benutzerordner fehlten auf dem neuen Desktop.** Beide sind
  Systemsymbole, die auf einem frisch eingerichteten Rechner ausgeschaltet sind. Lagen
  sie beim Sichern auf dem Desktop, schaltet das Tool sie jetzt ein, wie unter
  *Desktopsymboleinstellungen*, und setzt sie an ihre Stelle. Zugeordnet wird über
  ihre Kennung: Der Benutzerordner heißt nach dem Benutzer und auf dem neuen Rechner
  oft anders („Anwender“ → „Anwender123“).

### Geändert

- **Gebaut wird immer mit der neuesten LTS-Version von .NET** – nicht mit der
  neuesten Version überhaupt. Die Workflows holen sich ihr SDK über die gemeinsame
  Aktion `.github/actions/dotnet-lts` aus der Versionsliste von Microsoft, ohne
  feste Nummer. Derzeit ist das .NET 10; das neuere .NET 11 ist eine STS-Version
  mit kurzem Support und wird übersprungen. Die wöchentliche Prüfung
  (`dotnet-version.yml`) misst ebenfalls an der neuesten LTS-Version statt an der
  neuesten stabilen und wird erst rot, wenn eine neue LTS-Version erscheint
  (voraussichtlich .NET 12 im November 2027) und die Projektdateien nachgezogen
  werden müssen.

---

## [1.2.5] - 2026-11-11

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
- **Dialoge blieben im hellen Modus dunkel.** Meldungen, Rückfragen und die
  Winget-Auswahl hatten fest eingetragene dunkle Farben. Jetzt nutzen sie dieselben
  Theme-Farben wie das Hauptfenster und folgen Hell/Dunkel. Die Knöpfe zeigen dabei
  auch beim Überfahren mit der Maus die passenden Farben.
- **Gesperrte Unterpunkte sahen aus wie freie.** *AppData* und *Cloud-Anbieter* in der
  Profilkachel wurden bei gesperrter Oberfläche nicht blasser dargestellt. Jetzt
  sind sie wie die Kacheln deutlich ausgegraut.

### Geändert

- **Avalonia 12.1.6** (bisher 12.1.5): Avalonia, Avalonia.Desktop,
  Avalonia.Fonts.Inter und Avalonia.Themes.Fluent.
- **Sicherungsordner nur mit Datum:** `DASI_<Rechner>_TT_MM_JJJJ` statt mit Uhrzeit.
- **Eine vorhandene Sicherung wird aktualisiert.** Ist das gewählte Ziel schon eine
  Sicherung (ein `DASI_…`-Ordner oder ein Ordner mit Sicherungsinhalt), geht die
  Sicherung dort hinein, statt daneben einen neuen Ordner anzulegen. Ebenso bei
  einer zweiten Sicherung am selben Tag. Stammt der Ordner erkennbar von einem
  anderen Rechner, fragt das Tool vorher nach.
- **„Desktop-Screenshot“ heißt jetzt „Desktop-Layout“** und steht über die volle
  Breite unter den anderen Kacheln, mit der Auswahl *Screenshot* oder
  *Symbolpositionen*.

### Hinzugefügt

- **winget hilft sich selbst.** Ist winget nicht registriert, registriert das Tool
  den App-Installer wie bisher. Neu: Fehlt winget ganz oder ist es zu alt, lädt das
  Tool den App-Installer (MSIX-Bundle samt Abhängigkeiten) aus dem neuesten Release
  von github.com/microsoft/winget-cli und installiert ihn. Antwortet die Paketquelle
  nicht, setzt es die Quellen zurück (`winget source reset --force`) und lädt sie neu.
  Erst wenn nichts davon hilft, erscheint der Hinweis.
- **Symbolpositionen sichern und zurücksetzen (wie DesktopOK).** Das Tool merkt sich,
  welches Desktop-Symbol wo liegt, und setzt beim Wiederherstellen jedes wieder an
  seine Stelle. Zugeordnet wird über den Namen im Desktop-Ordner, unabhängig von der
  Sprache. Bei anderer Desktop-Größe werden die Stellen im selben Verhältnis
  umgerechnet. *Automatisch anordnen* wird dafür ausgeschaltet, das Raster wie beim
  Sichern eingestellt. Fehlende Symbole nennt das Protokoll. Gespeichert wird
  `Desktop-Symbole\Symbolpositionen.json`.
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

[1.2.6]: https://github.com/Tinnitus97/Windows.DaSi.Tool/releases/tag/1.2.6
[1.2.5]: https://github.com/Tinnitus97/Windows.DaSi.Tool/releases/tag/1.2.5
[1.2.1]: https://github.com/Tinnitus97/Windows.DaSi.Tool/releases/tag/1.2.1
[1.2.0]: https://github.com/Tinnitus97/Windows.DaSi.Tool/releases/tag/1.2.0
[1.1.0]: https://github.com/Tinnitus97/Windows.DaSi.Tool/releases/tag/1.1.0
[1.0.8]: https://github.com/Tinnitus97/Windows.DaSi.Tool/releases/tag/1.0.8
[1.0.7]: https://github.com/Tinnitus97/Windows.DaSi.Tool/releases/tag/1.0.7
[1.0.6]: https://github.com/Tinnitus97/Windows.DaSi.Tool/releases/tag/1.0.6
[1.0.5]: https://github.com/Tinnitus97/Windows.DaSi.Tool/releases/tag/v1.0.5
[1.0.0]: https://github.com/Tinnitus97/Windows.DaSi.Tool/releases/tag/v1.0.0
