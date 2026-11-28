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
    /// Returns the encoding Windows console programs (robocopy, PrintBrm) write
    /// their output in. That is the system's OEM code page - 850 on German
    /// Windows, but it can differ by region (e.g. 437, 852, 866). Determined at
    /// runtime, with 850 as the fallback.
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
    /// Folder that holds the EXE. A custom update address (Update-Url.txt)
    /// belongs there, and that is where the EXE is replaced during a
    /// self-update.
    ///
    /// Works for a single-file EXE as well: there BaseDirectory points to the
    /// extraction folder under %TEMP%, whereas ProcessPath points to the EXE
    /// itself.
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
        catch { /* fallback below */ }

        try
        {
            var baseDir = AppContext.BaseDirectory?.TrimEnd(Path.DirectorySeparatorChar);
            if (!string.IsNullOrEmpty(baseDir) && Directory.Exists(baseDir)) return baseDir;
        }
        catch { /* fallback below */ }

        return Directory.GetCurrentDirectory();
    }

    /// <summary>
    /// Reads from the registry whether Windows runs in light mode.
    /// true = light mode, false = dark mode (fallback when unclear: dark).
    /// </summary>
    public static bool IsWindowsLightTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            // AppsUseLightTheme: 1 = light, 0 = dark
            if (key?.GetValue("AppsUseLightTheme") is int v) return v != 0;
        }
        catch { /* registry not readable */ }
        return false; // Fallback: dark
    }

    /// <summary>
    /// Determines the Windows display language. Returns "de" or "en"; "de" for
    /// anything else or on errors.
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
        return "de"; // Fallback: German
    }

    /// <summary>
    /// Lists the real user profiles under C:\Users (without system and service
    /// profiles). Returns full paths, sorted alphabetically.
    /// </summary>
    public static List<string> GetUserProfiles()
    {
        var result = new List<string>();
        try
        {
            // Determine the profiles' base folder (usually C:\Users)
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
                // Only real profiles: they must contain an NTUSER.DAT
                try { if (!File.Exists(Path.Combine(dir, "NTUSER.DAT"))) continue; } catch { continue; }
                result.Add(dir);
            }
        }
        catch { /* access denied or the like */ }
        return result.OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase).ToList();
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;        // 0 = battery, 1 = mains, 255 = unknown
        public byte BatteryFlag;         // 128 = no system battery (desktop)
        public byte BatteryLifePercent;  // 0-100, 255 = unknown
        public byte SystemStatusFlag;
        public int  BatteryLifeTime;
        public int  BatteryFullLifeTime;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

    /// <summary>
    /// Checks whether the device is running on battery right now (notebook
    /// without a charger connected). Returns:
    ///   true  = running on battery, the charger should be connected
    ///   false = on mains power OR desktop PC without battery OR status unknown
    /// The current charge level (0-100, or -1 if unknown) comes back through
    /// the out parameter.
    /// </summary>
    public static bool IsOnBattery(out int batteryPercent)
    {
        batteryPercent = -1;
        try
        {
            if (!GetSystemPowerStatus(out var status))
                return false;

            // BatteryFlag 128 = no system battery (desktop) -> no warning.
            if (status.BatteryFlag == 128)
                return false;

            if (status.BatteryLifePercent <= 100)
                batteryPercent = status.BatteryLifePercent;

            // ACLineStatus 0 = no power supply -> running on battery.
            return status.ACLineStatus == 0;
        }
        catch
        {
            return false; // when in doubt, do not warn
        }
    }

    /// <summary>
    /// Returns the free space (in bytes) on the drive of the given path. -1 on
    /// error (e.g. network path not reachable).
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
    /// Calculates the size of a folder (recursively, in bytes). Folders in the
    /// exclude list (fully qualified paths) are skipped - so the estimate
    /// matches what is actually backed up. Inaccessible subfolders are skipped
    /// silently. Honors cancellation through the CancellationToken.
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

            // Sum up the files of this folder.
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    ct.ThrowIfCancellationRequested();
                    try { total += new FileInfo(f).Length; } catch { /* file not readable */ }
                }
            }
            catch { /* folder not readable */ }

            // Queue the subfolders.
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    // Skip symlinks/reparse points (prevents endless loops).
                    try
                    {
                        var attr = File.GetAttributes(sub);
                        if ((attr & FileAttributes.ReparsePoint) != 0) continue;
                    }
                    catch { continue; }
                    stack.Push(sub);
                }
            }
            catch { /* folder not readable */ }
        }
        return total;
    }

    /// <summary>Formats a byte count for humans (e.g. "12,3 GB").</summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "?";
        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        double val = bytes;
        int i = 0;
        while (val >= 1024 && i < units.Length - 1) { val /= 1024; i++; }
        return i == 0 ? $"{val:0} {units[i]}" : $"{val:0.0} {units[i]}";
    }

    /// <summary>
    /// Opens an address or a file with the associated program (browser, image
    /// viewer). false if that did not work - the caller keeps running.
    /// </summary>
    public static bool Open(string ziel)
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ziel) { UseShellExecute = true });
            return true;
        }
        catch { return false; }
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
