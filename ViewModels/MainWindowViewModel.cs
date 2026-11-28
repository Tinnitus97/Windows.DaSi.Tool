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
    // ---- Constants ----
    private const string VersionString = "1.2.8";

    private readonly CancellationTokenState _cancel = new();
    private readonly ProcessRunner _runner;
    private readonly BackupService _backup;
    private readonly WingetService _winget;

    private readonly object _logLock = new();
    private readonly System.Text.StringBuilder _logBuffer = new();
    private bool _logUntouched = true;   // true as long as only the welcome text is in the log

    /// <summary>
    /// The complete log of the current run - for the log file in the backup
    /// folder. The display truncates after 40,000 characters; here everything
    /// is kept (up to the limit). The file used to contain only the summary -
    /// too little for troubleshooting after a restore.
    /// </summary>
    private System.Text.StringBuilder? _laufLog;
    internal const int LaufLogGrenze = 20_000_000;

    private static string InitialLogText()
        => Tr("Warte auf Eingabe...\r\nBitte wähle zuerst die benötigten Pfade.\r\n",
              "Waiting for input...\r\nPlease select the required paths first.\r\n");

    public string WindowTitle => $"Windows DaSi Tool {VersionString}";

    // ---- Bound state ----
    [ObservableProperty] private string _logText = "";
    [ObservableProperty] private bool _uiEnabled = true;

    // The extra options for the user profile depend on it as well and must be
    // locked while an action is running.
    partial void OnUiEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(UserOptionsEnabled));
        OnPropertyChanged(nameof(OwnFolderToggleEnabled));
        // Re-evaluate after a run: a backup created folders, a restore installed
        // programs.
        if (value) CheckAvailability(); else NotifyItemsChanged();
    }

    /// <summary>
    /// Is an action from the queue running right now? Deliberately separate
    /// from <see cref="UiEnabled"/>: the UI is locked for other reasons as well
    /// - e.g. during the self-update. Only this property may decide whether
    /// closing asks about a hard abort. Otherwise the window would ask about an
    /// abort although nothing is running.
    /// </summary>
    [ObservableProperty] private bool _isActionRunning;
    [ObservableProperty] private string _sourcePath = "";
    [ObservableProperty] private string _backupPath = "";

    // ---- Quick selection of the user profile ----
    public System.Collections.ObjectModel.ObservableCollection<string> UserProfiles { get; } = new();

    [ObservableProperty] private string? _selectedProfile;

    partial void OnSelectedProfileChanged(string? value)
    {
        if (!string.IsNullOrEmpty(value)) SourcePath = value;
    }

    // Keeps the quick selection in sync with a manually chosen path.
    partial void OnSourcePathChanged(string value)
    {
        if (!string.IsNullOrEmpty(value) && UserProfiles.Contains(value) && SelectedProfile != value)
            SelectedProfile = value;
        CheckAvailability();
        ScheduleAutoStorageCheck();
    }

    // ---- Storage monitoring ----
    [ObservableProperty] private string _storageInfo = "";

    // The storage line in both languages. On a language change only the other
    // version is shown - the line used to stay in the old language until the
    // next recalculation.
    private string _storageInfoDe = "", _storageInfoEn = "";

    private void SetStorageInfo(string de, string en)
    {
        _storageInfoDe = de;
        _storageInfoEn = en;
        StorageInfo = Tr(de, en);
    }

    [ObservableProperty] private bool _storageInfoVisible;
    // Traffic light: 0 = neutral/gray, 1 = green (fits), 2 = yellow (tight), 3 = red (not enough)
    [ObservableProperty] private int _storageStatus;
    [ObservableProperty] private bool _storageChecking;

    // ---- Progress display ----
    [ObservableProperty] private bool _progressVisible;
    [ObservableProperty] private string _progressText = "";
    private string _currentActionText = "";

    public string BtnCheckStorage => Tr("Speicher prüfen", "Check storage");

    // Color of the status line matching the traffic light.
    public Avalonia.Media.IBrush StorageBrush => StorageStatus switch
    {
        1 => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#40C057")), // green
        2 => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#F1A100")), // yellow/orange
        3 => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#E03131")), // red
        _ => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#9AA0A6")), // gray
    };

    partial void OnStorageStatusChanged(int value) => OnPropertyChanged(nameof(StorageBrush));

    // Automatic size calculation (can be switched off).
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

    // ---- Own backup folder (can be switched off, default: on) ----
    // When backing up, a folder per computer and day is created below the
    // chosen target: G:\ -> G:\DASI_<computer>_DD_MM_YYYY. If the selection
    // already is a backup (or today's folder already exists), it is updated.
    // The switch has no effect on restore - an existing backup is chosen
    // there.
    [ObservableProperty] private bool _ownBackupFolder = true;

    public string OwnFolderLabel => OwnBackupFolder
        ? Tr("Eigener Ordner: AN", "Own folder: ON")
        : Tr("Eigener Ordner: AUS", "Own folder: OFF");

    /// <summary>Tells where the next backup goes - based on the real folder.</summary>
    public string OwnFolderTip
    {
        get
        {
            var basis = string.IsNullOrWhiteSpace(_zielBasis) ? @"G:\" : _zielBasis;
            var beispiel = !IsRestoreMode && OwnBackupFolder && !string.IsNullOrWhiteSpace(BackupPath)
                ? BackupPath
                : BackupTarget.Resolve(basis, true, Environment.MachineName, DateTime.Now);
            if (OwnBackupFolder && BackupTarget.IsBackupFolder(beispiel))
                return Tr($"Gesichert wird in die vorhandene Sicherung - sie wird aktualisiert:\n{beispiel}\n\nAbschalten, um direkt in das gewählte Ziel zu sichern.",
                          $"The backup goes into the existing backup and updates it:\n{beispiel}\n\nTurn off to back up directly into the chosen target.");
            return OwnBackupFolder
                ? Tr($"Beim Sichern wird ein Ordner je Rechner und Tag angelegt:\n{beispiel}\n\nGibt es ihn schon, wird die Sicherung darin aktualisiert. Abschalten, um direkt in das gewählte Ziel zu sichern.",
                     $"When backing up, a folder per computer and day is created:\n{beispiel}\n\nIf it already exists, the backup in it is updated. Turn off to back up directly into the chosen target.")
                : Tr($"Gesichert wird direkt in das gewählte Ziel.\n\nEinschalten, um je Sicherung einen eigenen Ordner anzulegen, z. B.:\n{beispiel}",
                     $"The backup goes directly into the chosen target.\n\nTurn on to create a separate folder per backup, e.g.:\n{beispiel}");
        }
    }

    public bool OwnFolderToggleEnabled => UiEnabled && !IsRestoreMode;

    partial void OnOwnBackupFolderChanged(bool value)
    {
        OnPropertyChanged(nameof(OwnFolderLabel));
        OnPropertyChanged(nameof(OwnFolderTip));
        ShowTarget();
    }

    /// <summary>
    /// What the user picked as the target, e.g. "G:\". When backing up with
    /// an own folder, the field already shows the final folder derived from it
    /// ("G:\DASI_Computer_DD_MM_YYYY") - exactly where the backup goes. The
    /// selection is remembered so that switching off and changing the mode can
    /// go back to what was picked.
    /// </summary>
    private string _zielBasis = "";

    private void SetTarget(string basis)
    {
        _zielBasis = basis;
        ShowTarget();
    }

    private void ShowTarget()
    {
        if (string.IsNullOrWhiteSpace(_zielBasis)) return;
        BackupPath = !IsRestoreMode && OwnBackupFolder
            ? BackupTarget.Resolve(_zielBasis, true, Environment.MachineName, DateTime.Now)
            : _zielBasis;
    }

    [RelayCommand]
    private void ToggleOwnFolder() => OwnBackupFolder = !OwnBackupFolder;

    /// <summary>
    /// Target of the current run. When backing up with an own folder, this is
    /// the newly created DASI folder, otherwise the chosen target. The winget
    /// export and the log file must go there - not into the chosen target
    /// above it.
    /// </summary>
    private string _laufZiel = "";

    private System.Threading.CancellationTokenSource? _autoStorageCts;

    /// <summary>
    /// Triggers the automatic calculation with a delay (debounce), so the whole
    /// folder tree is not walked right away on every click. Shows no popup -
    /// only the colored info line.
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
                await Task.Delay(900, cts.Token); // wait briefly until the selection has "settled"
                if (cts.Token.IsCancellationRequested) return;
                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    if (!cts.Token.IsCancellationRequested)
                        await RunStorageCheck(showPopup: false);
                });
            }
            catch (TaskCanceledException) { /* a new trigger came in between */ }
        });
    }

    partial void OnBackupPathChanged(string value)
    {
        OnPropertyChanged(nameof(OwnFolderTip));
        // Another backup, other folders in it: the selection no longer applies.
        if (IsRestoreMode && SelLaufwerk) SelLaufwerk = false;
        CheckAvailability();
        // Show the free space on the target drive right away (lightweight).
        if (string.IsNullOrWhiteSpace(value))
        {
            StorageInfoVisible = false;
            SetStorageInfo("", "");
            StorageStatus = 0;
            return;
        }
        var free = SystemHelpers.GetFreeSpace(value);
        if (free < 0)
        {
            SetStorageInfo("Freier Speicher am Ziel: nicht ermittelbar",
                           "Free space on target: unavailable");
            StorageStatus = 0;
        }
        else
        {
            SetStorageInfo($"Frei am Ziel: {SystemHelpers.FormatBytes(free)}  -  für Größenschätzung 'Speicher prüfen'",
                           $"Free on target: {SystemHelpers.FormatBytes(free)}  -  use 'Check storage' for a size estimate");
            StorageStatus = 0;
        }
        StorageInfoVisible = true;
        ScheduleAutoStorageCheck();
    }

    [ObservableProperty] private bool _detailedLogging;
    [ObservableProperty] private string _loggingLabel = "Log: aus";

    [ObservableProperty] private bool _updateAvailable;
    // The offered version; the notice text is built from it in the current
    // language on every read, so it follows a language change as well.
    private string? _angeboteneVersion;
    public string UpdateBannerText => _angeboteneVersion is null ? "" : Tr(
        $"\uD83D\uDD04 Neue Version {_angeboteneVersion} verfügbar  (installiert: {VersionString})",
        $"\uD83D\uDD04 New version {_angeboteneVersion} available  (installed: {VersionString})");

    // ---- Language ----
    // IsEnglish drives the switch; true = English.
    [ObservableProperty] private bool _isEnglish;

    // Selection in the language drop-down: 0 = German, 1 = English.
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
        // Have all localized labels re-evaluated.
        OnPropertyChanged(string.Empty);
        // Update the dynamic labels as well.
        LoggingLabel = Tr(DetailedLogging ? "Log: an" : "Log: aus",
                          DetailedLogging ? "Log: on" : "Log: off");
        StorageInfo = Tr(_storageInfoDe, _storageInfoEn);

        // Switch the welcome text along as long as the log has no real lines yet.
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

    // ---- Localized UI labels ----
    public string SectionOptions   => Tr("Optionen & Verzeichnisse", "Options & directories");
    public string LabelUserProfile => Tr("Benutzerprofil:", "User profile:");
    public string LabelQuickSelect => Tr("Schnellauswahl:", "Quick select:");
    public string QuickSelectHint  => Tr("– Profil wählen –", "– select profile –");
    public string LabelBackupTarget=> Tr("Backup-Ziel/Quelle:", "Backup target/source:");
    public string AppSubtitle      => Tr("Backup & Wiederherstellung von Benutzerprofilen",
                                         "Backup & restore of user profiles");
    public string BtnClearPaths    => Tr("Pfade leeren", "Clear paths");
    public string BtnExecute       => Tr("Ausgewählte Aktionen starten", "Start selected actions");
    public string LogTitle         => Tr("Aktivitäts-Protokoll", "Activity log");
    public string BtnClearLog      => Tr("Log leeren", "Clear log");
    public string BannerHint       => Tr("Wird geprüft, heruntergeladen und ersetzt - erst nach Rückfrage.",
                                         "Checked, downloaded and replaced - only after confirmation.");
    public string BtnUpdateNow     => Tr("Jetzt aktualisieren", "Update now");
    public string BtnUpdateNotes   => Tr("Was ist neu?", "What's new?");

    public string TgUserProfile    => Tr("Windows Benutzerprofil", "Windows user profile");
    // As with the other special items, the label follows the mode - the boxes
    // work in both directions, and "back up" would simply be wrong on restore.
    // Deliberately without "with": both work without the profile as well -
    // then on their own.
    public string TgUserAppData => IsRestoreMode
        ? Tr("AppData wiederherstellen", "Restore AppData")
        : Tr("AppData sichern", "Back up AppData");
    public string TgUserCloud => IsRestoreMode
        ? Tr("Cloud-Anbieter wiederherstellen", "Restore cloud providers")
        : Tr("Cloud-Anbieter sichern", "Back up cloud providers");
    public string TgFirefox        => Tr("Firefox-Profil", "Firefox profile");
    public string TgEdge           => Tr("Edge-Profil", "Edge profile");
    public string TgChrome         => Tr("Chrome-Profil", "Chrome profile");
    public string TgBrave          => Tr("Brave-Profil", "Brave profile");
    // One item for all Opera editions (Opera, GX, Beta ...) - the tooltip
    // lists which ones were found.
    public string TgOpera          => Tr("Opera-Profil (auch GX)", "Opera profile (incl. GX)");
    public string TgThunderbird    => Tr("Thunderbird-Profil", "Thunderbird profile");

    // Labels of the switch
    public string TabBackup        => Tr("Sichern", "Backup");
    public string TabRestore       => Tr("Wiederherstellen", "Restore");

    // ---- Selection of actions (applies to the currently selected mode) ----
    [ObservableProperty] private bool _selUser;
    [ObservableProperty] private bool _selFirefox;
    [ObservableProperty] private bool _selEdge;
    [ObservableProperty] private bool _selChrome;
    [ObservableProperty] private bool _selBrave;
    [ObservableProperty] private bool _selOpera;
    [ObservableProperty] private bool _selThunderbird;
    [ObservableProperty] private bool _selWinget;
    [ObservableProperty] private bool _selWlan;
    [ObservableProperty] private bool _selPrinter;
    /// <summary>Design &amp; Desktop - formerly two items (Design, Desktop layout), now one with three parts.</summary>
    [ObservableProperty] private bool _selDesignDesktop;
    /// <summary>Folders under C:\ - which ones is picked from a list when the box is checked.</summary>
    [ObservableProperty] private bool _selLaufwerk;

    // ---- Extra options for the user profile ----
    //
    // Both are deliberately OFF by default. They multiply the amount of data
    // and the run time; whoever needs them should switch them on deliberately -
    // not leave them on by accident.
    [ObservableProperty] private bool _selUserAppData;
    [ObservableProperty] private bool _selUserCloud;

    /// <summary>
    /// The two extra options can only be used if the user profile is selected
    /// at all - otherwise they would have no effect.
    /// </summary>
    // AppData and cloud work without the profile as well - they used to be
    // tied to it and locked without the profile box.
    public bool UserOptionsEnabled => UiEnabled;

    // When a size-relevant selection changes, trigger the automatic calculation.
    partial void OnSelUserChanged(bool value)
    {
        ScheduleAutoStorageCheck();
    }

    partial void OnSelUserAppDataChanged(bool value) => ScheduleAutoStorageCheck();
    partial void OnSelUserCloudChanged(bool value) => ScheduleAutoStorageCheck();

    partial void OnSelFirefoxChanged(bool value) => ScheduleAutoStorageCheck();
    partial void OnSelEdgeChanged(bool value) => ScheduleAutoStorageCheck();
    partial void OnSelChromeChanged(bool value) => ScheduleAutoStorageCheck();
    partial void OnSelBraveChanged(bool value) => ScheduleAutoStorageCheck();
    partial void OnSelOperaChanged(bool value) => ScheduleAutoStorageCheck();
    partial void OnSelThunderbirdChanged(bool value) => ScheduleAutoStorageCheck();
    partial void OnSelWlanChanged(bool value) => ScheduleAutoStorageCheck();
    partial void OnSelPrinterChanged(bool value) => ScheduleAutoStorageCheck();
    // ---- Folders under C:\: pre-select the choice ----
    //
    // Like checking any other item, this one only pre-selects: checking the
    // box shows the list, "Übernehmen" (Apply) remembers the folders, and the
    // backup or restore only runs with "Ausgewählte Aktionen starten" (Start
    // selected actions). The button in the item shows the count and changes
    // the choice.
    private List<DriveFolders.Eintrag> _laufwerkGewaehlt = new();
    private bool _laufwerkWaehltGerade;

    /// <summary>The pre-selected folders.</summary>
    public IReadOnlyList<DriveFolders.Eintrag> LaufwerkGewaehlt => _laufwerkGewaehlt;
    public string LaufwerkKnopf => Tr($"{_laufwerkGewaehlt.Count} Ordner …", $"{_laufwerkGewaehlt.Count} {(_laufwerkGewaehlt.Count == 1 ? "folder" : "folders")} …");
    public bool LaufwerkKnopfSichtbar => SelLaufwerk && _laufwerkGewaehlt.Count > 0;

    partial void OnSelLaufwerkChanged(bool value)
    {
        if (value && _laufwerkGewaehlt.Count == 0 && !_laufwerkWaehltGerade)
            // Only after the click: if the box is unchecked again by then, no list.
            Dispatcher.UIThread.Post(async () => { if (SelLaufwerk && _laufwerkGewaehlt.Count == 0) await ChooseDriveFolders(); });
        else if (!value) _laufwerkGewaehlt = new();
        NotifyDriveFolders();
        ScheduleAutoStorageCheck();
    }

    private void NotifyDriveFolders()
    {
        OnPropertyChanged(nameof(LaufwerkKnopf));
        OnPropertyChanged(nameof(LaufwerkKnopfSichtbar));
        OnPropertyChanged(nameof(LaufwerkGewaehlt));
        OnPropertyChanged(nameof(TipLaufwerk));
    }

    [RelayCommand]
    private async Task ChangeDriveFolders() => await ChooseDriveFolders();

    /// <summary>
    /// Shows the list and remembers the choice. Cancelled: the previous choice
    /// stays - if there was none, the box is unchecked again. Nothing chosen:
    /// box unchecked.
    /// </summary>
    private async Task ChooseDriveFolders()
    {
        if (_laufwerkWaehltGerade) return;
        _laufwerkWaehltGerade = true;
        try
        {
            var eintraege = IsRestoreMode ? DriveFolders.InBackup(BackupPath)
                                          : DriveFolders.ListFolders(DriveFolders.Wurzel, BackupPath);
            var vorher = _laufwerkGewaehlt.Select(e => e.Name).ToList();
            List<DriveFolders.Eintrag>? wahl = null;
            if (LaufwerkAuswahl is not null) wahl = await LaufwerkAuswahl(eintraege, IsRestoreMode, vorher);
            else if (GetMainWindow() is { } win) wahl = await FolderSelectionDialog.Show(win, eintraege, IsRestoreMode, vorher);
            if (wahl is null)
            {
                if (_laufwerkGewaehlt.Count == 0) SelLaufwerk = false;
                return;
            }
            _laufwerkGewaehlt = wahl;
            if (wahl.Count == 0) { SelLaufwerk = false; return; }
            SelLaufwerk = true;
            var namen = string.Join(", ", wahl.Select(e => e.Name));
            Log(IsRestoreMode
                ? Tr($"[INFO] Vorgemerkt - Ordner nach {DriveFolders.Wurzel}: {namen}. Zurückgeschrieben wird mit \"Ausgewählte Aktionen starten\".",
                     $"[INFO] Selected - folders to {DriveFolders.Wurzel}: {namen}. They are written back with \"Start selected actions\".")
                : Tr($"[INFO] Vorgemerkt - Ordner unter {DriveFolders.Wurzel}: {namen}. Gesichert wird mit \"Ausgewählte Aktionen starten\".",
                     $"[INFO] Selected - folders under {DriveFolders.Wurzel}: {namen}. They are backed up with \"Start selected actions\"."));
        }
        finally
        {
            _laufwerkWaehltGerade = false;
            NotifyDriveFolders();
            ScheduleAutoStorageCheck();
        }
    }

    partial void OnSelDesignDesktopChanged(bool value)
    {
        // Item on but no part selected: the defaults. Item off: all parts off -
        // only what actually runs stays checked. If the change comes from a part
        // (PartChanged), the parts stay as they are.
        if (!_teileSetzen)
        {
            if (value && !TeilDesign && !TeilSymbole && !TeilScreenshot) SetParts(_vorgabe);
            else if (!value) SetParts((false, false, false));
        }
        ScheduleAutoStorageCheck();
    }

    // ---- Availability of the items ----
    //
    // Backup: program backups are locked if the program is not set up for the
    // selected user. Restore: every item the selected backup does not contain
    // is locked. The reason is in the tooltip of the locked item. The rules
    // live in AppCatalog.
    private readonly System.Collections.Generic.Dictionary<Punkt, (string De, string En)?> _gesperrt = new();
    private string? _operaFunde;

    private bool IsAvailable(Punkt p) => !_gesperrt.TryGetValue(p, out var g) || g is null;
    private string? Reason(Punkt p) => _gesperrt.TryGetValue(p, out var g) && g is { } x ? Tr(x.De, x.En) : null;

    public bool EnUser        => UiEnabled && IsAvailable(Punkt.Profil);
    public bool EnAppData     => UserOptionsEnabled && IsAvailable(Punkt.AppData);
    public bool EnCloud       => UserOptionsEnabled && IsAvailable(Punkt.Cloud);
    public bool EnFirefox     => UiEnabled && IsAvailable(Punkt.Firefox);
    public bool EnEdge        => UiEnabled && IsAvailable(Punkt.Edge);
    public bool EnChrome      => UiEnabled && IsAvailable(Punkt.Chrome);
    public bool EnBrave       => UiEnabled && IsAvailable(Punkt.Brave);
    public bool EnOpera       => UiEnabled && IsAvailable(Punkt.Opera);
    public bool EnThunderbird => UiEnabled && IsAvailable(Punkt.Thunderbird);
    public bool EnWinget      => UiEnabled && IsAvailable(Punkt.Winget);
    public bool EnWlan        => UiEnabled && IsAvailable(Punkt.Wlan);
    public bool EnPrinter     => UiEnabled && IsAvailable(Punkt.Drucker);
    public bool EnDesignDesktop => UiEnabled && (IsAvailable(Punkt.Hintergrund) || IsAvailable(Punkt.DesktopShot));
    public bool EnLaufwerk    => UiEnabled && IsAvailable(Punkt.Laufwerk);

    public string? TipUser        => Reason(Punkt.Profil);
    public string? TipAppData     => Reason(Punkt.AppData);
    public string? TipCloud       => Reason(Punkt.Cloud);
    public string? TipFirefox     => Reason(Punkt.Firefox);
    public string? TipEdge        => Reason(Punkt.Edge);
    public string? TipChrome      => Reason(Punkt.Chrome);
    public string? TipBrave       => Reason(Punkt.Brave);
    public string? TipThunderbird => Reason(Punkt.Thunderbird);
    public string? TipWinget      => Reason(Punkt.Winget);
    public string? TipWlan        => Reason(Punkt.Wlan);
    public string? TipPrinter     => Reason(Punkt.Drucker);
    // Also when available: what happens when checking it, or what is
    // pre-selected.
    public string? TipLaufwerk    => Reason(Punkt.Laufwerk)
        ?? (_laufwerkGewaehlt.Count > 0
            ? Tr($"Vorgemerkt: {string.Join(", ", _laufwerkGewaehlt.Select(e => e.Name))}. Ändern über den Knopf rechts.",
                 $"Selected: {string.Join(", ", _laufwerkGewaehlt.Select(e => e.Name))}. Change with the button on the right.")
            : IsRestoreMode
            ? Tr($"Beim Anhaken erscheint die Liste der gesicherten Ordner zum Vormerken. Zurückgeschrieben nach {DriveFolders.Wurzel} wird mit \"Ausgewählte Aktionen starten\"; was dort schon liegt, wird ergänzt.",
                 $"When ticked, the list of the backed-up folders appears for selection. They are written back to {DriveFolders.Wurzel} with \"Start selected actions\"; anything already there is added to.")
            : Tr($"Beim Anhaken erscheint die Liste der Ordner unter {DriveFolders.Wurzel} (etwa Daten, Scans) zum Vormerken. Gesichert wird mit \"Ausgewählte Aktionen starten\". Die Windows-Ordner (Windows, Programme, Benutzer …) sind in der Liste ausgeblendet.",
                 $"When ticked, the list of the folders under {DriveFolders.Wurzel} (such as data or scans) appears for selection. They are backed up with \"Start selected actions\". The Windows folders (Windows, Program Files, Users …) are hidden in the list."));
    // Also when available: everything that belongs to it.
    public string? TipDesignDesktop => !IsAvailable(Punkt.Hintergrund) && !IsAvailable(Punkt.DesktopShot)
        ? Tr("Die gewählte Sicherung enthält weder Design noch Desktop-Symbole oder Screenshot.", "The selected backup contains neither design nor desktop icons or a screenshot.")
        : Tr("Wie der Arbeitsplatz aussieht: Design (Hintergrund, Farben, Mauszeiger, Taskleiste, Start), die Lage der Desktop-Symbole, die angehefteten Taskleisten-Symbole und auf Wunsch ein Foto des Desktops.",
             "How the workspace looks: design (wallpaper, colors, pointer, taskbar, Start), where the desktop icons are, the icons pinned to the taskbar and, if wanted, a picture of the desktop.");
    // For Opera also when available: which editions were found.
    public string? TipOpera => Reason(Punkt.Opera)
        ?? (_operaFunde is null ? null : Tr($"Gefunden: {_operaFunde}", $"Found: {_operaFunde}"));

    /// <summary>Re-evaluates all items and unchecks the locked ones.</summary>
    private void CheckAvailability()
    {
        var sicherung = IsRestoreMode ? AppCatalog.ForRestore(BackupPath) : null;
        foreach (var p in Enum.GetValues<Punkt>())
            _gesperrt[p] = sicherung is not null ? sicherung[p] : AppCatalog.ForBackup(p, SourcePath);

        // Which Opera editions exist where we are reading right now?
        var operaOrt = IsRestoreMode
            ? (string.IsNullOrWhiteSpace(BackupPath) ? null : Path.Combine(BackupPath, AppCatalog.Opera.BackupOrdner))
            : (string.IsNullOrWhiteSpace(SourcePath) ? null : AppCatalog.PathIn(SourcePath, AppCatalog.Opera.Rel));
        var funde = operaOrt is null ? new System.Collections.Generic.List<string>() : AppCatalog.Opera.Variants(operaOrt);
        _operaFunde = funde.Count == 0 ? null : string.Join(", ", funde.Select(AppCatalog.Opera.DisplayName));

        // Design & Desktop: which parts are in the backup? When backing up, all of
        // them exist. If that changes (mode, another backup), the parts go back to
        // their defaults - see ApplyPartDefaults.
        _hatScreenshot = _hatSymbole = true;
        if (IsRestoreMode && !string.IsNullOrWhiteSpace(BackupPath) && Directory.Exists(BackupPath))
        {
            _hatScreenshot = AppCatalog.HasDesktopScreenshot(BackupPath);
            _hatSymbole = AppCatalog.HasDesktopIcons(BackupPath);
        }
        ApplyPartDefaults();

        // What is locked cannot stay selected.
        if (!IsAvailable(Punkt.Profil)) SelUser = false;
        if (!IsAvailable(Punkt.AppData)) SelUserAppData = false;
        if (!IsAvailable(Punkt.Cloud)) SelUserCloud = false;
        if (!IsAvailable(Punkt.Firefox)) SelFirefox = false;
        if (!IsAvailable(Punkt.Edge)) SelEdge = false;
        if (!IsAvailable(Punkt.Chrome)) SelChrome = false;
        if (!IsAvailable(Punkt.Brave)) SelBrave = false;
        if (!IsAvailable(Punkt.Opera)) SelOpera = false;
        if (!IsAvailable(Punkt.Thunderbird)) SelThunderbird = false;
        if (!IsAvailable(Punkt.Winget)) SelWinget = false;
        if (!IsAvailable(Punkt.Wlan)) SelWlan = false;
        if (!IsAvailable(Punkt.Drucker)) SelPrinter = false;
        if (!IsAvailable(Punkt.Hintergrund) && !IsAvailable(Punkt.DesktopShot)) SelDesignDesktop = false;
        if (!IsAvailable(Punkt.Laufwerk)) SelLaufwerk = false;

        NotifyItemsChanged();
    }

    private void NotifyItemsChanged()
    {
        foreach (var n in new[]
                 {
                     nameof(EnUser), nameof(EnAppData), nameof(EnCloud), nameof(EnFirefox), nameof(EnEdge),
                     nameof(EnChrome), nameof(EnBrave), nameof(EnOpera), nameof(EnThunderbird), nameof(EnWinget),
                     nameof(EnWlan), nameof(EnPrinter), nameof(EnDesignDesktop), nameof(EnLaufwerk),
                     nameof(TipUser), nameof(TipAppData), nameof(TipCloud), nameof(TipFirefox), nameof(TipEdge),
                     nameof(TipChrome), nameof(TipBrave), nameof(TipOpera), nameof(TipThunderbird), nameof(TipWinget),
                     nameof(TipWlan), nameof(TipPrinter), nameof(TipDesignDesktop), nameof(TipLaufwerk),
                     nameof(EnTeilDesign), nameof(EnTeilSymbole), nameof(EnTeilScreenshot),
                     nameof(TipTeilDesign), nameof(TipTeilSymbole), nameof(TipTeilScreenshot),
                 })
            OnPropertyChanged(n);
    }

    // ---- Mode: false = backup, true = restore ----
    [ObservableProperty] private bool _isRestoreMode;

    public bool IsBackupMode
    {
        get => !IsRestoreMode;
        set { if (value) IsRestoreMode = false; }
    }

    partial void OnIsRestoreModeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsBackupMode));
        // Update labels that depend on the mode.
        OnPropertyChanged(nameof(TgUserAppData));
        OnPropertyChanged(nameof(TgUserCloud));
        OnPropertyChanged(nameof(TgWinget));
        OnPropertyChanged(nameof(TgWlan));
        OnPropertyChanged(nameof(TgPrinter));
        OnPropertyChanged(nameof(TgDesignDesktop));
        OnPropertyChanged(nameof(TgLaufwerk));
        // The choice only applies to the direction it was made for.
        SelLaufwerk = false;
        OnPropertyChanged(nameof(OwnFolderToggleEnabled));
        ShowTarget();
        CheckAvailability();
        ScheduleAutoStorageCheck();
    }

    // Mode-dependent labels for the special items.
    public string TgWinget => IsRestoreMode
        ? Tr("Programme installieren (Winget)", "Install programs (winget)")
        : Tr("Programme exportieren (Winget)", "Export programs (winget)");
    public string TgWlan => IsRestoreMode
        ? Tr("WLAN Profile importieren", "Import WiFi profiles")
        : Tr("WLAN Profile exportieren", "Export WiFi profiles");
    public string TgPrinter => IsRestoreMode
        ? Tr("Drucker & Treiber wiederherstellen", "Restore printers & drivers")
        : Tr("Drucker & Treiber sichern", "Back up printers & drivers");
    public string TgLaufwerk => IsRestoreMode
        ? Tr($"Ordner unter {DriveFolders.Wurzel} wiederherstellen", $"Restore folders under {DriveFolders.Wurzel}")
        : Tr($"Ordner unter {DriveFolders.Wurzel} sichern", $"Back up folders under {DriveFolders.Wurzel}");
    public string TgDesignDesktop => IsRestoreMode
        ? Tr("Design & Desktop wiederherstellen", "Restore design & desktop")
        : Tr("Design & Desktop sichern", "Back up design & desktop");

    // ---- Design & Desktop: the three parts ----
    //
    // Formerly two items: "Design" and "Desktop layout" (screenshot OR icon
    // positions). Now one item; the boxes next to it choose what belongs to
    // it - icons AND picture together, too. Item and parts are linked:
    // checking a part checks the item, unchecking the last one unchecks it.
    [ObservableProperty] private bool _teilDesign;
    [ObservableProperty] private bool _teilSymbole;
    [ObservableProperty] private bool _teilScreenshot;

    partial void OnTeilDesignChanged(bool value) { PartChanged(value); ScheduleAutoStorageCheck(); }
    partial void OnTeilSymboleChanged(bool value) => PartChanged(value);
    partial void OnTeilScreenshotChanged(bool value) => PartChanged(value);

    /// <summary>A part checked: the item too. The last one unchecked: the item off.</summary>
    private void PartChanged(bool an)
    {
        if (_teileSetzen) return;
        _teileSetzen = true;
        try
        {
            if (an) SelDesignDesktop = true;
            else if (!TeilDesign && !TeilSymbole && !TeilScreenshot) SelDesignDesktop = false;
        }
        finally { _teileSetzen = false; }
    }

    /// <summary>The parts are being set by code right now - then do not feed back into the item.</summary>
    private bool _teileSetzen;

    /// <summary>The parts that checking the item selects - see ApplyPartDefaults.</summary>
    private (bool Design, bool Symbole, bool Screenshot) _vorgabe = (true, true, false);

    private void SetParts((bool Design, bool Symbole, bool Screenshot) t)
    {
        var vorher = _teileSetzen;
        _teileSetzen = true;
        try { TeilDesign = t.Design; TeilSymbole = t.Symbole; TeilScreenshot = t.Screenshot; }
        finally { _teileSetzen = vorher; }
    }

    public string TgTeilDesign => Tr("Design", "Design");
    public string TgTeilSymbole => Tr("Symbole + Taskleiste", "Icons + taskbar");
    public string TgTeilScreenshot => Tr("Screenshot", "Screenshot");

    // On restore: only what is in the backup.
    private bool _hatScreenshot = true, _hatSymbole = true;
    private (bool, bool, bool, bool)? _teileStand;
    // Usable as far as the part exists - even with the item unchecked.
    public bool EnTeilDesign => EnDesignDesktop && IsAvailable(Punkt.Hintergrund);
    public bool EnTeilSymbole => EnDesignDesktop && _hatSymbole;
    public bool EnTeilScreenshot => EnDesignDesktop && _hatScreenshot;

    /// <summary>
    /// The default parts, determined anew as soon as the mode or the content of
    /// the backup changes - otherwise the user's choice stays. Backup: design
    /// and icons, the picture only on request. Restore: everything the backup
    /// contains; the picture only if there are no icon positions (then one
    /// arranges by it by hand). If the item is on, the new default applies at
    /// once, otherwise when it is checked.
    /// </summary>
    private void ApplyPartDefaults()
    {
        var design = IsAvailable(Punkt.Hintergrund);
        var stand = (IsRestoreMode, design, _hatSymbole, _hatScreenshot);
        if (_teileStand == stand) return;
        _teileStand = stand;
        _vorgabe = (design, _hatSymbole, IsRestoreMode && _hatScreenshot && !_hatSymbole);
        if (SelDesignDesktop) SetParts(_vorgabe);
    }

    public string? TipTeilDesign => Reason(Punkt.Hintergrund)
        ?? Tr("Hintergrund (Bild oder Volltonfarbe), Hell/Dunkel für Windows und Apps, Akzentfarbe, Transparenz, Mauszeiger, Taskleiste und Start",
              "Wallpaper (image or solid color), light/dark mode for Windows and apps, accent color, transparency, mouse pointer, taskbar and Start");
    public string TipTeilSymbole => !_hatSymbole
        ? Tr("Die gewählte Sicherung enthält weder Symbolpositionen noch Taskleisten-Symbole.", "The selected backup contains neither icon positions nor taskbar icons.")
        : IsRestoreMode
            ? Tr("Heftet die gesicherten Symbole wieder an die Taskleiste (der Explorer startet dafür kurz neu) und setzt jedes Desktop-Symbol an seine Stelle (wie DesktopOK).",
                 "Pins the saved icons to the taskbar again (Explorer restarts briefly) and puts every desktop icon back in its place (like DesktopOK).")
            : Tr("Merkt sich, welches Desktop-Symbol wo liegt (wie DesktopOK), und sichert die an die Taskleiste angehefteten Symbole.",
                 "Remembers where each desktop icon is (like DesktopOK) and backs up the icons pinned to the taskbar.");
    public string TipTeilScreenshot => !_hatScreenshot
        ? Tr("Die gewählte Sicherung enthält keinen Desktop-Screenshot.", "The selected backup contains no desktop screenshot.")
        : IsRestoreMode
            ? Tr("Öffnet das Foto des Desktops - die Symbole danach von Hand anordnen.", "Opens the picture of the desktop - then arrange the icons by hand.")
            : Tr("Fotografiert den Desktop, ein Bild pro Bildschirm.", "Takes a picture of the desktop, one per screen.");

    // ---- Hooks for the tests (tests/DaSiGuiTests) ----

    /// <summary>Replaces the selection of folders under C:\ - null: the selection window.</summary>
    internal Func<IReadOnlyList<DriveFolders.Eintrag>, bool, IReadOnlyCollection<string>, Task<List<DriveFolders.Eintrag>?>>? LaufwerkAuswahl { get; set; }

    /// <summary>Replaces the system's folder dialog - null: the real one.</summary>
    internal Func<string, Task<string?>>? OrdnerDialog { get; set; }

    /// <summary>Opens an address in the browser.</summary>
    internal Func<string, bool> Oeffnen { get; set; } = SystemHelpers.Open;

    /// <summary>The services behind the UI - for the winget fake and the image viewer.</summary>
    internal WingetService Winget => _winget;
    internal BackupService Sicherung => _backup;

    public MainWindowViewModel()
    {
        _runner = new ProcessRunner(_cancel);
        _backup = new BackupService(_runner, _cancel, Log);
        // Mirror file progress from the backup into the display.
        _backup.OnFilesProgress = count =>
        {
            var baseText = _currentActionText;
            Dispatcher.UIThread.Post(() =>
                ProgressText = string.IsNullOrEmpty(baseText)
                    ? Tr($"{count} Dateien kopiert...", $"{count} files copied...")
                    : Tr($"{baseText}  -  {count} Dateien", $"{baseText}  -  {count} files"));
        };
        _winget = new WingetService(_runner, _cancel, Log);
        _backup.Winget = _winget;

        // Take the language from the Windows display language (fallback: German).
        // Sets Localizer directly, so the welcome text matches right away.
        var lang = SystemHelpers.GetWindowsLanguage();
        _isEnglish = lang == "en";
        _selectedLanguageIndex = _isEnglish ? 1 : 0;
        Localizer.I.Lang = _isEnglish ? AppLang.En : AppLang.De;
        // This used to keep saying "Log: aus" - even in an English environment,
        // until the language was switched once.
        _loggingLabel = Tr("Log: aus", "Log: off");

        // Take light/dark mode from Windows.
        _isLightTheme = SystemHelpers.IsWindowsLightTheme();
        var app = Avalonia.Application.Current;
        if (app is not null)
            app.RequestedThemeVariant = _isLightTheme
                ? Avalonia.Styling.ThemeVariant.Light
                : Avalonia.Styling.ThemeVariant.Dark;

        // Show the welcome text in the (now set) language.
        _logBuffer.Append(InitialLogText());
        LogText = _logBuffer.ToString();

        // Load user profiles for the quick selection.
        foreach (var p in SystemHelpers.GetUserProfiles()) UserProfiles.Add(p);

        _ = CheckForUpdatesAsync();
    }

    // ---- Toggle labels ----
    partial void OnDetailedLoggingChanged(bool value)
    {
        LoggingLabel = Tr(value ? "Log: an" : "Log: aus",
                          value ? "Log: on" : "Log: off");
        // When switching ON, point out the performance impact: both in the log and
        // as a message on screen.
        if (value)
        {
            var msg = Tr("Detailliertes Log ist aktiv. Das erzeugt sehr viele Zeilen und kann die Geschwindigkeit spürbar verringern.",
                         "Detailed logging is on. It produces a lot of output and can noticeably slow things down.");
            Log(Tr("[HINWEIS] ", "[NOTE] ") + msg);
            // Trigger the dialog without blocking (the handler is synchronous).
            Dispatcher.UIThread.Post(async () =>
                await ShowInfo(Tr("Hinweis", "Notice"), msg));
        }
    }

    /// <summary>
    /// Error lines of the action currently running.
    ///
    /// Why via the log: the services do not throw on failure, they write
    /// "[FEHLER] ..." and return normally. So the queue used to count only
    /// exceptions as errors - a failed robocopy (code 16), a missing backup
    /// folder or a winget that would not start ended up as [OK] in the summary
    /// AND in the log file in the backup target. Precisely the file that proves
    /// a backup ran then reported success.
    ///
    /// In this program the log is the one channel through which every service
    /// reports errors - every place uses "[FEHLER]" or "[ERROR]" for it. Lines
    /// from robocopy's own output ("FEHLER 5 (0x00000005) ...") carry no square
    /// brackets and do not count; if robocopy really fails, the service reports
    /// the exit code itself anyway.
    /// </summary>
    private int _aktionsFehler;

    private void Log(string text)
    {
        if (ActionOutcome.IsErrorLine(text)) System.Threading.Interlocked.Increment(ref _aktionsFehler);

        string snapshot;
        lock (_logLock)
        {
            _logUntouched = false;   // from now on the history is kept
            _logBuffer.Append(text).Append("\r\n");
            if (_laufLog is not null && _laufLog.Length < LaufLogGrenze)
            {
                _laufLog.Append(text).Append("\r\n");
                if (_laufLog.Length >= LaufLogGrenze)
                    _laufLog.Append(Tr("... [PROTOKOLL HIER ABGESCHNITTEN] ...\r\n", "... [LOG CUT OFF HERE] ...\r\n"));
            }
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

    // Collects the relative profile paths of the selected browser/mail items.
    private System.Collections.Generic.List<string> SelectedProfileRelPaths()
    {
        var list = new System.Collections.Generic.List<string>();
        if (SelFirefox)     list.Add(AppCatalog.Firefox.ProfilRel);
        if (SelEdge)        list.Add(AppCatalog.Edge.ProfilRel);
        if (SelChrome)      list.Add(AppCatalog.Chrome.ProfilRel);
        if (SelBrave)       list.Add(AppCatalog.Brave.ProfilRel);
        if (SelOpera)       list.Add(AppCatalog.Opera.Rel);
        if (SelThunderbird) list.Add(AppCatalog.Thunderbird.ProfilRel);
        return list;
    }

    // Collects the names of the backup subfolders/files of the selected items
    // - for the size calculation in restore mode.
    private System.Collections.Generic.List<string> SelectedBackupSubdirs()
    {
        var list = new System.Collections.Generic.List<string>();
        if (SelUser)        list.Add("Benutzerprofil");
        if (SelFirefox)     list.Add("Firefox-Profil");
        if (SelEdge)        list.Add("Edge-Profil");
        if (SelChrome)      list.Add("Chrome-Profil");
        if (SelBrave)       list.Add("Brave-Profil");
        if (SelOpera)       list.Add(AppCatalog.Opera.BackupOrdner);
        if (SelThunderbird) list.Add("Thunderbird-Profil");
        if (SelWlan)        list.Add("WLAN-Profile");
        if (SelPrinter)     list.Add("Drucker");
        if (SelDesignDesktop && TeilDesign) { list.Add("Hintergrundbild"); list.Add(DesignSettings.Ordner); }
        // The winget list is just a small text file; negligible.
        return list;
    }

    /// <summary>
    /// Cancellation for the size calculation currently running, and whether it
    /// has to run again afterwards with the selection changed in the meantime.
    ///
    /// A second call during a running calculation used to simply return. If
    /// AppData was checked during the seconds a large profile takes, it was
    /// never included - the display showed the size WITHOUT AppData, and the
    /// space needed was underestimated. The two extra options in particular
    /// change the size the most.
    /// </summary>
    private System.Threading.CancellationTokenSource? _storageWalkCts;
    private bool _storageRecheck;
    private bool _storageRecheckPopup;

    private async Task RunStorageCheck(bool showPopup)
    {
        if (StorageChecking)
        {
            _storageRecheck = true;
            _storageRecheckPopup |= showPopup;
            _storageWalkCts?.Cancel();
            return;
        }
        if (string.IsNullOrWhiteSpace(BackupPath))
        {
            SetStorageInfo("Bitte zuerst ein Backup-Ziel/-Quelle festlegen.",
                           "Please set a backup target/source first.");
            StorageStatus = 2;
            StorageInfoVisible = true;
            return;
        }

        StorageChecking = true;
        StorageStatus = 0;
        SetStorageInfo("Berechne Datenmenge...", "Calculating data size...");
        StorageInfoVisible = true;

        var walkCts = new System.Threading.CancellationTokenSource();
        _storageWalkCts = walkCts;
        var ct = walkCts.Token;

        try
        {
            var backupPath = BackupPath;
            var sourcePath = SourcePath;
            bool restore = IsRestoreMode;
            bool selUser = SelUser;
            bool selAppData = SelUserAppData;
            bool selCloud = SelUserCloud;
            var relPaths = SelectedProfileRelPaths();
            var restoreSubdirs = SelectedBackupSubdirs(); // restore only
            // Folders under C:\: for a backup the folders themselves, for a restore their copy in the backup.
            var laufwerk = SelLaufwerk ? _laufwerkGewaehlt.Select(e => e.Pfad).ToList() : new List<string>();

            var (dataSize, free) = await Task.Run(() =>
            {
                long size = 0;
                if (restore)
                {
                    // Restore: sum up only the SELECTED backup subfolders (not the whole
                    // backup folder).
                    foreach (var sub in restoreSubdirs)
                    {
                        var full = System.IO.Path.Combine(backupPath, sub);
                        // For the user profile, the same excludes as for the restore itself:
                        // unchecked, AppData and the cloud folders stay out - then they do not
                        // count here either.
                        if (sub == "Benutzerprofil" && System.IO.Directory.Exists(full))
                            size += SystemHelpers.GetDirectorySize(full,
                                        ProfileExcludes.Build(full, selAppData, selCloud), ct);
                        else if (System.IO.Directory.Exists(full))
                            size += SystemHelpers.GetDirectorySize(full, ct: ct);
                        else if (System.IO.File.Exists(full))
                        {
                            try { size += new System.IO.FileInfo(full).Length; } catch { }
                        }
                    }
                    // Without the profile: AppData and cloud folders from the backup on their own.
                    if (!selUser)
                    {
                        var profilSicherung = System.IO.Path.Combine(backupPath, "Benutzerprofil");
                        var appData = System.IO.Path.Combine(profilSicherung, "AppData");
                        if (selAppData && System.IO.Directory.Exists(appData))
                            size += SystemHelpers.GetDirectorySize(appData, ct: ct);
                        if (selCloud)
                            foreach (var cloud in CloudProviders.Detect(profilSicherung))
                                size += SystemHelpers.GetDirectorySize(cloud.Path, ct: ct);
                    }
                    foreach (var ordner in laufwerk)
                        if (System.IO.Directory.Exists(ordner)) size += SystemHelpers.GetDirectorySize(ordner, ct: ct);
                    var restoreTarget = string.IsNullOrWhiteSpace(sourcePath) ? backupPath : sourcePath;
                    return (size, SystemHelpers.GetFreeSpace(restoreTarget));
                }
                else
                {
                    // Backup: sum of ALL selected sources.
                    if (selUser && !string.IsNullOrWhiteSpace(sourcePath))
                        size += SystemHelpers.GetDirectorySize(sourcePath,
                                    BackupService.GetProfileExcludes(sourcePath, selAppData, selCloud), ct);

                    // Without the profile: AppData (without caches) and cloud folders on their own.
                    if (!selUser && !string.IsNullOrWhiteSpace(sourcePath))
                    {
                        var appData = System.IO.Path.Combine(sourcePath, "AppData");
                        if (selAppData && System.IO.Directory.Exists(appData))
                            size += SystemHelpers.GetDirectorySize(appData,
                                        ProfileExcludes.Build(sourcePath, includeAppData: true, includeCloud: true), ct);
                        if (selCloud)
                            foreach (var cloud in CloudProviders.Detect(sourcePath))
                                size += SystemHelpers.GetDirectorySize(cloud.Path, ct: ct);
                    }

                    // Add the browser/mail profiles one by one.
                    if (!string.IsNullOrWhiteSpace(sourcePath))
                    {
                        foreach (var rel in relPaths)
                        {
                            var full = System.IO.Path.Combine(sourcePath, rel);
                            if (System.IO.Directory.Exists(full))
                                size += SystemHelpers.GetDirectorySize(full, ct: ct);
                        }
                    }
                    foreach (var ordner in laufwerk)
                        if (System.IO.Directory.Exists(ordner)) size += SystemHelpers.GetDirectorySize(ordner, ct: ct);
                    return (size, SystemHelpers.GetFreeSpace(backupPath));
                }
            }, ct);

            var margin = (long)(dataSize * 0.10); // 10% safety margin
            var needed = dataSize + margin;

            var sizeStr = SystemHelpers.FormatBytes(dataSize);
            var freeStr = SystemHelpers.FormatBytes(free);
            string? popupMsg = null;

            if (free < 0)
            {
                SetStorageInfo($"Datenmenge: ca. {sizeStr}  |  Freier Platz: nicht ermittelbar",
                               $"Data size: ~{sizeStr}  |  Free space: unavailable");
                StorageStatus = 2;
            }
            else if (free < needed)
            {
                var missing = SystemHelpers.FormatBytes(needed - free);
                SetStorageInfo($"WARNUNG: Datenmenge ca. {sizeStr}, aber nur {freeStr} frei. Es fehlen ca. {missing} (inkl. 10% Reserve).",
                               $"WARNING: Data size ~{sizeStr}, but only {freeStr} free. About {missing} short (incl. 10% margin).");
                StorageStatus = 3;
                Log(Tr($"[WARNUNG] Speicher reicht möglicherweise nicht: ca. {sizeStr} Daten, {freeStr} frei.",
                       $"[WARNING] Storage may be insufficient: ~{sizeStr} data, {freeStr} free."));
                popupMsg = Tr($"Der Speicher am Ziel reicht möglicherweise nicht aus.\n\nBenötigt (inkl. 10% Reserve): ca. {SystemHelpers.FormatBytes(needed)}\nFrei: {freeStr}\nEs fehlen ca. {missing}.",
                              $"The target may not have enough free space.\n\nNeeded (incl. 10% margin): ~{SystemHelpers.FormatBytes(needed)}\nFree: {freeStr}\nAbout {missing} short.");
            }
            else if (free < needed * 1.5)
            {
                SetStorageInfo($"Datenmenge ca. {sizeStr}, {freeStr} frei - passt, aber knapp.",
                               $"Data size ~{sizeStr}, {freeStr} free - fits, but tight.");
                StorageStatus = 2;
                popupMsg = Tr($"Der Speicher am Ziel reicht, ist aber knapp.\n\nDatenmenge: ca. {sizeStr}\nFrei: {freeStr}",
                              $"The target has enough space, but it is tight.\n\nData size: ~{sizeStr}\nFree: {freeStr}");
            }
            else
            {
                var afterStr = SystemHelpers.FormatBytes(free - dataSize);
                SetStorageInfo($"Datenmenge ca. {sizeStr}, {freeStr} frei. Danach noch ca. {afterStr} frei.",
                               $"Data size ~{sizeStr}, {freeStr} free. About {afterStr} left afterwards.");
                StorageStatus = 1;
            }
            StorageInfoVisible = true;

            // Popup for yellow/red - but only when asked for (manual check), so the
            // automatic calculation does not keep opening windows.
            if (showPopup && popupMsg != null)
                await ShowInfo(Tr("Speicherplatz", "Storage"), popupMsg);
        }
        catch (OperationCanceledException)
        {
            // The selection changed - the recalculation follows in finally.
        }
        catch (Exception ex)
        {
            SetStorageInfo($"Größenberechnung fehlgeschlagen: {ex.Message}",
                           $"Size calculation failed: {ex.Message}");
            StorageStatus = 2;
        }
        finally
        {
            StorageChecking = false;
            if (ReferenceEquals(_storageWalkCts, walkCts)) _storageWalkCts = null;
            walkCts.Dispose();
        }

        if (_storageRecheck)
        {
            var popup = _storageRecheckPopup;
            _storageRecheck = false;
            _storageRecheckPopup = false;
            await RunStorageCheck(popup);
        }
    }

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

    // ---- Light/dark mode ----
    [ObservableProperty] private bool _isLightTheme;

    public string ThemeIcon => IsLightTheme ? "\u2600" : "\u263D"; // sun / moon

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
        _zielBasis = "";
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
        if (SystemHelpers.IsNtfsDrive(folder)) SetTarget(folder);
        else await ShowInfo(Tr("Fehler", "Error"),
            Tr("Das ausgewählte Laufwerk ist NICHT mit NTFS formatiert!\nRobocopy benötigt zwingend NTFS.",
               "The selected drive is NOT formatted as NTFS!\nRobocopy strictly requires NTFS."));
    }

    /// <summary>
    /// Warns before cloud folders are backed up or restored.
    ///
    /// The reason is not just the amount of data. Cloud folders often contain
    /// placeholder files that the provider only downloads on access - robocopy
    /// triggers that and pulls the entire online content onto the disk.
    /// Restoring takes the same path backwards: the client considers the files
    /// changed and uploads everything again, which causes conflicts in shared
    /// folders.
    /// </summary>
    private async Task<bool> ConfirmCloudAsync()
    {
        // On restore, what matters is what is IN THE BACKUP - that is what gets
        // written back. This used to search the target profile; on a fresh
        // computer it has no cloud folders yet, and the prompt said "none
        // detected" while OneDrive was about to be restored.
        var ort = IsRestoreMode ? Path.Combine(BackupPath, "Benutzerprofil") : SourcePath;
        var gefunden = CloudProviders.Detect(ort);
        var liste = gefunden.Count > 0
            ? CloudProviders.Summarize(gefunden)
            : Tr("keine erkannt", "none detected");

        // The real folder name makes it easier to see what is about to happen.
        var beispielName = gefunden.Count > 0 ? Path.GetFileName(gefunden[0].Path) : "OneDrive";
        var beispiel = CloudProviders.RestoredFolderName(beispielName);

        var frage = IsRestoreMode
            ? Tr($"Cloud-Anbieter in der Sicherung: {liste}\n\n"
                 + "Die Cloud-Ordner werden NICHT in die laufenden Cloud-Ordner zurückgeschrieben, sondern "
                 + $"daneben: \"{beispielName}\" landet in \"{beispiel}\". So lädt der Cloud-Client nichts "
                 + "doppelt hoch, und in geteilten Ordnern entstehen keine Konflikte. Was noch fehlt, lässt "
                 + "sich danach in Ruhe vergleichen und von Hand übernehmen.\n\n"
                 + "Das Zurückschreiben dauert entsprechend lange. Oft reicht es, den Cloud-Client auf dem "
                 + "neuen Rechner neu anzumelden - er holt die Daten dann selbst.\n\nTrotzdem fortfahren?",
                 $"Cloud providers in the backup: {liste}\n\n"
                 + "The cloud folders are NOT restored into the live cloud folders but next to them: "
                 + $"\"{beispielName}\" goes to \"{beispiel}\". This way the cloud client does not upload "
                 + "anything twice and shared folders do not get conflicts. Anything still missing can be "
                 + "compared and copied over by hand afterwards.\n\n"
                 + "Restoring takes correspondingly long. Often it is enough to sign in to the cloud client "
                 + "on the new machine - it then fetches the data itself.\n\nContinue anyway?")
            : Tr($"Erkannte Cloud-Anbieter: {liste}\n\n"
                 + "Das Mitsichern dauert sehr lange. Dateien, die nur online liegen, werden dabei "
                 + "vollständig heruntergeladen - das Backup kann dadurch um ein Vielfaches größer "
                 + "werden, als die Anzeige im Explorer vermuten lässt.\n\n"
                 + "Beim späteren Zurückschreiben landen sie neben den Cloud-Ordnern "
                 + $"(\"{beispielName}\" -> \"{beispiel}\"), damit der Cloud-Client nichts doppelt "
                 + "hochlädt.\n\nTrotzdem mitsichern?",
                 $"Detected cloud providers: {liste}\n\n"
                 + "Including them takes a very long time. Files that exist only online are downloaded "
                 + "in full - the backup can therefore become many times larger than Explorer suggests.\n\n"
                 + "When restored later, they go next to the cloud folders "
                 + $"(\"{beispielName}\" -> \"{beispiel}\"), so the cloud client does not upload "
                 + "anything twice.\n\nInclude them anyway?");

        var weiter = await ShowYesNo(Tr("Cloud-Ordner", "Cloud folders"), frage);
        if (!weiter)
            Log(Tr("[INFO] Start abgebrochen - Cloud-Ordner wurden nicht bestätigt.",
                   "[INFO] Start cancelled - cloud folders were not confirmed."));
        return weiter;
    }

    /// <summary>
    /// Warns before AppData is restored. Backing up AppData is harmless - it
    /// only becomes dangerous the other way round: it holds machine-bound data
    /// (licenses, device IDs, sessions) and files of running programs.
    /// </summary>
    private async Task<bool> ConfirmAppDataRestoreAsync()
    {
        var weiter = await ShowYesNo(
            Tr("AppData wiederherstellen", "Restore AppData"),
            Tr("AppData wird zurückgeschrieben.\n\n"
               + "ACHTUNG: In AppData liegen Einstellungen, die an den Rechner gebunden sind - "
               + "Lizenzen, Gerätekennungen und angemeldete Sitzungen. Auf einem anderen Rechner "
               + "oder über eine neuere Programmfassung geschrieben, können einzelne Programme "
               + "danach nicht mehr starten oder eine Neuanmeldung verlangen.\n\n"
               + "Schließe vorher alle Programme. Dateien, die gerade benutzt werden, werden "
               + "übersprungen.\n\nAppData wirklich wiederherstellen?",
               "AppData will be restored.\n\n"
               + "CAUTION: AppData holds settings tied to the machine - licences, device identifiers "
               + "and signed-in sessions. Written on a different machine, or over a newer version of a "
               + "program, individual programs may fail to start afterwards or ask you to sign in "
               + "again.\n\n"
               + "Close all programs first. Files that are in use will be skipped.\n\n"
               + "Really restore AppData?"));

        if (!weiter)
            Log(Tr("[INFO] Start abgebrochen - AppData-Wiederherstellung wurde nicht bestätigt.",
                   "[INFO] Start cancelled - AppData restore was not confirmed."));
        return weiter;
    }

    /// <summary>
    /// Checks before export or import whether winget is ready.
    ///
    /// If it is not, the answer depends on what else is queued: if winget is
    /// alone in the queue, that is the end. If there are other tasks, the user
    /// may continue without the winget step - a broken winget should not
    /// prevent the profile backup.
    /// </summary>
    /// <returns>true if it may continue.</returns>
    private async Task<bool> EnsureWingetReadyAsync(List<string> tasks)
    {
        _cancel.Reset();
        ProgressVisible = true;
        ProgressText = Tr("Prüfe, ob winget einsatzbereit ist...", "Checking whether winget is ready...");
        UiEnabled = false;
        try
        {
            var stand = await _winget.CheckReadinessAsync();
            if (stand.Ready) return true;

            Log(Tr($"[FEHLER] winget ist nicht einsatzbereit: {stand.Problem}",
                   $"[ERROR] winget is not ready: {stand.Problem}"));

            var andere = tasks.Count(t => t is not ("Export-Winget" or "Import-Winget"));
            if (andere == 0)
            {
                await ShowInfo(Tr("winget nicht einsatzbereit", "winget not ready"),
                               stand.Problem + "\n\n" + stand.Hint);
                return false;
            }

            var ohne = await ShowYesNo(
                Tr("winget nicht einsatzbereit", "winget not ready"),
                stand.Problem + "\n\n" + stand.Hint + "\n\n" +
                Tr((andere == 1 ? "Die übrige Aktion ist davon nicht betroffen."
                                : $"Die übrigen {andere} Aktionen sind davon nicht betroffen.")
                   + "\n\nOhne den winget-Schritt fortfahren?",
                   (andere == 1 ? "The remaining action is not affected."
                                : $"The remaining {andere} actions are not affected.")
                   + "\n\nContinue without the winget step?"));

            if (!ohne)
            {
                Log(Tr("[INFO] Start abgebrochen.", "[INFO] Start cancelled."));
                return false;
            }

            tasks.RemoveAll(t => t is "Export-Winget" or "Import-Winget");
            Log(Tr("[INFO] Der winget-Schritt wurde übersprungen, die übrigen Aktionen laufen weiter.",
                   "[INFO] The winget step was skipped, the remaining actions continue."));
            return true;
        }
        finally
        {
            ProgressVisible = false;
            ProgressText = "";
            UiEnabled = true;
        }
    }

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
        // A profile that does not exist is NOT created. Otherwise a restore would
        // create a folder under C:\Users that no user owns - Windows creates a new
        // user's real profile only at the first sign-in, and next to it.
        if (!Directory.Exists(SourcePath))
        {
            await ShowInfo(Tr("Fehler", "Error"), IsRestoreMode
                ? Tr($"Das Zielprofil \"{SourcePath}\" gibt es auf diesem Rechner nicht.\n\n"
                     + "Bitte das Profil des Benutzers wählen, in das zurückgeschrieben werden soll (z. B. C:\\Users\\Anwender). "
                     + "Ein neuer Benutzer muss sich dafür einmal angemeldet haben - erst dann legt Windows sein Profil an.",
                     $"The target profile \"{SourcePath}\" does not exist on this computer.\n\n"
                     + "Please select the profile of the user to restore into (e.g. C:\\Users\\User). "
                     + "A new user must have signed in once - only then does Windows create the profile.")
                : Tr($"Das Benutzerprofil \"{SourcePath}\" gibt es nicht.", $"The user profile \"{SourcePath}\" does not exist."));
            return;
        }
        if (IsRestoreMode && !Directory.Exists(BackupPath))
        {
            await ShowInfo(Tr("Fehler", "Error"),
                Tr($"Den Sicherungsordner \"{BackupPath}\" gibt es nicht.", $"The backup folder \"{BackupPath}\" does not exist."));
            return;
        }

        // Check for battery mode: warn and ask on notebooks without a charger.
        if (SystemHelpers.IsOnBattery(out var battery))
        {
            var pct = battery >= 0 ? $" ({battery}%)" : "";
            // Also write the warning to the log (it stays there).
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

        // ---- Confirmation prompts for the extra options of the user profile ----
        if (SelUserCloud && !await ConfirmCloudAsync()) return;
        if (SelUserAppData && IsRestoreMode && !await ConfirmAppDataRestoreAsync()) return;

        // ---- winget: check first, then work ----
        //
        // Deliberately BEFORE the package selection and before the first copy: a
        // winget that is not ready should show up while the user can still decide
        // something - not after twenty minutes of profile copying.
        if (tasks.Contains("Export-Winget") || tasks.Contains("Import-Winget"))
        {
            if (!await EnsureWingetReadyAsync(tasks)) return;
        }

        // Winget import: ask for the selection NOW, on the UI thread
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
                if (wingetSelection == null) return; // Cancelled
            }
        }

        // Folders under C:\: pre-selected when the item was checked. If the choice
        // is missing (item checked without a list), ask once more now.
        _laufwerkAuswahl = null;
        if (tasks.Contains("Backup-Laufwerk") || tasks.Contains("Restore-Laufwerk"))
        {
            if (_laufwerkGewaehlt.Count == 0) await ChooseDriveFolders();
            if (_laufwerkGewaehlt.Count == 0)
            {
                Log(Tr($"[INFO] Start abgebrochen - keine Ordner unter {DriveFolders.Wurzel} gewählt.",
                       $"[INFO] Start cancelled - no folders under {DriveFolders.Wurzel} selected."));
                return;
            }
            _laufwerkAuswahl = _laufwerkGewaehlt.ToList();
            Log(Tr($"[INFO] Ordner unter {DriveFolders.Wurzel}: {string.Join(", ", _laufwerkAuswahl.Select(e => e.Name))}",
                   $"[INFO] Folders under {DriveFolders.Wurzel}: {string.Join(", ", _laufwerkAuswahl.Select(e => e.Name))}"));
        }

        _cancel.Reset();
        _cancel.FastMode = !DetailedLogging;
        _backup.SourcePath = SourcePath;
        // With the profile, AppData and cloud run as part of the profile backup;
        // without it as actions of their own (see BuildTaskList).
        _backup.IncludeAppData = SelUserAppData;
        _backup.IncludeCloud = SelUserCloud;

        // The backup goes exactly where the field points - with an own folder it
        // already says "G:\DASI_...". It is only created now.
        _laufZiel = BackupPath;
        if (!IsRestoreMode && Directory.Exists(_laufZiel) && BackupTarget.IsBackupFolder(_laufZiel))
        {
            // Into an existing backup: it is updated (the profile mirrored with
            // /MIR). If it evidently comes from another computer, that computer's
            // state would be lost - ask first.
            var vonRechner = BackupTarget.MachineOf(_laufZiel);
            if (vonRechner is not null
                && !string.Equals(vonRechner, BackupTarget.SafeMachineName(Environment.MachineName), StringComparison.OrdinalIgnoreCase)
                && !await ShowYesNo(Tr("Sicherung eines anderen Rechners", "Backup of another computer"),
                    Tr($"Der gewählte Ordner enthält die Sicherung des Rechners \"{vonRechner}\":\n{_laufZiel}\n\n"
                       + "Wird hineingesichert, wird sie mit diesem Rechner abgeglichen - was es hier nicht gibt, wird dort gelöscht.\n\nTrotzdem in diesen Ordner sichern?",
                       $"The selected folder contains the backup of the computer \"{vonRechner}\":\n{_laufZiel}\n\n"
                       + "Backing up into it syncs it with this computer - anything that does not exist here is deleted there.\n\nBack up into this folder anyway?")))
            {
                Log(Tr("[INFO] Start abgebrochen - bitte ein anderes Ziel wählen.",
                       "[INFO] Start cancelled - please choose another target."));
                return;
            }
            Log(Tr($"[INFO] Vorhandene Sicherung wird aktualisiert: {_laufZiel}",
                   $"[INFO] Updating the existing backup: {_laufZiel}"));
        }
        if (!IsRestoreMode && !Directory.Exists(_laufZiel))
        {
            try
            {
                Directory.CreateDirectory(_laufZiel);
            }
            catch (Exception ex)
            {
                await ShowInfo(Tr("Fehler", "Error"),
                    Tr($"Der Sicherungsordner konnte nicht angelegt werden:\n{_laufZiel}\n\n{ex.Message}",
                       $"The backup folder could not be created:\n{_laufZiel}\n\n{ex.Message}"));
                return;
            }
            Log(Tr($"[INFO] Sicherungsordner angelegt: {_laufZiel}",
                   $"[INFO] Backup folder created: {_laufZiel}"));
        }
        _backup.BackupPath = _laufZiel;

        UiEnabled = false;
        IsActionRunning = true;
        Log("\r\n=========================================");
        Log(Tr("Starte Abarbeitung der Warteschlange...", "Starting to process the queue..."));

        // Request performance mode: no standby, higher priority.
        // Guaranteed to be fully reset in finally.
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

    // ---------------------------------------------------------------- Queue

    private List<string> BuildTaskList()
    {
        var t = new List<string>();

        if (IsRestoreMode)
        {
            if (SelUser)        t.Add("Restore-User");
            else
            {
                // Without the profile: AppData and cloud on their own.
                if (SelUserAppData) t.Add("Restore-AppData");
                if (SelUserCloud)   t.Add("Restore-Cloud");
            }
            if (SelFirefox)     t.Add("Restore-Firefox");
            if (SelEdge)        t.Add("Restore-Edge");
            if (SelChrome)      t.Add("Restore-Chrome");
            if (SelBrave)       t.Add("Restore-Brave");
            if (SelOpera)       t.Add("Restore-Opera");
            if (SelThunderbird) t.Add("Restore-Thunderbird");
            if (SelWinget)      t.Add("Import-Winget");
            if (SelWlan)        t.Add("Import-Wlan");
            if (SelPrinter)     t.Add("Import-Printer");
            if (SelLaufwerk)    t.Add("Restore-Laufwerk");
            // Icons and taskbar BEFORE the design: Explorer is restarted for the
            // taskbar. On Windows 11, an Explorer that was killed restores its last
            // saved wallpaper at startup - set before that, the restored color was
            // gone right away and after a Windows restart (even one chosen in
            // Settings).
            if (SelDesignDesktop && TeilSymbole) t.Add("Restore-DesktopIcons");
            if (SelDesignDesktop && TeilDesign)  t.Add("Import-Design");
            // Last: the picture should be open when everything else is done and it is
            // time to arrange the icons.
            if (SelDesignDesktop && TeilScreenshot) t.Add("Show-DesktopShot");
        }
        else
        {
            // First: the picture shows the desktop as it was BEFORE the backup, and
            // hiding the windows briefly right after clicking "Start" does not come
            // across as a disturbance.
            // The picture before the icons: it should show the desktop before
            // anything touches it.
            if (SelDesignDesktop && TeilScreenshot) t.Add("Capture-DesktopShot");
            if (SelDesignDesktop && TeilSymbole)    t.Add("Backup-DesktopIcons");
            if (SelUser)        t.Add("Backup-User");
            else
            {
                // Without the profile: AppData and cloud on their own.
                if (SelUserAppData) t.Add("Backup-AppData");
                if (SelUserCloud)   t.Add("Backup-Cloud");
            }
            if (SelFirefox)     t.Add("Backup-Firefox");
            if (SelEdge)        t.Add("Backup-Edge");
            if (SelChrome)      t.Add("Backup-Chrome");
            if (SelBrave)       t.Add("Backup-Brave");
            if (SelOpera)       t.Add("Backup-Opera");
            if (SelThunderbird) t.Add("Backup-Thunderbird");
            if (SelWinget)      t.Add("Export-Winget");
            if (SelWlan)        t.Add("Export-Wlan");
            if (SelPrinter)     t.Add("Export-Printer");
            if (SelLaufwerk)    t.Add("Backup-Laufwerk");
            if (SelDesignDesktop && TeilDesign) t.Add("Export-Design");
        }
        return t;
    }

    /// <summary>The folders under C:\ chosen for this run.</summary>
    private List<DriveFolders.Eintrag>? _laufwerkAuswahl;

    private async Task RunQueue(List<string> tasks, List<WingetPackage>? wingetSelection)
    {
        int total = tasks.Count;
        int index = 0;
        ProgressVisible = true;

        // For the backup log: start, results per action, all lines.
        lock (_logLock) _laufLog = new System.Text.StringBuilder();
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
            System.Threading.Interlocked.Exchange(ref _aktionsFehler, 0);
            Log(Tr($"\r\n--- Führe Aktion {choice} aus ---", $"\r\n--- Running action {choice} ---"));

            bool ok = true;
            try
            {
                switch (choice)
                {
                    case "Backup-User":        await _backup.BackupUserProfile(); break;
                    case "Backup-AppData":     await _backup.BackupAppDataOnly(); break;
                    case "Backup-Cloud":       await _backup.BackupCloudOnly(); break;
                    case "Backup-Firefox":     await _backup.BackupAppProfile(AppCatalog.Firefox); break;
                    case "Backup-Edge":        await _backup.BackupAppProfile(AppCatalog.Edge); break;
                    case "Backup-Chrome":      await _backup.BackupAppProfile(AppCatalog.Chrome); break;
                    case "Backup-Brave":       await _backup.BackupAppProfile(AppCatalog.Brave); break;
                    case "Backup-Opera":       await _backup.BackupOpera(); break;
                    case "Backup-Thunderbird": await _backup.BackupAppProfile(AppCatalog.Thunderbird); break;
                    case "Export-Winget":      await _winget.ExportAsync(_laufZiel); break;
                    case "Export-Wlan":        await _backup.ExportWlan(); break;
                    case "Export-Printer":     await _backup.ExportPrinters(); break;
                    case "Export-Design":      await _backup.ExportDesign(); break;
                    case "Backup-Laufwerk":    await _backup.BackupDriveFolders((_laufwerkAuswahl ?? new()).Select(e => e.Pfad).ToList()); break;
                    case "Capture-DesktopShot":
                        await _backup.CaptureDesktop();
                        await BringMainWindowToFront();
                        break;
                    case "Backup-DesktopIcons": await _backup.BackupDesktopIcons(); break;

                    case "Restore-User":        await _backup.RestoreUserProfile(); break;
                    case "Restore-AppData":     await _backup.RestoreAppDataOnly(); break;
                    case "Restore-Cloud":       await _backup.RestoreCloudOnly(); break;
                    case "Restore-Firefox":     await _backup.RestoreAppProfile(AppCatalog.Firefox); break;
                    case "Restore-Edge":        await _backup.RestoreAppProfile(AppCatalog.Edge); break;
                    case "Restore-Chrome":      await _backup.RestoreAppProfile(AppCatalog.Chrome); break;
                    case "Restore-Brave":       await _backup.RestoreAppProfile(AppCatalog.Brave); break;
                    case "Restore-Opera":       await _backup.RestoreOpera(); break;
                    case "Restore-Thunderbird": await _backup.RestoreAppProfile(AppCatalog.Thunderbird); break;
                    case "Import-Winget":       await _winget.ImportAsync(wingetSelection ?? new List<WingetPackage>()); break;
                    case "Import-Wlan":         await _backup.ImportWlan(); break;
                    case "Import-Printer":      await _backup.ImportPrinters(); break;
                    case "Import-Design":       await _backup.ImportDesign(); break;
                    case "Restore-Laufwerk":    await _backup.RestoreDriveFolders((_laufwerkAuswahl ?? new()).Select(e => e.Name).ToList()); break;
                    case "Show-DesktopShot":    await _backup.ShowDesktopScreenshot(); break;
                    case "Restore-DesktopIcons": await _backup.RestoreDesktopIcons(); break;
                }
            }
            catch (Exception ex)
            {
                // An error in ONE action must not end the app.
                ok = false;
                Log(Tr($"[FEHLER] Aktion '{choice}' abgebrochen: {ex.Message}", $"[ERROR] Action '{choice}' aborted: {ex.Message}"));
                Program.WriteCrashLog($"RunQueue/{choice}", ex);
            }

            // Successful means: no exception, no error line, no cancellation.
            if (System.Threading.Volatile.Read(ref _aktionsFehler) > 0 || _cancel.IsCancelled) ok = false;

            results.Add((choice, ok));
            await Task.Delay(1000);
        }
        swTotal.Stop();

        // Hide the progress display after the queue.
        _currentActionText = "";
        ProgressText = "";
        ProgressVisible = false;

        // Write the summary to the log and the log file.
        WriteRunSummaryAndReport(startTime, swTotal.Elapsed, results);
        lock (_logLock) _laufLog = null;
    }

    /// <summary>
    /// Writes a short summary to the log and puts a log file into the backup
    /// target (Sicherungs-Protokoll_YYYY-MM-DD_HH-MM.txt).
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

        // Short summary to the log.
        Log(Tr($"\r\n[BILANZ] {okCount} von {results.Count} erfolgreich, {failCount} Fehler. Dauer: {durStr}.",
               $"\r\n[SUMMARY] {okCount} of {results.Count} succeeded, {failCount} errors. Duration: {durStr}."));

        // Write the log file (only if a valid target exists).
        try
        {
            if (string.IsNullOrWhiteSpace(_laufZiel) || !Directory.Exists(_laufZiel)) return;

            var modeStr = restore ? Tr("Wiederherstellung", "Restore") : Tr("Sicherung", "Backup");
            // Put the computer name into the file name (remove invalid characters).
            var pcName = Environment.MachineName;
            foreach (var ch in Path.GetInvalidFileNameChars())
                pcName = pcName.Replace(ch, '_');
            var fileName = $"{(restore ? "Wiederherstellung" : "Sicherung")}-Protokoll_{pcName}_{start:yyyy-MM-dd_HH-mm}.txt";
            var path = Path.Combine(_laufZiel, fileName);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Windows DaSi Tool - " + Tr("Protokoll", "Report"));
            sb.AppendLine("========================================");
            sb.AppendLine($"{Tr("Modus", "Mode")}: {modeStr}");
            sb.AppendLine($"{Tr("Computer", "Computer")}: {Environment.MachineName}");
            sb.AppendLine($"{Tr("Datum", "Date")}: {start:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"{Tr("Dauer", "Duration")}: {durStr}");
            sb.AppendLine($"{Tr("Benutzerprofil", "User profile")}: {SourcePath}");
            sb.AppendLine($"{Tr("Backup-Ziel/-Quelle", "Backup target/source")}: {_laufZiel}");
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
            string? ablauf;
            lock (_logLock) ablauf = _laufLog?.ToString();
            if (!string.IsNullOrEmpty(ablauf))
            {
                sb.AppendLine();
                sb.AppendLine(Tr("Ablauf", "Details"));
                sb.AppendLine("========================================");
                sb.Append(ablauf.TrimStart('\r', '\n'));
            }

            File.WriteAllText(path, sb.ToString(), new System.Text.UTF8Encoding(true));
            Log(Tr($"[INFO] Protokoll gespeichert: {fileName}", $"[INFO] Report saved: {fileName}"));
        }
        catch (Exception ex)
        {
            Log(Tr($"[WARNUNG] Protokoll konnte nicht gespeichert werden: {ex.Message}",
                   $"[WARNING] Report could not be saved: {ex.Message}"));
        }
    }

    /// <summary>Translates an internal action name into readable text.</summary>
    private string FriendlyAction(string choice)
    {
        return choice switch
        {
            "Backup-User" or "Restore-User"               => Tr("Windows Benutzerprofil", "Windows user profile"),
            "Backup-AppData" or "Restore-AppData"         => Tr("AppData", "AppData"),
            "Backup-Cloud" or "Restore-Cloud"             => Tr("Cloud-Ordner", "Cloud folders"),
            "Backup-Firefox" or "Restore-Firefox"         => Tr("Firefox-Profil", "Firefox profile"),
            "Backup-Edge" or "Restore-Edge"               => Tr("Edge-Profil", "Edge profile"),
            "Backup-Chrome" or "Restore-Chrome"           => Tr("Chrome-Profil", "Chrome profile"),
            "Backup-Brave" or "Restore-Brave"             => Tr("Brave-Profil", "Brave profile"),
            "Backup-Opera" or "Restore-Opera"             => Tr("Opera-Profil", "Opera profile"),
            "Backup-Thunderbird" or "Restore-Thunderbird" => Tr("Thunderbird-Profil", "Thunderbird profile"),
            "Export-Winget" or "Import-Winget"            => Tr("Programme (Winget)", "Programs (winget)"),
            "Export-Wlan" or "Import-Wlan"                => Tr("WLAN-Profile", "WiFi profiles"),
            "Export-Printer" or "Import-Printer"          => Tr("Drucker & Treiber", "Printers & drivers"),
            "Export-Design" or "Import-Design"            => Tr("Design (Hintergrund, Farben, Mauszeiger, Taskleiste)", "Design (wallpaper, colors, pointer, taskbar)"),
            "Capture-DesktopShot" or "Show-DesktopShot"   => Tr("Desktop-Screenshot", "Desktop screenshot"),
            "Backup-DesktopIcons" or "Restore-DesktopIcons" => Tr("Desktop-Symbole und Taskleiste", "Desktop icons and taskbar"),
            "Backup-Laufwerk" or "Restore-Laufwerk"       => Tr($"Ordner unter {DriveFolders.Wurzel}", $"Folders under {DriveFolders.Wurzel}"),
            _ => choice
        };
    }

    // ---------------------------------------------------------------- Update

    /// <summary>
    /// The release found by the last check. Remembered on purpose: the check
    /// runs at startup, the click often comes minutes later.
    /// </summary>
    private UpdateManifest? _manifest;

    /// <summary>
    /// Fetches update.json and shows the bar if there is a newer version.
    /// Nothing is downloaded here yet.
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
            _angeboteneVersion = manifest.Program.Version;
            OnPropertyChanged(nameof(UpdateBannerText));
            UpdateAvailable = true;
        });
    }

    /// <summary>
    /// Downloads the new EXE, compares its SHA256 checksum with the one from
    /// update.json and swaps it in.
    ///
    /// On Windows a running EXE cannot overwrite itself. So a small script does
    /// it: it waits for this process to end, replaces the file and starts the
    /// new version.
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

            var skript = UpdateService.WriteUpdateScript(ziel, current);
            UpdateService.StartUpdateScript(skript, ziel, current);

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

    /// <summary>Opens the overview of the releases in the browser.</summary>
    [RelayCommand]
    private void OpenUpdateNotes()
    {
        var url = string.IsNullOrWhiteSpace(_manifest?.Program.Notes)
            ? UpdateService.ReleasesPageUrl
            : _manifest!.Program.Notes;

        Oeffnen(url);
    }

    public void RequestCancelAndKill()
    {
        _cancel.RequestCancel();
        _cancel.KillAll();
    }

    // ---------------------------------------------------------------- UI helpers

    private static Window? GetMainWindow()
        => (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    /// <summary>
    /// Reliably brings the main window back to the front after the desktop
    /// picture. The screenshot restores the windows itself - but if the user
    /// clicks something in the meantime, the tool would otherwise stay
    /// minimized and the running backup would no longer be visible.
    /// </summary>
    private static async Task BringMainWindowToFront()
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var w = GetMainWindow();
            if (w is null) return;
            if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
            w.Activate();
        });
    }

    private async Task<string?> PickFolder(string title)
    {
        if (OrdnerDialog is not null) return await OrdnerDialog(title);
        var win = GetMainWindow();
        if (win?.StorageProvider is not { } sp) return null;

        try
        {
            // Pass a start folder that is guaranteed to exist. This matters because
            // Avalonia otherwise builds the list of recently visited places and can
            // crash with "Directory must exist" on a path in it that no longer exists
            // (removed USB stick, disconnected network drive). A valid start folder
            // avoids that reliably.
            Avalonia.Platform.Storage.IStorageFolder? startFolder = null;
            foreach (var candidate in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Path.GetPathRoot(Environment.SystemDirectory), // e.g. "C:\"
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
                catch { /* try the next candidate */ }
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
            // Crash protection: should the dialog fail anyway, do not crash.
            Program.WriteCrashLog("PickFolder", ex);
            // The path fields are read-only on purpose (the dialog checks the target
            // for NTFS). This used to say "enter the path manually" - an instruction
            // that could not be followed.
            var msg = Tr("Der Ordner-Dialog konnte nicht geöffnet werden. Bitte erneut versuchen. "
                         + "Bleibt der Fehler, steht die Ursache in WindowsDaSiTool_Absturz.log auf dem Desktop.",
                         "The folder dialog could not be opened. Please try again. "
                         + "If the error persists, the cause is in WindowsDaSiTool_Absturz.log on the desktop.");
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
        if (win == null) return true; // do not block without a window
        return await MessageBox.ShowYesNo(win, title, message);
    }
}
