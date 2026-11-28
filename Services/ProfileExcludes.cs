using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WindowsDaSiTool.Services;

/// <summary>
/// Stellt zusammen, WAS beim Sichern eines Benutzerprofils ausgelassen wird.
///
/// Bewusst getrennt von <see cref="BackupService"/> und ohne jede Abhaengigkeit:
/// Diese Entscheidung laesst sich dadurch pruefen, ohne dass dabei irgendetwas
/// kopiert wird - dieselbe Trennung wie zwischen Katalog und Ausfuehrung.
///
/// Dieselbe Liste benutzen das Backup und die Groessenschaetzung. Liefen sie
/// auseinander, zeigte die Ampel eine Zahl an, die mit dem spaeteren Ergebnis
/// nichts zu tun haette.
/// </summary>
public static class ProfileExcludes
{
    /// <summary>
    /// Altlasten aus Windows XP. Es sind Verknuepfungspunkte (Junctions), die
    /// auf andere Stellen im Profil zeigen - ohne Ausschluss laeuft robocopy
    /// im Kreis. Sie bleiben IMMER draussen, egal was angehakt ist.
    /// </summary>
    private static readonly string[] AlwaysExcluded =
    {
        "Anwendungsdaten", "Application Data", "Cookies", "Links", "Favorites",
        "Local Settings", "My Documents", "NetHood", "PrintHood", "Recent", "Templates",
        "Start Menu", "Druckumgebung", "Netzwerkumgebung", "SendTo", "Vorlagen",
        "Lokale Einstellungen", "Eigene Dateien"
    };

    /// <summary>
    /// Zwischenspeicher innerhalb von AppData. Sie fliegen auch dann raus,
    /// wenn AppData ausdruecklich mitgesichert wird: Es sind Dateien, die sich
    /// jederzeit neu anlegen, zusammen aber mehrere Gigabyte wiegen. Sie
    /// mitzunehmen kostet nur Zeit und Platz.
    /// </summary>
    private static readonly string[] AppDataCaches =
    {
        @"AppData\Local\Temp",
        @"AppData\Local\Microsoft\Windows\INetCache",
        @"AppData\Local\Microsoft\Windows\Explorer",
        @"AppData\Local\CrashDumps",
        @"AppData\Local\Packages\Microsoft.Windows.Search_cw5n1h2txyewy"
    };

    /// <summary>Die Datenordner der Chromium-Browser in AppData.</summary>
    private static readonly string[] ChromiumUserData =
    {
        @"AppData\Local\Google\Chrome\User Data",
        @"AppData\Local\BraveSoftware\Brave-Browser\User Data",
        @"AppData\Local\Microsoft\Edge\User Data",
    };

    /// <summary>
    /// Zwischenspeicher der Grafikkarte direkt unter "User Data". Sie passen
    /// ohnehin nur zu Grafikkarte und Treiber des alten Rechners, und solange
    /// der Browser im Hintergrund laeuft, sind sie gesperrt.
    /// </summary>
    private static readonly string[] ChromiumGemeinsameCaches = { "GrShaderCache", "ShaderCache", "GraphiteDawnCache" };

    /// <summary>Zwischenspeicher im Standardprofil ("Default") eines Chromium-Browsers.</summary>
    private static readonly string[] ChromiumProfilCaches = { "Cache", "Code Cache", "GPUCache", "DawnWebGPUCache", "DawnGraphiteCache" };

