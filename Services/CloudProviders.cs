using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WindowsDaSiTool.Services;

/// <summary>Ein im Benutzerprofil gefundener Ordner eines Cloud-Anbieters.</summary>
public sealed record CloudFolder(string Provider, string Path);

/// <summary>
/// Erkennt die Ordner der gaengigen Cloud-Anbieter im Benutzerprofil.
///
/// Warum erkannt und nicht fest eingetragen: Die Ordner heissen je nach
/// Anbieter, Fassung und Sprache anders - OneDrive haengt den Firmennamen an
/// ("OneDrive - Muster GmbH"), Nextcloud und ownCloud lassen den Namen beim
/// Einrichten frei waehlen. Eine feste Liste trifft deshalb nie alle.
///
/// Die Erkennung schaut ausschliesslich auf Ordnernamen DIREKT im Profil. Sie
/// liest nichts aus der Registry und startet nichts - sie darf jederzeit und
/// ohne Rechte laufen, auch waehrend die Oberflaeche auf sie wartet.
/// </summary>
public static class CloudProviders
{
    /// <summary>
    /// Bekannte Anbieter. Je Eintrag der Anzeigename und die Ordnermuster.
    ///
    /// Ein Muster ohne '*' muss genau passen, eines mit '*' am Ende passt auf
    /// jeden Ordner, der so beginnt. Das deckt die Faelle ab, in denen der
    /// Anbieter den Konto- oder Firmennamen anhaengt.
    ///
    /// Bewusst eine ueberschaubare Auswahl: Was haeufig vorkommt, steht drin -
    /// eine vollstaendige Liste aller Anbieter waere nicht zu pflegen.
    /// </summary>
    public static readonly IReadOnlyList<(string Provider, string[] Patterns)> Known = new[]
    {
        ("OneDrive",      new[] { "OneDrive", "OneDrive*" }),
        ("Dropbox",       new[] { "Dropbox", "Dropbox*" }),
        ("Google Drive",  new[] { "Google Drive", "GoogleDrive", "Google Drive File Stream" }),
        ("iCloud",        new[] { "iCloudDrive", "iCloud Drive", "iCloudPhotos" }),
        ("STRATO HiDrive", new[] { "HiDrive", "STRATO HiDrive", "HiDrive*" }),
        ("Nextcloud",     new[] { "Nextcloud", "Nextcloud*" }),
        ("ownCloud",      new[] { "ownCloud", "owncloud", "ownCloud*" }),
        ("MagentaCLOUD",  new[] { "MagentaCLOUD", "Magenta CLOUD", "Telekom Mediencenter" }),
        ("pCloud",        new[] { "pCloud", "pCloudDrive" }),
        ("MEGA",          new[] { "MEGA", "MEGAsync" }),
        ("Box",           new[] { "Box", "Box Sync" }),
        ("Tresorit",      new[] { "Tresorit" }),
        ("Seafile",       new[] { "Seafile" }),
        ("Proton Drive",  new[] { "Proton Drive", "ProtonDrive" }),
        ("Jottacloud",    new[] { "Jottacloud" }),
        ("Sync.com",      new[] { "Sync" }),
    };

    /// <summary>
    /// Sucht die Cloud-Ordner direkt unterhalb von <paramref name="userDir"/>.
    ///
    /// Jeder Ordner wird hoechstens einmal gemeldet, auch wenn mehrere Muster
    /// auf ihn passen (bei OneDrive treffen "OneDrive" und "OneDrive*"
    /// zugleich). Die Reihenfolge folgt <see cref="Known"/>, damit die Meldung
    /// an den Benutzer immer gleich aussieht.
    /// </summary>
    public static List<CloudFolder> Detect(string userDir)
    {
        var gefunden = new List<CloudFolder>();
        if (string.IsNullOrWhiteSpace(userDir)) return gefunden;

        string[] unterordner;
        try
        {
            if (!Directory.Exists(userDir)) return gefunden;
            unterordner = Directory.GetDirectories(userDir)
                .Select(System.IO.Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .ToArray();
        }
        catch
        {
            // Ein nicht lesbares Profil darf die Sicherung nicht aufhalten.
            return gefunden;
        }

        var schonGemeldet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (provider, muster) in Known)
        {
            foreach (var name in unterordner)
            {
                if (schonGemeldet.Contains(name)) continue;
                if (!muster.Any(m => Matches(name, m))) continue;

                schonGemeldet.Add(name);
                gefunden.Add(new CloudFolder(provider, System.IO.Path.Combine(userDir, name)));
            }
        }

        return gefunden;
    }

    /// <summary>
    /// Vergleicht einen Ordnernamen mit einem Muster. Nur ein '*' am Ende wird
    /// unterstuetzt - mehr braucht es hier nicht, und eine vollstaendige
    /// Mustersprache waere nur eine weitere Fehlerquelle.
    ///
    /// Ein Praefixmuster verlangt mindestens ein weiteres Zeichen: Sonst meldet
    /// "OneDrive*" denselben Ordner ein zweites Mal, den "OneDrive" schon
    /// getroffen hat - harmlos wegen der Dublettenpruefung, aber irrefuehrend.
    /// </summary>
    private static bool Matches(string ordnerName, string muster)
    {
        if (!muster.EndsWith('*'))
            return string.Equals(ordnerName, muster, StringComparison.OrdinalIgnoreCase);

        var praefix = muster[..^1];
        return ordnerName.Length > praefix.Length
               && ordnerName.StartsWith(praefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Fasst die gefundenen Anbieter fuer eine Meldung zusammen, etwa
    /// "OneDrive, Dropbox, STRATO HiDrive". Jeder Anbieter erscheint einmal,
    /// auch wenn er mehrere Ordner hat.
    /// </summary>
    public static string Summarize(IEnumerable<CloudFolder> folders)
        => string.Join(", ", folders.Select(f => f.Provider).Distinct());
}
