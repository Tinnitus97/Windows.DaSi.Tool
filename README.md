Windows DaSi Tool
Das Windows DaSi Tool ist ein hochperformantes, portables Dienstprogramm zur automatisierten Datensicherung und -wiederherstellung von Benutzerprofilen, Browser-Einstellungen und Systemkonfigurationen. Entwickelt für IT-Administratoren und Power-User, vereint es die Robustheit von Robocopy mit einer modernen, benutzerfreundlichen Oberfläche.

🚀 Features
Portable & Direkt: Keine Installation erforderlich – einfach die .exe ausführen.

Moderne WPF-GUI: Intuitive Benutzeroberfläche im Dark-Mode.

UAC-Unterstützung: Das Tool fordert beim Start automatisch die erforderlichen Administratorrechte via Windows-Benutzerkontensteuerung (UAC) an.

Asynchrone Verarbeitung: Dank PowerShell-Runspaces bleibt die Oberfläche während der Backups stets reaktionsfähig.

Automatisierte Abläufe:

Benutzerprofile: Komplette Sicherung mit intelligentem Ausschluss von Cache- und Temp-Dateien.

Browser-Profile: Automatisierte Sicherung & Wiederherstellung (Firefox, Edge, Chrome, Brave).

E-Mail-Profile: Umfassender Thunderbird-Support inkl. automatischem Versionsabgleich.

System-Tools: Export/Import von Winget-Paketlisten und WLAN-Profilen.

Robustes Back-End: Nutzt Robocopy für zuverlässige Dateioperationen.

🛡 Systemanforderungen
Betriebssystem: Windows 10 oder Windows 11.

Berechtigungen: Das Tool erfordert Administratorrechte. Die UAC-Abfrage erfolgt automatisch beim Programmstart.

Dateisystem: Das Backup-Zielverzeichnis muss zwingend auf einem NTFS-formatierten Laufwerk liegen.

📥 Nutzung
Lade die aktuelle WindowsDaSiTool.exe aus dem Releases-Bereich herunter.

Starte die .exe. Bestätige die erscheinende UAC-Abfrage mit "Ja", um die nötigen Admin-Rechte zu gewähren.

Wähle über die Schaltflächen dein Quell-Verzeichnis (z. B. C:\Users\DeinName) und das Backup-Ziel aus.

Aktiviere die gewünschten Sicherungs- oder Wiederherstellungs-Aufgaben.

Klicke auf "Ausgewählte Aktionen starten" – der Fortschritt wird dir in Echtzeit im Aktivitäts-Protokoll (rechts) angezeigt.

🏗 Technische Details
Technologie: Entwickelt mit PowerShell, WPF und PS2EXE.

Prozess-Management: Anwendungen (Browser/E-Mail) werden vor dem Backup-Start automatisch beendet, um Dateikonflikte zu vermeiden.

Logging: Echtzeit-Protokollierung mit automatischer Speicherbereinigung für optimale Performance.

📜 Lizenz
Dieses Projekt steht unter der MIT-Lizenz.