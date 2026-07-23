using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    private const string VersionString = "1.0.0";
    private const string UpdateCheckUrl = "https://raw.githubusercontent.com/Tinnitus97/backup_my_windows_Updater/main/newversion.txt";
    private const string ProjectUrl = "https://github.com/Tinnitus97/Windows.DaSi.Tool";

    private readonly CancellationTokenState _cancel = new();
    private readonly ProcessRunner _runner;
    private readonly BackupService _backup;
    private readonly WingetService _winget;

    private readonly object _logLock = new();
    private readonly System.Text.StringBuilder _logBuffer = new();
    private bool _logUntouched = true;   // true, solange nur der Begruessungstext im Log steht

    private static string InitialLogText()
        => Tr("Warte auf Eingabe...\r\nBitte waehle zuerst die benoetigten Pfade.\r\n",
              "Waiting for input...\r\nPlease select the required paths first.\r\n");

    public string WindowTitle => $"Windows DaSi Tool {VersionString}";

    // ---- gebundener Zustand ----
    [ObservableProperty] private string _logText = "";
    [ObservableProperty] private bool _uiEnabled = true;
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
    }

    [ObservableProperty] private bool _detailedLogging;
    [ObservableProperty] private bool _autoUpdate;
    [ObservableProperty] private string _loggingLabel = "Logging: Minimal / AUS";
    [ObservableProperty] private string _autoUpdateLabel = "Apps updaten: AUS";

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
        LoggingLabel = Tr(DetailedLogging ? "Logging: Detailliert / AN" : "Logging: Minimal / AUS",
                          DetailedLogging ? "Logging: detailed / ON" : "Logging: minimal / OFF");
        AutoUpdateLabel = Tr(AutoUpdate ? "Apps updaten: AN" : "Apps updaten: AUS",
                             AutoUpdate ? "Update apps: ON" : "Update apps: OFF");

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
    public string QuickSelectHint  => Tr("– Profil waehlen –", "– select profile –");
    public string LabelBackupTarget=> Tr("Backup-Ziel/Quelle:", "Backup target/source:");
    public string AppSubtitle      => Tr("Backup & Wiederherstellung von Benutzerprofilen",
                                         "Backup & restore of user profiles");
    public string BtnClearPaths    => Tr("Pfade leeren", "Clear paths");
    public string BtnExit          => Tr("Beenden", "Exit");
    public string BtnExecute       => Tr("Ausgewaehlte Aktionen starten", "Start selected actions");
    public string LogTitle         => Tr("Aktivitaets-Protokoll", "Activity log");
    public string BannerHint       => Tr("Klicken, um die Projektseite zu oeffnen.", "Click to open the project page.");

    public string TgUserProfile    => Tr("Windows Benutzerprofil", "Windows user profile");
    public string TgFirefox        => Tr("Firefox-Profil", "Firefox profile");
    public string TgEdge           => Tr("Edge-Profil", "Edge profile");
    public string TgChrome         => Tr("Chrome-Profil", "Chrome profile");
    public string TgBrave          => Tr("Brave-Profil", "Brave profile");
    public string TgThunderbird    => Tr("Thunderbird-Profil", "Thunderbird profile");

    // Umschalter-Beschriftungen
    public string TabBackup        => Tr("Sichern", "Backup");
    public string TabRestore       => Tr("Wiederherstellen", "Restore");

    public bool LogAutoScroll { get; private set; } = true;

    // ---- Auswahl der Aktionen (gilt fuer den gerade gewaehlten Modus) ----
    [ObservableProperty] private bool _selUser;
    [ObservableProperty] private bool _selFirefox;
    [ObservableProperty] private bool _selEdge;
    [ObservableProperty] private bool _selChrome;
    [ObservableProperty] private bool _selBrave;
    [ObservableProperty] private bool _selThunderbird;
    [ObservableProperty] private bool _selWinget;
    [ObservableProperty] private bool _selWlan;

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
        OnPropertyChanged(nameof(ModeTitle));
    }

    // Modusabhaengige Beschriftungen fuer die beiden Sonderpunkte.
    public string TgWinget => IsRestoreMode
        ? Tr("Programme installieren (Winget)", "Install programs (winget)")
        : Tr("Programme exportieren (Winget)", "Export programs (winget)");
    public string TgWlan => IsRestoreMode
        ? Tr("WLAN Profile importieren", "Import WiFi profiles")
        : Tr("WLAN Profile exportieren", "Export WiFi profiles");
    public string ModeTitle => IsRestoreMode
        ? Tr("Wiederherstellen", "Restore")
        : Tr("Sichern", "Backup");

    public MainWindowViewModel()
    {
        _runner = new ProcessRunner(_cancel);
        _backup = new BackupService(_runner, _cancel, Log);
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
        => LoggingLabel = Tr(value ? "Logging: Detailliert / AN" : "Logging: Minimal / AUS",
                             value ? "Logging: detailed / ON" : "Logging: minimal / OFF");
    partial void OnAutoUpdateChanged(bool value)
        => AutoUpdateLabel = Tr(value ? "Apps updaten: AN" : "Apps updaten: AUS",
                                value ? "Update apps: ON" : "Update apps: OFF");

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
    private void ToggleLanguage() => IsEnglish = !IsEnglish;

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
    private void ToggleAutoUpdate() => AutoUpdate = !AutoUpdate;

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
        var folder = await PickFolder(Tr("Quell-Benutzerprofil auswaehlen (z.B. C:\\Users\\Name)",
                                         "Select source user profile (e.g. C:\\Users\\Name)"));
        if (folder != null) SourcePath = folder;
    }

    [RelayCommand]
    private async Task SelectBackup()
    {
        var folder = await PickFolder(Tr("Backup Basis-Verzeichnis auswaehlen", "Select backup base directory"));
        if (folder == null) return;
        if (SystemHelpers.IsNtfsDrive(folder)) BackupPath = folder;
        else await ShowInfo(Tr("Fehler", "Error"),
            Tr("Das ausgewaehlte Laufwerk ist NICHT mit NTFS formatiert!\nRobocopy benoetigt zwingend NTFS.",
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
            await ShowInfo(Tr("Hinweis", "Notice"), Tr("Bitte waehle mindestens eine Aktion aus.", "Please select at least one action."));
            return;
        }
        if (string.IsNullOrEmpty(SourcePath) || string.IsNullOrEmpty(BackupPath))
        {
            await ShowInfo(Tr("Fehler", "Error"), Tr("Bitte lege zuerst das Quell- und Zielverzeichnis fest.", "Please set the source and target directory first."));
            return;
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
                case "Empty":      await ShowInfo(Tr("Fehler", "Error"), Tr("Die Exportdatei enthaelt keine gueltigen Programme.", "The export file contains no valid programs.")); return;
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
        _cancel.AutoUpdate = AutoUpdate;
        _backup.SourcePath = SourcePath;
        _backup.BackupPath = BackupPath;

        UiEnabled = false;
        LogAutoScroll = true;
        Log("\r\n=========================================");
        Log(Tr("Starte Abarbeitung der Warteschlange...", "Starting to process the queue..."));

        try
        {
            await Task.Run(() => RunQueue(tasks, wingetSelection));
        }
        finally
        {
            Log("\r\n=========================================");
            if (_cancel.IsCancelled) Log(Tr("Tool sicher beendet.", "Tool stopped safely."));
            else { Log(Tr("Alle ausgewaehlten Aktionen abgeschlossen!", "All selected actions completed!")); }
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
        }
        return t;
    }

    private async Task RunQueue(List<string> tasks, List<WingetPackage>? wingetSelection)
    {
        foreach (var choice in tasks)
        {
            if (_cancel.IsCancelled) { Log(Tr("\r\n[ABBRUCH] Vorgang durch Benutzer abgebrochen!", "\r\n[CANCELLED] Operation cancelled by user!")); break; }
            Log(Tr($"\r\n--- Fuehre Aktion {choice} aus ---", $"\r\n--- Running action {choice} ---"));

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

                    case "Restore-User":        await _backup.RestoreUserProfile(); break;
                    case "Restore-Firefox":     await _backup.RestoreAppProfile("Firefox", @"AppData\Roaming\Mozilla\Firefox", "firefox"); break;
                    case "Restore-Edge":        await _backup.RestoreAppProfile("Edge", @"AppData\Local\Microsoft\Edge\User Data", "msedge"); break;
                    case "Restore-Chrome":      await _backup.RestoreAppProfile("Chrome", @"AppData\Local\Google\Chrome\User Data", "chrome"); break;
                    case "Restore-Brave":       await _backup.RestoreAppProfile("Brave", @"AppData\Local\BraveSoftware\Brave-Browser\User Data", "brave"); break;
                    case "Restore-Thunderbird": await _backup.RestoreAppProfile("Thunderbird", @"AppData\Roaming\Thunderbird", "thunderbird"); break;
                    case "Import-Winget":       await _winget.ImportAsync(wingetSelection ?? new List<WingetPackage>()); break;
                    case "Import-Wlan":         await _backup.ImportWlan(); break;
                }
            }
            catch (Exception ex)
            {
                // Ein Fehler in EINER Aktion darf nicht die App beenden.
                Log(Tr($"[FEHLER] Aktion '{choice}' abgebrochen: {ex.Message}", $"[ERROR] Action '{choice}' aborted: {ex.Message}"));
                Program.WriteCrashLog($"RunQueue/{choice}", ex);
            }

            await Task.Delay(1000);
        }
    }

    // ---------------------------------------------------------------- Update-Check

    private async Task CheckForUpdatesAsync()
    {
        var newVersion = await SystemHelpers.CheckForUpdate(UpdateCheckUrl, VersionString);
        if (newVersion != null)
        {
            Dispatcher.UIThread.Post(() =>
            {
                UpdateBannerText = Tr($"\uD83D\uDD04 Neue Version {newVersion} verfuegbar  (installiert: {VersionString})",
                                      $"\uD83D\uDD04 New version {newVersion} available  (installed: {VersionString})");
                UpdateAvailable = true;
            });
        }
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
            // KEINEN Startordner vorgeben: Dadurch oeffnet der native Windows-
            // Ordner-Dialog bei "Dieser PC" (Laufwerksuebersicht). "Dieser PC"
            // ist ein virtueller Shell-Ort und kein echtes Verzeichnis. Auch
            // Environment.SpecialFolder.MyComputer hilft hier NICHT: Laut
            // Microsoft-Doku liefert GetFolderPath fuer virtuelle Ordner wie
            // "My Computer" einen leeren String zurueck - und ein leerer Pfad
            // wuerde in TryGetFolderFromPathAsync wieder den "Directory must
            // exist"-Absturz ausloesen. null ist daher genau richtig.
            var result = await sp.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                SuggestedStartLocation = null
            });

            if (result.Count > 0 && result[0].TryGetLocalPath() is { } p) return p;
        }
        catch (Exception ex)
        {
            // Absturzschutz: Sollte der Dialog (z.B. wegen eines Avalonia-Bugs)
            // scheitern, faengt der Catch das ab, statt die App zu beenden.
            // Es wird bewusst NICHT auf ein Benutzerprofil zurueckgefallen - der
            // Dialog soll immer bei "Dieser PC" bleiben.
            Program.WriteCrashLog("PickFolder", ex);
            Log(Tr("[WARNUNG] Der Ordner-Dialog konnte nicht geoeffnet werden. Bitte den Pfad manuell eingeben oder erneut versuchen.",
                   "[WARNING] The folder dialog could not be opened. Please enter the path manually or try again."));
        }
        return null;
    }

    private async Task ShowInfo(string title, string message)
    {
        var win = GetMainWindow();
        if (win != null) await MessageBox.ShowInfo(win, title, message);
    }
}
