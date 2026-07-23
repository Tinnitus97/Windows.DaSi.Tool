using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace WindowsDaSiTool.Services;

public static class SystemHelpers
{
    /// <summary>
    /// Liest aus der Registry, ob Windows im hellen Modus laeuft.
    /// true = heller Modus, false = dunkler Modus (Fallback bei Unklarheit: dunkel).
    /// </summary>
    public static bool IsWindowsLightTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            // AppsUseLightTheme: 1 = hell, 0 = dunkel
            if (key?.GetValue("AppsUseLightTheme") is int v) return v != 0;
        }
        catch { /* Registry nicht lesbar */ }
        return false; // Fallback: dunkel
    }

    /// <summary>
    /// Ermittelt die Windows-Anzeigesprache. Gibt "de" oder "en" zurueck;
    /// bei allem anderen oder Fehlern Fallback "de".
    /// </summary>
    public static string GetWindowsLanguage()
    {
        try
        {
            var culture = System.Globalization.CultureInfo.CurrentUICulture;
            var two = culture.TwoLetterISOLanguageName?.ToLowerInvariant();
            if (two == "en") return "en";
            if (two == "de") return "de";
        }
        catch { /* ignore */ }
        return "de"; // Fallback: Deutsch
    }

    /// <summary>
    /// Listet die echten Benutzerprofile unter C:\Users auf (ohne System-
    /// und Dienstprofile). Rueckgabe sind vollstaendige Pfade, alphabetisch.
    /// </summary>
    public static List<string> GetUserProfiles()
    {
        var result = new List<string>();
        try
        {
            // Basisverzeichnis der Profile ermitteln (i.d.R. C:\Users)
            var current = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var usersRoot = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(usersRoot) || !Directory.Exists(usersRoot))
                usersRoot = Path.Combine(Path.GetPathRoot(current) ?? "C:\\", "Users");
            if (!Directory.Exists(usersRoot)) return result;

            var systemProfiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Default", "Default User", "Public", "All Users", "defaultuser0",
                "WDAGUtilityAccount", "systemprofile", "LocalService", "NetworkService"
            };

            foreach (var dir in Directory.GetDirectories(usersRoot))
            {
                var name = Path.GetFileName(dir);
                if (systemProfiles.Contains(name)) continue;
                if (name.StartsWith(".")) continue;
                // Nur echte Profile: muessen ein NTUSER.DAT enthalten
                try { if (!File.Exists(Path.Combine(dir, "NTUSER.DAT"))) continue; } catch { continue; }
                result.Add(dir);
            }
        }
        catch { /* Zugriff verweigert o.ae. */ }
        return result.OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static bool IsNtfsDrive(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root)) return false;
            if (root.StartsWith("\\")) return true; // UNC
            var di = new DriveInfo(root);
            return string.Equals(di.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// Prueft die Online-Version gegen die aktuelle. Gibt die neue Version
    /// zurueck, wenn ein Update verfuegbar ist, sonst null.
    /// </summary>
    public static async Task<string?> CheckForUpdate(string updateUrl, string currentVersion)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            var raw = (await http.GetStringAsync(updateUrl)).Trim();
            if (Version.TryParse(raw, out var online) && Version.TryParse(currentVersion, out var current)
                && online > current)
            {
                return raw;
            }
        }
        catch { /* offline / nicht erreichbar */ }
        return null;
    }
}
