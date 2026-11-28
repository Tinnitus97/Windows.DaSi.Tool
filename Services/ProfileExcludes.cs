using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WindowsDaSiTool.Services;

/// <summary>
/// Puts together WHAT is left out when a user profile is backed up.
///
/// Deliberately separate from <see cref="BackupService"/> and free of any
/// dependency: this way the decision can be tested without copying
/// anything - the same split as between catalog and execution.
///
/// The backup and the size estimate use the same list. If they drifted
/// apart, the traffic light would show a number that has nothing to do with
/// the later result.
/// </summary>
public static class ProfileExcludes
{
    /// <summary>
    /// Leftovers from Windows XP. They are junctions pointing to other places in
    /// the profile - without excluding them, robocopy runs in circles. They are
    /// ALWAYS left out, no matter what is checked.
    /// </summary>
    private static readonly string[] AlwaysExcluded =
    {
        "Anwendungsdaten", "Application Data", "Cookies", "Links", "Favorites",
        "Local Settings", "My Documents", "NetHood", "PrintHood", "Recent", "Templates",
        "Start Menu", "Druckumgebung", "Netzwerkumgebung", "SendTo", "Vorlagen",
        "Lokale Einstellungen", "Eigene Dateien"
    };

    /// <summary>
    /// Caches inside AppData. They are dropped even when AppData is explicitly
    /// included: these files are recreated at any time, but together they weigh
    /// several gigabytes. Taking them along only costs time and space.
    /// </summary>
    private static readonly string[] AppDataCaches =
    {
        @"AppData\Local\Temp",
        @"AppData\Local\Microsoft\Windows\INetCache",
        @"AppData\Local\Microsoft\Windows\Explorer",
        @"AppData\Local\CrashDumps",
        @"AppData\Local\Packages\Microsoft.Windows.Search_cw5n1h2txyewy"
    };

    /// <summary>The data folders of the Chromium browsers in AppData.</summary>
    private static readonly string[] ChromiumUserData =
    {
        @"AppData\Local\Google\Chrome\User Data",
        @"AppData\Local\BraveSoftware\Brave-Browser\User Data",
        @"AppData\Local\Microsoft\Edge\User Data",
    };

    /// <summary>
    /// Graphics card caches directly under "User Data". They only fit the old
    /// computer's graphics card and driver anyway, and while the browser runs
    /// in the background they are locked.
    /// </summary>
    private static readonly string[] ChromiumGemeinsameCaches = { "GrShaderCache", "ShaderCache", "GraphiteDawnCache" };

    /// <summary>Caches in the default profile ("Default") of a Chromium browser.</summary>
    private static readonly string[] ChromiumProfilCaches = { "Cache", "Code Cache", "GPUCache", "DawnWebGPUCache", "DawnGraphiteCache" };

    /// <summary>
    /// State of Windows components that only fits this one computer and this
    /// one version. On another computer it does harm instead of helping:
    ///
    /// - App Installer (winget): its databases. If they come from a newer winget
    ///   version than the one on the target computer, EVERY install fails
    ///   afterwards with 0x8A150009 ("Cannot write to index; it is a higher
    ///   schema version") - as happened after a restore with AppData onto a
    ///   freshly set up computer.
    /// - Windows shell (search, widgets, Start menu): its app index and its
    ///   embedded browser view. Windows recreates both itself, and they are
    ///   locked while running anyway.
    /// - "WindowsApps": the app aliases (winget.exe, python.exe ...) Windows
    ///   creates for installed apps. They are reparse points, not files - none
    ///   can be read (error 1920), and on the new computer they belong to the
    ///   apps installed there.
    ///
    /// They stay out in both directions: on backup and - also for older backups
    /// that still contain them - on restore.
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
    /// Everything inside AppData that is never taken along - relative to the
    /// profile. Public for the tests.
    /// </summary>
    public static IEnumerable<string> AppDataExcludes()
    {
        foreach (var c in AppDataCaches) yield return c;
        foreach (var browser in ChromiumUserData)
        {
            // Deliberately "\\" instead of Path.Combine: these are Windows paths, like
            // the entries above - also in the Linux test environment.
            foreach (var c in ChromiumGemeinsameCaches) yield return browser + "\\" + c;
            foreach (var c in ChromiumProfilCaches) yield return browser + "\\Default\\" + c;
        }
        foreach (var m in MaschinenZustand) yield return m;
    }

    /// <summary>
    /// The profile's registry files. They are locked for copying, belong to
    /// exactly this computer and must NEVER be written into another profile -
    /// that would overwrite the target user's settings.
    /// </summary>
    public static readonly string[] RegistryHiveFiles = { "NTUSER.DAT*", "ntuser.ini", "ntuser.pol", "UsrClass.dat*" };

