using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WindowsDaSiTool.Services;

/// <summary>
/// After a browser or mail profile is restored, checks whether the files the
/// profile depends on are really in the target exactly as in the backup.
///
/// Reason: after a Chrome restore, Chrome was empty - although robocopy had
/// finished. If the browser starts during or shortly after copying (some
/// installers start it on their own after installing), it creates a fresh
/// profile and, when it exits, writes "Local State" and "Preferences" over
/// the restored ones. Copied by hand - with the browser closed - everything
/// was there.
///
/// No dependencies, so it can be tested.
/// </summary>
public static class ProfileComparison
{
    /// <summary>Files directly in the data folder: Chromium ("Local State"), Firefox/Thunderbird ("profiles.ini").</summary>
    private static readonly string[] Oben = { "Local State", "profiles.ini", "installs.ini" };

    /// <summary>Files per profile: Chromium (Preferences, Bookmarks ...), Firefox/Thunderbird (prefs.js, places.sqlite ...).</summary>
    private static readonly string[] JeProfil =
    {
        "Preferences", "Bookmarks", "History", "Login Data", "Web Data",
        "prefs.js", "places.sqlite", "logins.json", "key4.db",
    };

    /// <summary>
    /// The profile folders in a data folder, relative: for Chromium "Default",
    /// "Profile 1" ...; for Firefox and Thunderbird "Profiles\xxxx.default-release".
    /// Recognized by their settings file, sorted by name.
    /// </summary>
    public static List<string> Profiles(string ordner)
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
        catch { /* then there are none */ }
        erg.Sort(StringComparer.OrdinalIgnoreCase);
        return erg;
    }

    /// <summary>
    /// The important files of the backup that are missing in the target or have
    /// a different size - relative to the data folder. Empty means: the profile
    /// is in the target exactly as it was backed up.
    /// </summary>
    public static List<string> Differences(string quelle, string ziel)
    {
        var erg = new List<string>();
        if (!Directory.Exists(quelle)) return erg;

        void Compare(string rel)
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

        foreach (var datei in Oben) Compare(datei);
        foreach (var profil in Profiles(quelle))
            foreach (var datei in JeProfil)
                Compare(Path.Combine(profil, datei));
        return erg;
    }
}
