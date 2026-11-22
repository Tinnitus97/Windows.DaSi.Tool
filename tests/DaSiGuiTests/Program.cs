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
/// Prueft die Oberflaeche: das echte Hauptfenster samt ViewModel, ohne
/// Bildschirm (Avalonia.Headless). Geklickt wird mit der Maus - an der Stelle,
/// an der der Knopf steht. Ein verdeckter oder gesperrter Knopf faellt so auf.
///
/// Was hinter den Knoepfen passiert, laeuft echt: robocopy, die Sicherung
/// eines Testprofils, das Update ueber einen eigenen Webserver. Ersetzt sind
/// nur der Ordner-Dialog des Systems, der Browser und winget (Attrappe).
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

        // Update-Abfrage auf den eigenen Server umlenken (Update-Url.txt neben der EXE).
        using var server = new MiniHttp();
        var urlDatei = Path.Combine(SystemHelpers.GetApplicationDirectory(), UpdateService.UrlOverrideFileName);
        File.WriteAllText(urlDatei, "# Pruefung\n" + server.Basis + "update.json\n");
        server.Setze("update.json", $$"""{ "program": { "version": "9.9.9", "url": "{{server.Basis}}neu.exe", "sha256": "{{new string('a', 64)}}", "notes": "https://example.test/notes" } }""");
        server.Setze("neu.exe", new byte[50_000]);

        int ergebnis = 1;
        try
        {
            var lifetime = new ClassicDesktopStyleApplicationLifetime();
            BuildAvaloniaApp().SetupWithLifetime(lifetime);
            var cts = new CancellationTokenSource();
            Dispatcher.UIThread.Post(async () =>
            {
                try { await Laufen(lifetime, root, server); }
                catch (Exception ex) { Check("Pruefung lief ohne Ausnahme durch", false, ex.ToString()); }
                finally { cts.Cancel(); }
            });
            Dispatcher.UIThread.MainLoop(cts.Token);
            ProgrammStartPruefen();
        }
        finally
        {
            try { File.Delete(urlDatei); } catch { }
            Environment.SetEnvironmentVariable("DASI_WINGET_ATTRAPPE", null);
            try { Directory.Delete(root, true); } catch { }
        }
        Console.WriteLine($"\nErgebnis: {_ok} bestanden, {_fehler} fehlgeschlagen.");
        foreach (var f in Fehlschlaege) Console.WriteLine("  fehlgeschlagen: " + f);
        ergebnis = _fehler == 0 ? 0 : 1;
        return ergebnis;
    }

    // ------------------------------------------------------------ Hilfen

    private static void Rendern()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task<bool> Warte(Func<bool> bedingung, int ms = 15_000)
    {
        var uhr = Stopwatch.StartNew();
        while (!bedingung())
        {
            if (uhr.ElapsedMilliseconds > ms) return false;
            await Task.Delay(30);
            Rendern();
        }
        return true;
    }

    private static string Text(object? inhalt) => inhalt switch
    {
        string s => s,
        TextBlock t => t.Text ?? "",
        _ => inhalt?.ToString() ?? "",
    };

    private static IEnumerable<T> Alle<T>(Visual v) where T : Visual => v.GetVisualDescendants().OfType<T>();

    private static Button Knopf(Visual v, string beschriftung)
        => Alle<Button>(v).Where(b => b.IsEffectivelyVisible).FirstOrDefault(b => Text(b.Content) == beschriftung)
           ?? throw new InvalidOperationException($"Knopf \"{beschriftung}\" nicht gefunden: "
                                                  + string.Join(" | ", Alle<Button>(v).Select(b => Text(b.Content))));

    private static T Element<T>(Visual v, string beschriftung) where T : ContentControl
        => Alle<T>(v).FirstOrDefault(b => Text(b.Content) == beschriftung)
           ?? throw new InvalidOperationException($"{typeof(T).Name} \"{beschriftung}\" nicht gefunden.");

    /// <summary>Klickt mit der Maus in die Mitte des Steuerelements - wie ein Mensch.</summary>
    private static void Klicken(Control c)
    {
        Rendern();
        var top = TopLevel.GetTopLevel(c) ?? throw new InvalidOperationException("kein Fenster");
        var p = c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), top)
                ?? throw new InvalidOperationException("Position unbekannt");
        top.MouseMove(p);
        top.MouseDown(p, MouseButton.Left);
        top.MouseUp(p, MouseButton.Left);
        Rendern();
    }

    private static async Task<Window> Dialog(Window owner, int ms = 20_000)
    {
        await Warte(() => owner.OwnedWindows.Count > 0, ms);
        Rendern();
        return owner.OwnedWindows.LastOrDefault() ?? throw new TimeoutException("Kein Dialog erschienen.");
    }

    private static string DialogText(Window d) => string.Join("\n", Alle<TextBlock>(d).Select(t => t.Text));

    /// <summary>Wartet auf den Dialog, prueft seinen Text und klickt die Antwort.</summary>
    private static async Task<string> Antworten(Window owner, string antwort, string? erwartet = null, string? pruefung = null)
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
        Klicken(Knopf(d, antwort));
        await Warte(() => !owner.OwnedWindows.Contains(d));
        return text;
    }

    private static async Task<bool> Fertig(MainWindowViewModel vm, int ms = 300_000)
    {
        await Task.Delay(100);
        return await Warte(() => !vm.ExecuteCommand.IsRunning, ms);
    }

    private static void Datei(string pfad, string inhalt = "x")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(pfad)!);
        File.WriteAllText(pfad, inhalt);
    }

    // ------------------------------------------------------------ Ablauf

    private static async Task Laufen(ClassicDesktopStyleApplicationLifetime life, string root, MiniHttp server)
    {
        Console.WriteLine("== Start: Fenster, Titel, Begruessung ==");
        life.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var w = (MainWindow)life.MainWindow!;
        var vm = (MainWindowViewModel)w.DataContext!;
        var oeffnen = new List<string>();
        vm.Oeffnen = u => { oeffnen.Add(u); return true; };
        var bilder = new List<string>();
        vm.Sicherung.BildOeffnen = bilder.Add;
        // Gleiche Ausgangslage fuer jeden Lauf: deutsch, dunkel.
        vm.IsEnglish = false;
        if (vm.IsLightTheme) vm.ToggleThemeCommand.Execute(null);
        w.Show();
        Rendern();
        Check("Hauptfenster erscheint mit Versionsnummer", w.IsVisible && w.Title == vm.WindowTitle && vm.WindowTitle.StartsWith("Windows DaSi Tool "), w.Title);
        Check("Begruessungstext im Protokoll", vm.LogText.Contains("Warte auf Eingabe"), vm.LogText);
        var links = w.FindControl<ScrollViewer>("LeftScroll")!;
        Check("linke Spalte ohne Rollbalken (1200 x 720)", links.Extent.Height <= links.Viewport.Height + 1, $"{links.Extent.Height} > {links.Viewport.Height}");
        var eigeneKnoepfe = Alle<Button>(w).Where(b => b.GetType() == typeof(Button) && b.TemplatedParent is null).ToList();
        Check("alle Knoepfe haben einen Befehl", eigeneKnoepfe.Count >= 12 && eigeneKnoepfe.All(b => b.Command is not null),
            string.Join(", ", eigeneKnoepfe.Where(b => b.Command is null).Select(b => Text(b.Content))));

        Console.WriteLine("\n== Update-Streifen (eigener Server) ==");
        Check("neuere Version: Streifen erscheint", await Warte(() => vm.UpdateAvailable) && vm.UpdateBannerText.Contains("9.9.9"), vm.UpdateBannerText);
        Klicken(Knopf(w, "Was ist neu?"));
        Check("'Was ist neu?' oeffnet die Seite aus update.json", oeffnen.LastOrDefault() == "https://example.test/notes", string.Join(", ", oeffnen));
        Klicken(Knopf(w, "Jetzt aktualisieren"));
        await Antworten(w, "Nein", "Version 9.9.9 herunterladen", "Update: erst eine Rueckfrage");
        Check("Update abgelehnt: nichts geladen", await Warte(() => vm.LogText.Contains("Abgebrochen.")) && !server.Abrufe.ContainsKey("neu.exe"), vm.LogText);
        Klicken(Knopf(w, "Jetzt aktualisieren"));
        await Antworten(w, "Ja", "herunterladen");
        await Antworten(w, "OK", "Prüfsumme stimmt nicht", "falsche Pruefsumme: Download wird abgelehnt");
        Check("nach dem Fehlschlag bleibt das Programm bedienbar", await Warte(() => vm.UiEnabled) && w.IsVisible);

        await SpracheUndDarstellung(w, vm);
        await PfadeUndSpeicher(w, vm, root);
        await Moduswechsel(w, vm, root);
        await Warteschlange(w, vm, root);
        await WingetInDerOberflaeche(w, vm, root);
        await Dialoge(w);
        WeitereFunktionen(root);
        await Schliessen(w, vm);
    }

    private static async Task SpracheUndDarstellung(MainWindow w, MainWindowViewModel vm)
    {
        Console.WriteLine("\n== Sprache und Hell/Dunkel ==");
        Klicken(Knopf(w, "Log leeren"));
        Check("Protokoll geleert", await Warte(() => vm.LogText == ""), vm.LogText);
        var sprache = Alle<ComboBox>(w).First(c => c.ItemsSource == vm.Languages);
        sprache.SelectedIndex = 1;
        Rendern();
        Check("Englisch: Beschriftungen wechseln", Alle<Button>(w).Any(b => Text(b.Content) == "Start selected actions")
            && Alle<CheckBox>(w).Any(c => Text(c.Content) == "Windows user profile"));
        Check("Englisch: Begruessung wechselt mit, solange nichts im Protokoll steht",
            await Warte(() => vm.LogText.Contains("Waiting for input")), vm.LogText);
        sprache.SelectedIndex = 0;
        Rendern();
        Check("zurueck auf Deutsch", !vm.IsEnglish && Alle<Button>(w).Any(b => Text(b.Content) == "Ausgewählte Aktionen starten"));

        var thema = Alle<Button>(w).First(b => Text(b.Content) is "☀" or "☽");
        var vorher = Application.Current!.RequestedThemeVariant;
        Klicken(thema);
        Check("Hell/Dunkel umgeschaltet", Application.Current.RequestedThemeVariant != vorher && Text(thema.Content) == vm.ThemeIcon);
        Klicken(thema);
        Check("und wieder zurueck", Application.Current.RequestedThemeVariant == vorher);

        // Jede Beschriftung gibt es in beiden Sprachen.
        var props = typeof(MainWindowViewModel).GetProperties().Where(p => p.PropertyType == typeof(string) && p.GetIndexParameters().Length == 0).ToList();
        var de = props.ToDictionary(p => p.Name, p => (string?)p.GetValue(vm));
        vm.IsEnglish = true;
        var en = props.ToDictionary(p => p.Name, p => (string?)p.GetValue(vm));
        vm.IsEnglish = false;
        var gleich = props.Where(p => de[p.Name] == en[p.Name] && !string.IsNullOrEmpty(de[p.Name])
                                      && p.Name is not ("WindowTitle" or "SourcePath" or "BackupPath" or "LogText" or "ThemeIcon" or "UpdateBannerText"
                                                        or "TgDesktopScreenshot" or "ProgressText" or "StorageInfo"))
                          .Select(p => p.Name).ToList();
        Check("jede Beschriftung ist uebersetzt", gleich.Count == 0, string.Join(", ", gleich));

        Klicken(Knopf(w, "Log: aus"));
        await Antworten(w, "OK", "Detailliertes Log ist aktiv", "ausfuehrliches Protokoll: Hinweis auf die Geschwindigkeit");
        Check("ausfuehrliches Protokoll an", vm.DetailedLogging && vm.LoggingLabel == "Log: an" && vm.LogText.Contains("[HINWEIS]"));
        Klicken(Knopf(w, "Log: an"));
        Check("ausfuehrliches Protokoll wieder aus", !vm.DetailedLogging);

        foreach (var s in new[] { 0, 1, 2, 3 })
        {
            vm.StorageStatus = s;
            Check($"Ampelfarbe fuer Stufe {s}", vm.StorageBrush is Avalonia.Media.ISolidColorBrush);
        }
        vm.StorageStatus = 0;
    }

    private static async Task PfadeUndSpeicher(MainWindow w, MainWindowViewModel vm, string root)
    {
        Console.WriteLine("\n== Pfade waehlen, eigener Ordner, Speicher ==");
        var profil = Path.Combine(root, "Besitzer");
        Datei(Path.Combine(profil, "Documents", "brief.txt"), new string('b', 5000));
        Datei(Path.Combine(profil, "OneDrive", "Vertrag.docx"), "cloud");
        Datei(Path.Combine(profil, @"AppData\Roaming\Werkzeug\a.ini"), "a=1");
        var ziel = Path.Combine(root, "Ziel");
        Directory.CreateDirectory(ziel);

        // Ohne Haken: der echte Ordner-Dialog - ohne Bildschirm liefert er nichts.
        var pfadKnoepfe = Alle<Button>(w).Where(b => Text(b.Content) == "…").ToList();
        Klicken(pfadKnoepfe[0]);
        Check("Ordner-Dialog ohne Auswahl: Pfad bleibt leer", await Warte(() => !vm.SelectSourceCommand.IsRunning) && vm.SourcePath == "");

        var titel = new List<string>();
        vm.OrdnerDialog = t => { titel.Add(t); return Task.FromResult<string?>(titel.Count == 1 ? profil : ziel); };
        Klicken(pfadKnoepfe[0]);
        Check("Quelle gewaehlt", await Warte(() => vm.SourcePath == profil) && titel[0].Contains("Quell-Benutzerprofil"), vm.SourcePath);
        Klicken(pfadKnoepfe[1]);
        if (SystemHelpers.IsNtfsDrive(ziel))
        {
            var heute = BackupTarget.Resolve(ziel, true, Environment.MachineName, DateTime.Now);
            Check("Ziel gewaehlt: Feld zeigt gleich den eigenen Sicherungsordner", await Warte(() => vm.BackupPath == heute), vm.BackupPath);
            Check("Tooltip nennt den Ordner", vm.OwnFolderTip.Contains(heute));
            Klicken(Knopf(w, "Eigener Ordner: AN"));
            Check("eigener Ordner aus: direkt ins Ziel", vm.BackupPath == ziel && vm.OwnFolderTip.Contains("direkt in das gewählte Ziel"));
            Klicken(Knopf(w, "Eigener Ordner: AUS"));
            Check("eigener Ordner wieder an", vm.BackupPath == heute);
        }
        else
        {
            await Antworten(w, "OK", "NICHT mit NTFS formatiert", "Ziel ohne NTFS: abgelehnt");
            vm.BackupPath = ziel;
        }

        // Speicher pruefen: von Hand (mit Hinweisfenster nur bei knapp/rot) und automatisch.
        vm.SelUser = true;
        Klicken(Knopf(w, "Speicher prüfen"));
        Check("Speicher: Datenmenge berechnet", await Warte(() => !vm.StorageChecking && vm.StorageInfo.Contains("Datenmenge")), vm.StorageInfo);
        Check("Speicher: reicht (gruen)", vm.StorageStatus == 1, vm.StorageStatus.ToString());
        Klicken(Knopf(w, "Auto-Berechnung: AN"));
        Check("Auto-Berechnung aus", !vm.AutoStorageCheck && vm.AutoStorageLabel == "Auto-Berechnung: AUS");
        Klicken(Knopf(w, "Auto-Berechnung: AUS"));
        vm.SelUserAppData = true;
        vm.SelUserCloud = true;
        vm.SelFirefox = true;
        Check("Auto-Berechnung nach Auswahl", await Warte(() => !vm.StorageChecking && vm.StorageInfo.Contains("Datenmenge")), vm.StorageInfo);
        vm.SelUser = false;
        vm.SelFirefox = false;
        await Warte(() => !vm.StorageChecking);
        Klicken(Knopf(w, "Speicher prüfen"));
        Check("Speicher ohne Profil: AppData und Cloud einzeln", await Warte(() => !vm.StorageChecking && vm.StorageInfo.Contains("Datenmenge")), vm.StorageInfo);
        vm.SelUserAppData = vm.SelUserCloud = false;

        // Schnellauswahl: ein Profil daraus setzt den Pfad.
        if (vm.UserProfiles.Count > 0)
        {
            var schnell = Alle<ComboBox>(w).First(c => c.ItemsSource == vm.UserProfiles);
            schnell.SelectedIndex = 0;
            Check("Schnellauswahl setzt das Profil", vm.SourcePath == vm.UserProfiles[0]);
            vm.SourcePath = vm.UserProfiles[0];
            Check("Pfad aus der Liste: Schnellauswahl folgt", vm.SelectedProfile == vm.UserProfiles[0]);
        }

        Klicken(Knopf(w, "Pfade leeren"));
        Check("Pfade geleert", vm.SourcePath == "" && vm.BackupPath == "" && vm.LogText.Contains("Pfade wurden geleert"));
        Klicken(Knopf(w, "Speicher prüfen"));
        Check("Speicher ohne Ziel: Hinweis", await Warte(() => vm.StorageInfo.Contains("Bitte zuerst ein Backup-Ziel")), vm.StorageInfo);
        vm.OrdnerDialog = null;
    }

    private static async Task Moduswechsel(MainWindow w, MainWindowViewModel vm, string root)
    {
        Console.WriteLine("\n== Sichern / Wiederherstellen: Beschriftungen und gesperrte Kacheln ==");
        // Eine Sicherung, die nur ein Firefox-Profil und ein Desktop-Foto enthaelt.
        var sicherung = Path.Combine(root, "NurFirefox");
        Datei(Path.Combine(sicherung, "Firefox-Profil", "profiles.ini"), "[Profile0]");
        Datei(Path.Combine(sicherung, DesktopScreenshot.FolderName, DesktopScreenshot.FilePrefix + "1.png"), "png");
        vm.SourcePath = Path.Combine(root, "Besitzer");
        vm.BackupPath = sicherung;

        Klicken(Element<RadioButton>(w, "Wiederherstellen"));
        Check("Modus Wiederherstellen", vm.IsRestoreMode && !vm.IsBackupMode);
        Check("Beschriftungen folgen dem Modus", vm.TgWinget == "Programme installieren (Winget)" && vm.TgWallpaper == "Design wiederherstellen"
            && vm.TgUserAppData == "AppData wiederherstellen" && Alle<CheckBox>(w).Any(c => Text(c.Content) == "WLAN Profile importieren"));
        Check("eigener Ordner wirkt beim Wiederherstellen nicht", !vm.OwnFolderToggleEnabled && !Knopf(w, "Eigener Ordner: AN").IsEffectivelyEnabled);
        var chrome = Element<CheckBox>(w, "Chrome-Profil");
        Check("nicht in der Sicherung: Kachel gesperrt, Grund im Tooltip", !chrome.IsEffectivelyEnabled && (vm.TipChrome ?? "").Length > 0, vm.TipChrome);
        Check("in der Sicherung: Kachel frei", Element<CheckBox>(w, "Firefox-Profil").IsEffectivelyEnabled);
        Check("nur ein Foto gesichert: Variante Screenshot gewaehlt, Symbole gesperrt",
            !vm.DesktopSymbole && vm.EnDesktopScreenshot && !vm.EnDesktopSymbole && vm.TipDesktopSymbole.Contains("weder Symbolpositionen"), vm.TipDesktopSymbole);
        Check("Tooltip Screenshot beim Wiederherstellen", vm.TipDesktopScreenshot.Contains("Öffnet das Foto"));
        Check("Design nicht in der Sicherung: Grund statt Inhaltsangabe", !vm.EnWallpaper && !(vm.TipWallpaper ?? "").Contains("Mauszeiger"), vm.TipWallpaper);
        Klicken(Element<CheckBox>(w, "Firefox-Profil"));
        Check("freie Kachel laesst sich anhaken", vm.SelFirefox);
        vm.SelFirefox = false;

        Klicken(Element<RadioButton>(w, "Sichern"));
        Check("zurueck zum Sichern", !vm.IsRestoreMode && vm.TgWinget == "Programme exportieren (Winget)" && vm.TipDesktopScreenshot.Contains("Fotografiert"));
        Check("Design-Kachel nennt auch frei, was dazugehoert", vm.EnWallpaper && (vm.TipWallpaper ?? "").Contains("Mauszeiger"), vm.TipWallpaper);
        Klicken(Element<RadioButton>(w, "Symbole + Taskleiste"));
        Check("Desktop-Layout: Symbole gewaehlt", vm.DesktopSymbole && vm.DesktopSymboleGewaehlt && !vm.DesktopScreenshotGewaehlt && vm.TipDesktopSymbole.Contains("Merkt sich"));
        Klicken(Element<RadioButton>(w, "Screenshot"));
        Check("Desktop-Layout: Screenshot gewaehlt", !vm.DesktopSymbole);
        vm.IsBackupMode = true;
        Check("IsBackupMode setzen aendert nichts am Sichern", !vm.IsRestoreMode);

        // Jede Auswahl einmal an und aus - jede stoesst die Groessenberechnung an.
        var auswahl = typeof(MainWindowViewModel).GetProperties().Where(p => p.Name.StartsWith("Sel") && p.PropertyType == typeof(bool)).ToList();
        foreach (var p in auswahl) { p.SetValue(vm, true); p.SetValue(vm, false); }
        Check("jede Kachel laesst sich an- und abwaehlen", auswahl.Count >= 14 && auswahl.All(p => (bool)p.GetValue(vm)! == false), auswahl.Count.ToString());
    }

    private static async Task Warteschlange(MainWindow w, MainWindowViewModel vm, string root)
    {
        Console.WriteLine("\n== Starten: Rueckfragen und Warteschlange ==");
        var start = Knopf(w, "Ausgewählte Aktionen starten");
        var profil = Path.Combine(root, "Besitzer");
        foreach (var p in typeof(MainWindowViewModel).GetProperties().Where(p => p.Name.StartsWith("Sel") && p.PropertyType == typeof(bool)))
            p.SetValue(vm, false);
        vm.SourcePath = "";
        vm.BackupPath = "";

        Klicken(start);
        await Antworten(w, "OK", "mindestens eine Aktion", "ohne Auswahl: Hinweis");
        vm.SelUser = true;
        Klicken(start);
        await Antworten(w, "OK", "Quell- und Zielverzeichnis", "ohne Pfade: Hinweis");
        vm.SourcePath = Path.Combine(root, "GibtEsNicht");
        vm.BackupPath = Path.Combine(root, "Ziel");
        vm.SelUser = true;
        Klicken(start);
        await Antworten(w, "OK", "gibt es nicht", "Profil fehlt: Hinweis");

        // Cloud: Rueckfrage, abgelehnt.
        vm.SourcePath = profil;
        var ziel = Path.Combine(root, "Ziel", "Lauf1");
        vm.BackupPath = ziel;
        vm.SelUser = true;
        vm.SelUserCloud = true;
        Klicken(start);
        await Antworten(w, "Nein", "Erkannte Cloud-Anbieter: OneDrive", "Cloud: Rueckfrage nennt den Anbieter");
        Check("Cloud abgelehnt: nichts gestartet", await Fertig(vm) && vm.LogText.Contains("Cloud-Ordner wurden nicht bestätigt") && !Directory.Exists(ziel));

        // Jetzt wirklich: Profil mit Cloud, Desktop-Foto, Design - Ordner wird angelegt.
        vm.SelUserCloud = true;
        vm.SelUserAppData = true;
        vm.SelDesktopShot = true;
        vm.SelWallpaper = true;
        Klicken(start);
        await Antworten(w, "Ja", "Erkannte Cloud-Anbieter");
        Check("Lauf beendet", await Fertig(vm), vm.LogText);
        Check("Sicherungsordner angelegt und gemeldet", Directory.Exists(ziel) && vm.LogText.Contains("Sicherungsordner angelegt"), vm.LogText);
        Check("Profil samt Cloud gesichert", !OperatingSystem.IsWindows() || File.Exists(Path.Combine(ziel, "Benutzerprofil", "OneDrive", "Vertrag.docx")));
        Check("Bilanz im Protokoll", vm.LogText.Contains("[BILANZ]") && vm.LogText.Contains("Alle ausgewählten Aktionen abgeschlossen"), vm.LogText);
        var bericht = Directory.GetFiles(ziel, "Sicherung-Protokoll_*.txt").FirstOrDefault();
        Check("Protokolldatei im Ziel, jede Aktion mit Ergebnis", bericht is not null && File.ReadAllText(bericht).Contains("Windows Benutzerprofil")
            && File.ReadAllText(bericht).Contains("Desktop-Screenshot"), bericht);
        Check("Oberflaeche nach dem Lauf wieder frei", vm.UiEnabled && !vm.IsActionRunning && !vm.ProgressVisible);

        // In eine vorhandene Sicherung eines anderen Rechners: Rueckfrage.
        var fremd = Path.Combine(root, "Ziel", "DASI_ANDERER-PC_01_01_2026");
        Directory.CreateDirectory(fremd);
        foreach (var p in typeof(MainWindowViewModel).GetProperties().Where(p => p.Name.StartsWith("Sel") && p.PropertyType == typeof(bool)))
            p.SetValue(vm, false);
        vm.BackupPath = fremd;
        vm.SelUser = true;
        Klicken(start);
        await Antworten(w, "Nein", "ANDERER-PC", "fremde Sicherung: Rueckfrage");
        Check("fremde Sicherung abgelehnt", await Fertig(vm) && vm.LogText.Contains("bitte ein anderes Ziel"));
        Klicken(start);
        await Antworten(w, "Ja", "ANDERER-PC");
        Check("fremde Sicherung bestaetigt: wird aktualisiert", await Fertig(vm) && vm.LogText.Contains("Vorhandene Sicherung wird aktualisiert"));

        if (OperatingSystem.IsWindows())
        {
            // Ein Ziel, das sich nicht anlegen laesst.
            var frei = "QRSTUVWXYZ".Select(c => c + @":\").First(l => !DriveInfo.GetDrives().Any(d => string.Equals(d.Name, l, StringComparison.OrdinalIgnoreCase)));
            vm.BackupPath = frei + "Sicherung";
            Klicken(start);
            await Antworten(w, "OK", "konnte nicht angelegt werden", "Ziel nicht anlegbar: Hinweis");
        }

        // Wiederherstellen: fehlende Sicherung, AppData-Rueckfrage, dann echt.
        Klicken(Element<RadioButton>(w, "Wiederherstellen"));
        vm.BackupPath = Path.Combine(root, "GibtEsNicht");
        vm.SelUser = true;
        Klicken(start);
        await Antworten(w, "OK", "Den Sicherungsordner", "Sicherung fehlt: Hinweis");
        var anwender = Path.Combine(root, "Anwender");
        Directory.CreateDirectory(anwender);
        vm.SourcePath = anwender;
        vm.BackupPath = ziel;
        vm.SelUser = false;
        vm.SelUserAppData = true;
        vm.SelUserCloud = true;
        Klicken(start);
        await Antworten(w, "Ja", OperatingSystem.IsWindows() ? "Cloud-Anbieter in der Sicherung: OneDrive" : "Cloud-Anbieter in der Sicherung",
                        "Wiederherstellen: Cloud-Rueckfrage nennt, was in der Sicherung liegt");
        await Antworten(w, "Nein", "AppData wird zurückgeschrieben", "AppData zurueck: Warnung");
        Check("AppData abgelehnt: nichts gestartet", await Fertig(vm) && vm.LogText.Contains("AppData-Wiederherstellung wurde nicht bestätigt"));
        vm.SelUser = true;
        vm.SelDesktopShot = true;
        vm.SelWallpaper = true;
        Klicken(start);
        await Antworten(w, "Ja", "Cloud-Anbieter");
        await Antworten(w, "Ja", "AppData");
        Check("Wiederherstellung beendet", await Fertig(vm), vm.LogText);
        Check("Profil zurueck, Cloud daneben", !OperatingSystem.IsWindows() || File.Exists(Path.Combine(anwender, "Documents", "brief.txt"))
            && Directory.Exists(Path.Combine(anwender, "OneDrive_wiederhergestellt")), vm.LogText);
        Check("Protokolldatei der Wiederherstellung", Directory.GetFiles(ziel, "Wiederherstellung-Protokoll_*.txt").Length == 1);

        // Ohne Profil: AppData und Cloud einzeln - in beide Richtungen.
        vm.SelUser = false;
        vm.SelDesktopShot = vm.SelWallpaper = false;
        vm.SelUserAppData = true;
        vm.SelUserCloud = false;
        Klicken(start);
        await Antworten(w, "Ja", "AppData");
        Check("nur AppData zurueck", await Fertig(vm) && vm.LogText.Contains("Restore-AppData"), vm.LogText);
        Klicken(Element<RadioButton>(w, "Sichern"));
        vm.SourcePath = profil;
        vm.BackupPath = Path.Combine(root, "Ziel", "Teile");
        vm.SelUserAppData = true;
        vm.SelUserCloud = true;
        Klicken(start);
        await Antworten(w, "Ja", "Cloud");
        Check("nur AppData und Cloud gesichert", await Fertig(vm) && vm.LogText.Contains("Backup-AppData") && vm.LogText.Contains("Backup-Cloud"), vm.LogText);
        vm.SelUserAppData = vm.SelUserCloud = false;
    }

    private static async Task WingetInDerOberflaeche(MainWindow w, MainWindowViewModel vm, string root)
    {
        Console.WriteLine("\n== winget in der Oberflaeche (Attrappe) ==");
        var attrappe = Attrappen.WingetAttrappe();
        if (attrappe is null || !OperatingSystem.IsWindows())
        {
            Console.WriteLine("         (nur unter Windows mit gebauter Attrappe)");
            return;
        }
        var start = Knopf(w, "Ausgewählte Aktionen starten");
        vm.Winget.FesteKandidaten = new[] { attrappe };
        vm.Winget.Nachinstallation = () => Task.FromResult(false);
        var sicherung = Path.Combine(root, "Ziel", "Winget");
        var roh = Path.Combine(root, "export.json");
        File.WriteAllText(roh, """{ "Sources": [ { "Packages": [ { "PackageIdentifier": "Notepad++.Notepad++" }, { "PackageIdentifier": "7zip.7zip" } ] } ] }""");

        // winget kaputt, aber noch etwas anderes ausgewaehlt: ohne winget weiter.
        Attrappen.WingetSzenario(root, new Dictionary<string, string> { ["version.code"] = "1" });
        vm.SourcePath = Path.Combine(root, "Besitzer");
        vm.BackupPath = sicherung;
        vm.SelWinget = true;
        vm.SelUser = true;
        Klicken(start);
        await Antworten(w, "Ja", "Ohne den winget-Schritt fortfahren?", "winget kaputt, andere Aktion: weiter ohne winget");
        Check("ohne winget weiter: Profil gesichert, winget uebersprungen", await Fertig(vm) && vm.LogText.Contains("winget-Schritt wurde übersprungen")
            && !vm.LogText.Contains("Aktion Export-Winget"), vm.LogText);
        vm.SelUser = false;
        Klicken(start);
        await Antworten(w, "OK", "winget nicht einsatzbereit", "winget allein und kaputt: Schluss");
        Check("winget allein und kaputt: nichts gestartet", await Fertig(vm));
        vm.SelUser = true;
        Klicken(start);
        await Antworten(w, "Nein", "Ohne den winget-Schritt fortfahren?");
        Check("ohne winget weiter? Nein: nichts gestartet", await Fertig(vm) && vm.LogText.Contains("[INFO] Start abgebrochen."));
        vm.SelUser = false;

        // winget heil: Export ueber die Attrappe.
        Attrappen.WingetSzenario(root, new Dictionary<string, string> { ["export.datei"] = roh });
        Klicken(start);
        Check("Export ueber die Oberflaeche", await Fertig(vm) && File.Exists(Path.Combine(sicherung, "Winget", "Export.json")), vm.LogText);

        // Import: Liste fehlt, kaputt, leer - dann die Auswahl.
        Klicken(Element<RadioButton>(w, "Wiederherstellen"));
        vm.SelWinget = true;
        var leer = Path.Combine(root, "Ziel", "WingetLeer");
        Directory.CreateDirectory(Path.Combine(leer, "Winget"));
        vm.BackupPath = leer;
        vm.SelWinget = true;
        Klicken(start);
        await Antworten(w, "OK", "Exportdatei nicht gefunden", "Import ohne Liste");
        File.WriteAllText(Path.Combine(leer, "Winget", "Export.json"), "{ kaputt");
        vm.SelWinget = true;
        Klicken(start);
        await Antworten(w, "OK", "nicht als JSON", "Import mit kaputter Liste");
        File.WriteAllText(Path.Combine(leer, "Winget", "Export.json"), """{ "Sources": [] }""");
        vm.SelWinget = true;
        Klicken(start);
        await Antworten(w, "OK", "keine gültigen Programme", "Import mit leerer Liste");

        vm.BackupPath = sicherung;
        vm.SelWinget = true;
        Klicken(start);
        var auswahl = await Dialog(w);
        Check("Auswahl zeigt jedes Programm, alle angehakt", Alle<CheckBox>(auswahl).Count() == 2 && Alle<CheckBox>(auswahl).All(c => c.IsChecked == true));
        Klicken(Knopf(auswahl, "Keine"));
        Check("'Keine' hakt alles ab", Alle<CheckBox>(auswahl).All(c => c.IsChecked == false));
        Klicken(Knopf(auswahl, "Alle"));
        Klicken(Knopf(auswahl, "Abbrechen"));
        Check("Auswahl abgebrochen: nichts installiert", await Fertig(vm) && !vm.LogText.Contains("Installiere Notepad"));
        Klicken(start);
        auswahl = await Dialog(w);
        Klicken(Alle<CheckBox>(auswahl).First(c => Text(c.Content) == "7zip.7zip"));
        Klicken(Knopf(auswahl, "Installieren"));
        Check("Import: nur das Ausgewaehlte installiert", await Fertig(vm) && vm.LogText.Contains("Installiere Notepad++.Notepad++") && !vm.LogText.Contains("Installiere 7zip"), vm.LogText);
        Klicken(Element<RadioButton>(w, "Sichern"));
        vm.SelWinget = false;
        Environment.SetEnvironmentVariable("DASI_WINGET_ATTRAPPE", null);
    }

    private static async Task Dialoge(MainWindow w)
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
        Check("Paket ohne Kennung: 'Unbekanntes Paket'", Alle<CheckBox>(d).Any(c => Text(c.Content) == "Unbekanntes Paket"));
        d.Close();
        Check("Winget-Auswahl per X geschlossen: Abbruch", await auswahl is null);
    }

    private static void WeitereFunktionen(string root)
    {
        Console.WriteLine("\n== Sprache von Windows, zweites Fenster, Absturzbericht ==");
        var vorher = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("en-US");
            Check("Windows englisch -> Englisch", SystemHelpers.GetWindowsLanguage() == "en");
            var englisch = new MainWindowViewModel();
            Check("neues Fenster in englischer Umgebung startet englisch", englisch.IsEnglish && englisch.SelectedLanguageIndex == 1 && englisch.LogText.Contains("Waiting for input"));
            englisch.IsEnglish = false;
            CultureInfo.CurrentUICulture = new CultureInfo("de-DE");
            Check("Windows deutsch -> Deutsch", SystemHelpers.GetWindowsLanguage() == "de");
            CultureInfo.CurrentUICulture = new CultureInfo("fr-FR");
            Check("andere Sprache -> Deutsch", SystemHelpers.GetWindowsLanguage() == "de");
        }
        finally { CultureInfo.CurrentUICulture = vorher; }

        // Absturzbericht: landet auf dem Desktop (oder im Temp-Ordner).
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

    private static async Task Schliessen(MainWindow w, MainWindowViewModel vm)
    {
        Console.WriteLine("\n== Schliessen: Rueckfrage nur, wenn etwas laeuft ==");
        vm.IsActionRunning = true;
        w.Close();
        await Antworten(w, "Nein", "Es läuft gerade eine Aktion", "laufende Aktion: Rueckfrage vor dem Schliessen");
        Check("Nein: Fenster bleibt offen", w.IsVisible);
        w.Close();
        await Antworten(w, "Ja", "hart abgebrochen");
        Check("Ja: Fenster geschlossen", await Warte(() => !w.IsVisible));

        // Ohne laufende Aktion: ohne Rueckfrage zu.
        var life = (ClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        var w2 = new MainWindow { DataContext = new MainWindowViewModel() };
        life.MainWindow = w2;
        w2.Show();
        Rendern();
        w2.Close();
        Check("ohne laufende Aktion: schliesst ohne Rueckfrage", await Warte(() => !w2.IsVisible) && w2.OwnedWindows.Count == 0);
    }

    /// <summary>
    /// Das Programm selbst starten - so, wie ein Anwender es tut - und wieder
    /// schliessen. Nur unter Windows (dort steht ein Desktop zur Verfuegung).
    /// </summary>
    private static void ProgrammStartPruefen()
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