    /// <summary>
    /// robocopy switches for BACKING UP from the profile (without source,
    /// target, /XD).
    ///
    /// This used to include /XA:SH as well. That skipped EVERY hidden or system
    /// file - without a word in the log. Affected were not only the registry
    /// files it was meant for, but user data as well: hidden files and the
    /// contents of folders Windows marks as system folders (customized folders
    /// with their own icon or view). Now only the registry files are excluded,
    /// explicitly.
    ///
    /// /XJ stays: junctions like "My Pictures" in Documents point to other
    /// places in the profile; robocopy would run in circles otherwise.
    /// </summary>
    public static List<string> BackupSwitches()
    {
        var s = new List<string> { "/MIR", "/ZB", "/SL", "/R:0", "/W:0", "/MT:32", "/XJ", "/XF" };
        s.AddRange(RegistryHiveFiles);
        return s;
    }

    /// <summary>
    /// robocopy switches for RESTORING into the profile.
    ///
    /// /COPY:DAT instead of /COPYALL: content, attributes and timestamps are
    /// copied - the permissions are NOT. New files thus inherit the target
    /// profile's permissions and belong to the user whose profile they land in.
    /// /COPYALL carried over the permissions of the backup medium (the backup
    /// itself stores none) - on another computer or for another user, those are
    /// foreign permissions.
    /// </summary>
    public static List<string> RestoreSwitches()
    {
        var s = new List<string> { "/E", "/ZB", "/COPY:DAT", "/DCOPY:DAT", "/R:1", "/W:1", "/MT:32", "/XJ", "/XF" };
        s.AddRange(RegistryHiveFiles);
        return s;
    }

    /// <summary>
    /// Builds the list of excluded folders as full paths.
    /// </summary>
    /// <param name="userDir">The user profile to back up.</param>
    /// <param name="includeAppData">
    /// true: AppData is backed up as well (the caches above still stay out).
    /// false: AppData is left out completely.
    /// </param>
    /// <param name="includeCloud">
    /// true: the detected cloud folders are backed up as well. false: they stay
    /// out - otherwise the backup would drag along data that lives at the
    /// provider anyway.
    /// </param>
    public static List<string> Build(string userDir, bool includeAppData = false, bool includeCloud = false)
    {
        var excluded = AlwaysExcluded.Select(d => Path.Combine(userDir, d)).ToList();

        if (!includeAppData)
        {
            excluded.Add(Path.Combine(userDir, "AppData"));
        }

        // The caches always - with AppData excluded they are covered already, and
        // naming them twice does not bother robocopy.
        excluded.AddRange(AppDataExcludes().Select(d => Path.Combine(userDir, d)));

        if (!includeCloud)
        {
            excluded.AddRange(CloudProviders.Detect(userDir).Select(c => c.Path));
        }

        // Without duplicates: passing the same entry to robocopy twice is harmless,
        // but makes the command line needlessly long.
        return excluded.Distinct(System.StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// If the tool itself runs from inside the target profile - typically
    /// "Desktop\WindowsDaSiTool.exe" -, the file at the same place in the
    /// backup. It is skipped on restore: the running executable cannot be
    /// overwritten anyway (error 32), and the backup usually holds an older
    /// version. Otherwise null.
    /// </summary>
    /// <param name="zielProfil">The profile being restored into.</param>
    /// <param name="sicherungsProfil">The backup's "Benutzerprofil" folder.</param>
    /// <param name="programmPfad">Path of the running program (Environment.ProcessPath).</param>
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
    /// true if <paramref name="path"/> is the same folder as
    /// <paramref name="baseDir"/> or lies below it.
    ///
    /// Deliberately not just StartsWith: "C:\Users\Maximilian" starts with
    /// "C:\Users\Max" but is not inside it. So the comparison is against the
    /// folder WITH a trailing separator.
    /// </summary>
    public static bool IsInside(string path, string baseDir)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(baseDir)) return false;

        static string Normal(string p)
        {
            try { p = Path.GetFullPath(p); } catch { /* leave it as it is */ }
            return p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        var pfad = Normal(path);
        var basis = Normal(baseDir);
        return string.Equals(pfad, basis, System.StringComparison.OrdinalIgnoreCase)
               || pfad.StartsWith(basis + Path.DirectorySeparatorChar, System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// true if both mean the same folder - regardless of casing and a trailing
    /// separator.
    /// </summary>
    public static bool SameFolder(string a, string b) => IsInside(a, b) && IsInside(b, a);
}
