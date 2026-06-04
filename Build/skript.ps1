#Requires -Version 5.1
#Requires -RunAsAdministrator

#------------------------------------------------------------------------------------
# Windows DaSi Tool - PowerShell Version (All-in-One Master)
#------------------------------------------------------------------------------------

# --- Globale Einstellungen und Initialisierung ---
$Host.UI.RawUI.WindowTitle = "Windows DaSi Tool"

# Lese die Version dynamisch aus der Versionsdatei (Hardcoded für .exe).
$script:VersionString = "0.6.3" 
$script:BuildString = "05.03.2026"
# Logging ist standardmäßig AUS (FastMode = $true)
$script:FastMode = $true

$script:GlobalSourceUserProfileDir = $null
$script:GlobalBackupBaseDir = $null

# ============================================================================
#                      Online-Versionscheck für Skript
# ============================================================================
function Invoke-UpdateCheck {
    $versionCheckUrl = "https://raw.githubusercontent.com/Tinnitus97/backup_my_windows_Updater/main/newversion.txt"
    $projectUrl = "https://github.com/Tinnitus97/backup_my_windows"
    
    Write-Host "--------------------------------------------------"
    Write-Host "Aktuelle Skript-Version: $($script:VersionString) (Build: $($script:BuildString))"
    Write-Host "Prüfe auf neue Version online..." -NoNewline

    try {
        # Versucht, die Versionsnummer von der URL herunterzuladen.
        $onlineVersion = (Invoke-WebRequest -Uri $versionCheckUrl -UseBasicParsing -ErrorAction Stop).Content.Trim()

        Write-Host " Fertig."
        Write-Host "Online gefundene Version: $onlineVersion"

        # Vergleich der Versionen.
        if ([version]$onlineVersion -gt [version]$script:VersionString) {
            Write-Host ""
            Write-Host "**************************************************" -ForegroundColor Yellow
            Write-Host "Eine neue Version ($onlineVersion) ist verfügbar!" -ForegroundColor Yellow
            Write-Host "**************************************************" -ForegroundColor Yellow
            Write-Host ""
            Write-Host "Was möchten Sie tun?" -ForegroundColor Cyan
            Write-Host " [1] Update-Seite im Browser öffnen (Skript wird danach beendet)"
            Write-Host " [2] Update ablehnen und die aktuelle Version trotzdem starten"
            Write-Host " [3] Vorgang komplett abbrechen"
            Write-Host ""
            
            # Benutzer nach seiner Wahl fragen
            $choice = Read-Host "Ihre Wahl (1, 2 oder 3)"
            
            switch ($choice) {
                '1' {
                    Write-Host "Öffne die Projektseite: $projectUrl"
                    Start-Process $projectUrl
                    Write-Host "Das Skript wird beendet, damit Sie das Update durchführen können."
                    Start-Sleep -Seconds 3
                    exit
                }
                '2' {
                    Write-Host "Update wird ignoriert. Starte das Hauptskript..."
                    Write-Host "--------------------------------------------------"
                    Write-Host ""
                }
                default { # Fängt '3' und alle anderen Eingaben ab
                    Write-Host "Sie haben eine falsche Taste gedrückt, der Vorgang wird nun abgebrochen."
                    Start-Sleep -Seconds 3
                    exit
                }
            }
        } else {
            Write-Host "Sie verwenden die aktuellste Version des Skripts."
            Write-Host "--------------------------------------------------"
            Write-Host ""
        }
    }
    catch {
        # Dieser Block wird bei einem Fehler ausgeführt (z.B. keine Internetverbindung).
        Write-Host ""
        Write-Host "FEHLER: Konnte nicht online nach Updates suchen." -ForegroundColor Red
        Write-Host "Das Skript wird trotzdem normal fortgesetzt."
        Write-Host "--------------------------------------------------"
        Write-Host ""
    }
}

# --- Hilfsfunktionen ---

# Funktion zum Anzeigen eines Ordnerauswahldialogs (Modern UI)
function Select-FolderDialog {
    param (
        [string]$Description
    )
    try {
        Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
        Add-Type -AssemblyName System.Drawing -ErrorAction Stop

        # Wir nutzen den OpenFileDialog für den modernen Look
        $dialog = New-Object System.Windows.Forms.OpenFileDialog
        $dialog.Title = $Description
        $dialog.ValidateNames = $false
        $dialog.CheckFileExists = $false
        $dialog.CheckPathExists = $true
        # Dummy-Dateiname, um die Ordnerauswahl zu erzwingen
        $dialog.FileName = "Ordner_auswählen" 
        $dialog.Filter = "Ordner|\."

        # Erstelle ein unsichtbares Besitzer-Formular (owner form), damit der Dialog im Vordergrund bleibt
        $form = New-Object System.Windows.Forms.Form
        $form.StartPosition = 'CenterScreen'
        $form.Size = [System.Drawing.Size]::new(0, 0)
        $form.ShowInTaskbar = $false
        $form.TopMost = $true
        $form.WindowState = 'Normal'

        $null = $form.Show()
        $null = $form.BringToFront()
        $null = $form.Focus()

        $result = $dialog.ShowDialog($form)
        $form.Dispose()

        if ($result -eq [System.Windows.Forms.DialogResult]::OK) {
            # Da wir technisch gesehen den Datei-Dialog nutzen, müssen wir den Ordnerpfad aus der Eingabe extrahieren
            $selectedPath = [System.IO.Path]::GetDirectoryName($dialog.FileName)
            return [string]$selectedPath
        }
        else {
            return $null
        }
    }
    catch {
        Write-Warning "Fehler beim Initialisieren des Ordnerdialogs: $($_.Exception.Message)"
    }
    return $null
}

# Funktion zum Pausieren und Beenden des Skripts
function Invoke-PauseAndExit {
    param(
        [string]$Message = "[INFO] Aktion abgeschlossen oder Fehler aufgetreten."
    )
    Write-Host ""
    Write-Host $Message -ForegroundColor Yellow
    Write-Host "Druecken Sie eine beliebige Taste, um das Fenster zu schliessen..." -NoNewline
    if ($Host.Name -eq "ConsoleHost") {
        $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown") | Out-Null
    }
    else {
        Read-Host
    }
    exit
}

