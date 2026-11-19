using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WindowsDaSiTool.Services;

/// <summary>
/// Prueft nach dem Zurueckschreiben eines Browser- oder Mailprofils, ob die
/// Dateien, an denen das Profil haengt, wirklich so im Ziel stehen wie in der
/// Sicherung.
///
/// Anlass: Nach einer Chrome-Wiederherstellung war Chrome leer - obwohl
/// robocopy fertig war. Startet der Browser waehrend oder kurz nach dem
/// Kopieren (manche Installer starten ihn nach der Installation von selbst),
/// legt er ein frisches Profil an und schreibt beim Beenden "Local State" und
/// "Preferences" ueber die zurueckgeschriebenen. Von Hand kopiert - bei
/// geschlossenem Browser - war alles da.
///
/// Ohne Abhaengigkeiten, damit es sich pruefen laesst.
/// </summary>
public static class ProfilAbgleich
{
    /// <summary>Dateien direkt im Datenordner: Chromium ("Local State"), Firefox/Thunderbird ("profiles.ini").</summary>
    private static readonly string[] Oben = { "Local State", "profiles.ini", "installs.ini" };

    /// <summary>Dateien je Profil: Chromium (Preferences, Bookmarks ...), Firefox/Thunderbird (prefs.js, places.sqlite ...).</summary>
    private static readonly string[] JeProfil =
    {
        "Preferences", "Bookmarks", "History", "Login Data", "Web Data",
        "prefs.js", "places.sqlite", "logins.json", "key4.db",
    };

    /// <summary>
    /// Die Profilordner in einem Datenordner, relativ: bei Chromium "Default",
    /// "Profile 1" ...; bei Firefox und Thunderbird "Profiles\xxxx.default-release".
    /// Erkannt an ihrer Einstellungsdatei, nach Namen sortiert.
    /// </summary>
    public static List<string> Profile(string ordner)
    {
        var erg = new List<string>();
        try
        {
            if (!Directory.Exists(ordner)) return erg;
            var kandidaten = Directory.GetDirectories(ordner).ToList();
            var profiles = Path.Combine(ordner, "Profiles");
            if (Directory.Exists(profiles)) kandidaten.AddRange(Directory.GetDirectories(profiles));
            foreach (var k in kandidaten)
            {
                if (File.Exists(Path.Combine(k, "Preferences")) || File.Exists(Path.Combine(k, "prefs.js")))
                    erg.Add(Path.GetRelativePath(ordner, k));
            }
        }
        catch { /* dann eben keine */ }
        erg.Sort(StringComparer.OrdinalIgnoreCase);
        return erg;
    }

    /// <summary>
    /// Die wichtigen Dateien der Sicherung, die im Ziel fehlen oder eine andere
    /// Groesse haben - relativ zum Datenordner. Leer heisst: Das Profil steht
    /// so im Ziel, wie es gesichert wurde.
    /// </summary>
    public static List<string> Abweichungen(string quelle, string ziel)
    {
        var erg = new List<string>();
        if (!Directory.Exists(quelle)) return erg;

        void Vergleiche(string rel)
        {
            var q = Path.Combine(quelle, rel);
            if (!File.Exists(q)) return;
            var z = Path.Combine(ziel, rel);
            try
            {
                if (!File.Exists(z) || new FileInfo(z).Length != new FileInfo(q).Length) erg.Add(rel);
            }
            catch { erg.Add(rel); }
        }

        foreach (var datei in Oben) Vergleiche(datei);
        foreach (var profil in Profile(quelle))
            foreach (var datei in JeProfil)
                Vergleiche(Path.Combine(profil, datei));
        return erg;
    }
}
