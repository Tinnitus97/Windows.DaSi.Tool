Windows DaSi Tool
Das Windows DaSi Tool (Datensicherung) ist ein kompaktes, leistungsstarkes Utility für die automatisierte Sicherung und Wiederherstellung von Benutzerdaten und Systemkonfigurationen. Es wurde als portable .exe entwickelt, um IT-Administratoren und Power-Usern eine schnelle, zuverlässige Lösung ohne aufwendige Installation zu bieten.

🚀 Features
Portable Executable: Keine Abhängigkeiten, einfach starten und loslegen.

Moderne GUI: Benutzerfreundliche Oberfläche basierend auf WPF im dunklen Design.

Asynchrone Ausführung: Die GUI bleibt während der Sicherungsvorgänge voll bedienbar.

Umfassende Sicherungs-Optionen:

Vollständige Benutzerprofile (mit intelligentem Ausschluss von Cache- und temporären Dateien).

Browser-Profile (Firefox, Edge, Chrome, Brave).

E-Mail-Profile (Thunderbird).

System-Tools: Export/Import von Winget-Programmlisten und WLAN-Profilen.

Robuste Technik: Basiert auf Robocopy für maximale Zuverlässigkeit bei Dateioperationen.

🛠 Voraussetzungen
Betriebssystem: Windows 10 oder Windows 11.

Berechtigungen: Muss mit Administratorrechten ausgeführt werden (erforderlich für den Zugriff auf Benutzerprofile und Systemkonfigurationen).

Dateisystem: Das Backup-Zielverzeichnis muss zwingend auf einem NTFS-formatierten Laufwerk liegen.

📥 Nutzung
Lade die aktuelle WindowsDaSiTool.exe aus dem Repository herunter.

Führe die Datei per Rechtsklick "Als Administrator ausführen" aus.

Wähle über die Schaltflächen das Quell-Verzeichnis (z. B. C:\Users\DeinName) und das Backup-Ziel aus.

Wähle die gewünschten Aktionen (Sichern oder Wiederherstellen) in der Liste aus.

Klicke auf "Ausgewählte Aktionen starten". Das Aktivitäts-Protokoll informiert dich in Echtzeit über den Fortschritt.

🏗 Technische Details
Das Tool wurde mittels PowerShell und der WPF-Engine entwickelt und anschließend mit PS2EXE in eine eigenständige ausführbare Datei konvertiert.

Threading: Die Sicherungslogik läuft in isolierten Runspaces, um ein Einfrieren der Oberfläche zu verhindern.

Logging: Echtzeit-Protokollierung mit integrierter Speicherverwaltung für optimale Performance.

📜 Lizenz
Dieses Projekt steht unter der MIT-Lizenz.