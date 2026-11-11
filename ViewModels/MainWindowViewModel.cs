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
    private const string VersionString = "1.2.1";
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

    // Die Zusatzoptionen zum Benutzerprofil haengen ebenfalls daran und muessen
    // mitgesperrt werden, solange eine Aktion laeuft.
    partial void OnUiEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(UserOptionsEnabled));
        OnPropertyChanged(nameof(OwnFolderToggleEnabled));
        // Nach einem Lauf neu bewerten: Eine Sicherung hat Ordner angelegt,
        // eine Wiederherstellung Programme installiert.
        if (value) VerfuegbarkeitPruefen(); else PunkteNeuMelden();
    }

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
        VerfuegbarkeitPruefen();
        ScheduleAutoStorageCheck();
    }

    // ---- Speicherueberwachung ----
    [ObservableProperty] private string _storageInfo = "";

    // Die Speicherzeile in beiden Sprachen. Beim Sprachwechsel wird nur die
    // andere Fassung angezeigt - frueher blieb die Zeile bis zur naechsten
    // Neuberechnung in der alten Sprache stehen.
    private string _storageInfoDe = "", _storageInfoEn = "";

    private void SetStorageInfo(string de, string en)
    {
        _storageInfoDe = de;
        _storageInfoEn = en;
        StorageInfo = Tr(de, en);
    }

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

    // ---- Eigener Sicherungsordner (abschaltbar, Vorgabe: an) ----
    // Beim Sichern entsteht unter dem gewaehlten Ziel ein Ordner je Rechner und
    // Tag: G:\ -> G:\DASI_<Rechner>_TT_MM_JJJJ. Ist das Gewaehlte schon eine
    // Sicherung (oder gibt es den Ordner von heute schon), wird sie
    // aktualisiert. Beim Wiederherstellen wirkt der Schalter nicht - dort wird
    // ja eine vorhandene Sicherung gewaehlt.
    [ObservableProperty] private bool _ownBackupFolder = true;

    public string OwnFolderLabel => OwnBackupFolder
        ? Tr("Eigener Ordner: AN", "Own folder: ON")
        : Tr("Eigener Ordner: AUS", "Own folder: OFF");

    /// <summary>Sagt, wohin die naechste Sicherung geht - am echten Ordner.</summary>
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
        ZielAnzeigen();
    }

    /// <summary>
    /// Was der Benutzer als Ziel gewaehlt hat, z. B. "G:\". Das Feld zeigt beim
    /// Sichern mit eigenem Ordner daraus schon den fertigen Ordner
    /// ("G:\DASI_Rechner_TT_MM_JJJJ") - genau dorthin wird gesichert.
    /// Gemerkt wird die Auswahl, damit Ausschalten und Moduswechsel wieder auf
    /// das Gewaehlte zurueckrechnen koennen.
    /// </summary>
    private string _zielBasis = "";

    private void SetzeZiel(string basis)
    {
        _zielBasis = basis;
        ZielAnzeigen();
    }

    private void ZielAnzeigen()
    {
        if (string.IsNullOrWhiteSpace(_zielBasis)) return;
        BackupPath = !IsRestoreMode && OwnBackupFolder
            ? BackupTarget.Resolve(_zielBasis, true, Environment.MachineName, DateTime.Now)
            : _zielBasis;
    }

    [RelayCommand]
    private void ToggleOwnFolder() => OwnBackupFolder = !OwnBackupFolder;

    /// <summary>
    /// Ziel des laufenden Vorgangs. Beim Sichern mit eigenem Ordner ist das
    /// der neu angelegte DASI-Ordner, sonst das gewaehlte Ziel. Winget-Export
    /// und Protokolldatei muessen dorthin - nicht ins gewaehlte Ziel darueber.
    /// </summary>
    private string _laufZiel = "";

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
        OnPropertyChanged(nameof(OwnFolderTip));
        VerfuegbarkeitPruefen();
        // Freien Platz am Ziellaufwerk sofort anzeigen (leichtgewichtig).
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
    // Die angebotene Version; der Hinweistext daraus wird bei jedem Abruf in
    // der aktuellen Sprache gebaut, folgt also auch einem Sprachwechsel.
    private string? _angeboteneVersion;
    public string UpdateBannerText => _angeboteneVersion is null ? "" : Tr(
        $"\uD83D\uDD04 Neue Version {_angeboteneVersion} verfügbar  (installiert: {VersionString})",
        $"\uD83D\uDD04 New version {_angeboteneVersion} available  (installed: {VersionString})");

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
        StorageInfo = Tr(_storageInfoDe, _storageInfoEn);

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
    // Wie bei den anderen Sonderpunkten folgt die Beschriftung dem Modus - die
    // Haken wirken in beide Richtungen, und "sichern" waere beim
    // Wiederherstellen schlicht falsch.
    // Bewusst ohne "mit": Beide gehen auch ohne das Profil - dann allein.
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
    // Ein Punkt fuer alle Opera-Fassungen (Opera, GX, Beta ...) - welche gefunden
    // wurden, steht im Tooltip.
    public string TgOpera          => Tr("Opera-Profil (auch GX)", "Opera profile (incl. GX)");
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
    [ObservableProperty] private bool _selOpera;
    [ObservableProperty] private bool _selThunderbird;
    [ObservableProperty] private bool _selWinget;
    [ObservableProperty] private bool _selWlan;
    [ObservableProperty] private bool _selPrinter;
    [ObservableProperty] private bool _selWallpaper;
    [ObservableProperty] private bool _selDesktopShot;

    // ---- Zusatzoptionen zum Benutzerprofil ----
    //
    // Beide stehen bewusst auf AUS. Sie vervielfachen die Datenmenge und die
    // Laufzeit; wer sie braucht, soll sie bewusst einschalten - nicht
    // versehentlich eingeschaltet lassen.
    [ObservableProperty] private bool _selUserAppData;
    [ObservableProperty] private bool _selUserCloud;

    /// <summary>
    /// Die beiden Zusatzoptionen sind nur bedienbar, wenn das Benutzerprofil
    /// ueberhaupt gewaehlt ist - sonst haetten sie keine Wirkung.
    /// </summary>
    // AppData und Cloud gehen auch ohne das Profil - frueher waren sie daran
    // gekoppelt und ohne Profil-Haken gesperrt.
    public bool UserOptionsEnabled => UiEnabled;

    // Bei Aenderung der groessenrelevanten Auswahl die Auto-Berechnung anstossen.
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
    partial void OnSelWallpaperChanged(bool value) => ScheduleAutoStorageCheck();

    // ---- Verfuegbarkeit der Punkte ----
    //
    // Sichern: Programmsicherungen sind gesperrt, wenn das Programm beim
    // gewaehlten Benutzer nicht eingerichtet ist. Wiederherstellen: jeder Punkt
    // ist gesperrt, den die gewaehlte Sicherung nicht enthaelt. Der Grund steht
    // im Tooltip der gesperrten Kachel. Die Regeln stehen in AppCatalog.
    private readonly System.Collections.Generic.Dictionary<Punkt, (string De, string En)?> _gesperrt = new();
    private string? _operaFunde;

    private bool Frei(Punkt p) => !_gesperrt.TryGetValue(p, out var g) || g is null;
    private string? Grund(Punkt p) => _gesperrt.TryGetValue(p, out var g) && g is { } x ? Tr(x.De, x.En) : null;

    public bool EnUser        => UiEnabled && Frei(Punkt.Profil);
    public bool EnAppData     => UserOptionsEnabled && Frei(Punkt.AppData);
    public bool EnCloud       => UserOptionsEnabled && Frei(Punkt.Cloud);
    public bool EnFirefox     => UiEnabled && Frei(Punkt.Firefox);
    public bool EnEdge        => UiEnabled && Frei(Punkt.Edge);
    public bool EnChrome      => UiEnabled && Frei(Punkt.Chrome);
    public bool EnBrave       => UiEnabled && Frei(Punkt.Brave);
    public bool EnOpera       => UiEnabled && Frei(Punkt.Opera);
    public bool EnThunderbird => UiEnabled && Frei(Punkt.Thunderbird);
    public bool EnWinget      => UiEnabled && Frei(Punkt.Winget);
    public bool EnWlan        => UiEnabled && Frei(Punkt.Wlan);
    public bool EnPrinter     => UiEnabled && Frei(Punkt.Drucker);
    public bool EnWallpaper   => UiEnabled && Frei(Punkt.Hintergrund);
    public bool EnDesktopShot => UiEnabled && Frei(Punkt.DesktopShot);

    public string? TipUser        => Grund(Punkt.Profil);
    public string? TipAppData     => Grund(Punkt.AppData);
    public string? TipCloud       => Grund(Punkt.Cloud);
    public string? TipFirefox     => Grund(Punkt.Firefox);
    public string? TipEdge        => Grund(Punkt.Edge);
    public string? TipChrome      => Grund(Punkt.Chrome);
    public string? TipBrave       => Grund(Punkt.Brave);
    public string? TipThunderbird => Grund(Punkt.Thunderbird);
    public string? TipWinget      => Grund(Punkt.Winget);
    public string? TipWlan        => Grund(Punkt.Wlan);
    public string? TipPrinter     => Grund(Punkt.Drucker);
    public string? TipWallpaper   => Grund(Punkt.Hintergrund);
    public string? TipDesktopShot => Grund(Punkt.DesktopShot);
    // Bei Opera auch im freien Zustand: welche Fassungen gefunden wurden.
    public string? TipOpera => Grund(Punkt.Opera)
        ?? (_operaFunde is null ? null : Tr($"Gefunden: {_operaFunde}", $"Found: {_operaFunde}"));

    /// <summary>Bewertet alle Punkte neu und waehlt gesperrte ab.</summary>
    private void VerfuegbarkeitPruefen()
    {
        var sicherung = IsRestoreMode ? AppCatalog.BeimWiederherstellen(BackupPath) : null;
        foreach (var p in Enum.GetValues<Punkt>())
            _gesperrt[p] = sicherung is not null ? sicherung[p] : AppCatalog.BeimSichern(p, SourcePath);

        // Welche Opera-Fassungen gibt es dort, wo gerade gelesen wird?
        var operaOrt = IsRestoreMode
            ? (string.IsNullOrWhiteSpace(BackupPath) ? null : Path.Combine(BackupPath, AppCatalog.Opera.BackupOrdner))
            : (string.IsNullOrWhiteSpace(SourcePath) ? null : AppCatalog.Pfad(SourcePath, AppCatalog.Opera.Rel));
        var funde = operaOrt is null ? new System.Collections.Generic.List<string>() : AppCatalog.Opera.Varianten(operaOrt);
        _operaFunde = funde.Count == 0 ? null : string.Join(", ", funde.Select(AppCatalog.Opera.Anzeigename));

        // Desktop-Layout: welche Variante liegt in der Sicherung? Beim Sichern
        // gibt es beide. Liegt nur die andere vor, wird auf sie umgeschaltet.
        _hatScreenshot = _hatSymbole = true;
        if (IsRestoreMode && !string.IsNullOrWhiteSpace(BackupPath) && Directory.Exists(BackupPath))
        {
            _hatScreenshot = AppCatalog.HatDesktopScreenshot(BackupPath);
            _hatSymbole = AppCatalog.HatDesktopSymbole(BackupPath);
            if (_hatScreenshot != _hatSymbole) DesktopSymbole = _hatSymbole;
        }

        // Was gesperrt ist, kann nicht gewaehlt bleiben.
        if (!Frei(Punkt.Profil)) SelUser = false;
        if (!Frei(Punkt.AppData)) SelUserAppData = false;
        if (!Frei(Punkt.Cloud)) SelUserCloud = false;
        if (!Frei(Punkt.Firefox)) SelFirefox = false;
        if (!Frei(Punkt.Edge)) SelEdge = false;
        if (!Frei(Punkt.Chrome)) SelChrome = false;
        if (!Frei(Punkt.Brave)) SelBrave = false;
        if (!Frei(Punkt.Opera)) SelOpera = false;
        if (!Frei(Punkt.Thunderbird)) SelThunderbird = false;
        if (!Frei(Punkt.Winget)) SelWinget = false;
        if (!Frei(Punkt.Wlan)) SelWlan = false;
        if (!Frei(Punkt.Drucker)) SelPrinter = false;
        if (!Frei(Punkt.Hintergrund)) SelWallpaper = false;
        if (!Frei(Punkt.DesktopShot)) SelDesktopShot = false;

        PunkteNeuMelden();
    }

    private void PunkteNeuMelden()
    {
        foreach (var n in new[]
                 {
                     nameof(EnUser), nameof(EnAppData), nameof(EnCloud), nameof(EnFirefox), nameof(EnEdge),
                     nameof(EnChrome), nameof(EnBrave), nameof(EnOpera), nameof(EnThunderbird), nameof(EnWinget),
                     nameof(EnWlan), nameof(EnPrinter), nameof(EnWallpaper), nameof(EnDesktopShot),
                     nameof(TipUser), nameof(TipAppData), nameof(TipCloud), nameof(TipFirefox), nameof(TipEdge),
                     nameof(TipChrome), nameof(TipBrave), nameof(TipOpera), nameof(TipThunderbird), nameof(TipWinget),
                     nameof(TipWlan), nameof(TipPrinter), nameof(TipWallpaper), nameof(TipDesktopShot),
                     nameof(EnDesktopScreenshot), nameof(EnDesktopSymbole), nameof(TipDesktopScreenshot), nameof(TipDesktopSymbole),
                 })
            OnPropertyChanged(n);
    }

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
        OnPropertyChanged(nameof(TgUserAppData));
        OnPropertyChanged(nameof(TgUserCloud));
        OnPropertyChanged(nameof(TgWinget));
        OnPropertyChanged(nameof(TgWlan));
        OnPropertyChanged(nameof(TgPrinter));
        OnPropertyChanged(nameof(TgWallpaper));
        OnPropertyChanged(nameof(TgDesktopShot));
        OnPropertyChanged(nameof(ModeTitle));
        OnPropertyChanged(nameof(OwnFolderToggleEnabled));
        ZielAnzeigen();
        VerfuegbarkeitPruefen();
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
    // Sichern: Foto des Desktops. Wiederherstellen: dieses Foto oeffnen, damit
    // sich die Symbole von Hand wieder anordnen lassen.
    public string TgDesktopShot => IsRestoreMode
        ? Tr("Desktop-Layout wiederherstellen", "Restore desktop layout")
        : Tr("Desktop-Layout sichern", "Back up desktop layout");

    // ---- Desktop-Layout: Screenshot ODER Symbolpositionen (wie DesktopOK) ----
    // Zwei Radioknoepfe an einer Eigenschaft: DesktopSymbole = Symbolpositionen,
    // sonst Screenshot.
    [ObservableProperty] private bool _desktopSymbole;
    public bool DesktopScreenshot
    {
        get => !DesktopSymbole;
        set { if (value) DesktopSymbole = false; }
    }
    public bool DesktopSymboleGewaehlt
    {
        get => DesktopSymbole;
        set { if (value) DesktopSymbole = true; }
    }
    partial void OnDesktopSymboleChanged(bool value)
    {
        OnPropertyChanged(nameof(DesktopScreenshot));
        OnPropertyChanged(nameof(DesktopSymboleGewaehlt));
    }

    public string TgDesktopScreenshot => Tr("Screenshot", "Screenshot");
    public string TgDesktopSymbole => Tr("Symbolpositionen", "Icon positions");

    // Beim Wiederherstellen: nur die Variante, die in der Sicherung liegt.
    private bool _hatScreenshot = true, _hatSymbole = true;
    public bool EnDesktopScreenshot => EnDesktopShot && _hatScreenshot;
    public bool EnDesktopSymbole => EnDesktopShot && _hatSymbole;
    public string TipDesktopScreenshot => !_hatScreenshot
        ? Tr("Die gewählte Sicherung enthält keinen Desktop-Screenshot.", "The selected backup contains no desktop screenshot.")
        : IsRestoreMode
            ? Tr("Öffnet das Foto des Desktops - die Symbole danach von Hand anordnen.", "Opens the picture of the desktop - then arrange the icons by hand.")
            : Tr("Fotografiert den Desktop, ein Bild pro Bildschirm.", "Takes a picture of the desktop, one per screen.");
    public string TipDesktopSymbole => !_hatSymbole
        ? Tr("Die gewählte Sicherung enthält keine Symbolpositionen.", "The selected backup contains no icon positions.")
        : IsRestoreMode
            ? Tr("Setzt jedes Desktop-Symbol wieder an seine gesicherte Stelle (wie DesktopOK).", "Puts every desktop icon back in its saved position (like DesktopOK).")
            : Tr("Merkt sich, welches Desktop-Symbol wo liegt (wie DesktopOK).", "Remembers where each desktop icon is (like DesktopOK).");
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

    /// <summary>
    /// Fehlerzeilen der gerade laufenden Aktion.
    ///
    /// Warum ueber das Protokoll: Die Dienste werfen bei einem Fehlschlag
    /// keine Ausnahme, sie schreiben "[FEHLER] ..." und kehren normal zurueck.
    /// Die Warteschlange hat deshalb frueher nur Ausnahmen als Fehler gezaehlt -
    /// ein gescheitertes robocopy (Code 16), ein fehlender Sicherungsordner
    /// oder ein nicht startbares winget landeten als [OK] in der Bilanz UND in
    /// der Protokolldatei im Sicherungsziel. Ausgerechnet die Datei, mit der
    /// sich nachweisen laesst, dass eine Sicherung gelaufen ist, meldete dann
    /// Erfolg.
    ///
    /// Das Protokoll ist in diesem Programm der eine Weg, auf dem jeder Dienst
    /// Fehler meldet - jede Stelle benutzt dafuer "[FEHLER]" bzw. "[ERROR]".
    /// Zeilen aus der robocopy-Ausgabe selbst ("FEHLER 5 (0x00000005) ...")
    /// tragen keine eckigen Klammern und zaehlen nicht mit; scheitert robocopy
    /// wirklich, meldet der Dienst den Fehlercode ohnehin selbst.
    /// </summary>
    private int _aktionsFehler;

    private void Log(string text)
    {
        if (ActionOutcome.IsErrorLine(text)) System.Threading.Interlocked.Increment(ref _aktionsFehler);

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
        if (SelFirefox)     list.Add(AppCatalog.Firefox.ProfilRel);
        if (SelEdge)        list.Add(AppCatalog.Edge.ProfilRel);
        if (SelChrome)      list.Add(AppCatalog.Chrome.ProfilRel);
        if (SelBrave)       list.Add(AppCatalog.Brave.ProfilRel);
        if (SelOpera)       list.Add(AppCatalog.Opera.Rel);
        if (SelThunderbird) list.Add(AppCatalog.Thunderbird.ProfilRel);
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
        if (SelOpera)       list.Add(AppCatalog.Opera.BackupOrdner);
        if (SelThunderbird) list.Add("Thunderbird-Profil");
        if (SelWlan)        list.Add("WLAN-Profile");
        if (SelPrinter)     list.Add("Drucker");
        if (SelWallpaper)   list.Add("Hintergrundbild");
        // Winget-Liste ist nur eine kleine Textdatei; vernachlaessigbar.
        return list;
    }

    /// <summary>
    /// Abbruch fuer die gerade laufende Groessenberechnung, und ob danach mit
    /// der inzwischen geaenderten Auswahl neu gerechnet werden muss.
    ///
    /// Frueher kehrte ein zweiter Aufruf waehrend einer laufenden Berechnung
    /// einfach zurueck. Hakte man in den Sekunden, die ein grosses Profil
    /// braucht, AppData an, wurde das nie berechnet - die Anzeige zeigte die
    /// Groesse OHNE AppData, und der Platzbedarf wurde unterschaetzt. Gerade
    /// die beiden Zusatzoptionen veraendern die Groesse am staerksten.
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
                        // Beim Benutzerprofil dieselben Ausschluesse wie beim
                        // Zurueckschreiben selbst: Ohne Haken bleiben AppData und
                        // die Cloud-Ordner draussen - dann zaehlen sie auch hier
                        // nicht mit.
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
                    // Ohne Profil: AppData und Cloud-Ordner aus der Sicherung einzeln.
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
                    var restoreTarget = string.IsNullOrWhiteSpace(sourcePath) ? backupPath : sourcePath;
                    return (size, SystemHelpers.GetFreeSpace(restoreTarget));
                }
                else
                {
                    // Sicherung: Summe ALLER ausgewaehlten Quellen.
                    if (selUser && !string.IsNullOrWhiteSpace(sourcePath))
                        size += SystemHelpers.GetDirectorySize(sourcePath,
                                    BackupService.GetProfileExcludes(sourcePath, selAppData, selCloud), ct);

                    // Ohne Profil: AppData (ohne Zwischenspeicher) und Cloud-Ordner einzeln.
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

                    // Browser-/Mail-Profile einzeln dazurechnen.
                    if (!string.IsNullOrWhiteSpace(sourcePath))
                    {
                        foreach (var rel in relPaths)
                        {
                            var full = System.IO.Path.Combine(sourcePath, rel);
                            if (System.IO.Directory.Exists(full))
                                size += SystemHelpers.GetDirectorySize(full, ct: ct);
                        }
                    }
                    return (size, SystemHelpers.GetFreeSpace(backupPath));
                }
            }, ct);

            var margin = (long)(dataSize * 0.10); // 10% Sicherheitsreserve
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

            // Popup bei gelb/rot - aber nur wenn gewuenscht (manuelle Pruefung),
            // damit die automatische Berechnung nicht staendig Fenster oeffnet.
            if (showPopup && popupMsg != null)
                await ShowInfo(Tr("Speicherplatz", "Storage"), popupMsg);
        }
        catch (OperationCanceledException)
        {
            // Die Auswahl hat sich geaendert - die Neuberechnung folgt im finally.
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
        if (SystemHelpers.IsNtfsDrive(folder)) SetzeZiel(folder);
        else await ShowInfo(Tr("Fehler", "Error"),
            Tr("Das ausgewählte Laufwerk ist NICHT mit NTFS formatiert!\nRobocopy benötigt zwingend NTFS.",
               "The selected drive is NOT formatted as NTFS!\nRobocopy strictly requires NTFS."));
    }

    [RelayCommand]
    private void Exit() => GetMainWindow()?.Close();

    /// <summary>
    /// Warnt, bevor Cloud-Ordner mitgesichert oder zurueckgeschrieben werden.
    ///
    /// Der Grund ist nicht nur die Datenmenge. Cloud-Ordner enthalten oft
    /// Platzhalterdateien, die erst beim Zugriff vom Anbieter geladen werden -
    /// robocopy loest sie dabei aus und zieht damit den gesamten
    /// Online-Bestand auf die Platte. Beim Zurueckschreiben laeuft derselbe
    /// Weg rueckwaerts: Der Client haelt die Dateien fuer geaendert und laedt
    /// alles erneut hoch, was in geteilten Ordnern zu Konflikten fuehrt.
    /// </summary>
    private async Task<bool> ConfirmCloudAsync()
    {
        // Beim Wiederherstellen kommt es darauf an, was IN DER SICHERUNG liegt -
        // das wird gleich zurueckgeschrieben. Frueher wurde hier das Zielprofil
        // durchsucht; auf einem frischen Rechner hat das noch keine Cloud-Ordner,
        // und die Rueckfrage meldete "keine erkannt", waehrend gleich OneDrive
        // zurueckgeschrieben wurde.
        var ort = IsRestoreMode ? Path.Combine(BackupPath, "Benutzerprofil") : SourcePath;
        var gefunden = CloudProviders.Detect(ort);
        var liste = gefunden.Count > 0
            ? CloudProviders.Summarize(gefunden)
            : Tr("keine erkannt", "none detected");

        // Am echten Ordnernamen ist leichter zu erkennen, was gleich passiert.
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
    /// Warnt vor dem Zurueckschreiben von AppData. Beim Sichern ist AppData
    /// harmlos - gefaehrlich wird es erst in der Gegenrichtung: Dort liegen
    /// maschinengebundene Angaben (Lizenzen, Geraetekennungen, Sitzungen) und
    /// Dateien laufender Programme.
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
    /// Prueft vor Export oder Import, ob winget einsatzbereit ist.
    ///
    /// Ist es das nicht, haengt die Antwort davon ab, was sonst noch ansteht:
    /// Steht winget allein in der Warteschlange, ist Schluss. Gibt es daneben
    /// andere Aufgaben, darf der Benutzer ohne den winget-Schritt weitermachen -
    /// ein kaputtes winget soll nicht die Profilsicherung verhindern.
    /// </summary>
    /// <returns>true, wenn weitergemacht werden darf.</returns>
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
        // Ein Profil, das es nicht gibt, wird NICHT angelegt. Beim
        // Wiederherstellen entstuende sonst ein Ordner unter C:\Users, zu dem
        // kein Benutzer gehoert - Windows legt das echte Profil eines neuen
        // Benutzers erst bei dessen erster Anmeldung an, und zwar daneben.
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

        // ---- Rueckfragen zu den Zusatzoptionen des Benutzerprofils ----
        if (SelUserCloud && !await ConfirmCloudAsync()) return;
        if (SelUserAppData && IsRestoreMode && !await ConfirmAppDataRestoreAsync()) return;

        // ---- winget: erst pruefen, dann arbeiten ----
        //
        // Bewusst VOR der Paketauswahl und vor dem ersten Kopiervorgang: Ein
        // nicht einsatzbereites winget soll auffallen, solange der Benutzer
        // noch etwas entscheiden kann - nicht nach zwanzig Minuten Profilkopie.
        if (tasks.Contains("Export-Winget") || tasks.Contains("Import-Winget"))
        {
            if (!await EnsureWingetReadyAsync(tasks)) return;
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
        // Mit Profil laufen AppData und Cloud in der Profilsicherung mit;
        // ohne Profil als eigene Aktionen (siehe BuildTaskList).
        _backup.IncludeAppData = SelUserAppData;
        _backup.IncludeCloud = SelUserCloud;

        // Gesichert wird genau dorthin, wo das Feld zeigt - mit eigenem Ordner
        // steht dort schon "G:\DASI_...". Angelegt wird er erst jetzt.
        _laufZiel = BackupPath;
        if (!IsRestoreMode && Directory.Exists(_laufZiel) && BackupTarget.IsBackupFolder(_laufZiel))
        {
            // Hinein in eine vorhandene Sicherung: Sie wird aktualisiert (das
            // Profil per /MIR gespiegelt). Stammt sie erkennbar von einem
            // anderen Rechner, ginge dabei dessen Stand verloren - nachfragen.
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
            else
            {
                // Ohne Profil: AppData und Cloud einzeln.
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
            if (SelWallpaper)   t.Add("Import-Wallpaper");
            // Zuletzt: Das Foto soll offen sein, wenn alles andere fertig ist
            // und es ans Anordnen der Symbole geht.
            if (SelDesktopShot) t.Add(DesktopSymbole ? "Restore-DesktopIcons" : "Show-DesktopShot");
        }
        else
        {
            // Zuerst: Das Foto zeigt den Desktop, wie er VOR der Sicherung war,
            // und das kurze Ausblenden der Fenster faellt direkt nach dem Klick
            // auf "Starten" nicht als Stoerung auf.
            if (SelDesktopShot) t.Add(DesktopSymbole ? "Backup-DesktopIcons" : "Capture-DesktopShot");
            if (SelUser)        t.Add("Backup-User");
            else
            {
                // Ohne Profil: AppData und Cloud einzeln.
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
                    case "Export-Wallpaper":   await _backup.ExportWallpaper(); break;
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
                    case "Import-Wallpaper":    await _backup.ImportWallpaper(); break;
                    case "Show-DesktopShot":    await _backup.ShowDesktopScreenshot(); break;
                    case "Restore-DesktopIcons": await _backup.RestoreDesktopIcons(); break;
                }
            }
            catch (Exception ex)
            {
                // Ein Fehler in EINER Aktion darf nicht die App beenden.
                ok = false;
                Log(Tr($"[FEHLER] Aktion '{choice}' abgebrochen: {ex.Message}", $"[ERROR] Action '{choice}' aborted: {ex.Message}"));
                Program.WriteCrashLog($"RunQueue/{choice}", ex);
            }

            // Erfolgreich heisst: keine Ausnahme, keine Fehlerzeile, kein Abbruch.
            if (System.Threading.Volatile.Read(ref _aktionsFehler) > 0 || _cancel.IsCancelled) ok = false;

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
            if (string.IsNullOrWhiteSpace(_laufZiel) || !Directory.Exists(_laufZiel)) return;

            var modeStr = restore ? Tr("Wiederherstellung", "Restore") : Tr("Sicherung", "Backup");
            // Computername in den Dateinamen aufnehmen (ungueltige Zeichen entfernen).
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
            "Export-Wallpaper" or "Import-Wallpaper"      => Tr("Hintergrundbild", "Wallpaper"),
            "Capture-DesktopShot" or "Show-DesktopShot"   => Tr("Desktop-Screenshot", "Desktop screenshot"),
            "Backup-DesktopIcons" or "Restore-DesktopIcons" => Tr("Desktop-Symbolpositionen", "Desktop icon positions"),
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
            _angeboteneVersion = manifest.Program.Version;
            OnPropertyChanged(nameof(UpdateBannerText));
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

    /// <summary>
    /// Holt das Hauptfenster nach dem Desktop-Foto sicher wieder nach vorn.
    /// "Desktop anzeigen" stellt die Fenster zwar selbst zurueck - klickt der
    /// Benutzer in der Zwischenzeit aber etwas an, bliebe das Tool sonst
    /// minimiert, und die laufende Sicherung waere nicht mehr zu sehen.
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
            // Die Pfadfelder sind bewusst schreibgeschuetzt (der Dialog prueft das
            // Ziel auf NTFS). Frueher stand hier "Pfad manuell eingeben" - eine
            // Anweisung, die sich nicht befolgen liess.
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
        if (win == null) return true; // ohne Fenster nicht blockieren
        return await MessageBox.ShowYesNo(win, title, message);
    }
}
