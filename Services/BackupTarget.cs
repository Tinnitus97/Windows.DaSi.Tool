using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace WindowsDaSiTool.Services;

/// <summary>
/// The tool's own backup folder: whoever picks just "G:\" as the target gets
/// one folder per computer and day below it - "G:\DASI_DESKTOP-7K4Q2M_10_11_2026".
///
/// That way several computers and several snapshots sit cleanly side by side
/// instead of overwriting each other in the root: the profile is mirrored
/// with /MIR, so a second backup into the same folder would delete
/// everything the first computer left there.
///
/// If the chosen target already is a backup, it is updated - no further
/// folder is created. The same goes for a second backup on the same day:
/// the folder name is identical.
///
/// No dependencies on the UI, so the rule can be tested.
/// </summary>
public static class BackupTarget
{
    /// <summary>Prefix of every folder the tool creates itself.</summary>
    public const string Prefix = "DASI_";

    /// <summary>
    /// "DASI_&lt;computer name&gt;_DD_MM_YYYY". Characters that are not allowed
    /// in a folder name become "_".
    /// </summary>
    public static string FolderName(string machineName, DateTime when)
        => Prefix + SafeMachineName(machineName) + "_" + when.ToString("dd_MM_yyyy", CultureInfo.InvariantCulture);

    /// <summary>The computer name as it appears in the folder name.</summary>
    public static string SafeMachineName(string machineName)
    {
        var pc = string.IsNullOrWhiteSpace(machineName) ? "PC" : machineName.Trim();
        foreach (var ch in Path.GetInvalidFileNameChars()) pc = pc.Replace(ch, '_');
        // Also the characters Windows forbids - on Linux, Path only reports '/'
        // and '\0', but the rule should behave the same everywhere.
        foreach (var ch in "\\/:*?\"<>|") pc = pc.Replace(ch, '_');
        return pc;
    }

    /// <summary>
    /// The computer a DASI folder comes from: "DASI_ALTPC_01_10_2026" -> "ALTPC".
    /// Also works for folders of older versions with a time ("..._2026_18_45").
    /// null if the name does not follow this pattern.
    /// </summary>
    public static string? MachineOf(string path)
    {
        var m = Regex.Match(LastSegment(path), @"^DASI_(.+?)_\d{2}_\d{2}_\d{4}(_\d{2}_\d{2})?$", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>true if the folder itself already is a DASI folder.</summary>
    public static bool IsOwnFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        return LastSegment(path).StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Is the folder already a backup - by its name (DASI_...) or by its content
    /// (user profile, browser profiles, program list ... or a log)?
    /// </summary>
    public static bool IsBackupFolder(string path)
        => IsOwnFolder(path) || AppCatalog.IsBackup(path);

    /// <summary>
    /// The actual target of a backup.
    ///
    /// If the selection already is a backup, the new backup goes right into it
    /// and updates it - no new folder inside it and none next to it. Otherwise
    /// "DASI_&lt;computer&gt;_&lt;date&gt;" is created below it; if today's one
    /// already exists, it is updated the same way.
    /// </summary>
    /// <param name="basePath">What the user picked, e.g. "G:\".</param>
    /// <param name="enabled">The "own folder" switch.</param>
    public static string Resolve(string basePath, bool enabled, string machineName, DateTime when)
    {
        if (!enabled || string.IsNullOrWhiteSpace(basePath)) return basePath;
        if (IsBackupFolder(basePath)) return basePath;
        return Path.Combine(basePath, FolderName(machineName, when));
    }

    private static string LastSegment(string path)
    {
        var name = (path ?? "").TrimEnd('\\', '/');
        var schnitt = name.LastIndexOfAny(new[] { '\\', '/' });
        return schnitt >= 0 ? name[(schnitt + 1)..] : name;
    }
}
