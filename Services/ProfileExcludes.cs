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
        @"AppData\Local\Google\Chrome\User Data\Default\Cache",
        @"AppData\Local\BraveSoftware\Brave-Browser\User Data\Default\Cache",
        @"AppData\Local\Microsoft\Edge\User Data\Default\Cache",
        @"AppData\Local\Packages\Microsoft.Windows.Search_cw5n1h2txyewy"
    };

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
        excluded.AddRange(AppDataCaches.Select(d => Path.Combine(userDir, d)));

        if (!includeCloud)
        {
            excluded.AddRange(CloudProviders.Detect(userDir).Select(c => c.Path));
        }

        // Dublettenfrei: dieselbe Angabe zweimal an robocopy zu uebergeben ist
        // zwar harmlos, macht die Befehlszeile aber unnoetig lang.
        return excluded.Distinct(System.StringComparer.OrdinalIgnoreCase).ToList();
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
}
