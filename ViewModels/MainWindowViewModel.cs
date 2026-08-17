using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindowsDaSiTool.Services;
using WindowsDaSiTool.Views;

namespace WindowsDaSiTool.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    // ---- Konstanten (entsprechen den $script:-Variablen) ----
    private const string VersionString = "1.0.7";
    private const string ProjectUrl = "https://github.com/Tinnitus97/Windows.DaSi.Tool";

    private readonly CancellationTokenState _cancel = new();
    private readonly ProcessRunner _runner;
    private readonly BackupService _backup;
    private readonly WingetService _winget;

    private readonly object _logLock = new();
    private readonly System.Text.StringBuilder _logBuffer = new();
    private bool _logUntouched = true;   // true, solange nur der Begruessungstext im Log steht

    private static string InitialLogText()
        => Tr("Warte auf Eingabe...\r\nBitte wähle zuerst die benötigten Pfade.\r\n",
              "Waiting for input...\r\nPlease select the required paths first.\r\n");

    public string WindowTitle => $"Windows DaSi Tool {VersionString}";

    // ---- gebundener Zustand ----
    [ObservableProperty] private string _logText = "";
    [ObservableProperty] private bool _uiEnabled = true;

    /// <summary>
    /// Laeuft gerade eine Aktion aus der Warteschlange? Bewusst getrennt von
    /// <see cref="UiEnabled"/>: Die Oberflaeche wird auch aus anderen Gruenden
    /// gesperrt - etwa waehrend des Selbstupdates. Nur diese Eigenschaft darf
    /// darueber entscheiden, ob beim Schliessen nach einem harten Abbruch
    /// gefragt wird. Sonst fragt das Fenster nach einem Abbruch, obwohl gar
    /// nichts laeuft.
    /// </summary>
    [ObservableProperty] private bool _isActionRunning;
    [ObservableProperty] private string _sourcePath = "";
    [ObservableProperty] private string _backupPath = "";

    // ---- Schnellauswahl Benutzerprofil ----
    public System.Collections.ObjectModel.ObservableCollection<string> UserProfiles { get; } = new();

    [ObservableProperty] private string? _selectedProfile;

    partial void OnSelectedProfileChanged(string? value)
    {
        if (!string.IsNullOrEmpty(value)) SourcePath = value;
    }

    // Haelt die Schnellauswahl mit einem manuell gewaehlten Pfad synchron.
    partial void OnSourcePathChanged(string value)
    {
        if (!string.IsNullOrEmpty(value) && UserProfiles.Contains(value) && SelectedProfile != value)
            SelectedProfile = value;
        ScheduleAutoStorageCheck();
    }

    // ---- Speicherueberwachung ----
    [ObservableProperty] private string _storageInfo = "";
    [ObservableProperty] private bool _storageInfoVisible;
    // Ampel: 0 = neutral/grau, 1 = gruen (passt), 2 = gelb (knapp), 3 = rot (reicht nicht)
    [ObservableProperty] private int _storageStatus;
    [ObservableProperty] private bool _storageChecking;

    // ---- Fortschrittsanzeige ----
    [ObservableProperty] private bool _progressVisible;
    [ObservableProperty] private string _progressText = "";
    private string _currentActionText = "";

    public string BtnCheckStorage => Tr("Speicher prüfen", "Check storage");

    // Farbe der Statuszeile passend zur Ampel.
    public Avalonia.Media.IBrush StorageBrush => StorageStatus switch
    {
        1 => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#40C057")), // gruen
        2 => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#F1A100")), // gelb/orange
        3 => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#E03131")), // rot
        _ => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#9AA0A6")), // grau
    };

    partial void OnStorageStatusChanged(int value) => OnPropertyChanged(nameof(StorageBrush));

    // Automatische Groessenberechnung (abschaltbar).
    [ObservableProperty] private bool _autoStorageCheck = true;
    public string AutoStorageLabel => AutoStorageCheck
        ? Tr("Auto-Berechnung: AN", "Auto-calc: ON")
        : Tr("Auto-Berechnung: AUS", "Auto-calc: OFF");

    partial void OnAutoStorageCheckChanged(bool value)
    {
        OnPropertyChanged(nameof(AutoStorageLabel));
        if (value) ScheduleAutoStorageCheck();
    }

    [RelayCommand]
    private void ToggleAutoStorage() => AutoStorageCheck = !AutoStorageCheck;

    private System.Threading.CancellationTokenSource? _autoStorageCts;

    /// <summary>
    /// Stoesst die automatische Berechnung verzoegert an (Debounce), damit nicht
    /// bei jedem Klick sofort der ganze Ordnerbaum durchlaufen wird. Zeigt kein
    /// Popup - nur die farbige Info-Zeile.
    /// </summary>
    private void ScheduleAutoStorageCheck()
    {
        if (!AutoStorageCheck) return;
        if (string.IsNullOrWhiteSpace(BackupPath)) return;

        _autoStorageCts?.Cancel();
        var cts = new System.Threading.CancellationTokenSource();
        _autoStorageCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(900, cts.Token); // kurz warten, bis die Auswahl "steht"
                if (cts.Token.IsCancellationRequested) return;
                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    if (!cts.Token.IsCancellationRequested)
                        await RunStorageCheck(showPopup: false);
                });
            }
            catch (TaskCanceledException) { /* neuer Trigger kam dazwischen */ }
        });
    }

    partial void OnBackupPathChanged(string value)
    {
        // Freien Platz am Ziellaufwerk sofort anzeigen (leichtgewichtig).
        if (string.IsNullOrWhiteSpace(value))
        {
            StorageInfoVisible = false;
            StorageInfo = "";
            StorageStatus = 0;
            return;
        }
        var free = SystemHelpers.GetFreeSpace(value);
        if (free < 0)
        {
            StorageInfo = Tr("Freier Speicher am Ziel: nicht ermittelbar",
                             "Free space on target: unavailable");
            StorageStatus = 0;
        }
        else
        {
            StorageInfo = Tr($"Frei am Ziel: {SystemHelpers.FormatBytes(free)}  -  für Größenschätzung 'Speicher prüfen'",
                             $"Free on target: {SystemHelpers.FormatBytes(free)}  -  use 'Check storage' for a size estimate");
            StorageStatus = 0;
        }
        StorageInfoVisible = true;
        ScheduleAutoStorageCheck();
    }

    [ObservableProperty] private bool _detailedLogging;
    [ObservableProperty] private string _loggingLabel = "Log: aus";

    [ObservableProperty] private bool _updateAvailable;
    [ObservableProperty] private string _updateBannerText = "";

    // ---- Sprache ----
    // IsEnglish steuert den Umschalter; true = Englisch.
    [ObservableProperty] private bool _isEnglish;

    // Auswahl im Sprach-Dropdown: 0 = Deutsch, 1 = English.
    public System.Collections.Generic.List<string> Languages { get; } = new() { "Deutsch", "English" };

    [ObservableProperty] private int _selectedLanguageIndex;

    partial void OnSelectedLanguageIndexChanged(int value)
    {
        IsEnglish = value == 1;
    }

    partial void OnIsEnglishChanged(bool value)
    {
        Localizer.I.Lang = value ? AppLang.En : AppLang.De;
        if (SelectedLanguageIndex != (value ? 1 : 0)) SelectedLanguageIndex = value ? 1 : 0;
        // Alle lokalisierten Beschriftungen neu auswerten lassen.
        OnPropertyChanged(string.Empty);
        // Dynamische Labels ebenfalls aktualisieren.
        LoggingLabel = Tr(DetailedLogging ? "Log: an" : "Log: aus",
                          DetailedLogging ? "Log: on" : "Log: off");

        // Begruessungstext mitschalten, solange noch keine echten Zeilen im Log stehen.
        lock (_logLock)
        {
            if (_logUntouched)
            {
                _logBuffer.Clear();
                _logBuffer.Append(InitialLogText());
                var snapshot = _logBuffer.ToString();
                Dispatcher.UIThread.Post(() => LogText = snapshot);
            }
        }
    }

    private static string Tr(string de, string en) => Loc.Tr(de, en);

    // ---- Lokalisierte UI-Beschriftungen ----
    public string LangLabel        => Tr("Sprache: Deutsch", "Language: English");
    public string SectionOptions   => Tr("Optionen & Verzeichnisse", "Options & directories");
    public string LabelUserProfile => Tr("Benutzerprofil:", "User profile:");
    public string LabelQuickSelect => Tr("Schnellauswahl:", "Quick select:");
    public string QuickSelectHint  => Tr("– Profil wählen –", "– select profile –");
    public string LabelBackupTarget=> Tr("Backup-Ziel/Quelle:", "Backup target/source:");
    public string AppSubtitle      => Tr("Backup & Wiederherstellung von Benutzerprofilen",
                                         "Backup & restore of user profiles");
    public string BtnClearPaths    => Tr("Pfade leeren", "Clear paths");
    public string BtnExit          => Tr("Beenden", "Exit");
    public string BtnExecute       => Tr("Ausgewählte Aktionen starten", "Start selected actions");
    public string LogTitle         => Tr("Aktivitäts-Protokoll", "Activity log");
    public string BtnClearLog      => Tr("Log leeren", "Clear log");
    public string BannerHint       => Tr("Wird geprüft, heruntergeladen und ersetzt - erst nach Rückfrage.",
                                         "Checked, downloaded and replaced - only after confirmation.");
    public string BtnUpdateNow     => Tr("Jetzt aktualisieren", "Update now");
    public string BtnUpdateNotes   => Tr("Was ist neu?", "What's new?");

    public string TgUserProfile    => Tr("Windows Benutzerprofil", "Windows user profile");
    public string TgFirefox        => Tr("Firefox-Profil", "Firefox profile");
    public string TgEdge           => Tr("Edge-Profil", "Edge profile");
    public string TgChrome         => Tr("Chrome-Profil", "Chrome profile");
    public string TgBrave          => Tr("Brave-Profil", "Brave profile");
    public string TgThunderbird    => Tr("Thunderbird-Profil", "Thunderbird profile");

    // Umschalter-Beschriftungen
    public string TabBackup        => Tr("Sichern", "Backup");
    public string TabRestore       => Tr("Wiederherstellen", "Restore");

    // ---- Auswahl der Aktionen (gilt fuer den gerade gewaehlten Modus) ----
    [ObservableProperty] private bool _selUser;
    [ObservableProperty] private bool _selFirefox;
    [ObservableProperty] private bool _selEdge;
    [ObservableProperty] private bool _selChrome;
    [ObservableProperty] private bool _selBrave;
    [ObservableProperty] private bool _selThunderbird;
    [ObservableProperty] private bool _selWinget;
    [ObservableProperty] private bool _selWlan;
    [ObservableProperty] private bool _selPrinter;
    [ObservableProperty] private bool _selWallpaper;

    // Bei Aenderung der groessenrelevanten Auswahl die Auto-Berechnung anstossen.
    partial void OnSelUserChanged(bool value) => ScheduleAutoStorageCheck();
    partial void OnSelFirefoxChanged(bool value) => ScheduleAutoStorageCheck();
    partial void OnSelEdgeChanged(bool value) => ScheduleAutoStorageCheck();
    partial void OnSelChromeChanged(bool value) => ScheduleAutoStorageCheck();
    partial void OnSelBraveChanged(bool value) => ScheduleAutoStorageCheck();
    partial void OnSelThunderbirdChanged(bool value) => ScheduleAutoStorageCheck();
    partial void OnSelWlanChanged(bool value) => ScheduleAutoStorageCheck();
    partial void OnSelPrinterChanged(bool value) => ScheduleAutoStorageCheck();
    partial void OnSelWallpaperChanged(bool value) => ScheduleAutoStorageCheck();

    // ---- Modus: false = Sichern (Backup), true = Wiederherstellen (Restore) ----
    [ObservableProperty] private bool _isRestoreMode;

    public bool IsBackupMode
    {
        get => !IsRestoreMode;
        set { if (value) IsRestoreMode = false; }
    }

    partial void OnIsRestoreModeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsBackupMode));
        // Beschriftungen, die vom Modus abhaengen, aktualisieren.
        OnPropertyChanged(nameof(TgWinget));
        OnPropertyChanged(nameof(TgWlan));
        OnPropertyChanged(nameof(TgPrinter));
        OnPropertyChanged(nameof(TgWallpaper));
        OnPropertyChanged(nameof(ModeTitle));
        ScheduleAutoStorageCheck();
    }

    // Modusabhaengige Beschriftungen fuer die Sonderpunkte.
    public string TgWinget => IsRestoreMode
        ? Tr("Programme installieren (Winget)", "Install programs (winget)")
        : Tr("Programme exportieren (Winget)", "Export programs (winget)");
    public string TgWlan => IsRestoreMode
        ? Tr("WLAN Profile importieren", "Import WiFi profiles")
        : Tr("WLAN Profile exportieren", "Export WiFi profiles");
    public string TgPrinter => IsRestoreMode
        ? Tr("Drucker & Treiber wiederherstellen", "Restore printers & drivers")
        : Tr("Drucker & Treiber sichern", "Back up printers & drivers");
    public string TgWallpaper => IsRestoreMode
        ? Tr("Hintergrundbild wiederherstellen", "Restore wallpaper")
        : Tr("Hintergrundbild sichern", "Back up wallpaper");
    public string ModeTitle => IsRestoreMode
        ? Tr("Wiederherstellen", "Restore")
        : Tr("Sichern", "Backup");

    public MainWindowViewModel()
    {
        _runner = new ProcessRunner(_cancel);
        _backup = new BackupService(_runner, _cancel, Log);
        // Datei-Fortschritt aus dem Backup in die Anzeige spiegeln.
        _backup.OnFilesProgress = count =>
        {
            var baseText = _currentActionText;
            Dispatcher.UIThread.Post(() =>
                ProgressText = string.IsNullOrEmpty(baseText)
                    ? Tr($"{count} Dateien kopiert...", $"{count} files copied...")
                    : Tr($"{baseText}  -  {count} Dateien", $"{baseText}  -  {count} files"));
        };
        _winget = new WingetService(_runner, _cancel, Log);

        // Sprache aus der Windows-Anzeigesprache uebernehmen (Fallback: Deutsch).
        // Setzt Localizer direkt, damit der Begruessungstext sofort passt.
        var lang = SystemHelpers.GetWindowsLanguage();
        _isEnglish = lang == "en";
        _selectedLanguageIndex = _isEnglish ? 1 : 0;
        Localizer.I.Lang = _isEnglish ? AppLang.En : AppLang.De;

        // Hell-/Dunkelmodus aus Windows uebernehmen.
        _isLightTheme = SystemHelpers.IsWindowsLightTheme();
        var app = Avalonia.Application.Current;
        if (app is not null)
            app.RequestedThemeVariant = _isLightTheme
                ? Avalonia.Styling.ThemeVariant.Light
                : Avalonia.Styling.ThemeVariant.Dark;

        // Begruessungstext in der (jetzt gesetzten) Sprache anzeigen.
        _logBuffer.Append(InitialLogText());
        LogText = _logBuffer.ToString();

        // Benutzerprofile fuer die Schnellauswahl laden.
        foreach (var p in SystemHelpers.GetUserProfiles()) UserProfiles.Add(p);

        _ = CheckForUpdatesAsync();
    }

    // ---- Label-Umschaltung wie im Skript ----
    partial void OnDetailedLoggingChanged(bool value)
    {
        LoggingLabel = Tr(value ? "Log: an" : "Log: aus",
                          value ? "Log: on" : "Log: off");
        // Beim EINSCHALTEN auf die Performance-Auswirkung hinweisen:
        // sowohl im Log als auch als Meldung auf dem Bildschirm.
        if (value)
        {
            var msg = Tr("Detailliertes Log ist aktiv. Das erzeugt sehr viele Zeilen und kann die Geschwindigkeit spürbar verringern.",
                         "Detailed logging is on. It produces a lot of output and can noticeably slow things down.");
            Log("[HINWEIS] " + msg);
            // Dialog nicht blockierend anstossen (Handler ist synchron).
            Dispatcher.UIThread.Post(async () =>
                await ShowInfo(Tr("Hinweis", "Notice"), msg));
        }
    }

    private void Log(string text)
    {
        string snapshot;
        lock (_logLock)
        {
            _logUntouched = false;   // ab jetzt bleibt der Verlauf stehen
            _logBuffer.Append(text).Append("\r\n");
            if (_logBuffer.Length > 40000)
            {
                var keep = _logBuffer.ToString(_logBuffer.Length - 30000, 30000);
                _logBuffer.Clear();
                _logBuffer.Append(Tr("... [LOG GEKUERZT] ...\r\n", "... [LOG TRUNCATED] ...\r\n")).Append(keep);
            }
            snapshot = _logBuffer.ToString();
        }
        Dispatcher.UIThread.Post(() => LogText = snapshot);
    }

    // ---------------------------------------------------------------- Commands

    [RelayCommand]
    private async Task CheckStorage() => await RunStorageCheck(showPopup: true);

    // Sammelt die relativen Profilpfade der ausgewaehlten Browser-/Mail-Punkte.
    private System.Collections.Generic.List<string> SelectedProfileRelPaths()
    {
        var list = new System.Collections.Generic.List<string>();
        if (SelFirefox)     list.Add(@"AppData\Roaming\Mozilla\Firefox");
        if (SelEdge)        list.Add(@"AppData\Local\Microsoft\Edge\User Data");
        if (SelChrome)      list.Add(@"AppData\Local\Google\Chrome\User Data");
        if (SelBrave)       list.Add(@"AppData\Local\BraveSoftware\Brave-Browser\User Data");
        if (SelThunderbird) list.Add(@"AppData\Roaming\Thunderbird");
        return list;
    }

    // Sammelt die Namen der Backup-Unterordner/-Dateien der ausgewaehlten
    // Punkte - fuer die Groessenberechnung im Wiederherstellungsmodus.
    private System.Collections.Generic.List<string> SelectedBackupSubdirs()
    {
        var list = new System.Collections.Generic.List<string>();
        if (SelUser)        list.Add("Benutzerprofil");
        if (SelFirefox)     list.Add("Firefox-Profil");
        if (SelEdge)        list.Add("Edge-Profil");
        if (SelChrome)      list.Add("Chrome-Profil");
        if (SelBrave)       list.Add("Brave-Profil");
        if (SelThunderbird) list.Add("Thunderbird-Profil");
        if (SelWlan)        list.Add("WLAN-Profile");
        if (SelPrinter)     list.Add("Drucker");
        if (SelWallpaper)   list.Add("Hintergrundbild");
        // Winget-Liste ist nur eine kleine Textdatei; vernachlaessigbar.
        return list;
    }

    private async Task RunStorageCheck(bool showPopup)
    {
        if (StorageChecking) return;
        if (string.IsNullOrWhiteSpace(BackupPath))
        {
            StorageInfo = Tr("Bitte zuerst ein Backup-Ziel/-Quelle festlegen.",
                             "Please set a backup target/source first.");
            StorageStatus = 2;
            StorageInfoVisible = true;
            return;
        }

        StorageChecking = true;
        StorageStatus = 0;
        StorageInfo = Tr("Berechne Datenmenge...", "Calculating data size...");
        StorageInfoVisible = true;

        try
        {
            var backupPath = BackupPath;
            var sourcePath = SourcePath;
            bool restore = IsRestoreMode;
            bool selUser = SelUser;
            var relPaths = SelectedProfileRelPaths();
            var restoreSubdirs = SelectedBackupSubdirs(); // nur fuer Restore

            var (dataSize, free) = await Task.Run(() =>
            {
                long size = 0;
                if (restore)
                {
                    // Wiederherstellung: nur die AUSGEWAEHLTEN Backup-Unterordner
                    // summieren (nicht den gesamten Backup-Ordner).
                    foreach (var sub in restoreSubdirs)
                    {
                        var full = System.IO.Path.Combine(backupPath, sub);
                        if (System.IO.Directory.Exists(full))
                            size += SystemHelpers.GetDirectorySize(full);
                        else if (System.IO.File.Exists(full))
                        {
                            try { size += new System.IO.FileInfo(full).Length; } catch { }
                        }
                    }
                    var restoreTarget = string.IsNullOrWhiteSpace(sourcePath) ? backupPath : sourcePath;
                    return (size, SystemHelpers.GetFreeSpace(restoreTarget));
                }
                else
                {
                    // Sicherung: Summe ALLER ausgewaehlten Quellen.
                    if (selUser && !string.IsNullOrWhiteSpace(sourcePath))
                        size += SystemHelpers.GetDirectorySize(sourcePath,
                                    BackupService.GetProfileExcludes(sourcePath));

                    // Browser-/Mail-Profile einzeln dazurechnen.
                    if (!string.IsNullOrWhiteSpace(sourcePath))
                    {
                        foreach (var rel in relPaths)
                        {
                            var full = System.IO.Path.Combine(sourcePath, rel);
                            if (System.IO.Directory.Exists(full))
                                size += SystemHelpers.GetDirectorySize(full);
                        }
                    }
                    return (size, SystemHelpers.GetFreeSpace(backupPath));
                }
            });

            var margin = (long)(dataSize * 0.10); // 10% Sicherheitsreserve
            var needed = dataSize + margin;

            var sizeStr = SystemHelpers.FormatBytes(dataSize);
            var freeStr = SystemHelpers.FormatBytes(free);
            string? popupMsg = null;

            if (free < 0)
            {
                StorageInfo = Tr($"Datenmenge: ca. {sizeStr}  |  Freier Platz: nicht ermittelbar",
                                 $"Data size: ~{sizeStr}  |  Free space: unavailable");
                StorageStatus = 2;
            }
            else if (free < needed)
            {
                var missing = SystemHelpers.FormatBytes(needed - free);
                StorageInfo = Tr($"WARNUNG: Datenmenge ca. {sizeStr}, aber nur {freeStr} frei. Es fehlen ca. {missing} (inkl. 10% Reserve).",
                                 $"WARNING: Data size ~{sizeStr}, but only {freeStr} free. About {missing} short (incl. 10% margin).");
                StorageStatus = 3;
                Log(Tr($"[WARNUNG] Speicher reicht möglicherweise nicht: ca. {sizeStr} Daten, {freeStr} frei.",
                       $"[WARNING] Storage may be insufficient: ~{sizeStr} data, {freeStr} free."));
                popupMsg = Tr($"Der Speicher am Ziel reicht möglicherweise nicht aus.\n\nBenötigt (inkl. 10% Reserve): ca. {SystemHelpers.FormatBytes(needed)}\nFrei: {freeStr}\nEs fehlen ca. {missing}.",
                              $"The target may not have enough free space.\n\nNeeded (incl. 10% margin): ~{SystemHelpers.FormatBytes(needed)}\nFree: {freeStr}\nAbout {missing} short.");
            }
            else if (free < needed * 1.5)
            {
                StorageInfo = Tr($"Datenmenge ca. {sizeStr}, {freeStr} frei - passt, aber knapp.",
                                 $"Data size ~{sizeStr}, {freeStr} free - fits, but tight.");
                StorageStatus = 2;
                popupMsg = Tr($"Der Speicher am Ziel reicht, ist aber knapp.\n\nDatenmenge: ca. {sizeStr}\nFrei: {freeStr}",
                              $"The target has enough space, but it is tight.\n\nData size: ~{sizeStr}\nFree: {freeStr}");
            }
            else
            {
                var afterStr = SystemHelpers.FormatBytes(free - dataSize);
                StorageInfo = Tr($"Datenmenge ca. {sizeStr}, {freeStr} frei. Danach noch ca. {afterStr} frei.",
                                 $"Data size ~{sizeStr}, {freeStr} free. About {afterStr} left afterwards.");
                StorageStatus = 1;
            }
            StorageInfoVisible = true;

            // Popup bei gelb/rot - aber nur wenn gewuenscht (manuelle Pruefung),
            // damit die automatische Berechnung nicht staendig Fenster oeffnet.
            if (showPopup && popupMsg != null)
                await ShowInfo(Tr("Speicherplatz", "Storage"), popupMsg);
        }
        catch (Exception ex)
        {
            StorageInfo = Tr($"Größenberechnung fehlgeschlagen: {ex.Message}",
                             $"Size calculation failed: {ex.Message}");
            StorageStatus = 2;
        }
        finally
        {
            StorageChecking = false;
        }
    }

    [RelayCommand]
    private void ToggleLanguage() => IsEnglish = !IsEnglish;

    [RelayCommand]
    private void ClearLog()
    {
        lock (_logLock)
        {
            _logBuffer.Clear();
            _logUntouched = true;
            var snapshot = "";
            Dispatcher.UIThread.Post(() => LogText = snapshot);
        }
    }

    // ---- Hell-/Dunkelmodus ----
    [ObservableProperty] private bool _isLightTheme;

    public string ThemeIcon => IsLightTheme ? "\u2600" : "\u263D"; // Sonne / Mond

    [RelayCommand]
    private void ToggleTheme()
    {
        IsLightTheme = !IsLightTheme;
    }

    partial void OnIsLightThemeChanged(bool value)
    {
        OnPropertyChanged(nameof(ThemeIcon));
        var app = Avalonia.Application.Current;
        if (app is not null)
            app.RequestedThemeVariant = value
                ? Avalonia.Styling.ThemeVariant.Light
                : Avalonia.Styling.ThemeVariant.Dark;
    }

    [RelayCommand]
    private void ToggleLogging() => DetailedLogging = !DetailedLogging;

    [RelayCommand]
    private void ClearPaths()
    {
        SourcePath = "";
        BackupPath = "";
        Log(Tr(">> Pfade wurden geleert.", ">> Paths cleared."));
    }

    [RelayCommand]
    private async Task SelectSource()
    {
        var folder = await PickFolder(Tr("Quell-Benutzerprofil auswählen (z.B. C:\\Users\\Name)",
                                         "Select source user profile (e.g. C:\\Users\\Name)"));
        if (folder != null) SourcePath = folder;
    }

    [RelayCommand]
    private async Task SelectBackup()
    {
        var folder = await PickFolder(Tr("Backup Basis-Verzeichnis auswählen", "Select backup base directory"));
        if (folder == null) return;
        if (SystemHelpers.IsNtfsDrive(folder)) BackupPath = folder;
        else await ShowInfo(Tr("Fehler", "Error"),
            Tr("Das ausgewählte Laufwerk ist NICHT mit NTFS formatiert!\nRobocopy benötigt zwingend NTFS.",
               "The selected drive is NOT formatted as NTFS!\nRobocopy strictly requires NTFS."));
    }

    [RelayCommand]
    private void Exit() => GetMainWindow()?.Close();

    [RelayCommand]
    private async Task Execute()
    {
        var tasks = BuildTaskList();
        if (tasks.Count == 0)
        {
            await ShowInfo(Tr("Hinweis", "Notice"), Tr("Bitte wähle mindestens eine Aktion aus.", "Please select at least one action."));
            return;
        }
        if (string.IsNullOrEmpty(SourcePath) || string.IsNullOrEmpty(BackupPath))
        {
            await ShowInfo(Tr("Fehler", "Error"), Tr("Bitte lege zuerst das Quell- und Zielverzeichnis fest.", "Please set the source and target directory first."));
            return;
        }

        // Akkubetrieb pruefen: Bei Notebooks ohne Netzteil warnen und rueckfragen.
        if (SystemHelpers.IsOnBattery(out var battery))
        {
            var pct = battery >= 0 ? $" ({battery}%)" : "";
            // Warnung zusaetzlich ins Log schreiben (bleibt dort bestehen).
            Log(Tr($"[HINWEIS] Notebook läuft im Akkubetrieb{pct}. Bitte zur Sicherheit das Ladegerät anschließen.",
                   $"[NOTE] The notebook is running on battery{pct}. Please connect the charger to be safe."));
            var proceed = await ShowYesNo(
                Tr("Akkubetrieb", "Running on battery"),
                Tr($"Das Notebook läuft im Akkubetrieb{pct}.\n\nBei längeren Backups sollte das Ladegerät angeschlossen werden, damit der Vorgang nicht durch einen leeren Akku unterbrochen wird.\n\nTrotzdem fortfahren?",
                   $"The notebook is running on battery{pct}.\n\nFor longer backups you should connect the charger so the process is not interrupted by an empty battery.\n\nContinue anyway?"));
            if (!proceed)
            {
                Log(Tr("[INFO] Start abgebrochen - bitte Ladegerät anschließen.",
                       "[INFO] Start cancelled - please connect the charger."));
                return;
            }
        }

        // Winget-Import: Auswahl JETZT auf dem UI-Thread abfragen
        List<WingetPackage>? wingetSelection = null;
        if (tasks.Contains("Import-Winget"))
        {
            var pkgs = WingetService.ReadExportedPackages(BackupPath, out var status);
            switch (status)
            {
                case "NotFound":   await ShowInfo(Tr("Fehler", "Error"), Tr("Winget Exportdatei nicht gefunden.", "Winget export file not found.")); return;
                case "ParseError": await ShowInfo(Tr("Fehler", "Error"), Tr("Die Exportdatei konnte nicht als JSON verarbeitet werden.", "The export file could not be parsed as JSON.")); return;
                case "Empty":      await ShowInfo(Tr("Fehler", "Error"), Tr("Die Exportdatei enthält keine gültigen Programme.", "The export file contains no valid programs.")); return;
            }
            var win = GetMainWindow();
            if (win != null)
            {
                wingetSelection = await WingetSelectionDialog.Show(win, pkgs);
                if (wingetSelection == null) return; // Abgebrochen
            }
        }

        _cancel.Reset();
        _cancel.FastMode = !DetailedLogging;
        _backup.SourcePath = SourcePath;
        _backup.BackupPath = BackupPath;

        UiEnabled = false;
        IsActionRunning = true;
        Log("\r\n=========================================");
        Log(Tr("Starte Abarbeitung der Warteschlange...", "Starting to process the queue..."));

        // Leistungsmodus anfordern: kein Standby, hoehere Prioritaet.
        // Wird im finally garantiert vollstaendig zurueckgesetzt.
        var perf = new PerformanceMode(Log);
        perf.Enable();

        try
        {
            await Task.Run(() => RunQueue(tasks, wingetSelection));
        }
        finally
        {
            perf.Dispose();
            Log("\r\n=========================================");
            if (_cancel.IsCancelled) Log(Tr("Tool sicher beendet.", "Tool stopped safely."));
            else { Log(Tr("Alle ausgewählten Aktionen abgeschlossen!", "All selected actions completed!")); }
            IsActionRunning = false;
            UiEnabled = true;
        }
    }

    // ---------------------------------------------------------------- Warteschlange

    private List<string> BuildTaskList()
    {
        var t = new List<string>();

        if (IsRestoreMode)
        {
            if (SelUser)        t.Add("Restore-User");
            if (SelFirefox)     t.Add("Restore-Firefox");
            if (SelEdge)        t.Add("Restore-Edge");
            if (SelChrome)      t.Add("Restore-Chrome");
            if (SelBrave)       t.Add("Restore-Brave");
            if (SelThunderbird) t.Add("Restore-Thunderbird");
            if (SelWinget)      t.Add("Import-Winget");
            if (SelWlan)        t.Add("Import-Wlan");
            if (SelPrinter)     t.Add("Import-Printer");
            if (SelWallpaper)   t.Add("Import-Wallpaper");
        }
        else
        {
            if (SelUser)        t.Add("Backup-User");
            if (SelFirefox)     t.Add("Backup-Firefox");
            if (SelEdge)        t.Add("Backup-Edge");
            if (SelChrome)      t.Add("Backup-Chrome");
            if (SelBrave)       t.Add("Backup-Brave");
            if (SelThunderbird) t.Add("Backup-Thunderbird");
            if (SelWinget)      t.Add("Export-Winget");
            if (SelWlan)        t.Add("Export-Wlan");
            if (SelPrinter)     t.Add("Export-Printer");
            if (SelWallpaper)   t.Add("Export-Wallpaper");
        }
        return t;
    }

    private async Task RunQueue(List<string> tasks, List<WingetPackage>? wingetSelection)
    {
        int total = tasks.Count;
        int index = 0;
        ProgressVisible = true;

        // Fuer das Backup-Protokoll: Start, Ergebnisse je Aktion.
        var startTime = DateTime.Now;
        var results = new List<(string action, bool ok)>();
        var swTotal = System.Diagnostics.Stopwatch.StartNew();

        foreach (var choice in tasks)
        {
            if (_cancel.IsCancelled) { Log(Tr("\r\n[ABBRUCH] Vorgang durch Benutzer abgebrochen!", "\r\n[CANCELLED] Operation cancelled by user!")); break; }
            index++;
            _currentActionText = Tr($"Aktion {index} von {total}: {FriendlyAction(choice)}",
                                    $"Action {index} of {total}: {FriendlyAction(choice)}");
            ProgressText = _currentActionText;
            Log(Tr($"\r\n--- Führe Aktion {choice} aus ---", $"\r\n--- Running action {choice} ---"));

            bool ok = true;
            try
            {
                switch (choice)
                {
                    case "Backup-User":        await _backup.BackupUserProfile(); break;
                    case "Backup-Firefox":     await _backup.BackupAppProfile("Firefox", @"AppData\Roaming\Mozilla\Firefox", "firefox"); break;
                    case "Backup-Edge":        await _backup.BackupAppProfile("Edge", @"AppData\Local\Microsoft\Edge\User Data", "msedge", _backup.ChromiumExcludes); break;
                    case "Backup-Chrome":      await _backup.BackupAppProfile("Chrome", @"AppData\Local\Google\Chrome\User Data", "chrome", _backup.ChromiumExcludes); break;
                    case "Backup-Brave":       await _backup.BackupAppProfile("Brave", @"AppData\Local\BraveSoftware\Brave-Browser\User Data", "brave", _backup.ChromiumExcludes); break;
                    case "Backup-Thunderbird": await _backup.BackupAppProfile("Thunderbird", @"AppData\Roaming\Thunderbird", "thunderbird"); break;
                    case "Export-Winget":      await _winget.ExportAsync(BackupPath); break;
                    case "Export-Wlan":        await _backup.ExportWlan(); break;
                    case "Export-Printer":     await _backup.ExportPrinters(); break;
                    case "Export-Wallpaper":   await _backup.ExportWallpaper(); break;

                    case "Restore-User":        await _backup.RestoreUserProfile(); break;
                    case "Restore-Firefox":     await _backup.RestoreAppProfile("Firefox", @"AppData\Roaming\Mozilla\Firefox", "firefox"); break;
                    case "Restore-Edge":        await _backup.RestoreAppProfile("Edge", @"AppData\Local\Microsoft\Edge\User Data", "msedge"); break;
                    case "Restore-Chrome":      await _backup.RestoreAppProfile("Chrome", @"AppData\Local\Google\Chrome\User Data", "chrome"); break;
                    case "Restore-Brave":       await _backup.RestoreAppProfile("Brave", @"AppData\Local\BraveSoftware\Brave-Browser\User Data", "brave"); break;
                    case "Restore-Thunderbird": await _backup.RestoreAppProfile("Thunderbird", @"AppData\Roaming\Thunderbird", "thunderbird"); break;
                    case "Import-Winget":       await _winget.ImportAsync(wingetSelection ?? new List<WingetPackage>()); break;
                    case "Import-Wlan":         await _backup.ImportWlan(); break;
                    case "Import-Printer":      await _backup.ImportPrinters(); break;
                    case "Import-Wallpaper":    await _backup.ImportWallpaper(); break;
                }
            }
            catch (Exception ex)
            {
                // Ein Fehler in EINER Aktion darf nicht die App beenden.
                ok = false;
                Log(Tr($"[FEHLER] Aktion '{choice}' abgebrochen: {ex.Message}", $"[ERROR] Action '{choice}' aborted: {ex.Message}"));
                Program.WriteCrashLog($"RunQueue/{choice}", ex);
            }

            results.Add((choice, ok));
            await Task.Delay(1000);
        }
        swTotal.Stop();

        // Fortschrittsanzeige nach der Warteschlange ausblenden.
        _currentActionText = "";
        ProgressText = "";
        ProgressVisible = false;

        // Zusammenfassung ins Log und Protokolldatei schreiben.
        WriteRunSummaryAndReport(startTime, swTotal.Elapsed, results);
    }

    /// <summary>
    /// Schreibt eine kurze Bilanz ins Log und legt eine Protokolldatei im
    /// Backup-Ziel ab (Sicherungs-Protokoll_JJJJ-MM-TT_HH-MM.txt).
    /// </summary>
    private void WriteRunSummaryAndReport(DateTime start, TimeSpan duration,
                                         List<(string action, bool ok)> results)
    {
        if (results.Count == 0) return;

        int okCount = results.Count(r => r.ok);
        int failCount = results.Count - okCount;
        bool restore = IsRestoreMode;

        var durStr = duration.TotalMinutes >= 1
            ? $"{(int)duration.TotalMinutes} min {duration.Seconds} s"
            : $"{duration.Seconds} s";

        // Kurze Bilanz ins Log.
        Log(Tr($"\r\n[BILANZ] {okCount} von {results.Count} erfolgreich, {failCount} Fehler. Dauer: {durStr}.",
               $"\r\n[SUMMARY] {okCount} of {results.Count} succeeded, {failCount} errors. Duration: {durStr}."));

        // Protokolldatei schreiben (nur wenn ein gueltiges Ziel existiert).
        try
        {
            if (string.IsNullOrWhiteSpace(BackupPath) || !Directory.Exists(BackupPath)) return;

            var modeStr = restore ? Tr("Wiederherstellung", "Restore") : Tr("Sicherung", "Backup");
            // Computername in den Dateinamen aufnehmen (ungueltige Zeichen entfernen).
            var pcName = Environment.MachineName;
            foreach (var ch in Path.GetInvalidFileNameChars())
                pcName = pcName.Replace(ch, '_');
            var fileName = $"{(restore ? "Wiederherstellung" : "Sicherung")}-Protokoll_{pcName}_{start:yyyy-MM-dd_HH-mm}.txt";
            var path = Path.Combine(BackupPath, fileName);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Windows DaSi Tool - " + Tr("Protokoll", "Report"));
            sb.AppendLine("========================================");
            sb.AppendLine($"{Tr("Modus", "Mode")}: {modeStr}");
            sb.AppendLine($"{Tr("Computer", "Computer")}: {Environment.MachineName}");
            sb.AppendLine($"{Tr("Datum", "Date")}: {start:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"{Tr("Dauer", "Duration")}: {durStr}");
            sb.AppendLine($"{Tr("Benutzerprofil", "User profile")}: {SourcePath}");
            sb.AppendLine($"{Tr("Backup-Ziel/-Quelle", "Backup target/source")}: {BackupPath}");
            sb.AppendLine($"{Tr("Version", "Version")}: {VersionString}");
            sb.AppendLine("----------------------------------------");
            sb.AppendLine($"{Tr("Ergebnis", "Result")}: {okCount}/{results.Count} {Tr("erfolgreich", "succeeded")}, {failCount} {Tr("Fehler", "errors")}");
            sb.AppendLine("----------------------------------------");
            foreach (var (action, ok) in results)
            {
                var mark = ok ? "[OK]   " : "[FEHLER]";
                sb.AppendLine($"{mark} {FriendlyAction(action)}");
            }
            sb.AppendLine("========================================");

            File.WriteAllText(path, sb.ToString(), new System.Text.UTF8Encoding(true));
            Log(Tr($"[INFO] Protokoll gespeichert: {fileName}", $"[INFO] Report saved: {fileName}"));
        }
        catch (Exception ex)
        {
            Log(Tr($"[WARNUNG] Protokoll konnte nicht gespeichert werden: {ex.Message}",
                   $"[WARNING] Report could not be saved: {ex.Message}"));
        }
    }

    /// <summary>Uebersetzt einen internen Aktionsnamen in einen lesbaren Text.</summary>
    private string FriendlyAction(string choice)
    {
        return choice switch
        {
            "Backup-User" or "Restore-User"               => Tr("Windows Benutzerprofil", "Windows user profile"),
            "Backup-Firefox" or "Restore-Firefox"         => Tr("Firefox-Profil", "Firefox profile"),
            "Backup-Edge" or "Restore-Edge"               => Tr("Edge-Profil", "Edge profile"),
            "Backup-Chrome" or "Restore-Chrome"           => Tr("Chrome-Profil", "Chrome profile"),
            "Backup-Brave" or "Restore-Brave"             => Tr("Brave-Profil", "Brave profile"),
            "Backup-Thunderbird" or "Restore-Thunderbird" => Tr("Thunderbird-Profil", "Thunderbird profile"),
            "Export-Winget" or "Import-Winget"            => Tr("Programme (Winget)", "Programs (winget)"),
            "Export-Wlan" or "Import-Wlan"                => Tr("WLAN-Profile", "WiFi profiles"),
            "Export-Printer" or "Import-Printer"          => Tr("Drucker & Treiber", "Printers & drivers"),
            "Export-Wallpaper" or "Import-Wallpaper"      => Tr("Hintergrundbild", "Wallpaper"),
            _ => choice
        };
    }

    // ---------------------------------------------------------------- Update

    /// <summary>
    /// Der zuletzt abgefragte Stand. Bewusst gemerkt: Die Abfrage laeuft beim
    /// Start, geklickt wird oft erst Minuten spaeter.
    /// </summary>
    private UpdateManifest? _manifest;

    /// <summary>
    /// Fragt update.json ab und blendet bei einem neueren Stand den Streifen
    /// ein. Heruntergeladen wird hier noch nichts.
    /// </summary>
    private async Task CheckForUpdatesAsync()
    {
        var url = UpdateService.ResolveManifestUrl(SystemHelpers.GetApplicationDirectory());
        var manifest = await UpdateService.FetchAsync(url, System.Threading.CancellationToken.None);
        _manifest = manifest;

        if (!manifest.Success || !manifest.Program.IsUsable) return;
        if (!UpdateService.IsProgramNewer(VersionString, manifest.Program.Version)) return;

        Dispatcher.UIThread.Post(() =>
        {
            UpdateBannerText = Tr(
                $"\uD83D\uDD04 Neue Version {manifest.Program.Version} verfügbar  (installiert: {VersionString})",
                $"\uD83D\uDD04 New version {manifest.Program.Version} available  (installed: {VersionString})");
            UpdateAvailable = true;
        });
    }

    /// <summary>
    /// Laedt die neue EXE, vergleicht ihre SHA256-Summe mit der Angabe aus
    /// update.json und tauscht sie aus.
    ///
    /// Eine laufende EXE kann sich unter Windows nicht selbst ueberschreiben.
    /// Deshalb uebernimmt das ein kleines Skript, das auf das Ende dieses
    /// Vorgangs wartet, die Datei ersetzt und danach neu startet.
    /// </summary>
    [RelayCommand]
    private async Task UpdateProgram()
    {
        if (_manifest is null || !_manifest.Program.IsUsable) return;

        var win = GetMainWindow();
        var entry = _manifest.Program;

        var current = Environment.ProcessPath;
        if (string.IsNullOrEmpty(current))
        {
            Log(Tr("[FEHLER] Der eigene Programmpfad ließ sich nicht ermitteln.",
                   "[ERROR] Could not determine own program path."));
            return;
        }

        if (win is not null)
        {
            var frage = Tr(
                $"Version {entry.Version} herunterladen und die laufende Fassung {VersionString} ersetzen?\n\n" +
                $"Datei: {current}\n\n" +
                "Das Programm wird dabei beendet und danach neu gestartet.",
                $"Download version {entry.Version} and replace the running {VersionString}?\n\n" +
                $"File: {current}\n\n" +
                "The program will close and restart afterwards.");

            if (!await MessageBox.ShowYesNo(win, Tr("Programm aktualisieren", "Update program"), frage))
            {
                Log(Tr("Abgebrochen.", "Cancelled."));
                return;
            }
        }

        try
        {
            UiEnabled = false;
            Log(Tr($"Lade Version {entry.Version}...", $"Downloading version {entry.Version}..."));

            var ziel = Path.Combine(UpdateService.WorkFolder, "WindowsDaSiTool.exe");
            var ergebnis = await UpdateService.DownloadAsync(
                entry.Url, entry.Sha256, ziel, System.Threading.CancellationToken.None);

            if (!ergebnis.Success)
            {
                Log(Tr($"[FEHLER] Download fehlgeschlagen: {ergebnis.Error}",
                       $"[ERROR] Download failed: {ergebnis.Error}"));
                if (win is not null)
                    await MessageBox.ShowInfo(win, Tr("Download fehlgeschlagen", "Download failed"),
                                              ergebnis.Error ?? "");
                return;
            }

            Log(Tr($"Geladen ({ergebnis.Bytes / 1024 / 1024} MB), Prüfsumme stimmt. Wird ersetzt...",
                   $"Downloaded ({ergebnis.Bytes / 1024 / 1024} MB), checksum matches. Replacing..."));

            var skript = UpdateService.WriteUpdateScript(ziel, current, Environment.ProcessId);
            UpdateService.StartUpdateScript(skript);

            GetMainWindow()?.Close();
        }
        catch (Exception ex)
        {
            Log(Tr($"[FEHLER] {ex.Message}", $"[ERROR] {ex.Message}"));
        }
        finally
        {
            UiEnabled = true;
        }
    }

    /// <summary>Oeffnet die Uebersicht der Veroeffentlichungen im Browser.</summary>
    [RelayCommand]
    private void OpenUpdateNotes()
    {
        var url = string.IsNullOrWhiteSpace(_manifest?.Program.Notes)
            ? UpdateService.ReleasesPageUrl
            : _manifest!.Program.Notes;

        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    public void OpenProjectPage()
    {
        try { Process.Start(new ProcessStartInfo(ProjectUrl) { UseShellExecute = true }); } catch { }
    }

    public void RequestCancelAndKill()
    {
        _cancel.RequestCancel();
        _cancel.KillAll();
    }

    // ---------------------------------------------------------------- UI-Helfer

    private static Window? GetMainWindow()
        => (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    private async Task<string?> PickFolder(string title)
    {
        var win = GetMainWindow();
        if (win?.StorageProvider is not { } sp) return null;

        try
        {
            // Einen garantiert existierenden Startordner vorgeben. Das ist wichtig,
            // weil Avalonia sonst die Liste zuletzt besuchter Orte aufbaut und bei
            // einem darin enthaltenen, nicht mehr existierenden Pfad (abgezogener
            // USB-Stick, getrenntes Netzlaufwerk) mit "Directory must exist"
            // abstuerzen kann. Ein gueltiger Startordner umgeht das zuverlaessig.
            Avalonia.Platform.Storage.IStorageFolder? startFolder = null;
            foreach (var candidate in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Path.GetPathRoot(Environment.SystemDirectory), // z.B. "C:\"
            })
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                try
                {
                    if (Directory.Exists(candidate))
                    {
                        startFolder = await sp.TryGetFolderFromPathAsync(new Uri(candidate));
                        if (startFolder != null) break;
                    }
                }
                catch { /* naechsten Kandidaten versuchen */ }
            }

            var result = await sp.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                SuggestedStartLocation = startFolder
            });

            if (result.Count > 0 && result[0].TryGetLocalPath() is { } p) return p;
        }
        catch (Exception ex)
        {
            // Absturzschutz: Sollte der Dialog trotzdem scheitern, nicht abstuerzen.
            Program.WriteCrashLog("PickFolder", ex);
            var msg = Tr("Der Ordner-Dialog konnte nicht geöffnet werden. Bitte den Pfad manuell in das Textfeld eingeben.",
                         "The folder dialog could not be opened. Please type the path into the text field instead.");
            Log("[WARNUNG] " + msg);
            await ShowInfo(Tr("Ordner-Dialog", "Folder dialog"), msg);
        }
        return null;
    }

    private async Task ShowInfo(string title, string message)
    {
        var win = GetMainWindow();
        if (win != null) await MessageBox.ShowInfo(win, title, message);
    }

    private async Task<bool> ShowYesNo(string title, string message)
    {
        var win = GetMainWindow();
        if (win == null) return true; // ohne Fenster nicht blockieren
        return await MessageBox.ShowYesNo(win, title, message);
    }
}