# --- NEU: Funktion zur Pruefung, ob ein Laufwerk NTFS-formatiert ist ---
function Test-IsNtfsDrive {
    param(
        [string]$Path
    )
    try {
        # Stammverzeichnis (Laufwerksbuchstabe) aus dem Pfad extrahieren
        $driveRoot = [System.IO.Path]::GetPathRoot($Path)
        
        # Netzwerkpfade (UNC) ueberspringen, da dort die lokale Abfrage oft nicht funktioniert
        if ($driveRoot.StartsWith("\")) {
            Write-Host "[INFO] Netzwerkpfad erkannt. Lokale NTFS-Pruefung wird uebersprungen." -ForegroundColor Cyan
            return $true
        }
        
        # Laufwerksinformationen abrufen
        $driveInfo = [System.IO.DriveInfo]::new($driveRoot)
        
        # Pruefen, ob das Format "NTFS" ist (Gross-/Kleinschreibung wird ignoriert)
        if ($driveInfo.DriveFormat -ine "NTFS") {
            return $false
        }
        
        return $true
    }
    catch {
        Write-Warning "Konnte das Dateisystem fuer '$Path' nicht ermitteln: $($_.Exception.Message)"
        # Im Zweifel einen Abbruch erzwingen, wenn das Laufwerk gar nicht lesbar ist
        return $false 
    }
}
# ------------------------------------------------------------------------

# Funktion zur Pruefung und Installation von Anwendungsupdates (Firefox/Thunderbird)
function Invoke-AppUpdateCheckAndInstall {
    param(
        [string]$AppName,
        [string]$ExeName # z.B. "firefox.exe" oder "thunderbird.exe"
    )

    Write-Host "`n--------------------------------------------------------------------------------------------------------------"
    Write-Host "[INFO] Pruefe auf Updates fuer $AppName..."

    # 1. Installierte Version aus der Registry holen
    $installedVersion = $null
    $exePath = $null
    try {
        $appPathEntry = Get-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\$ExeName" -ErrorAction SilentlyContinue
        if ($appPathEntry) {
            $exePath = $appPathEntry.'(Default)'
            if (Test-Path $exePath) {
                $installedVersionString = (Get-Item $exePath).VersionInfo.ProductVersion
                # Version-String wie "115.0.3 (64-bit)" behandeln
                $installedVersion = [version]($installedVersionString.Split(' ')[0])
                Write-Host "[INFO] Installierte $AppName Version: $installedVersion"
            }
        }
        else {
            Write-Host "[WARNUNG] $AppName scheint nicht installiert zu sein oder wurde im Registrierungspfad nicht gefunden. Update-Pruefung wird uebersprungen."
            return # Funktion beenden, wenn nicht gefunden
        }
    }
    catch {
        Write-Warning "Konnte installierte Version von $AppName nicht ermitteln: $($_.Exception.Message). Update-Pruefung wird uebersprungen."
        return
    }

    # 2. Neueste Version von Mozilla abrufen
    $latestVersion = $null
    $productIdentifier = $AppName.ToLower()
    $apiUrl = "https://product-details.mozilla.org/1.0/${productIdentifier}_versions.json"
    try {
        $versionInfo = Invoke-RestMethod -Uri $apiUrl -UseBasicParsing
        if ($productIdentifier -eq "firefox") {
            $latestVersionString = $versionInfo.LATEST_FIREFOX_VERSION
        }
        else {
            # thunderbird
            $latestVersionString = $versionInfo.LATEST_THUNDERBIRD_VERSION
        }
        $latestVersion = [version]$latestVersionString
        Write-Host "[INFO] Neueste verfuegbare $AppName Version: $latestVersion"
    }
    catch {
        Write-Warning "Konnte neueste Version von $AppName nicht von Mozilla abrufen: $($_.Exception.Message). Update-Pruefung wird uebersprungen."
        return
    }

    # 3. Vergleichen und bei Bedarf aktualisieren
    if ($latestVersion -gt $installedVersion) {
        Write-Host "[AKTION] Eine neuere Version von $AppName ($latestVersion) ist verfuegbar. Update wird durchgefuehrt." -ForegroundColor Yellow
        
        # Anwendung vor dem Update schließen
        $processName = $ExeName.Replace(".exe", "")
        Write-Host "[INFO] Der $AppName ($processName) wird nun beendet (falls er laeuft)..."
        Get-Process $processName -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 3

        # 4. Herunterladen
        $downloadUrl = "https://download.mozilla.org/?product=${productIdentifier}-latest-ssl&os=win64&lang=de"
        $installerPath = Join-Path $env:TEMP "${productIdentifier}-installer.exe"
        
        try {
            Write-Host "[INFO] Lade die neueste Version von $AppName herunter..."
            Invoke-WebRequest -Uri $downloadUrl -OutFile $installerPath -UseBasicParsing
            Write-Host "[ERFOLG] Download abgeschlossen." -ForegroundColor Green
        }
        catch {
            Write-Host "[FEHLER] Download des $AppName Installers fehlgeschlagen: $($_.Exception.Message)" -ForegroundColor Red
            if (Test-Path $installerPath) { Remove-Item $installerPath -Force }
            return
        }

        # 5. Stille Installation
        try {
            Write-Host "[INFO] Starte die stille Installation von $AppName. Bitte warten..."
            Start-Process -FilePath $installerPath -ArgumentList "-ms" -Wait -ErrorAction Stop
            Write-Host "[ERFOLG] $AppName wurde erfolgreich aktualisiert." -ForegroundColor Green
        }
        catch {
            Write-Host "[FEHLER] Die Installation von $AppName ist fehlgeschlagen: $($_.Exception.Message)" -ForegroundColor Red
        }
        finally {
            if (Test-Path $installerPath) { Remove-Item $installerPath -Force }
        }
        
    }
    else {
        Write-Host "[INFO] $AppName ist bereits auf der neuesten Version." -ForegroundColor Green
    }
    Write-Host "--------------------------------------------------------------------------------------------------------------`n"
}

# Funktion, die sicherstellt, dass eine App installiert ist
function Install-App {
    param (
        [string]$AppName,
        [string]$ExeName
    )
    
    Write-Host "`n--------------------------------------------------------------------------------------------------------------"
    Write-Host "[INFO] Pruefe, ob $AppName installiert ist..."
    
    # Pruefen, ob der App Path in der Registry existiert
    $appPath = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\$ExeName"
    if (-not (Test-Path $appPath)) {
        Write-Host "[WARNUNG] $AppName scheint nicht installiert zu sein." -ForegroundColor Yellow
        
        # Sonderfall fuer Edge
        if ($AppName -eq "Edge") {
            Write-Host "[INFO] Microsoft Edge ist eine Kernkomponente von Windows und sollte vorhanden sein. Installation wird uebersprungen."
            Write-Host "--------------------------------------------------------------------------------------------------------------`n"
            return
        }

        $confirmation = Read-Host "Soll versucht werden, $AppName jetzt zu installieren? (J/N)"
        if ($confirmation -ne 'j' -and $confirmation -ne 'J') {
            Write-Host "[INFO] Installation uebersprungen. Die Wiederherstellung des Profils koennte fehlschlagen."
            Write-Host "--------------------------------------------------------------------------------------------------------------`n"
            return # Funktion verlassen
        }

        # Winget-ID fuer deutsche Versionen verwenden
        $wingetId = switch ($AppName) {
            "Firefox" { "Mozilla.Firefox.de" }
            "Thunderbird" { "Mozilla.Thunderbird.de" }
            "Chrome" { "Google.Chrome" }
            "Brave" { "Brave.Brave" }
            default { $null }
        }

        if (-not $wingetId) {
            Write-Host "[FEHLER] Keine Winget-ID fuer '$AppName' definiert. Installation kann nicht durchgefuehrt werden." -ForegroundColor Red
            return
        }

        # Installation mit Winget
        try {
            Write-Host "[INFO] Versuche, $AppName (ID: $wingetId) mit Winget zu installieren..."
            winget install --id $wingetId -e --accept-package-agreements --accept-source-agreements
            if ($LASTEXITCODE -eq 0) {
                Write-Host "[ERFOLG] $AppName wurde erfolgreich installiert." -ForegroundColor Green
            }
            else {
                Write-Host "[FEHLER] Winget-Installation fehlgeschlagen mit Fehlercode: $LASTEXITCODE" -ForegroundColor Red
            }
        }
        catch {
            Write-Host "[FEHLER] Fehler bei der Ausfuehrung von Winget: $($_.Exception.Message)" -ForegroundColor Red
        }

    }
    else {
        # App ist bereits installiert.
        Write-Host "[INFO] $AppName ist bereits installiert. Pruefung abgeschlossen."
    }
    Write-Host "--------------------------------------------------------------------------------------------------------------`n"
}


# --- Kernfunktionen (Aktionen) ---

# 1. Windows Benutzerprofil sichern
function Backup-UserProfile {
    Write-Host "`n=============================================================================================================="
    Write-Host "                Windows Benutzerprofil sichern"
    Write-Host "=============================================================================================================="
    Write-Host ""
    
    if (-not $script:GlobalSourceUserProfileDir -or -not $script:GlobalBackupBaseDir) {
        Write-Host "[FEHLER] Globale Pfade fuer Benutzerprofil und Backup-Basisverzeichnis sind nicht gesetzt. Aktion uebersprungen." -ForegroundColor Red
        return $false
    }
    $userDir = $script:GlobalSourceUserProfileDir
    $destParentDir = $script:GlobalBackupBaseDir

    Write-Host "---------Die Sicherung des kompletten Benutzerprofils von '$userDir' wird ausgefuehrt---------" -ForegroundColor Cyan
    Write-Host "---------Ziel-Basisverzeichnis: '$destParentDir' ---------" -ForegroundColor Cyan

    # Pruefen, ob Zielpfad im Benutzerverzeichnis liegt
    try {
        $fullUserDir = (Get-Item -LiteralPath $userDir -ErrorAction Stop).FullName
        $fullDestParentDir = (Get-Item -LiteralPath $destParentDir -ErrorAction Stop).FullName
        $backupTargetDir = Join-Path $fullDestParentDir "Benutzerprofil"

        if ($backupTargetDir.StartsWith($fullUserDir, [System.StringComparison]::OrdinalIgnoreCase)) {
            Write-Host "[FEHLER] Das Backup-Zielverzeichnis '$backupTargetDir' liegt im zu sichernden Benutzerverzeichnis '$fullUserDir'. Sicherung wird abgebrochen..." -ForegroundColor Red
            return $false
        }
        else {
            Write-Host "[INFO] Das Backup-Zielverzeichnis '$backupTargetDir' liegt NICHT im Benutzerverzeichnis. Sicherung wird fortgesetzt..."
        }
    }
    catch {
        Write-Host "[FEHLER] Fehler beim ueberpruefen der Pfade: $($_.Exception.Message)" -ForegroundColor Red
        return $false
    }

    $excludedDirs = @(
        (Join-Path $userDir "AppData")
        (Join-Path $userDir "Anwendungsdaten") 
        (Join-Path $userDir "Application Data") 
        (Join-Path $userDir "Cookies")
        (Join-Path $userDir "Links") 
        (Join-Path $userDir "Favorites") 
        (Join-Path $userDir "Local Settings") 
        (Join-Path $userDir "My Documents") 
        (Join-Path $userDir "NetHood") 
        (Join-Path $userDir "PrintHood") 
        (Join-Path $userDir "Recent") 
        (Join-Path $userDir "Templates") 
        (Join-Path $userDir "Start Menu") 
        (Join-Path $userDir "Druckumgebung") 
        (Join-Path $userDir "Netzwerkumgebung") 
        (Join-Path $userDir "SendTo") 
        (Join-Path $userDir "Vorlagen") 
        (Join-Path $userDir "Lokale Einstellungen") 
        (Join-Path $userDir "Eigene Dateien") 
        (Join-Path $userDir "Dropbox")
        (Join-Path $userDir "HiDrive")
        (Join-Path $userDir "Google Drive")
        (Join-Path $userDir "iCloudDrive")
        (Join-Path $userDir "AppData\Local\Temp")
        (Join-Path $userDir "AppData\Local\Microsoft\Windows\INetCache")
        (Join-Path $userDir "AppData\Local\Google\Chrome\User Data\Default\Cache")
        (Join-Path $userDir "AppData\Local\BraveSoftware\Brave-Browser\User Data\Default\Cache")
    )

    # Dynamische Suche nach OneDrive Ordnern
    $oneDriveFolders = Get-ChildItem -Path $userDir -Filter "OneDrive*" -Directory -ErrorAction SilentlyContinue
    if ($oneDriveFolders) {
        foreach ($folder in $oneDriveFolders) {
            Write-Host "[INFO] Fuege OneDrive-Ordner zum Ausschluss hinzu: $($folder.FullName)"
            $excludedDirs += $folder.FullName
        }
    }

    $robocopyArgs = @(
        "`"$userDir`"",
        "`"$backupTargetDir`"",
        "/MIR", "/ZB", "/SL", "/R:0", "/W:0", "/MT:32", "/XJ", "/XA:SH", "/ETA"
    )
    # Performance-Optimierung
    if ($script:FastMode) {
        $robocopyArgs += "/NP", "/NFL", "/NDL"
    }

    foreach ($exDir in $excludedDirs) {
        if (Test-Path $exDir) {
            $robocopyArgs += "/XD", "`"$exDir`""
        }
    }
    
    Write-Host "[INFO] Starte Robocopy fuer Benutzerprofil-Sicherung. Dies kann einige Zeit dauern..."
    Write-Host "Robocopy Befehl: robocopy $($robocopyArgs -join ' ')"
    & robocopy @robocopyArgs 2>&1 | Out-Host

    if ($LASTEXITCODE -lt 8) {
        Write-Host "[ERFOLG] Benutzerprofil wurde erfolgreich gesichert nach '$backupTargetDir'." -ForegroundColor Green
    }
    else {
        Write-Host "[FEHLER] Fehler beim Sichern des Benutzerprofils. Robocopy Fehlercode: $LASTEXITCODE" -ForegroundColor Red
        return $false
    }

    Write-Host "---------Die Sicherung des kompletten Benutzerprofils wurde ausgefuehrt---------" -ForegroundColor Cyan
    return $true
}

# 7. WLAN Profile exportieren
function Export-WlanProfiles {
    Write-Host "`n=============================================================================================================="
    Write-Host "                WLAN Profile exportieren"
    Write-Host "=============================================================================================================="
    Write-Host ""
    
    if (-not $script:GlobalBackupBaseDir) {
        Write-Host "[FEHLER] Globales Backup-Basisverzeichnis ist nicht gesetzt. Aktion uebersprungen." -ForegroundColor Red
        return $false
    }
    
    $destDir = Join-Path $script:GlobalBackupBaseDir "WLAN-Profile"
    
    # Ordner erstellen, falls nicht vorhanden
    if (-not (Test-Path $destDir)) {
        try {
            New-Item -ItemType Directory -Path $destDir -Force -ErrorAction Stop | Out-Null
        }
        catch {
            Write-Host "[FEHLER] Konnte Verzeichnis '$destDir' nicht erstellen: $($_.Exception.Message)" -ForegroundColor Red
            return $false
        }
    }

    Write-Host "---------Exportiere WLAN Profile nach '$destDir'---------" -ForegroundColor Cyan
    
    try {
        Write-Host "[INFO] Fuehre 'netsh wlan export profile' aus..."
        $output = netsh wlan export profile folder="$destDir" key=clear
        
        $xmlFiles = Get-ChildItem -Path $destDir -Filter "*.xml"
        if ($xmlFiles.Count -gt 0) {
            Write-Host "[ERFOLG] Es wurden $($xmlFiles.Count) WLAN-Profile erfolgreich exportiert." -ForegroundColor Green
        }
        else {
            Write-Host "[WARNUNG] Es wurden keine WLAN-Profile gefunden oder exportiert." -ForegroundColor Yellow
        }
    }
    catch {
        Write-Host "[FEHLER] Fehler beim Exportieren der WLAN Profile: $_" -ForegroundColor Red
        return $false
    }
    
    Write-Host "---------WLAN Profile Export abgeschlossen---------" -ForegroundColor Cyan
    return $true
}

# 14. WLAN Profile importieren
function Import-WlanProfiles {
    Write-Host "`n=============================================================================================================="
    Write-Host "                WLAN Profile importieren"
    Write-Host "=============================================================================================================="
    Write-Host ""
    
    if (-not $script:GlobalBackupBaseDir) {
        Write-Host "[FEHLER] Globales Backup-Basisverzeichnis ist nicht gesetzt. Aktion uebersprungen." -ForegroundColor Red
        return $false
    }

    $srcDir = Join-Path $script:GlobalBackupBaseDir "WLAN-Profile"
    
    if (-not (Test-Path $srcDir)) {
        Write-Host "[FEHLER] Der Ordner '$srcDir' wurde nicht gefunden." -ForegroundColor Red
        return $false
    }

    Write-Host "---------Importiere WLAN Profile aus '$srcDir'---------" -ForegroundColor Cyan
    
    $xmlFiles = Get-ChildItem -Path $srcDir -Filter "*.xml"
    
    if ($xmlFiles.Count -eq 0) {
        Write-Host "[WARNUNG] Keine XML-Dateien im Ordner gefunden." -ForegroundColor Yellow
        return $false
    }

    foreach ($file in $xmlFiles) {
        try {
            Write-Host "[INFO] Importiere Profil: $($file.Name)..."
            netsh wlan add profile filename="$($file.FullName)"
        }
        catch {
            Write-Host "[FEHLER] Konnte $($file.Name) nicht importieren: $_" -ForegroundColor Red
        }
    }

    Write-Host "---------WLAN Profile Import abgeschlossen---------" -ForegroundColor Cyan
    return $true
}

# 8. Windows Benutzerprofil wiederherstellen
function Restore-UserProfile {
    Write-Host "`n=============================================================================================================="
    Write-Host "                Windows Benutzerprofil wiederherstellen"
    Write-Host "=============================================================================================================="
    Write-Host ""

    if (-not $script:GlobalSourceUserProfileDir -or -not $script:GlobalBackupBaseDir) {
        Write-Host "[FEHLER] Globale Pfade fuer Benutzerprofil und Backup-Basisverzeichnis sind nicht gesetzt. Aktion uebersprungen." -ForegroundColor Red
        return $false
    }
    $destDir = $script:GlobalSourceUserProfileDir
    $backupSourceDirParent = $script:GlobalBackupBaseDir
    
    $backupSourceDir = Join-Path $backupSourceDirParent "Benutzerprofil"
    if (-not (Test-Path $backupSourceDir)) {
        Write-Host "[FEHLER] Backup-Ordner '$backupSourceDir' im globalen Backup-Verzeichnis nicht gefunden. Aktion abgebrochen." -ForegroundColor Red
        return $false 
    }

    Write-Host "---------Die Wiederherstellung des kompletten Benutzerprofils von '$backupSourceDir' nach '$destDir' wird ausgefuehrt---------" -ForegroundColor Cyan
    
    Write-Host "[WARNUNG] Diese Aktion ueberschreibt Daten im Zielverzeichnis '$destDir'." -ForegroundColor Yellow

    Write-Host "[INFO] Starte Robocopy fuer Benutzerprofil-Wiederherstellung. Dies kann einige Zeit dauern..."
    $robocopyArgs = @(
        "`"$backupSourceDir`"",
        "`"$destDir`"",
        "/E", "/ZB", "/COPYALL", "/R:1", "/W:1", "/MT:32", "/ETA"
    )
    # Performance-Optimierung
    if ($script:FastMode) {
        $robocopyArgs += "/NP", "/NFL", "/NDL"
    }

    Write-Host "Robocopy Befehl: robocopy $($robocopyArgs -join ' ')"
    & robocopy @robocopyArgs 2>&1 | Out-Host

    if ($LASTEXITCODE -lt 8) {
        Write-Host "[ERFOLG] Das Benutzerprofil wurde erfolgreich wiederhergestellt nach '$destDir'." -ForegroundColor Green
    }
    else {
        Write-Host "[FEHLER] Fehler bei der Wiederherstellung des Benutzerprofils. Robocopy Fehlercode: $LASTEXITCODE" -ForegroundColor Red
        return $false
    }
    Write-Host "---------Die Wiederherstellung des kompletten Benutzerprofils wurde ausgefuehrt---------" -ForegroundColor Cyan
    return $true
}

# Hilfsfunktion fuer Profil-Backups (Firefox, Edge, Chrome, Thunderbird)
function Backup-ApplicationProfile {
    param (
        [string]$AppName,
        [string]$ProfilePathInUserDir, 
        [string]$ProcessName
    )

    if ($AppName -eq "Firefox") {
        Invoke-AppUpdateCheckAndInstall -AppName "Firefox" -ExeName "firefox.exe"
    }
    elseif ($AppName -eq "Thunderbird") {
        Invoke-AppUpdateCheckAndInstall -AppName "Thunderbird" -ExeName "thunderbird.exe"
    }
    
    Write-Host "`n=============================================================================================================="
    Write-Host "                $AppName-Profil sichern"
    Write-Host "=============================================================================================================="
    Write-Host ""

    if (-not $script:GlobalSourceUserProfileDir -or -not $script:GlobalBackupBaseDir) {
        Write-Host "[FEHLER] Globale Pfade fuer Benutzerprofil und Backup-Basisverzeichnis sind nicht gesetzt. Aktion uebersprungen." -ForegroundColor Red
        return $false
    }
    $userDir = $script:GlobalSourceUserProfileDir
    $destParentDir = $script:GlobalBackupBaseDir
    
    Write-Host "---------Die Sicherung von $AppName-Profil aus '$userDir' wird ausgefuehrt---------" -ForegroundColor Cyan
    Write-Host "---------Ziel-Basisverzeichnis: '$destParentDir' ---------" -ForegroundColor Cyan

    $appProfilePath = Join-Path $userDir $ProfilePathInUserDir
    
    if (Test-Path $appProfilePath) {
        Write-Host "[INFO] Der $AppName ($ProcessName) wird nun beendet (falls er laeuft)..."
        Start-Sleep -Seconds 3
        Get-Process $ProcessName -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2

        $targetBackupDir = Join-Path $destParentDir "$AppName-Profil"
        $robocopyArgs = @(
            "`"$appProfilePath`"",
            "`"$targetBackupDir`"",
            "/MIR", "/R:1", "/W:1", "/MT:32", "/ETA" 
        )
        # Performance-Optimierung
        if ($script:FastMode) {
            $robocopyArgs += "/NP", "/NFL", "/NDL"
        }

        Write-Host "Robocopy Befehl: robocopy $($robocopyArgs -join ' ')"
        & robocopy @robocopyArgs 2>&1 | Out-Host

        if ($LASTEXITCODE -lt 8) {
            Write-Host "[ERFOLG] Das $AppName-Profil wurde erfolgreich gesichert nach '$targetBackupDir'." -ForegroundColor Green
        }
        else {
            Write-Host "[FEHLER] Fehler beim Sichern des $AppName-Profils. Robocopy Fehlercode: $LASTEXITCODE" -ForegroundColor Red
            return $false
        }
    }
    else {
        Write-Host "[FEHLER] Das $AppName-Profil wurde unter '$appProfilePath' nicht gefunden." -ForegroundColor Red
        return $false
    }
    Write-Host "---------Die Sicherung von $AppName-Profil wurde ausgefuehrt---------" -ForegroundColor Cyan
    return $true
}

# Hilfsfunktion fuer Profil-Wiederherstellung
function Restore-ApplicationProfile {
    param (
        [string]$AppName,
        [string]$ProfilePathInUserDir, 
        [string]$ProcessName
    )
    
    # 1. Sicherstellen, dass die Anwendung installiert ist
    $exeName = "$ProcessName.exe"
    Install-App -AppName $AppName -ExeName $exeName

    # 2. Update-Check fuer bestimmte Anwendungen
    if ($AppName -eq "Firefox") {
        Invoke-AppUpdateCheckAndInstall -AppName "Firefox" -ExeName "firefox.exe"
    }
    elseif ($AppName -eq "Thunderbird") {
        Invoke-AppUpdateCheckAndInstall -AppName "Thunderbird" -ExeName "thunderbird.exe"
    }

    Write-Host "`n=============================================================================================================="
    Write-Host "                $AppName-Profil wiederherstellen"
    Write-Host "=============================================================================================================="
    Write-Host ""

    if (-not $script:GlobalSourceUserProfileDir -or -not $script:GlobalBackupBaseDir) {
        Write-Host "[FEHLER] Globale Pfade fuer Benutzerprofil und Backup-Basisverzeichnis sind nicht gesetzt. Aktion uebersprungen." -ForegroundColor Red
        return $false
    }
    $targetUserDir = $script:GlobalSourceUserProfileDir
    $backupParentDir = $script:GlobalBackupBaseDir

    $backupSourceDir = Join-Path $backupParentDir "$AppName-Profil"
    if (-not (Test-Path $backupSourceDir)) {
        Write-Host "[FEHLER] Das $AppName Backup-Profil '$backupSourceDir' im globalen Backup-Verzeichnis wurde nicht gefunden." -ForegroundColor Red
        return $false
    }
    
    $targetAppProfileDir = Join-Path $targetUserDir $ProfilePathInUserDir
    Write-Host "---------Die Wiederherstellung von $AppName-Profil von '$backupSourceDir' nach '$targetAppProfileDir' wird ausgefuehrt---------" -ForegroundColor Cyan

    Write-Host "[WARNUNG] Diese Aktion ueberschreibt Daten im Zielverzeichnis '$targetAppProfileDir'." -ForegroundColor Yellow
    
    Write-Host "[INFO] Der $AppName ($ProcessName) wird nun beendet (falls er laeuft)..."
    Start-Sleep -Seconds 3
    Get-Process $ProcessName -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2

    Write-Host "[INFO] Loesche altes $AppName Profilverzeichnis (falls vorhanden): '$targetAppProfileDir'"
    if (Test-Path $targetAppProfileDir) {
        try {
            Remove-Item -Path $targetAppProfileDir -Recurse -Force -ErrorAction Stop
            Write-Host "[INFO] Altes Profilverzeichnis '$targetAppProfileDir' geloescht."
        }
        catch {
            Write-Warning "Konnte das alte Profilverzeichnis '$targetAppProfileDir' nicht vollstaendig loeschen: $($_.Exception.Message). Wiederherstellung koennte fehlschlagen."
        }
        Start-Sleep -Seconds 2
    }
    $parentOfTarget = Split-Path $targetAppProfileDir
    if (-not (Test-Path $parentOfTarget)) {
        New-Item -ItemType Directory -Path $parentOfTarget -Force -ErrorAction SilentlyContinue | Out-Null
    }

    $robocopyArgs = @(
        "`"$backupSourceDir`"",
        "`"$targetAppProfileDir`"",
        "/MIR", "/R:1", "/W:1", "/MT:32", "/ETA"
    )
    # Performance-Optimierung
    if ($script:FastMode) {
        $robocopyArgs += "/NP", "/NFL", "/NDL"
    }

    Write-Host "Robocopy Befehl: robocopy $($robocopyArgs -join ' ')"
    & robocopy @robocopyArgs 2>&1 | Out-Host

    if ($LASTEXITCODE -lt 8) {
        Write-Host "[ERFOLG] Das $AppName-Profil wurde erfolgreich wiederhergestellt nach '$targetAppProfileDir'." -ForegroundColor Green
    }
    else {
        Write-Host "[FEHLER] Fehler bei der Wiederherstellung des $AppName-Profils. Robocopy Fehlercode: $LASTEXITCODE" -ForegroundColor Red
        return $false
    }
    Write-Host "---------Die Wiederherstellung von $AppName-Profil wurde ausgefuehrt---------" -ForegroundColor Cyan
    return $true
}

# 6. Liste von installierten Programmen exportieren (Winget)
function Export-WingetPackages {
    Write-Host "`n=============================================================================================================="
    Write-Host "                Liste von installierten Programmen exportieren"
    Write-Host "=============================================================================================================="
    Write-Host ""
    
    if (-not $script:GlobalBackupBaseDir) {
        Write-Host "[FEHLER] Globales Backup-Basisverzeichnis ist nicht gesetzt. Aktion uebersprungen." -ForegroundColor Red
        return $false
    }
    $destDir = $script:GlobalBackupBaseDir

    Write-Host "---------Eine Liste der Installierten Programme wird nach '$destDir\Winget' exportiert---------" -ForegroundColor Cyan

    $wingetDir = Join-Path $destDir "Winget"
    if (-not (Test-Path $wingetDir)) {
        try {
            New-Item -ItemType Directory -Path $wingetDir -Force -ErrorAction Stop | Out-Null
        }
        catch {
            Write-Host "[FEHLER] Konnte Winget-Verzeichnis '$wingetDir' nicht erstellen: $($_.Exception.Message)" -ForegroundColor Red
            return $false
        }
    }
    $exportFile = Join-Path $wingetDir "Export.json"

    try {
        Write-Host "[INFO] Exportiere Programmliste mit winget nach '$exportFile'..."
        if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
            Write-Host "[FEHLER] winget.exe nicht gefunden oder nicht im PATH. Bitte winget installieren." -ForegroundColor Red
            return $false
        }
        winget export -o "`"$exportFile`"" --accept-source-agreements
        if ($LASTEXITCODE -eq 0) {
            Write-Host "[ERFOLG] Programmliste erfolgreich nach '$exportFile' exportiert." -ForegroundColor Green
        }
        else {
            Write-Host "[FEHLER] Fehler beim Exportieren der Programmliste mit winget. Winget Fehlercode: $LASTEXITCODE" -ForegroundColor Red
            return $false 
        }
    }
    catch {
        Write-Host "[FEHLER] Winget-Befehl fehlgeschlagen: $($_.Exception.Message)" -ForegroundColor Red
        return $false 
    }
    Write-Host "---------Eine Liste der Installierten Programme wurde exportiert---------" -ForegroundColor Cyan
    return $true
}

# 13. Liste von installierten Programmen installieren (Winget)
function Import-WingetPackages {
    Write-Host "`n=============================================================================================================="
    Write-Host "                Liste von installierten Programmen installieren"
    Write-Host "=============================================================================================================="
    Write-Host ""

    if (-not $script:GlobalBackupBaseDir) {
        Write-Host "[FEHLER] Globales Backup-Basisverzeichnis ist nicht gesetzt. Aktion uebersprungen." -ForegroundColor Red
        return $false
    }
    $importFileDir = $script:GlobalBackupBaseDir
    
    Write-Host "---------Die exportierte Liste der Programme aus '$importFileDir\Winget\Export.json' wird installiert---------" -ForegroundColor Cyan
    
    $importFile = Join-Path $importFileDir "Winget\Export.json"
    if (-not (Test-Path $importFile)) {
        $importFileAlt = Join-Path $importFileDir "Export.json"
        if (Test-Path $importFileAlt) {
            $importFile = $importFileAlt
            Write-Warning "Winget Export.json in '$importFileDir\Winget' nicht gefunden, verwende '$importFileAlt'."
        }
        else {
            Write-Host "[FEHLER] Winget Importdatei 'Export.json' nicht im Ordner '$importFileDir\Winget' oder direkt in '$importFileDir' gefunden." -ForegroundColor Red
            return $false 
        }
    }

    try {
        Write-Host "[INFO] Importiere und installiere Programme von '$importFile' mit winget..."
        Write-Host "[WARNUNG] Dies kann einige Zeit dauern und erfordert moeglicherweise Benutzerinteraktion fuer einige Installationen." -ForegroundColor Yellow
        if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
            Write-Host "[FEHLER] winget.exe nicht gefunden oder nicht im PATH. Bitte winget installieren." -ForegroundColor Red
            return $false
        }
        winget import -i "`"$importFile`"" --accept-package-agreements --accept-source-agreements
        if ($LASTEXITCODE -eq 0) { 
            Write-Host "[ERFOLG] Programme von '$importFile' wurden (versucht) zu installieren." -ForegroundColor Green
        }
        else {
            Write-Host "[WARNUNG] Winget Import beendet mit Code $LASTEXITCODE. Einige Installationen koennten fehlgeschlagen sein. Bitte Ausgabe pruefen." -ForegroundColor Yellow
        }
    }
    catch {
        Write-Host "[FEHLER] Winget-Befehl fehlgeschlagen: $($_.Exception.Message)" -ForegroundColor Red
        return $false 
    }
    Write-Host "---------Die exportierte Liste der Programme wurde installiert---------" -ForegroundColor Cyan
    return $true
}


# --- Hauptmenue ---
function Show-MainMenu {
    param(
        [string]$Version,
        [string]$Build
    )
    Clear-Host
    $Host.UI.RawUI.WindowTitle = "Windows DaSi Tool - Hauptmenue"
    Write-Host "======================================================================================="
    Write-Host "                               Windows DaSi Tool - Hauptmenue"
    Write-Host "======================================================================================="
    Write-Host "Windows DaSi Tool Version $Version von $Build"
    Write-Host ""
    Write-Host "Aktuell ausgewaehltes Quell-Benutzerprofil: $($script:GlobalSourceUserProfileDir)"
    Write-Host "Aktuell ausgewaehltes Backup-Basisverzeichnis: $($script:GlobalBackupBaseDir)"
    Write-Host "---------------------------------------------------------------------------------------"
    Write-Host "   [P] Pfade neu auswaehlen (Benutzerprofil und Backup-Basisverzeichnis)"
    Write-Host "   [L] Logging umschalten     (Aktuell: $(if($script:FastMode){'Minimal / AUS'}else{'Detailliert / AN'}))"
    Write-Host "---------------------------------------------------------------------------------------"
    Write-Host "   --- SICHERN ---                                     --- WIEDERHERSTELLEN ---"
    Write-Host "   [1] Windows Benutzerprofil sichern                  [9]  Windows Benutzerprofil wiederherstellen"
    Write-Host "   [2] Firefox-Profil sichern                          [10] Firefox-Profil wiederherstellen"
    Write-Host "   [3] Edge-Profil sichern                             [11] Edge-Profil wiederherstellen"
    Write-Host "   [4] Chrome-Profil sichern                           [12] Chrome-Profil wiederherstellen"
    Write-Host "   [5] Brave-Profil sichern                            [13] Brave-Profil wiederherstellen"
    Write-Host "   [6] Thunderbird-Profil sichern                      [14] Thunderbird-Profil wiederherstellen"
    Write-Host "   [7] Liste installierter Programme exportieren       [15] Liste installierter Programme installieren"
    Write-Host "   [8] WLAN Profile exportieren                        [16] WLAN Profile importieren"
    Write-Host ""
    Write-Host "   [Q] Beenden"
    Write-Host "======================================================================================="
    Write-Host "Geben Sie eine oder mehrere Zahlen/Buchstaben kommagetrennt ein (z.B. 1,2,7) oder 'Q' zum Beenden."
}

# --- Globale Pfadauswahl mit NTFS-Pruefung fuer Backup-Drive ---
function Select-GlobalPaths {
    Write-Host "`n--- Globale Pfadauswahl ---" -ForegroundColor Green
    
    # 1. Quell-Benutzerprofil auswaehlen
    $tempSourceProfile = Select-FolderDialog -Description "Bitte waehlen Sie das lokale Benutzerprofil aus (C:\Users\Name), das gesichert oder wiederhergestellt werden soll."
    if (-not $tempSourceProfile) {
        Write-Host "[FEHLER] Kein Quell-Benutzerprofilordner ausgewaehlt. Das Skript kann nicht fortfahren." -ForegroundColor Red
        Invoke-PauseAndExit
    }
    $script:GlobalSourceUserProfileDir = $tempSourceProfile

    # 2. Backup-Basisverzeichnis auswaehlen
    $tempBackupBase = Select-FolderDialog -Description "Bitte waehlen Sie den Speicherort aus, an den die Daten gesichert oder von dem sie wiederhergestellt werden sollen."
    if (-not $tempBackupBase) {
        Write-Host "[FEHLER] Kein Backup-Basisverzeichnis ausgewaehlt. Das Skript kann nicht fortfahren." -ForegroundColor Red
        Invoke-PauseAndExit
    }

    # Pruefung auf NTFS-Formatierung des Backup-Laufwerks
    Write-Host "[INFO] Pruefe Dateisystem des Backup-Laufwerks..." -ForegroundColor Cyan
    if (-not (Test-IsNtfsDrive -Path $tempBackupBase)) {
        Write-Host ""
        Write-Host "==========================================================================================" -ForegroundColor Red
        Write-Host "[FEHLER] Das ausgewaehlte Backup-Laufwerk ist NICHT mit NTFS formatiert!" -ForegroundColor Red
        Write-Host "Robocopy benoetigt zwingend ein NTFS-Dateisystem, um Dateiberechtigungen (ACLs)" -ForegroundColor Red
        Write-Host "und Dateien, die groesser als 4 GB sind, korrekt sichern zu koennen." -ForegroundColor Red
        Write-Host "Bitte formatieren Sie den Datentraeger in NTFS oder waehlen Sie einen anderen." -ForegroundColor Red
        Write-Host "==========================================================================================" -ForegroundColor Red
        Invoke-PauseAndExit
    }

    # Die Abfrage fuer den Performance-Modus wurde hier entfernt!

    $script:GlobalBackupBaseDir = $tempBackupBase
    Write-Host "[INFO] Quell-Benutzerprofil gesetzt auf: $($script:GlobalSourceUserProfileDir)" -ForegroundColor Green
    Write-Host "[INFO] Backup-Basisverzeichnis gesetzt auf: $($script:GlobalBackupBaseDir)" -ForegroundColor Green
    Write-Host "[ERFOLG] Dateisystem-Pruefung bestanden (NTFS)." -ForegroundColor Green
    Write-Host "-----------------------------------"
    Start-Sleep -Seconds 2
}
# ------------------------------------------------------------------------------


# --- Skriptablauf ---

# 1. Update-Check vor allem anderen ausfuehren
Invoke-UpdateCheck

# Globale Pfade einmalig abfragen
Select-GlobalPaths

# Hauptschleife fuer Menue
do {
    Show-MainMenu -Version $script:VersionString -Build $script:BuildString
    $choicesString = Read-Host "Ihre Auswahl"
    $choicesArray = $choicesString.Trim() -split ',' | ForEach-Object { $_.Trim().ToUpper() }

    # Ueberpruefen, ob Beenden gedrueckt wurde
    if ($choicesArray -contains "Q") {
        Write-Host "Skript wird auf Wunsch beendet."
        Start-Sleep -Seconds 2
        exit
    }
    
    # Ueberpruefen, ob Pfade neu gewaehlt werden sollen
    if ($choicesArray -contains "P") {
        Select-GlobalPaths
        continue 
    }

    # Ueberpruefen, ob das Logging umgeschaltet werden soll
    if ($choicesArray -contains "L") {
        $script:FastMode = -not $script:FastMode
        Write-Host "`n[INFO] Logging-Modus umgeschaltet! " -ForegroundColor Cyan -NoNewline
        if ($script:FastMode) { Write-Host "Minimales Logging ist jetzt AKTIVIERT." -ForegroundColor Yellow }
        else { Write-Host "Detailliertes Logging ist jetzt AKTIVIERT." -ForegroundColor Yellow }
        Start-Sleep -Seconds 2
        continue
    }

    $validActionChosenOrAttempted = $false
    $numberOfChoices = $choicesArray.Count
    $currentChoiceIndex = 0

    foreach ($choice in $choicesArray) {
        # P, Q und L überspringen wir in der Schleife, da sie schon oben behandelt wurden
        if ($choice -eq "P" -or $choice -eq "Q" -or $choice -eq "L") { continue } 

        $currentChoiceIndex++
        $actionInvokedAndSuccessful = $false
        $actionAttempted = $true 

        $actionNames = @{
            "1"  = "Windows Benutzerprofil sichern"
            "2"  = "Firefox-Profil sichern"
            "3"  = "Edge-Profil sichern"
            "4"  = "Chrome-Profil sichern"
            "5"  = "Brave-Profil sichern"
            "6"  = "Thunderbird-Profil sichern"
            "7"  = "Installierte Programme exportieren (Winget)"
            "8"  = "WLAN Profile exportieren"
            "9"  = "Windows Benutzerprofil wiederherstellen"
            "10" = "Firefox-Profil wiederherstellen"
            "11" = "Edge-Profil wiederherstellen"
            "12" = "Chrome-Profil wiederherstellen"
            "13" = "Brave-Profil wiederherstellen"
            "14" = "Thunderbird-Profil wiederherstellen"
            "15" = "Installierte Programme installieren (Winget)"
            "16" = "WLAN Profile importieren"
        }

        if ($actionNames.ContainsKey($choice)) {
            $displayText = $actionNames[$choice]
        }
        else {
            $displayText = "Unbekannte Auswahl ($choice)"
        }

        Write-Host "`n=============================================================================================================="
        Write-Host " Verarbeitung: '$displayText' (Aktion $currentChoiceIndex von $numberOfChoices)" -ForegroundColor Yellow
        Write-Host "=============================================================================================================="
        Start-Sleep -Seconds 1

        switch ($choice) {
            "1" { $actionInvokedAndSuccessful = Backup-UserProfile }
            "2" { $actionInvokedAndSuccessful = Backup-ApplicationProfile -AppName "Firefox" -ProfilePathInUserDir "AppData\Roaming\Mozilla\Firefox" -ProcessName "firefox" }
            "3" { $actionInvokedAndSuccessful = Backup-ApplicationProfile -AppName "Edge" -ProfilePathInUserDir "AppData\Local\Microsoft\Edge\User Data\Default" -ProcessName "msedge" }
            "4" { $actionInvokedAndSuccessful = Backup-ApplicationProfile -AppName "Chrome" -ProfilePathInUserDir "AppData\Local\Google\Chrome\User Data\Default" -ProcessName "chrome" }
            "5" { $actionInvokedAndSuccessful = Backup-ApplicationProfile -AppName "Brave" -ProfilePathInUserDir "AppData\Local\BraveSoftware\Brave-Browser\User Data\Default" -ProcessName "brave" }
            "6" { $actionInvokedAndSuccessful = Backup-ApplicationProfile -AppName "Thunderbird" -ProfilePathInUserDir "AppData\Roaming\Thunderbird" -ProcessName "thunderbird" }
            "7" { $actionInvokedAndSuccessful = Export-WingetPackages }
            "8" { $actionInvokedAndSuccessful = Export-WlanProfiles }

            "9" { $actionInvokedAndSuccessful = Restore-UserProfile }
            "10" { $actionInvokedAndSuccessful = Restore-ApplicationProfile -AppName "Firefox" -ProfilePathInUserDir "AppData\Roaming\Mozilla\Firefox" -ProcessName "firefox" }
            "11" { $actionInvokedAndSuccessful = Restore-ApplicationProfile -AppName "Edge" -ProfilePathInUserDir "AppData\Local\Microsoft\Edge\User Data\Default" -ProcessName "msedge" }
            "12" { $actionInvokedAndSuccessful = Restore-ApplicationProfile -AppName "Chrome" -ProfilePathInUserDir "AppData\Local\Google\Chrome\User Data\Default" -ProcessName "chrome" }
            "13" { $actionInvokedAndSuccessful = Restore-ApplicationProfile -AppName "Brave" -ProfilePathInUserDir "AppData\Local\BraveSoftware\Brave-Browser\User Data\Default" -ProcessName "brave" }
            "14" { $actionInvokedAndSuccessful = Restore-ApplicationProfile -AppName "Thunderbird" -ProfilePathInUserDir "AppData\Roaming\Thunderbird" -ProcessName "thunderbird" }
            "15" { $actionInvokedAndSuccessful = Import-WingetPackages }
            "16" { $actionInvokedAndSuccessful = Import-WlanProfiles }

            default {
                Write-Host "[FEHLER] Ungueltige Auswahl: '$choice'. Diese Auswahl wird uebersprungen." -ForegroundColor Red
                $actionAttempted = $false
            }
        }

        if ($actionAttempted) {
            $validActionChosenOrAttempted = $true
            if ($actionInvokedAndSuccessful) {
                Write-Host "[ERFOLG] Aktion '$displayText' erfolgreich abgeschlossen." -ForegroundColor Green
            }
            else {
                Write-Host "[WARNUNG] Aktion '$displayText' wurde NICHT erfolgreich ausgefuehrt oder vom Benutzer abgebrochen." -ForegroundColor Yellow
            }
        }
        
        if ($currentChoiceIndex -lt $numberOfChoices) { 
            if ($actionAttempted) {
                Write-Host "`nNaechste Aktion in 10 Sekunden..."
                Start-Sleep -Seconds 10
            }
            else {
                Write-Host "`nUngueltige Auswahl '$choice' uebersprungen. Fahre mit naechster Auswahl fort (falls vorhanden)..."
                Start-Sleep -Seconds 2 
            }
        }
    } 

    if ($validActionChosenOrAttempted) {
        Write-Host "`nAlle ausgewaehlten Aktionen wurden abgearbeitet. Kehre zum Hauptmenue zurueck oder 'Q' zum Beenden." -ForegroundColor Green
        Write-Host "Druecken Sie eine Taste, um zum Menue zurueckzukehren..." -NoNewline
        if ($Host.Name -eq "ConsoleHost") { $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown") | Out-Null } else { Read-Host | Out-Null }
    }
    elseif ($choicesArray.Count -gt 0 -and -not ($choicesArray -contains "P")) { 
        Write-Host "`nKeine gueltigen Aktionen zur Ausfuehrung ausgewaehlt oder alle Eingaben waren ungueltig." -ForegroundColor Yellow
        Write-Host "Druecken Sie eine Taste, um zum Menue zurueckzukehren..." -NoNewline
        if ($Host.Name -eq "ConsoleHost") { $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown") | Out-Null } else { Read-Host | Out-Null }
    }

} while ($true)

Write-Host "Das Skript wird in 5 Sekunden geschlossen."
Start-Sleep -Seconds 5