    /// <summary>
    /// Zustand von Windows-Bestandteilen, der nur zu diesem einen Rechner und
    /// dieser einen Fassung passt. Auf einem anderen Rechner richtet er
    /// Schaden an, statt zu helfen:
    ///
    /// - App-Installer (winget): seine Datenbanken. Stammen sie von einer
    ///   neueren winget-Fassung als der auf dem Zielrechner, scheitert danach
    ///   JEDE Installation mit 0x8A150009 ("Cannot write to index; it is a
    ///   higher schema version") - so geschehen nach einer Wiederherstellung
    ///   mit AppData auf einen frisch eingerichteten Rechner.
    /// - Windows-Shell (Suche, Widgets, Startmenue): ihr App-Index und ihre
    ///   eingebettete Browser-Ansicht. Beides legt Windows selbst neu an, und
    ///   im laufenden Betrieb ist es ohnehin gesperrt.
    /// - "WindowsApps": die App-Aliase (winget.exe, python.exe ...), die
    ///   Windows fuer installierte Apps anlegt. Es sind Verweise, keine
    ///   Dateien - lesen laesst sich keiner (Fehler 1920), und auf dem neuen
    ///   Rechner gehoeren sie zu den dort installierten Apps.
    ///
    /// Sie bleiben in beide Richtungen draussen: beim Sichern und - auch fuer
    /// Sicherungen von frueher, die sie noch enthalten - beim Zurueckschreiben.
    /// </summary>
    private static readonly string[] MaschinenZustand =
    {
        @"AppData\Local\Packages\Microsoft.DesktopAppInstaller_8wekyb3d8bbwe",
        @"AppData\Local\Packages\Microsoft.Winget.Source_8wekyb3d8bbwe",
        @"AppData\Local\Packages\MicrosoftWindows.Client.CBS_cw5n1h2txyewy",
        @"AppData\Local\Packages\MicrosoftWindows.Client.Core_cw5n1h2txyewy",
        @"AppData\Local\Microsoft\WindowsApps",
    };

    /// <summary>
    /// Alles innerhalb von AppData, was nie mitgenommen wird - relativ zum
    /// Profil. Oeffentlich fuer die Pruefungen.
    /// </summary>
    public static IEnumerable<string> AppDataExcludes()
    {
        foreach (var c in AppDataCaches) yield return c;
        foreach (var browser in ChromiumUserData)
        {
            // Bewusst mit "\\" statt Path.Combine: Die Angaben sind Windows-Pfade,
            // wie die Eintraege oben - auch in der Pruefumgebung unter Linux.
            foreach (var c in ChromiumGemeinsameCaches) yield return browser + "\\" + c;
            foreach (var c in ChromiumProfilCaches) yield return browser + "\\Default\\" + c;
        }
        foreach (var m in MaschinenZustand) yield return m;
    }

    /// <summary>
    /// Die Registry-Dateien des Profils. Sie sind fuer das Kopieren gesperrt,
    /// gehoeren an genau diesen Rechner und duerfen NIE in ein anderes Profil
    /// geschrieben werden - das ueberschriebe die Einstellungen des Zielbenutzers.
    /// </summary>
    public static readonly string[] RegistryHiveFiles = { "NTUSER.DAT*", "ntuser.ini", "ntuser.pol", "UsrClass.dat*" };

    /// <summary>
    /// robocopy-Schalter fuer das SICHERN aus dem Profil (ohne Quelle, Ziel, /XD).
    ///
    /// Frueher stand hier zusaetzlich /XA:SH. Das liess JEDE versteckte oder
    /// System-Datei aus - ohne Meldung im Protokoll. Betroffen waren nicht nur
    /// die Registry-Dateien, um die es ging, sondern auch Benutzerdaten:
    /// versteckte Dateien und Inhalte von Ordnern, die Windows als
    /// Systemordner kennzeichnet (angepasste Ordner mit eigenem Symbol oder
    /// eigener Ansicht). Jetzt werden nur die Registry-Dateien gezielt
    /// ausgelassen.
    ///
    /// /XJ bleibt: Verknuepfungspunkte wie "Eigene Bilder" in Dokumente zeigen
    /// auf andere Stellen des Profils, robocopy liefe sonst im Kreis.
    /// </summary>
    public static List<string> BackupSwitches()
    {
        var s = new List<string> { "/MIR", "/ZB", "/SL", "/R:0", "/W:0", "/MT:32", "/XJ", "/XF" };
        s.AddRange(RegistryHiveFiles);
        return s;
    }

    /// <summary>
    /// robocopy-Schalter fuer das ZURUECKSCHREIBEN ins Profil.
    ///
    /// /COPY:DAT statt /COPYALL: Kopiert werden Inhalt, Attribute und
    /// Zeitstempel - die Zugriffsrechte NICHT. Neue Dateien erben so die Rechte
    /// des Zielprofils und gehoeren dem Benutzer, in dessen Profil sie landen.
    /// /COPYALL uebertrug die Rechte des Sicherungsmediums (die Sicherung
    /// selbst speichert keine) - auf einem anderen Rechner oder fuer einen
    /// anderen Benutzer sind das fremde Rechte.
    /// </summary>
    public static List<string> RestoreSwitches()
    {
        var s = new List<string> { "/E", "/ZB", "/COPY:DAT", "/DCOPY:DAT", "/R:1", "/W:1", "/MT:32", "/XJ", "/XF" };
        s.AddRange(RegistryHiveFiles);
        return s;
    }

