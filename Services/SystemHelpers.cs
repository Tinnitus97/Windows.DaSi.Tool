using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace WindowsDaSiTool.Services;

public static class SystemHelpers
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    private static System.Text.Encoding? _consoleEncoding;

    /// <summary>
    /// Liefert die Kodierung, in der Windows-Konsolenprogramme (robocopy,
    /// PrintBrm) ihre Ausgabe schreiben. Das ist die OEM-Codepage des Systems -
    /// auf deutschem Windows 850, kann aber je nach Region abweichen (z.B. 437,
    /// 852, 866). Wird dynamisch ermittelt, mit 850 als Rueckfallwert.
    /// </summary>
    public static System.Text.Encoding ConsoleEncoding
    {
        get
        {
            if (_consoleEncoding != null) return _consoleEncoding;
            try
            {
                var cp = (int)GetOEMCP();
                _consoleEncoding = System.Text.Encoding.GetEncoding(cp);
            }
            catch
            {
                try { _consoleEncoding = System.Text.Encoding.GetEncoding(850); }
                catch { _consoleEncoding = System.Text.Encoding.UTF8; }
            }
            return _consoleEncoding;
        }
    }

    /// <summary>
    /// Verzeichnis, in dem die EXE liegt. Dorthin gehoert eine abweichende
    /// Update-Adresse (Update-Url.txt), und dort wird die EXE beim
    /// Selbstaustausch ersetzt.
    ///
    /// Funktioniert auch bei einer SingleFile-EXE: Dort zeigt BaseDirectory in
    /// das Entpackverzeichnis unter %TEMP%, ProcessPath dagegen auf die EXE
    /// selbst.
    /// </summary>
    public static string GetApplicationDirectory()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var dir = Path.GetDirectoryName(exe);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return dir;
            }
        }
        catch { /* Rueckfall unten */ }

        try
        {
            var baseDir = AppContext.BaseDirectory?.TrimEnd(Path.DirectorySeparatorChar);
            if (!string.IsNullOrEmpty(baseDir) && Directory.Exists(baseDir)) return baseDir;
        }
        catch { /* Rueckfall unten */ }

        return Directory.GetCurrentDirectory();
    }

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

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;        // 0 = Akku, 1 = Netz, 255 = unbekannt
        public byte BatteryFlag;         // 128 = kein Systemakku (Desktop)
        public byte BatteryLifePercent;  // 0-100, 255 = unbekannt
        public byte SystemStatusFlag;
        public int  BatteryLifeTime;
        public int  BatteryFullLifeTime;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

    /// <summary>
    /// Prueft, ob das Geraet gerade im Akkubetrieb laeuft (Notebook ohne
    /// angeschlossenes Ladegeraet). Rueckgabe:
    ///   true  = laeuft auf Akku, Netzteil sollte angeschlossen werden
    ///   false = am Netz ODER Desktop-PC ohne Akku ODER Status unbekannt
    /// Der aktuelle Ladestand (0-100, oder -1 wenn unbekannt) kommt ueber
    /// den out-Parameter zurueck.
    /// </summary>
    public static bool IsOnBattery(out int batteryPercent)
    {
        batteryPercent = -1;
        try
        {
            if (!GetSystemPowerStatus(out var status))
                return false;

            // BatteryFlag 128 = kein Systemakku vorhanden (Desktop) -> nicht warnen.
            if (status.BatteryFlag == 128)
                return false;

            if (status.BatteryLifePercent <= 100)
                batteryPercent = status.BatteryLifePercent;

            // ACLineStatus 0 = kein Netzteil -> Akkubetrieb.
            return status.ACLineStatus == 0;
        }
        catch
        {
            return false; // im Zweifel nicht warnen
        }
    }

    /// <summary>
    /// Liefert den freien Speicherplatz (in Bytes) auf dem Laufwerk des
    /// angegebenen Pfads. -1 bei Fehler (z.B. Netzwerkpfad nicht erreichbar).
    /// </summary>
    public static long GetFreeSpace(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return -1;
            var drive = new DriveInfo(root);
            return drive.IsReady ? drive.AvailableFreeSpace : -1;
        }
        catch { return -1; }
    }

    /// <summary>
    /// Berechnet die Groesse eines Ordners (rekursiv, in Bytes). Ordner in der
    /// Ausschlussliste (voll qualifizierte Pfade) werden uebersprungen - so
    /// entspricht die Schaetzung dem, was tatsaechlich gesichert wird. Nicht
    /// zugaengliche Unterordner werden stillschweigend ausgelassen. Reagiert
    /// auf Abbruch ueber das CancellationToken.
    /// </summary>
    public static long GetDirectorySize(string path, IEnumerable<string>? excludeDirs = null,
                                        System.Threading.CancellationToken ct = default)
    {
        long total = 0;
        if (!Directory.Exists(path)) return 0;

        var excludeSet = new HashSet<string>(
            (excludeDirs ?? Enumerable.Empty<string>())
                .Select(d => { try { return Path.GetFullPath(d).TrimEnd('\\').ToLowerInvariant(); } catch { return d.ToLowerInvariant(); } }),
            StringComparer.OrdinalIgnoreCase);

        var stack = new Stack<string>();
        stack.Push(path);

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();

            var norm = dir.TrimEnd('\\').ToLowerInvariant();
            if (excludeSet.Contains(norm)) continue;

            // Dateien dieses Ordners aufsummieren.
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    ct.ThrowIfCancellationRequested();
                    try { total += new FileInfo(f).Length; } catch { /* Datei nicht lesbar */ }
                }
            }
            catch { /* Ordner nicht lesbar */ }

            // Unterordner einreihen.
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    // Symlinks/Reparse-Punkte auslassen (verhindert Endlosschleifen).
                    try
                    {
                        var attr = File.GetAttributes(sub);
                        if ((attr & FileAttributes.ReparsePoint) != 0) continue;
                    }
                    catch { continue; }
                    stack.Push(sub);
                }
            }
            catch { /* Ordner nicht lesbar */ }
        }
        return total;
    }

    /// <summary>Formatiert eine Byte-Zahl menschenlesbar (z.B. "12,3 GB").</summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "?";
        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        double val = bytes;
        int i = 0;
        while (val >= 1024 && i < units.Length - 1) { val /= 1024; i++; }
        return i == 0 ? $"{val:0} {units[i]}" : $"{val:0.0} {units[i]}";
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

}
