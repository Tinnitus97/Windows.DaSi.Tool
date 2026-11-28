using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DaSiPruefung;
using WindowsDaSiTool.Services;
using WindowsDaSiTool.ViewModels;
using WindowsDaSiTool.Views;

namespace DaSiGuiTests;

/// <summary>
/// Tests the UI: the real main window with its ViewModel, without a screen
/// (Avalonia.Headless). Clicks are mouse clicks - at the spot where the
/// button is. A covered or disabled button shows up that way.
///
/// What happens behind the buttons runs for real: robocopy, the backup of a
/// test profile, the update from our own web server. Only the system's
/// folder dialog, the browser and winget (a fake) are replaced.
/// </summary>
internal static class Program
{
    private static int _ok, _fehler;
    private static readonly List<string> Fehlschlaege = new();

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (ok) { _ok++; Console.WriteLine($"  [OK]   {name}"); }
        else
        {
            _fehler++;
            Fehlschlaege.Add(name);
            Console.WriteLine($"  [FAIL] {name}{(detail is null ? "" : $"  -> {detail}")}");
        }
    }

    private static AppBuilder BuildAvaloniaApp()
        => WindowsDaSiTool.Program.BuildAvaloniaApp()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    public static int Main()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var root = Path.Combine(Path.GetTempPath(), "dasi-gui-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);

        // Point the update check at our own server (Update-Url.txt next to the EXE).
        using var server = new MiniHttp();
        var urlDatei = Path.Combine(SystemHelpers.GetApplicationDirectory(), UpdateService.UrlOverrideFileName);
        File.WriteAllText(urlDatei, "# Pruefung\n" + server.Basis + "update.json\n");
        server.Set("update.json", $$"""{ "program": { "version": "9.9.9", "url": "{{server.Basis}}neu.exe", "sha256": "{{new string('a', 64)}}", "notes": "https://example.test/notes" } }""");
        server.Set("neu.exe", new byte[50_000]);

        int ergebnis = 1;
        try
        {
            var lifetime = new ClassicDesktopStyleApplicationLifetime();
            BuildAvaloniaApp().SetupWithLifetime(lifetime);
            var cts = new CancellationTokenSource();
            Dispatcher.UIThread.Post(async () =>
            {
                try { await RunAll(lifetime, root, server); }
                catch (Exception ex) { Check("Pruefung lief ohne Ausnahme durch", false, ex.ToString()); }
                finally { cts.Cancel(); }
            });
            Dispatcher.UIThread.MainLoop(cts.Token);
            ProgramStartCheck();
        }
        finally
        {
            try { File.Delete(urlDatei); } catch { }
            Environment.SetEnvironmentVariable("DASI_WINGET_FAKE", null);
            try { Directory.Delete(root, true); } catch { }
        }
        Console.WriteLine($"\nErgebnis: {_ok} bestanden, {_fehler} fehlgeschlagen.");
        foreach (var f in Fehlschlaege) Console.WriteLine("  fehlgeschlagen: " + f);
        ergebnis = _fehler == 0 ? 0 : 1;
        return ergebnis;
    }

    // ------------------------------------------------------------ Helpers

    private static void Render()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task<bool> WaitFor(Func<bool> bedingung, int ms = 15_000)
    {
        var uhr = Stopwatch.StartNew();
        while (!bedingung())
        {
            if (uhr.ElapsedMilliseconds > ms) return false;
            await Task.Delay(30);
            Render();
        }
        return true;
    }

    private static string Text(object? inhalt) => inhalt switch
    {
        string s => s,
        TextBlock t => t.Text ?? "",
        _ => inhalt?.ToString() ?? "",
    };

    private static IEnumerable<T> All<T>(Visual v) where T : Visual => v.GetVisualDescendants().OfType<T>();

    private static Button FindButton(Visual v, string beschriftung)
        => All<Button>(v).Where(b => b.IsEffectivelyVisible).FirstOrDefault(b => Text(b.Content) == beschriftung)
           ?? throw new InvalidOperationException($"Knopf \"{beschriftung}\" nicht gefunden: "
                                                  + string.Join(" | ", All<Button>(v).Select(b => Text(b.Content))));

    private static T Element<T>(Visual v, string beschriftung) where T : ContentControl
        => All<T>(v).FirstOrDefault(b => Text(b.Content) == beschriftung)
           ?? throw new InvalidOperationException($"{typeof(T).Name} \"{beschriftung}\" nicht gefunden.");

    /// <summary>Clicks the middle of the control with the mouse - like a human.</summary>
    private static void Click(Control c)
    {
        Render();
        var top = TopLevel.GetTopLevel(c) ?? throw new InvalidOperationException("kein Fenster");
        var p = c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), top)
                ?? throw new InvalidOperationException("Position unbekannt");
        top.MouseMove(p);
        top.MouseDown(p, MouseButton.Left);
        top.MouseUp(p, MouseButton.Left);
        Render();
    }

    private static async Task<Window> Dialog(Window owner, int ms = 20_000)
    {
        await WaitFor(() => owner.OwnedWindows.Count > 0, ms);
        Render();
        return owner.OwnedWindows.LastOrDefault() ?? throw new TimeoutException("Kein Dialog erschienen.");
    }

    private static string DialogText(Window d) => string.Join("\n", All<TextBlock>(d).Select(t => t.Text));

    /// <summary>Waits for the dialog, checks its text and clicks the answer.</summary>
    private static async Task<string> Answer(Window owner, string antwort, string? erwartet = null, string? pruefung = null)
    {
        Window d;
        try { d = await Dialog(owner); }
        catch (TimeoutException)
        {
            Check(pruefung ?? $"Dialog erscheint ({erwartet})", false, "kein Dialog");
            return "";
        }
        var text = d.Title + "\n" + DialogText(d);
        if (erwartet is not null) Check(pruefung ?? $"Dialog: {erwartet}", text.Contains(erwartet), text);
        Click(FindButton(d, antwort));
        await WaitFor(() => !owner.OwnedWindows.Contains(d));
        return text;
    }

    private static async Task<bool> Finished(MainWindowViewModel vm, int ms = 300_000)
    {
        await Task.Delay(100);
        return await WaitFor(() => !vm.ExecuteCommand.IsRunning, ms);
    }

    private static void WriteFile(string pfad, string inhalt = "x")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(pfad)!);
        File.WriteAllText(pfad, inhalt);
    }

    // ------------------------------------------------------------ Flow

    private static async Task RunAll(ClassicDesktopStyleApplicationLifetime life, string root, MiniHttp server)
    {
        Console.WriteLine("== Start: Fenster, Titel, Begruessung ==");
        life.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var w = (MainWindow)life.MainWindow!;
        var vm = (MainWindowViewModel)w.DataContext!;
        var oeffnen = new List<string>();
        vm.Oeffnen = u => { oeffnen.Add(u); return true; };
        var bilder = new List<string>();
        vm.Sicherung.BildOeffnen = bilder.Add;
        // Same starting point for every run: German, dark.
        vm.IsEnglish = false;
        if (vm.IsLightTheme) vm.ToggleThemeCommand.Execute(null);
        w.Show();
        Render();
        Check("Hauptfenster erscheint mit Versionsnummer", w.IsVisible && w.Title == vm.WindowTitle && vm.WindowTitle.StartsWith("Windows DaSi Tool "), w.Title);
        Check("Begruessungstext im Protokoll", vm.LogText.Contains("Warte auf Eingabe"), vm.LogText);
        var links = w.FindControl<ScrollViewer>("LeftScroll")!;
        Check("linke Spalte ohne Rollbalken (1200 x 720)", links.Extent.Height <= links.Viewport.Height + 1, $"{links.Extent.Height} > {links.Viewport.Height}");
        var eigeneKnoepfe = All<Button>(w).Where(b => b.GetType() == typeof(Button) && b.TemplatedParent is null).ToList();
        Check("alle Knoepfe haben einen Befehl", eigeneKnoepfe.Count >= 12 && eigeneKnoepfe.All(b => b.Command is not null),
            string.Join(", ", eigeneKnoepfe.Where(b => b.Command is null).Select(b => Text(b.Content))));

        Console.WriteLine("\n== Update-Streifen (eigener Server) ==");
        Check("neuere Version: Streifen erscheint", await WaitFor(() => vm.UpdateAvailable) && vm.UpdateBannerText.Contains("9.9.9"), vm.UpdateBannerText);
        Click(FindButton(w, "Was ist neu?"));
        Check("'Was ist neu?' oeffnet die Seite aus update.json", oeffnen.LastOrDefault() == "https://example.test/notes", string.Join(", ", oeffnen));
        Click(FindButton(w, "Jetzt aktualisieren"));
        await Answer(w, "Nein", "Version 9.9.9 herunterladen", "Update: erst eine Rueckfrage");
        Check("Update abgelehnt: nichts geladen", await WaitFor(() => vm.LogText.Contains("Abgebrochen.")) && !server.Abrufe.ContainsKey("neu.exe"), vm.LogText);
        Click(FindButton(w, "Jetzt aktualisieren"));
        await Answer(w, "Ja", "herunterladen");
        await Answer(w, "OK", "Prüfsumme stimmt nicht", "falsche Pruefsumme: Download wird abgelehnt");
        Check("nach dem Fehlschlag bleibt das Programm bedienbar", await WaitFor(() => vm.UiEnabled) && w.IsVisible);

        await LanguageAndTheme(w, vm);
        await PathsAndStorage(w, vm, root);
        await ModeSwitch(w, vm, root);
        await Queue(w, vm, root);
        await WingetInUi(w, vm, root);
        await DriveFoldersInUi(w, vm, root);
        await Dialogs(w);
        MoreFeatures(root);
        await Closing(w, vm);
    }

    private static async Task LanguageAndTheme(MainWindow w, MainWindowViewModel vm)
    {
        Console.WriteLine("\n== Sprache und Hell/Dunkel ==");
        Click(FindButton(w, "Log leeren"));
        Check("Protokoll geleert", await WaitFor(() => vm.LogText == ""), vm.LogText);
        var sprache = All<ComboBox>(w).First(c => c.ItemsSource == vm.Languages);
        sprache.SelectedIndex = 1;
        Render();
        Check("Englisch: Beschriftungen wechseln", All<Button>(w).Any(b => Text(b.Content) == "Start selected actions")
            && All<CheckBox>(w).Any(c => Text(c.Content) == "Windows user profile"));
        Check("Englisch: Begruessung wechselt mit, solange nichts im Protokoll steht",
            await WaitFor(() => vm.LogText.Contains("Waiting for input")), vm.LogText);
        sprache.SelectedIndex = 0;
        Render();
        Check("zurueck auf Deutsch", !vm.IsEnglish && All<Button>(w).Any(b => Text(b.Content) == "Ausgewählte Aktionen starten"));

        var thema = All<Button>(w).First(b => Text(b.Content) is "☀" or "☽");
        var vorher = Application.Current!.RequestedThemeVariant;
        Click(thema);
        Check("Hell/Dunkel umgeschaltet", Application.Current.RequestedThemeVariant != vorher && Text(thema.Content) == vm.ThemeIcon);
        Click(thema);
        Check("und wieder zurueck", Application.Current.RequestedThemeVariant == vorher);

        // Every label exists in both languages.
        var props = typeof(MainWindowViewModel).GetProperties().Where(p => p.PropertyType == typeof(string) && p.GetIndexParameters().Length == 0).ToList();
        var de = props.ToDictionary(p => p.Name, p => (string?)p.GetValue(vm));
        vm.IsEnglish = true;
        var en = props.ToDictionary(p => p.Name, p => (string?)p.GetValue(vm));
        vm.IsEnglish = false;
        var gleich = props.Where(p => de[p.Name] == en[p.Name] && !string.IsNullOrEmpty(de[p.Name])
                                      && p.Name is not ("WindowTitle" or "SourcePath" or "BackupPath" or "LogText" or "ThemeIcon" or "UpdateBannerText"
                                                        or "TgTeilScreenshot" or "TgTeilDesign" or "ProgressText" or "StorageInfo"))
                          .Select(p => p.Name).ToList();
        Check("jede Beschriftung ist uebersetzt", gleich.Count == 0, string.Join(", ", gleich));

        Click(FindButton(w, "Log: aus"));
        await Answer(w, "OK", "Detailliertes Log ist aktiv", "ausfuehrliches Protokoll: Hinweis auf die Geschwindigkeit");
        Check("ausfuehrliches Protokoll an", vm.DetailedLogging && vm.LoggingLabel == "Log: an" && vm.LogText.Contains("[HINWEIS]"));
        Click(FindButton(w, "Log: an"));
        Check("ausfuehrliches Protokoll wieder aus", !vm.DetailedLogging);
        // The same in English: label, notice and log line.
        vm.IsEnglish = true;
        Click(FindButton(w, "Log: off"));
        await Answer(w, "OK", "Detailed logging is on", "ausfuehrliches Protokoll auf Englisch: Hinweis");
        Check("ausfuehrliches Protokoll auf Englisch: Beschriftung und [NOTE]-Zeile", vm.LoggingLabel == "Log: on" && vm.LogText.Contains("[NOTE] Detailed logging is on"));
        Click(FindButton(w, "Log: on"));
        vm.IsEnglish = false;

        foreach (var s in new[] { 0, 1, 2, 3 })
        {
            vm.StorageStatus = s;
            Check($"Ampelfarbe fuer Stufe {s}", vm.StorageBrush is Avalonia.Media.ISolidColorBrush);
        }
        vm.StorageStatus = 0;
    }

    private static async Task PathsAndStorage(MainWindow w, MainWindowViewModel vm, string root)
    {
        Console.WriteLine("\n== Pfade waehlen, eigener Ordner, Speicher ==");
        var profil = Path.Combine(root, "Besitzer");
        WriteFile(Path.Combine(profil, "Documents", "brief.txt"), new string('b', 5000));
        WriteFile(Path.Combine(profil, "OneDrive", "Vertrag.docx"), "cloud");
        WriteFile(Path.Combine(profil, @"AppData\Roaming\Werkzeug\a.ini"), "a=1");
        var ziel = Path.Combine(root, "Ziel");
        Directory.CreateDirectory(ziel);

        // Without the hook: the real folder dialog - without a screen it returns nothing.
        var pfadKnoepfe = All<Button>(w).Where(b => Text(b.Content) == "…").ToList();
        Click(pfadKnoepfe[0]);
        Check("Ordner-Dialog ohne Auswahl: Pfad bleibt leer", await WaitFor(() => !vm.SelectSourceCommand.IsRunning) && vm.SourcePath == "");

        var titel = new List<string>();
        vm.OrdnerDialog = t => { titel.Add(t); return Task.FromResult<string?>(titel.Count == 1 ? profil : ziel); };
        Click(pfadKnoepfe[0]);
        Check("Quelle gewaehlt", await WaitFor(() => vm.SourcePath == profil) && titel[0].Contains("Quell-Benutzerprofil"), vm.SourcePath);
        Click(pfadKnoepfe[1]);
        if (SystemHelpers.IsNtfsDrive(ziel))
        {
            var heute = BackupTarget.Resolve(ziel, true, Environment.MachineName, DateTime.Now);
            Check("Ziel gewaehlt: Feld zeigt gleich den eigenen Sicherungsordner", await WaitFor(() => vm.BackupPath == heute), vm.BackupPath);
            Check("Tooltip nennt den Ordner", vm.OwnFolderTip.Contains(heute));
            Click(FindButton(w, "Eigener Ordner: AN"));
            Check("eigener Ordner aus: direkt ins Ziel", vm.BackupPath == ziel && vm.OwnFolderTip.Contains("direkt in das gewählte Ziel"));
            Click(FindButton(w, "Eigener Ordner: AUS"));
            Check("eigener Ordner wieder an", vm.BackupPath == heute);
        }
        else
        {
            await Answer(w, "OK", "NICHT mit NTFS formatiert", "Ziel ohne NTFS: abgelehnt");
            vm.BackupPath = ziel;
        }

        // Check storage: by hand (with a notice window only when tight/red) and automatically.
        vm.SelUser = true;
        Click(FindButton(w, "Speicher prüfen"));
        Check("Speicher: Datenmenge berechnet", await WaitFor(() => !vm.StorageChecking && vm.StorageInfo.Contains("Datenmenge")), vm.StorageInfo);
        Check("Speicher: reicht (gruen)", vm.StorageStatus == 1, vm.StorageStatus.ToString());
        Click(FindButton(w, "Auto-Berechnung: AN"));
        Check("Auto-Berechnung aus", !vm.AutoStorageCheck && vm.AutoStorageLabel == "Auto-Berechnung: AUS");
        Click(FindButton(w, "Auto-Berechnung: AUS"));
        vm.SelUserAppData = true;
        vm.SelUserCloud = true;
        vm.SelFirefox = true;
        Check("Auto-Berechnung nach Auswahl", await WaitFor(() => !vm.StorageChecking && vm.StorageInfo.Contains("Datenmenge")), vm.StorageInfo);
        vm.SelUser = false;
        vm.SelFirefox = false;
        await WaitFor(() => !vm.StorageChecking);
        Click(FindButton(w, "Speicher prüfen"));
        Check("Speicher ohne Profil: AppData und Cloud einzeln", await WaitFor(() => !vm.StorageChecking && vm.StorageInfo.Contains("Datenmenge")), vm.StorageInfo);
        vm.SelUserAppData = vm.SelUserCloud = false;

        // Quick selection: a profile from it sets the path.
        if (vm.UserProfiles.Count > 0)
        {
            var schnell = All<ComboBox>(w).First(c => c.ItemsSource == vm.UserProfiles);
            schnell.SelectedIndex = 0;
            Check("Schnellauswahl setzt das Profil", vm.SourcePath == vm.UserProfiles[0]);
            vm.SourcePath = vm.UserProfiles[0];
            Check("Pfad aus der Liste: Schnellauswahl folgt", vm.SelectedProfile == vm.UserProfiles[0]);
        }

        Click(FindButton(w, "Pfade leeren"));
        Check("Pfade geleert", vm.SourcePath == "" && vm.BackupPath == "" && vm.LogText.Contains("Pfade wurden geleert"));
        Click(FindButton(w, "Speicher prüfen"));
        Check("Speicher ohne Ziel: Hinweis", await WaitFor(() => vm.StorageInfo.Contains("Bitte zuerst ein Backup-Ziel")), vm.StorageInfo);
        vm.OrdnerDialog = null;
    }

    private static async Task ModeSwitch(MainWindow w, MainWindowViewModel vm, string root)
    {
        Console.WriteLine("\n== Sichern / Wiederherstellen: Beschriftungen und gesperrte Kacheln ==");
        // A backup that contains only a Firefox profile and a desktop picture.
        var sicherung = Path.Combine(root, "NurFirefox");
        WriteFile(Path.Combine(sicherung, "Firefox-Profil", "profiles.ini"), "[Profile0]");
        WriteFile(Path.Combine(sicherung, DesktopScreenshot.FolderName, DesktopScreenshot.FilePrefix + "1.png"), "png");
        vm.SourcePath = Path.Combine(root, "Besitzer");
        vm.BackupPath = sicherung;

        Click(Element<RadioButton>(w, "Wiederherstellen"));
        Check("Modus Wiederherstellen", vm.IsRestoreMode && !vm.IsBackupMode);
        Check("Beschriftungen folgen dem Modus", vm.TgWinget == "Programme installieren (Winget)" && vm.TgDesignDesktop == "Design & Desktop wiederherstellen"
            && vm.TgLaufwerk.EndsWith("wiederherstellen")
            && vm.TgUserAppData == "AppData wiederherstellen" && All<CheckBox>(w).Any(c => Text(c.Content) == "WLAN Profile importieren"));
        Check("eigener Ordner wirkt beim Wiederherstellen nicht", !vm.OwnFolderToggleEnabled && !FindButton(w, "Eigener Ordner: AN").IsEffectivelyEnabled);
        var chrome = Element<CheckBox>(w, "Chrome-Profil");
        Check("nicht in der Sicherung: Kachel gesperrt, Grund im Tooltip", !chrome.IsEffectivelyEnabled && (vm.TipChrome ?? "").Length > 0, vm.TipChrome);
        Check("in der Sicherung: Kachel frei", Element<CheckBox>(w, "Firefox-Profil").IsEffectivelyEnabled);
        Check("Kachel aus: kein Teil angehakt", vm.EnDesignDesktop && !vm.SelDesignDesktop && !vm.TeilScreenshot && !vm.TeilSymbole && !vm.TeilDesign,
            $"{vm.TeilDesign} {vm.TeilSymbole} {vm.TeilScreenshot}");
        Check("nur ein Foto gesichert: Screenshot bedienbar, Symbole und Design gesperrt",
            vm.EnTeilScreenshot && Element<CheckBox>(w, "Screenshot").IsEffectivelyEnabled && !vm.EnTeilSymbole && !vm.EnTeilDesign
            && vm.TipTeilSymbole.Contains("weder Symbolpositionen"), vm.TipTeilSymbole);
        Click(Element<CheckBox>(w, "Design & Desktop wiederherstellen"));
        Check("Kachel angehakt: Vorgabe - nur der Screenshot (mehr ist nicht gesichert)",
            vm.SelDesignDesktop && vm.TeilScreenshot && !vm.TeilSymbole && !vm.TeilDesign, $"{vm.TeilDesign} {vm.TeilSymbole} {vm.TeilScreenshot}");
        Click(Element<CheckBox>(w, "Screenshot"));
        Check("letzten Teil abgehakt: Kachel ab", !vm.TeilScreenshot && !vm.SelDesignDesktop);
        Click(Element<CheckBox>(w, "Screenshot"));
        Check("Teil angehakt: Kachel automatisch mit", vm.TeilScreenshot && vm.SelDesignDesktop);
        Check("Tooltip Screenshot beim Wiederherstellen", vm.TipTeilScreenshot.Contains("Öffnet das Foto"));
        Check("Design nicht in der Sicherung: Grund statt Inhaltsangabe", !vm.EnTeilDesign && !(vm.TipTeilDesign ?? "").Contains("Mauszeiger"), vm.TipTeilDesign);
        Check("Ordner unter C:\\ nicht in der Sicherung: gesperrt, mit Grund", !vm.EnLaufwerk && (vm.TipLaufwerk ?? "").Contains("keine Ordner von"), vm.TipLaufwerk);
        Click(Element<CheckBox>(w, "Firefox-Profil"));
        Check("freie Kachel laesst sich anhaken", vm.SelFirefox);
        vm.SelFirefox = false;

        Click(Element<RadioButton>(w, "Sichern"));
        Check("zurueck zum Sichern", !vm.IsRestoreMode && vm.TgWinget == "Programme exportieren (Winget)" && vm.TipTeilScreenshot.Contains("Fotografiert")
            && vm.TgDesignDesktop == "Design & Desktop sichern" && vm.TgLaufwerk.EndsWith("sichern"));
        Check("Sichern: Design und Symbole vorgewaehlt, Screenshot nicht", vm.TeilDesign && vm.TeilSymbole && !vm.TeilScreenshot);
        Check("Design nennt auch frei, was dazugehoert", vm.EnTeilDesign && (vm.TipTeilDesign ?? "").Contains("Mauszeiger") && vm.TipTeilSymbole.Contains("Merkt sich")
            && (vm.TipDesignDesktop ?? "").Contains("Taskleisten-Symbole"), vm.TipTeilDesign);
        Click(Element<CheckBox>(w, "Screenshot"));
        Check("Symbole und Screenshot zusammen moeglich", vm.TeilSymbole && vm.TeilScreenshot);
        Click(Element<CheckBox>(w, "Symbole + Taskleiste"));
        Click(Element<CheckBox>(w, "Design"));
        Check("Teile einzeln abwaehlbar", !vm.TeilSymbole && !vm.TeilDesign && vm.TeilScreenshot);
        Click(Element<CheckBox>(w, "Design & Desktop sichern"));
        Check("Kachel ab: alle Teile ab, aber bedienbar", !vm.SelDesignDesktop && !vm.TeilScreenshot && !vm.TeilDesign && !vm.TeilSymbole && vm.EnTeilSymbole);
        Click(Element<CheckBox>(w, "Symbole + Taskleiste"));
        Check("nur Symbole angehakt: Kachel automatisch mit, sonst nichts", vm.SelDesignDesktop && vm.TeilSymbole && !vm.TeilDesign && !vm.TeilScreenshot);
        Click(Element<CheckBox>(w, "Design"));
        Check("Design dazu: Kachel bleibt an", vm.SelDesignDesktop && vm.TeilDesign && vm.TeilSymbole);
        Click(Element<CheckBox>(w, "Design & Desktop sichern"));
        Click(Element<CheckBox>(w, "Design & Desktop sichern"));
        Check("Kachel ab und wieder an: Vorgabe Design und Symbole", vm.SelDesignDesktop && vm.TeilDesign && vm.TeilSymbole && !vm.TeilScreenshot);
        vm.SelDesignDesktop = false;
        Check("Ordner unter C:\\: Tooltip sagt, was beim Start kommt", (vm.TipLaufwerk ?? "").Contains("Liste") || !vm.EnLaufwerk, vm.TipLaufwerk);
        vm.IsBackupMode = true;
        Check("IsBackupMode setzen aendert nichts am Sichern", !vm.IsRestoreMode);

        // Every selection on and off once - each one triggers the size calculation.
        var auswahl = typeof(MainWindowViewModel).GetProperties().Where(p => p.Name.StartsWith("Sel") && p.PropertyType == typeof(bool)).ToList();
        foreach (var p in auswahl) { p.SetValue(vm, true); p.SetValue(vm, false); }
        Check("jede Kachel laesst sich an- und abwaehlen", auswahl.Count >= 14 && auswahl.All(p => (bool)p.GetValue(vm)! == false), auswahl.Count.ToString());
    }

    private static async Task Queue(MainWindow w, MainWindowViewModel vm, string root)
    {
        Console.WriteLine("\n== Starten: Rueckfragen und Warteschlange ==");
        var start = FindButton(w, "Ausgewählte Aktionen starten");
        var profil = Path.Combine(root, "Besitzer");
        foreach (var p in typeof(MainWindowViewModel).GetProperties().Where(p => p.Name.StartsWith("Sel") && p.PropertyType == typeof(bool)))
            p.SetValue(vm, false);
        vm.SourcePath = "";
        vm.BackupPath = "";

        Click(start);
        await Answer(w, "OK", "mindestens eine Aktion", "ohne Auswahl: Hinweis");
        vm.SelUser = true;
        Click(start);
        await Answer(w, "OK", "Quell- und Zielverzeichnis", "ohne Pfade: Hinweis");
        vm.SourcePath = Path.Combine(root, "GibtEsNicht");
        vm.BackupPath = Path.Combine(root, "Ziel");
        vm.SelUser = true;
        Click(start);
        await Answer(w, "OK", "gibt es nicht", "Profil fehlt: Hinweis");

        // Cloud: confirmation prompt, declined.
        vm.SourcePath = profil;
        var ziel = Path.Combine(root, "Ziel", "Lauf1");
        vm.BackupPath = ziel;
        vm.SelUser = true;
        vm.SelUserCloud = true;
        Click(start);
        await Answer(w, "Nein", "Erkannte Cloud-Anbieter: OneDrive", "Cloud: Rueckfrage nennt den Anbieter");
        Check("Cloud abgelehnt: nichts gestartet", await Finished(vm) && vm.LogText.Contains("Cloud-Ordner wurden nicht bestätigt") && !Directory.Exists(ziel));

        // Now for real: profile with cloud, desktop picture, design - the folder is created.
        vm.SelUserCloud = true;
        vm.SelUserAppData = true;
        // Design & Desktop: design and picture - not the icons (restoring them
        // would restart Explorer).
        vm.SelDesignDesktop = true;
        vm.TeilDesign = true;
        vm.TeilSymbole = false;
        vm.TeilScreenshot = true;
        Click(start);
        await Answer(w, "Ja", "Erkannte Cloud-Anbieter");
        Check("Lauf beendet", await Finished(vm), vm.LogText);
        Check("Sicherungsordner angelegt und gemeldet", Directory.Exists(ziel) && vm.LogText.Contains("Sicherungsordner angelegt"), vm.LogText);
        Check("Profil samt Cloud gesichert", !OperatingSystem.IsWindows() || File.Exists(Path.Combine(ziel, "Benutzerprofil", "OneDrive", "Vertrag.docx")));
        Check("Bilanz im Protokoll", vm.LogText.Contains("[BILANZ]") && vm.LogText.Contains("Alle ausgewählten Aktionen abgeschlossen"), vm.LogText);
        var bericht = Directory.GetFiles(ziel, "Sicherung-Protokoll_*.txt").FirstOrDefault();
        Check("Protokolldatei im Ziel, jede Aktion mit Ergebnis", bericht is not null && File.ReadAllText(bericht).Contains("Windows Benutzerprofil")
            && File.ReadAllText(bericht).Contains("Desktop-Screenshot"), bericht);
        var berichtText = bericht is null ? "" : File.ReadAllText(bericht);
        Check("Protokolldatei: auch der ganze Ablauf, mit Bilanz", berichtText.Contains("Ablauf" + Environment.NewLine + "=====")
            && berichtText.Contains("--- Führe Aktion Backup-User aus ---") && berichtText.Contains("[BILANZ]"), berichtText);
        Check("Oberflaeche nach dem Lauf wieder frei", vm.UiEnabled && !vm.IsActionRunning && !vm.ProgressVisible);

        // Into an existing backup of another computer: confirmation prompt.
        var fremd = Path.Combine(root, "Ziel", "DASI_ANDERER-PC_01_01_2026");
        Directory.CreateDirectory(fremd);
        foreach (var p in typeof(MainWindowViewModel).GetProperties().Where(p => p.Name.StartsWith("Sel") && p.PropertyType == typeof(bool)))
            p.SetValue(vm, false);
        vm.BackupPath = fremd;
        vm.SelUser = true;
        Click(start);
        await Answer(w, "Nein", "ANDERER-PC", "fremde Sicherung: Rueckfrage");
        Check("fremde Sicherung abgelehnt", await Finished(vm) && vm.LogText.Contains("bitte ein anderes Ziel"));
        Click(start);
        await Answer(w, "Ja", "ANDERER-PC");
        Check("fremde Sicherung bestaetigt: wird aktualisiert", await Finished(vm) && vm.LogText.Contains("Vorhandene Sicherung wird aktualisiert"));

        if (OperatingSystem.IsWindows())
        {
            // A target that cannot be created.
            var frei = "QRSTUVWXYZ".Select(c => c + @":\").First(l => !DriveInfo.GetDrives().Any(d => string.Equals(d.Name, l, StringComparison.OrdinalIgnoreCase)));
            vm.BackupPath = frei + "Sicherung";
            Click(start);
            await Answer(w, "OK", "konnte nicht angelegt werden", "Ziel nicht anlegbar: Hinweis");
        }

        // Restore: missing backup, AppData prompt, then for real.
        Click(Element<RadioButton>(w, "Wiederherstellen"));
        vm.BackupPath = Path.Combine(root, "GibtEsNicht");
        vm.SelUser = true;
        Click(start);
        await Answer(w, "OK", "Den Sicherungsordner", "Sicherung fehlt: Hinweis");
        var anwender = Path.Combine(root, "Anwender");
        Directory.CreateDirectory(anwender);
        vm.SourcePath = anwender;
        vm.BackupPath = ziel;
        vm.SelUser = false;
        vm.SelUserAppData = true;
        vm.SelUserCloud = true;
        Click(start);
        await Answer(w, "Ja", OperatingSystem.IsWindows() ? "Cloud-Anbieter in der Sicherung: OneDrive" : "Cloud-Anbieter in der Sicherung",
                        "Wiederherstellen: Cloud-Rueckfrage nennt, was in der Sicherung liegt");
        await Answer(w, "Nein", "AppData wird zurückgeschrieben", "AppData zurueck: Warnung");
        Check("AppData abgelehnt: nichts gestartet", await Finished(vm) && vm.LogText.Contains("AppData-Wiederherstellung wurde nicht bestätigt"));
        vm.SelUser = true;
        vm.SelDesignDesktop = true;
        Check("Wiederherstellen: Foto vorgewaehlt, wenn eins da ist; Symbole nicht (keine in der Sicherung)",
            vm.TeilScreenshot == AppCatalog.HasDesktopScreenshot(ziel) && !vm.TeilSymbole, $"{vm.TeilSymbole} {vm.TeilScreenshot}");
        Click(start);
        await Answer(w, "Ja", "Cloud-Anbieter");
        await Answer(w, "Ja", "AppData");
        Check("Wiederherstellung beendet", await Finished(vm), vm.LogText);
        Check("Profil zurueck, Cloud daneben", !OperatingSystem.IsWindows() || File.Exists(Path.Combine(anwender, "Documents", "brief.txt"))
            && Directory.Exists(Path.Combine(anwender, "OneDrive_wiederhergestellt")), vm.LogText);
        Check("Protokolldatei der Wiederherstellung", Directory.GetFiles(ziel, "Wiederherstellung-Protokoll_*.txt").Length == 1);

        // Without the profile: AppData and cloud on their own - in both directions.
        vm.SelUser = false;
        vm.SelDesignDesktop = false;
        vm.SelUserAppData = true;
        vm.SelUserCloud = false;
        Click(start);
        await Answer(w, "Ja", "AppData");
        Check("nur AppData zurueck", await Finished(vm) && vm.LogText.Contains("Restore-AppData"), vm.LogText);
        Click(Element<RadioButton>(w, "Sichern"));
        vm.SourcePath = profil;
        vm.BackupPath = Path.Combine(root, "Ziel", "Teile");
        vm.SelUserAppData = true;
        vm.SelUserCloud = true;
        Click(start);
        await Answer(w, "Ja", "Cloud");
        Check("nur AppData und Cloud gesichert", await Finished(vm) && vm.LogText.Contains("Backup-AppData") && vm.LogText.Contains("Backup-Cloud"), vm.LogText);
        vm.SelUserAppData = vm.SelUserCloud = false;
    }

    private static async Task WingetInUi(MainWindow w, MainWindowViewModel vm, string root)
    {
        Console.WriteLine("\n== winget in der Oberflaeche (Attrappe) ==");
        var attrappe = Fakes.WingetFake();
        if (attrappe is null || !OperatingSystem.IsWindows())
        {
            Console.WriteLine("         (nur unter Windows mit gebauter Attrappe)");
            return;
        }
        var start = FindButton(w, "Ausgewählte Aktionen starten");
        vm.Winget.FesteKandidaten = new[] { attrappe };
        vm.Winget.Nachinstallation = () => Task.FromResult(false);
        var sicherung = Path.Combine(root, "Ziel", "Winget");
        var roh = Path.Combine(root, "export.json");
        File.WriteAllText(roh, """{ "Sources": [ { "Packages": [ { "PackageIdentifier": "Notepad++.Notepad++" }, { "PackageIdentifier": "7zip.7zip" } ] } ] }""");

        // winget broken, but something else selected as well: continue without winget.
        Fakes.WingetScenario(root, new Dictionary<string, string> { ["version.code"] = "1" });
        vm.SourcePath = Path.Combine(root, "Besitzer");
        vm.BackupPath = sicherung;
        vm.SelWinget = true;
        vm.SelUser = true;
        Click(start);
        await Answer(w, "Ja", "Ohne den winget-Schritt fortfahren?", "winget kaputt, andere Aktion: weiter ohne winget");
        Check("ohne winget weiter: Profil gesichert, winget uebersprungen", await Finished(vm) && vm.LogText.Contains("winget-Schritt wurde übersprungen")
            && !vm.LogText.Contains("Aktion Export-Winget"), vm.LogText);
        vm.SelUser = false;
        Click(start);
        await Answer(w, "OK", "winget nicht einsatzbereit", "winget allein und kaputt: Schluss");
        Check("winget allein und kaputt: nichts gestartet", await Finished(vm));
        vm.SelUser = true;
        Click(start);
        await Answer(w, "Nein", "Ohne den winget-Schritt fortfahren?");
        Check("ohne winget weiter? Nein: nichts gestartet", await Finished(vm) && vm.LogText.Contains("[INFO] Start abgebrochen."));
        vm.SelUser = false;

        // winget healthy: export through the fake.
        Fakes.WingetScenario(root, new Dictionary<string, string> { ["export.datei"] = roh });
        Click(start);
        Check("Export ueber die Oberflaeche", await Finished(vm) && File.Exists(Path.Combine(sicherung, "Winget", "Export.json")), vm.LogText);

        // Import: list missing, broken, empty - then the selection.
        Click(Element<RadioButton>(w, "Wiederherstellen"));
        vm.SelWinget = true;
        var leer = Path.Combine(root, "Ziel", "WingetLeer");
        Directory.CreateDirectory(Path.Combine(leer, "Winget"));
        vm.BackupPath = leer;
        vm.SelWinget = true;
        Click(start);
        await Answer(w, "OK", "Exportdatei nicht gefunden", "Import ohne Liste");
        File.WriteAllText(Path.Combine(leer, "Winget", "Export.json"), "{ kaputt");
        vm.SelWinget = true;
        Click(start);
        await Answer(w, "OK", "nicht als JSON", "Import mit kaputter Liste");
        File.WriteAllText(Path.Combine(leer, "Winget", "Export.json"), """{ "Sources": [] }""");
        vm.SelWinget = true;
        Click(start);
        await Answer(w, "OK", "keine gültigen Programme", "Import mit leerer Liste");

        vm.BackupPath = sicherung;
        vm.SelWinget = true;
        Click(start);
        var auswahl = await Dialog(w);
        Check("Auswahl zeigt jedes Programm, alle angehakt", All<CheckBox>(auswahl).Count() == 2 && All<CheckBox>(auswahl).All(c => c.IsChecked == true));
        Click(FindButton(auswahl, "Keine"));
        Check("'Keine' hakt alles ab", All<CheckBox>(auswahl).All(c => c.IsChecked == false));
        Click(FindButton(auswahl, "Alle"));
        Click(FindButton(auswahl, "Abbrechen"));
        Check("Auswahl abgebrochen: nichts installiert", await Finished(vm) && !vm.LogText.Contains("Installiere Notepad"));
        Click(start);
        auswahl = await Dialog(w);
        Click(All<CheckBox>(auswahl).First(c => Text(c.Content) == "7zip.7zip"));
        Click(FindButton(auswahl, "Installieren"));
        Check("Import: nur das Ausgewaehlte installiert", await Finished(vm) && vm.LogText.Contains("Installiere Notepad++.Notepad++") && !vm.LogText.Contains("Installiere 7zip"), vm.LogText);
        Click(Element<RadioButton>(w, "Sichern"));
        vm.SelWinget = false;
        Environment.SetEnvironmentVariable("DASI_WINGET_FAKE", null);
    }

    /// <summary>
    /// Folders under C:\ - on a fake drive in the temp folder, with the real
    /// selection window: hide, choose, back up, cancel, restore.
    /// </summary>
    private static async Task DriveFoldersInUi(MainWindow w, MainWindowViewModel vm, string root)
    {
        Console.WriteLine("\n== Ordner unter C:\\ (Auswahlfenster) ==");
        var c = Path.Combine(root, "LaufwerkC");
        foreach (var n in new[] { "Windows", "Users", "Program Files", "$Recycle.Bin" }) Directory.CreateDirectory(Path.Combine(c, n));
        WriteFile(Path.Combine(c, "Daten", "Kunden", "liste.txt"), new string('d', 2048));
        WriteFile(Path.Combine(c, "Scans", "scan.pdf"), "s");
        var ziel = Path.Combine(c, "DaSi", "Lauf");
        var vorher = DriveFolders.Wurzel;
        var start = FindButton(w, "Ausgewählte Aktionen starten");
        try
        {
            DriveFolders.Wurzel = c + Path.DirectorySeparatorChar;
            // There and back: re-reads the labels with the new drive.
            Click(Element<RadioButton>(w, "Wiederherstellen"));
            Click(Element<RadioButton>(w, "Sichern"));
            foreach (var p in typeof(MainWindowViewModel).GetProperties().Where(p => p.Name.StartsWith("Sel") && p.PropertyType == typeof(bool)))
                p.SetValue(vm, false);
            vm.SourcePath = Path.Combine(root, "Besitzer");
            vm.BackupPath = ziel;
            Check("Kachel frei, Beschriftung nennt das Laufwerk", vm.EnLaufwerk && vm.TgLaufwerk == $"Ordner unter {DriveFolders.Wurzel} sichern", vm.TgLaufwerk);
            var kachel = All<CheckBox>(w).First(cb => Text(cb.Content) == vm.TgLaufwerk);
            Check("Tooltip: Liste beim Anhaken, gesichert wird beim Start", (vm.TipLaufwerk ?? "").Contains("Beim Anhaken") && vm.TipLaufwerk!.Contains("Ausgewählte Aktionen starten"), vm.TipLaufwerk);

            // Checking the box opens the list. Cancel: box unchecked again.
            Click(kachel);
            var d = await Dialog(w);
            CheckBox Row(string name) => All<CheckBox>(d).First(cb => cb.Tag is DriveFolders.Eintrag e && e.Name == name);
            var sichtbar = All<CheckBox>(d).Where(cb => cb.IsVisible && cb.Tag is DriveFolders.Eintrag).Select(cb => ((DriveFolders.Eintrag)cb.Tag!).Name).ToList();
            Check("Anhaken oeffnet die Liste: eigene Ordner sichtbar, Windows-Ordner und Sicherungsziel nicht", string.Join(",", sichtbar) == "Daten,Scans"
                && !All<CheckBox>(d).Any(cb => cb.Tag is DriveFolders.Eintrag e && e.Name == "DaSi"), string.Join(",", sichtbar));
            Check("Liste: nichts vorgewaehlt, Uebernehmen erst nach Wahl", All<CheckBox>(d).Where(cb => cb.Tag is DriveFolders.Eintrag).All(cb => cb.IsChecked != true)
                && !FindButton(d, "Übernehmen").IsEffectivelyEnabled);
            Check("Liste: Groesse erscheint", await WaitFor(() => DialogText(d).Contains("2 KB") || DialogText(d).Contains("2.0 KB") || DialogText(d).Contains("2,0 KB")), DialogText(d));
            Check("Liste sagt: erst vormerken, gestartet wird im Hauptfenster", DialogText(d).Contains("merkt sie vor"), DialogText(d));
            Click(FindButton(d, "Abbrechen"));
            Check("Abgebrochen: Haken wieder weg, nichts vorgemerkt", await WaitFor(() => !vm.SelLaufwerk) && vm.LaufwerkGewaehlt.Count == 0 && !vm.LaufwerkKnopfSichtbar);

            // Show Windows folders, choose one, hide them again: it drops out.
            Click(kachel);
            d = await Dialog(w);
            var ausblenden = All<CheckBox>(d).First(cb => Text(cb.Content).StartsWith("Windows-Standardordner ausblenden"));
            Check("Haken 'Windows-Standardordner ausblenden' steht", ausblenden.IsChecked == true);
            Click(ausblenden);
            Check("ohne Haken: Windows-Ordner sichtbar", Row("Windows").IsVisible && Row("Users").IsVisible && Row("$Recycle.Bin").IsVisible);
            Click(Row("Windows"));
            Click(ausblenden);
            Check("wieder ausgeblendet: Windows-Ordner abgewaehlt", !Row("Windows").IsVisible && Row("Windows").IsChecked != true);
            Click(FindButton(d, "Alle"));
            Check("'Alle' waehlt nur Sichtbares", Row("Daten").IsChecked == true && Row("Scans").IsChecked == true && Row("Users").IsChecked != true);
            Click(FindButton(d, "Keine"));
            Click(Row("Daten"));
            Check("Summe nennt die Wahl", DialogText(d).Contains("1 gewählt"), DialogText(d));
            Click(FindButton(d, "Übernehmen"));
            Check("Uebernommen: vorgemerkt, Knopf zeigt die Zahl, noch nichts gesichert",
                await WaitFor(() => vm.LaufwerkKnopfSichtbar) && vm.SelLaufwerk && vm.LaufwerkKnopf == "1 Ordner …" && vm.LogText.Contains("Vorgemerkt")
                && !vm.LogText.Contains("Backup-Laufwerk") && !Directory.Exists(ziel) && (vm.TipLaufwerk ?? "").Contains("Vorgemerkt: Daten"), vm.LogText);

            // Change through the button: pre-selected folders are checked.
            Click(FindButton(w, "1 Ordner …"));
            d = await Dialog(w);
            Check("Aendern: Vorgemerktes angehakt", Row("Daten").IsChecked == true && Row("Scans").IsChecked != true);
            Click(Row("Scans"));
            Click(FindButton(d, "Übernehmen"));
            Check("Aendern: jetzt zwei", await WaitFor(() => vm.LaufwerkKnopf == "2 Ordner …") && vm.LaufwerkGewaehlt.Select(e => e.Name).SequenceEqual(new[] { "Daten", "Scans" }), vm.LaufwerkKnopf);
            Click(FindButton(w, "2 Ordner …"));
            d = await Dialog(w);
            Click(FindButton(d, "Abbrechen"));
            Check("Aendern abgebrochen: Wahl bleibt", await WaitFor(() => w.OwnedWindows.Count == 0) && vm.SelLaufwerk && vm.LaufwerkGewaehlt.Count == 2);

            // Only now: Start - without another list.
            Click(start);
            await Task.Delay(300);
            Render();
            Check("Start fragt nicht noch einmal", w.OwnedWindows.Count == 0);
            Check("Lauf mit den vorgemerkten Ordnern", await Finished(vm) && vm.LogText.Contains(": Daten, Scans") && vm.LogText.Contains("Backup-Laufwerk"), vm.LogText);
            Check("Daten in der Sicherung (Windows)", !OperatingSystem.IsWindows() || File.Exists(Path.Combine(ziel, DriveFolders.BackupOrdner, "Daten", "Kunden", "liste.txt")), vm.LogText);
            var bericht = Directory.GetFiles(ziel, "Sicherung-Protokoll_*.txt").FirstOrDefault();
            Check("Protokolldatei nennt die Aktion", bericht is not null && File.ReadAllText(bericht).Contains("Ordner unter"), bericht);
            Check("nach dem Lauf bleibt die Wahl vorgemerkt", vm.SelLaufwerk && vm.LaufwerkGewaehlt.Count == 2);

            // Restore: onto an empty new drive.
            if (!OperatingSystem.IsWindows()) WriteFile(Path.Combine(ziel, DriveFolders.BackupOrdner, "Daten", "Kunden", "liste.txt"), "d");
            var neu = Path.Combine(root, "LaufwerkNeu");
            Directory.CreateDirectory(neu);
            DriveFolders.Wurzel = neu + Path.DirectorySeparatorChar;
            Click(Element<RadioButton>(w, "Wiederherstellen"));
            Check("Richtung gewechselt: Wahl verworfen", !vm.SelLaufwerk && vm.LaufwerkGewaehlt.Count == 0);
            vm.BackupPath = ziel;
            Check("Wiederherstellen: Kachel frei", vm.EnLaufwerk && vm.TgLaufwerk.EndsWith("wiederherstellen"), vm.TipLaufwerk);
            Click(All<CheckBox>(w).First(cb => Text(cb.Content) == vm.TgLaufwerk));
            d = await Dialog(w);
            Check("Wiederherstellen: Gesichertes vorgewaehlt", d.Title?.Contains("wiederherstellen") == true && Row("Daten").IsChecked == true && FindButton(d, "Übernehmen").IsEffectivelyEnabled, d.Title);
            Click(FindButton(d, "Übernehmen"));
            Check("Wiederherstellen: vorgemerkt", await WaitFor(() => vm.LaufwerkKnopfSichtbar) && vm.LogText.Contains("Vorgemerkt - Ordner nach"), vm.LogText);
            Click(start);
            Check("Wiederherstellung gelaufen", await Finished(vm) && vm.LogText.Contains("Restore-Laufwerk"), vm.LogText);
            Check("Daten auf dem neuen Laufwerk (Windows)", !OperatingSystem.IsWindows() || File.Exists(Path.Combine(neu, "Daten", "Kunden", "liste.txt")), vm.LogText);
            vm.BackupPath = Path.Combine(root, "Ziel", "Lauf1");
            Check("andere Sicherung gewaehlt: Wahl verworfen", !vm.SelLaufwerk && vm.LaufwerkGewaehlt.Count == 0);
            vm.BackupPath = ziel;

            // Without a window: an empty choice, a cancel.
            vm.LaufwerkAuswahl = (_, _, _) => Task.FromResult<List<DriveFolders.Eintrag>?>(new List<DriveFolders.Eintrag>());
            vm.SelLaufwerk = true;
            Check("leere Wahl: Haken wieder weg", await WaitFor(() => !vm.SelLaufwerk));
            vm.LaufwerkAuswahl = (_, _, _) => Task.FromResult<List<DriveFolders.Eintrag>?>(null);
            vm.SelLaufwerk = true;
            Check("Wahl abgebrochen: Haken wieder weg", await WaitFor(() => !vm.SelLaufwerk));
        }
        finally
        {
            vm.LaufwerkAuswahl = null;
            vm.SelLaufwerk = false;
            DriveFolders.Wurzel = vorher;
            Click(Element<RadioButton>(w, "Sichern"));
        }
    }

    private static async Task Dialogs(MainWindow w)
    {
        Console.WriteLine("\n== Dialoge: per Fenster-X geschlossen ==");
        var frage = MessageBox.ShowYesNo(w, "Titel", "Frage?");
        var d = await Dialog(w);
        d.Close();
        Check("Ja/Nein per X geschlossen: gilt als Nein", await frage == false);
        var info = MessageBox.ShowInfo(w, "Titel", "Info");
        d = await Dialog(w);
        d.Close();
        await info;
        Check("Hinweis per X geschlossen", w.OwnedWindows.Count == 0);
        var auswahl = WingetSelectionDialog.Show(w, new[] { new WingetPackage { PackageIdentifier = "" } });
        d = await Dialog(w);
        Check("Paket ohne Kennung: 'Unbekanntes Paket'", All<CheckBox>(d).Any(c => Text(c.Content) == "Unbekanntes Paket"));
        d.Close();
        Check("Winget-Auswahl per X geschlossen: Abbruch", await auswahl is null);
        var ordner = FolderSelectionDialog.Show(w, new[] { new DriveFolders.Eintrag("Windows", Path.GetTempPath(), true) }, false);
        d = await Dialog(w);
        Check("Ordner-Auswahl: nur Windows-Ordner - Hinweis statt leerer Liste", DialogText(d).Contains("außer den Windows-Ordnern keine Ordner"), DialogText(d));
        d.Close();
        Check("Ordner-Auswahl per X geschlossen: Abbruch", await ordner is null);
    }

    private static void MoreFeatures(string root)
    {
        Console.WriteLine("\n== Sprache von Windows, zweites Fenster, Absturzbericht ==");
        var vorher = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("en-US");
            Check("Windows englisch -> Englisch", SystemHelpers.GetWindowsLanguage() == "en");
            var englisch = new MainWindowViewModel();
            Check("neues Fenster in englischer Umgebung startet englisch", englisch.IsEnglish && englisch.SelectedLanguageIndex == 1 && englisch.LogText.Contains("Waiting for input"));
            Check("englisch von Anfang an: auch der Log-Knopf", englisch.LoggingLabel == "Log: off", englisch.LoggingLabel);
            englisch.IsEnglish = false;
            CultureInfo.CurrentUICulture = new CultureInfo("de-DE");
            Check("Windows deutsch -> Deutsch", SystemHelpers.GetWindowsLanguage() == "de");
            CultureInfo.CurrentUICulture = new CultureInfo("fr-FR");
            Check("andere Sprache -> Deutsch", SystemHelpers.GetWindowsLanguage() == "de");
        }
        finally { CultureInfo.CurrentUICulture = vorher; }

        // Crash report: ends up on the desktop (or in the temp folder).
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        if (string.IsNullOrEmpty(desktop) || !Directory.Exists(desktop)) { Console.WriteLine("         (kein Desktop-Ordner)"); return; }
        var bericht = Path.Combine(desktop, "WindowsDaSiTool_Absturz.log");
        var vorherLaenge = File.Exists(bericht) ? new FileInfo(bericht).Length : -1;
        try
        {
            WindowsDaSiTool.Program.WriteCrashLog("Pruefung", new InvalidOperationException("aussen", new ArgumentException("innen")));
            var text = File.Exists(bericht) ? File.ReadAllText(bericht) : "";
            Check("Absturzbericht mit Quelle, Meldung und innerer Ausnahme",
                text.Contains("Quelle:  Pruefung") && text.Contains("aussen") && text.Contains("InnerException") && text.Contains("innen"), bericht);
            WindowsDaSiTool.Program.WriteCrashLog("ohne Ausnahme", null);
            Check("Absturzbericht auch ohne Ausnahme", File.ReadAllText(bericht).Contains("(unbekannt)"));
        }
        finally
        {
            try
            {
                if (vorherLaenge < 0) File.Delete(bericht);
                else using (var f = new FileStream(bericht, FileMode.Open)) f.SetLength(vorherLaenge);
            }
            catch { }
        }
    }

    private static async Task Closing(MainWindow w, MainWindowViewModel vm)
    {
        Console.WriteLine("\n== Schliessen: Rueckfrage nur, wenn etwas laeuft ==");
        vm.IsActionRunning = true;
        w.Close();
        await Answer(w, "Nein", "Es läuft gerade eine Aktion", "laufende Aktion: Rueckfrage vor dem Schliessen");
        Check("Nein: Fenster bleibt offen", w.IsVisible);
        w.Close();
        await Answer(w, "Ja", "hart abgebrochen");
        Check("Ja: Fenster geschlossen", await WaitFor(() => !w.IsVisible));

        // Without a running action: closes without asking.
        var life = (ClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        var w2 = new MainWindow { DataContext = new MainWindowViewModel() };
        life.MainWindow = w2;
        w2.Show();
        Render();
        w2.Close();
        Check("ohne laufende Aktion: schliesst ohne Rueckfrage", await WaitFor(() => !w2.IsVisible) && w2.OwnedWindows.Count == 0);
    }

    /// <summary>
    /// Start the program itself - the way a user does - and close it again.
    /// Windows only (a desktop is available there).
    /// </summary>
    private static void ProgramStartCheck()
    {
        if (!OperatingSystem.IsWindows()) return;
        Console.WriteLine("\n== Das Programm selbst starten und schliessen ==");
        var exe = Path.Combine(AppContext.BaseDirectory, "WindowsDaSiTool.exe");
        if (!File.Exists(exe)) { Check("WindowsDaSiTool.exe liegt neben der Pruefung", false, exe); return; }
        using var p = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory })!;
        var uhr = Stopwatch.StartNew();
        while (uhr.Elapsed.TotalSeconds < 60 && !p.HasExited)
        {
            p.Refresh();
            if (p.MainWindowHandle != IntPtr.Zero) break;
            Thread.Sleep(250);
        }
        Check("Programm startet und zeigt sein Fenster", !p.HasExited && p.MainWindowHandle != IntPtr.Zero, $"{uhr.Elapsed.TotalSeconds:F1} s");
        Thread.Sleep(1500);
        p.CloseMainWindow();
        var zu = p.WaitForExit(30_000);
        if (!zu) try { p.Kill(true); } catch { }
        Check("Programm schliesst sich sauber", zu && p.ExitCode == 0, zu ? $"ExitCode {p.ExitCode}" : "haengt");
    }
}