    /// <summary>
    /// Baut die Liste der ausgeschlossenen Ordner als vollstaendige Pfade.
    /// </summary>
    /// <param name="userDir">Das zu sichernde Benutzerprofil.</param>
    /// <param name="includeAppData">
    /// true: AppData wird mitgesichert (die Zwischenspeicher oben bleiben
    /// trotzdem draussen). false: AppData bleibt komplett aussen vor.
    /// </param>
    /// <param name="includeCloud">
    /// true: die erkannten Cloud-Ordner werden mitgesichert. false: sie
    /// bleiben draussen - sonst zoege das Backup Daten mit, die ohnehin beim
    /// Anbieter liegen.
    /// </param>
    public static List<string> Build(string userDir, bool includeAppData = false, bool includeCloud = false)
    {
        var excluded = AlwaysExcluded.Select(d => Path.Combine(userDir, d)).ToList();

        if (!includeAppData)
        {
            excluded.Add(Path.Combine(userDir, "AppData"));
        }

        // Die Zwischenspeicher immer - bei ausgeschlossenem AppData sind sie
        // ohnehin schon mit drin, doppelt genannt stoert robocopy nicht.
        excluded.AddRange(AppDataExcludes().Select(d => Path.Combine(userDir, d)));

        if (!includeCloud)
        {
            excluded.AddRange(CloudProviders.Detect(userDir).Select(c => c.Path));
        }

        // Dublettenfrei: dieselbe Angabe zweimal an robocopy zu uebergeben ist
        // zwar harmlos, macht die Befehlszeile aber unnoetig lang.
        return excluded.Distinct(System.StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Laeuft das Tool selbst aus dem Zielprofil heraus - typisch:
    /// "Desktop\WindowsDaSiTool.exe" -, die Datei an derselben Stelle der
    /// Sicherung. Sie wird beim Zurueckschreiben ausgelassen: Die laufende
    /// Programmdatei laesst sich ohnehin nicht ueberschreiben (Fehler 32),
    /// und in der Sicherung liegt meist eine aeltere Fassung. Sonst null.
    /// </summary>
    /// <param name="zielProfil">Das Profil, in das zurueckgeschrieben wird.</param>
    /// <param name="sicherungsProfil">Der Ordner "Benutzerprofil" der Sicherung.</param>
    /// <param name="programmPfad">Pfad des laufenden Programms (Environment.ProcessPath).</param>
    public static string? RunningProgramInBackup(string zielProfil, string sicherungsProfil, string? programmPfad)
    {
        if (string.IsNullOrWhiteSpace(programmPfad) || !IsInside(programmPfad, zielProfil) || SameFolder(programmPfad, zielProfil))
            return null;
        try
        {
            var rel = Path.GetRelativePath(Path.GetFullPath(zielProfil), Path.GetFullPath(programmPfad));
            return Path.Combine(sicherungsProfil, rel);
        }
        catch { return null; }
    }

    /// <summary>
    /// true, wenn <paramref name="path"/> derselbe Ordner wie
    /// <paramref name="baseDir"/> ist oder darunter liegt.
    ///
    /// Bewusst nicht einfach StartsWith: "C:\Users\Maximilian" beginnt mit
    /// "C:\Users\Max", liegt aber nicht darin. Deshalb wird gegen den Ordner
    /// MIT abschliessendem Trennzeichen verglichen.
    /// </summary>
    public static bool IsInside(string path, string baseDir)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(baseDir)) return false;

        static string Normal(string p)
        {
            try { p = Path.GetFullPath(p); } catch { /* so lassen, wie er ist */ }
            return p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        var pfad = Normal(path);
        var basis = Normal(baseDir);
        return string.Equals(pfad, basis, System.StringComparison.OrdinalIgnoreCase)
               || pfad.StartsWith(basis + Path.DirectorySeparatorChar, System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// true, wenn beide Angaben denselben Ordner meinen - unabhaengig von
    /// Gross-/Kleinschreibung und abschliessendem Trennzeichen.
    /// </summary>
    public static bool SameFolder(string a, string b) => IsInside(a, b) && IsInside(b, a);
}
