using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WindowsDaSiTool.Services;

/// <summary>
/// Findet Ordner im Profil, die in Wahrheit Verknuepfungen sind (Junctions,
/// symbolische Links) und AUS dem Profil hinauszeigen.
///
/// Die Profilsicherung folgt Verknuepfungen bewusst nicht (/XJ): Die alten
/// Verweise wie "Eigene Bilder" in Dokumente zeigen zurueck ins Profil, robocopy
/// liefe sonst im Kreis oder saeh dieselben Dateien doppelt. Zeigt eine
/// Verknuepfung aber nach draussen - "Dokumente\Spiele" auf "D:\Spiele" -, fehlt
/// ihr Inhalt in der Sicherung. Das darf nicht unbemerkt bleiben.
///
/// Ohne Abhaengigkeiten, damit sich die Suche pruefen laesst.
/// </summary>
public static class ProfileLinks
{
    /// <summary>Eine Verknuepfung im Profil und wohin sie zeigt.</summary>
    public sealed record Link(string Path, string Target);

    /// <summary>Hoechstens so viele Ordner werden angesehen - ein riesiges Profil soll die Sicherung nicht aufhalten.</summary>
    public const int MaxFolders = 200_000;

    /// <summary>
    /// Alle Verknuepfungen im Profil, die NICHT ins Profil selbst zeigen.
    /// AppData und die ausgeschlossenen Ordner werden nicht durchsucht, in
    /// Verknuepfungen wird nicht hineingegangen.
    /// </summary>
    /// <param name="userDir">Das zu sichernde Profil.</param>
    /// <param name="skip">Ordner, die die Sicherung ohnehin auslaesst (vollstaendige Pfade).</param>
    /// <param name="truncated">true, wenn die Suche bei <see cref="MaxFolders"/> abgebrochen wurde.</param>
    public static List<Link> FindOutsideLinks(string userDir, IEnumerable<string> skip, out bool truncated)
    {
        truncated = false;
        var gefunden = new List<Link>();
        if (string.IsNullOrWhiteSpace(userDir) || !Directory.Exists(userDir)) return gefunden;

        var auslassen = new HashSet<string>(skip.Select(Normal), StringComparer.OrdinalIgnoreCase)
        {
            Normal(Path.Combine(userDir, "AppData"))
        };
        var profil = Normal(userDir);
        var offen = new Stack<string>();
        offen.Push(userDir);
        int gesehen = 0;

        while (offen.Count > 0)
        {
            var ordner = offen.Pop();
            IEnumerable<string> unter;
            try { unter = Directory.EnumerateDirectories(ordner); }
            catch { continue; }   // nicht lesbar - meldet robocopy selbst

            foreach (var u in unter)
            {
                if (++gesehen > MaxFolders) { truncated = true; return gefunden; }
                if (auslassen.Contains(Normal(u))) continue;

                FileAttributes attr;
                try { attr = File.GetAttributes(u); }
                catch { continue; }

                if ((attr & FileAttributes.ReparsePoint) == 0)
                {
                    offen.Push(u);
                    continue;
                }

                string? ziel = null;
                try
                {
                    var t = new DirectoryInfo(u).LinkTarget;
                    if (!string.IsNullOrEmpty(t))
                        ziel = Path.IsPathRooted(t) ? t : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(u) ?? "", t));
                }
                catch { /* Ziel nicht lesbar */ }

                // Ohne Ziel ist es keine Verknuepfung, sondern ein anderer
                // Analysepunkt (etwa ein Cloud-Platzhalter) - nicht melden.
                if (ziel is null) continue;
                // Zeigt sie ins Profil, ist ihr Inhalt ohnehin in der Sicherung.
                if (IsInside(Normal(ziel), profil)) continue;
                gefunden.Add(new Link(u, ziel));
            }
        }
        return gefunden;
    }

    private static string Normal(string p)
    {
        try { p = Path.GetFullPath(p); } catch { /* so lassen */ }
        return p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsInside(string pfad, string basis)
        => pfad.Equals(basis, StringComparison.OrdinalIgnoreCase)
           || pfad.StartsWith(basis + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
