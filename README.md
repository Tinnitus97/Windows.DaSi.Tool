# Windows DaSi Tool

> Hochperformantes, portables Dienstprogramm zur automatisierten Datensicherung und -wiederherstellung von Benutzerprofilen, Browser-Einstellungen und Systemkonfigurationen.

Entwickelt für IT-Administratoren und Power-User – vereint die Robustheit von **Robocopy** mit einer modernen, benutzerfreundlichen Oberfläche.

<img width="1991" height="1394" alt="image" src="https://github.com/user-attachments/assets/7aad6576-1391-4665-9c35-122e4a0a87bb" />


---

## 🚀 Features

| Feature | Beschreibung |
|---|---|
| **Portabel & direkt** | Keine Installation erforderlich – einfach die `.exe` ausführen |
| **Moderne WPF-GUI** | Intuitive Benutzeroberfläche im Dark-Mode |
| **UAC-Unterstützung** | Automatische Anforderung von Administratorrechten beim Programmstart |
| **Asynchrone Verarbeitung** | PowerShell-Runspaces halten die Oberfläche während Backups reaktionsfähig |
| **Robustes Back-End** | Robocopy für zuverlässige und performante Dateioperationen |

### Automatisierte Sicherungs- & Wiederherstellungsabläufe

- **Benutzerprofile** – Vollständige Sicherung mit intelligentem Ausschluss von Cache- und Temp-Dateien
- **Browser-Profile** – Automatisierte Sicherung & Wiederherstellung für Firefox, Edge, Chrome und Brave
- **E-Mail-Profile** – Umfassender Thunderbird-Support inkl. automatischem Versionsabgleich
- **System-Tools** – Export und Import von Winget-Paketlisten und WLAN-Profilen

---

## 🛡️ Systemanforderungen

| Anforderung | Details |
|---|---|
| **Betriebssystem** | Windows 10 oder Windows 11 |
| **Berechtigungen** | Administratorrechte (UAC-Abfrage erfolgt automatisch beim Start) |
| **Dateisystem** | Backup-Zielverzeichnis muss auf einem **NTFS**-formatierten Laufwerk liegen |

---

## 📥 Nutzung

1. Lade die aktuelle `WindowsDaSiTool.exe` aus dem [Releases-Bereich](../../releases) herunter.
2. Starte die `.exe` und bestätige die UAC-Abfrage mit **„Ja"**, um Administratorrechte zu gewähren.
3. Wähle über die Schaltflächen dein **Quellverzeichnis** (z. B. `C:\Users\DeinName`) und das **Backup-Ziel** aus.
4. Aktiviere die gewünschten Sicherungs- oder Wiederherstellungsaufgaben.
5. Klicke auf **„Ausgewählte Aktionen starten"** – der Fortschritt wird in Echtzeit im Aktivitätsprotokoll angezeigt.

---

## 🏗️ Technische Details

- **Technologie:** PowerShell, WPF, PS2EXE
- **Prozess-Management:** Browser- und E-Mail-Anwendungen werden vor dem Backup automatisch beendet, um Dateikonflikte zu vermeiden.
- **Logging:** Echtzeit-Protokollierung mit automatischer Speicherbereinigung für optimale Performance.

---

## 📜 Lizenz

Dieses Projekt steht unter der [MIT-Lizenz](LICENSE).
