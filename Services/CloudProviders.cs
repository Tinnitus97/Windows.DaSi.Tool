using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WindowsDaSiTool.Services;

/// <summary>A cloud provider's folder found in the user profile.</summary>
public sealed record CloudFolder(string Provider, string Path);

/// <summary>
/// Detects the folders of common cloud providers in the user profile.
///
/// Why detect them instead of listing them: the folder names differ by
/// provider, version and language - OneDrive appends the company name
/// ("OneDrive - Muster GmbH"), Nextcloud and ownCloud let the user choose
/// the name during setup. A fixed list never catches them all.
///
/// Detection looks only at folder names DIRECTLY in the profile. It reads
/// nothing from the registry and starts nothing - it may run at any time
/// and without privileges, even while the UI waits for it.
/// </summary>
public static class CloudProviders
{
    /// <summary>
    /// Known providers. Each entry has the display name and the folder patterns.
    ///
    /// A pattern without '*' must match exactly; one ending in '*' matches every
    /// folder that starts that way. That covers the cases where the provider
    /// appends the account or company name.
    ///
    /// Deliberately a short list: what is common is in it - a complete list of
    /// all providers could not be maintained.
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
    /// Finds the cloud folders directly below <paramref name="userDir"/>.
    ///
    /// Each folder is reported at most once, even if several patterns match it
    /// (for OneDrive, "OneDrive" and "OneDrive*" both match). The order follows
    /// <see cref="Known"/>, so the message to the user always looks the same.
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
            // A profile that cannot be read must not hold up the backup.
            return gefunden;
        }

        var schonGemeldet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (provider, muster) in Known)
        {
            foreach (var name in unterordner)
            {
                if (schonGemeldet.Contains(name)) continue;
                // "OneDrive_wiederhergestellt" matches the pattern "OneDrive*", but is a
                // plain copy that is NOT synchronized. Detected as a cloud folder, it would
                // be left out of the next backup by default - and with it possibly the only
                // copy of these files.
                if (IsRestoredCopy(name)) continue;
                if (!muster.Any(m => Matches(name, m))) continue;

                schonGemeldet.Add(name);
                gefunden.Add(new CloudFolder(provider, System.IO.Path.Combine(userDir, name)));
            }
        }

        return gefunden;
    }

    /// <summary>
    /// Matches a folder name against a pattern. Only a trailing '*' is
    /// supported - nothing more is needed here, and a full pattern language
    /// would just be another source of bugs.
    ///
    /// A prefix pattern needs at least one more character: otherwise
    /// "OneDrive*" would report the folder that "OneDrive" already matched a
    /// second time - harmless thanks to the duplicate check, but misleading.
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
    /// Joins the providers found into one message, e.g.
    /// "OneDrive, Dropbox, STRATO HiDrive". Each provider appears once, even if
    /// it has several folders.
    /// </summary>
    public static string Summarize(IEnumerable<CloudFolder> folders)
        => string.Join(", ", folders.Select(f => f.Provider).Distinct());

    // ------------------------------------------------------------ Restore

    /// <summary>
    /// Suffix for restored cloud folders. Deliberately always German, like all
    /// folder names the tool creates ("Benutzerprofil", "Hintergrundbild"): the
    /// UI language should not decide what a folder on disk is called.
    /// </summary>
    public const string RestoredSuffix = "_wiederhergestellt";

    /// <summary>
    /// Name a cloud folder is restored under:
    /// "OneDrive" -> "OneDrive_wiederhergestellt".
    ///
    /// Why not to its original place: the cloud client works there. It would
    /// treat every restored file as changed and upload it again - shared
    /// folders would get duplicates and conflicts for everyone using them.
    /// Placed next to it, the user can compare at leisure and take over by hand
    /// whatever is missing.
    /// </summary>
    public static string RestoredFolderName(string folderName) => folderName + RestoredSuffix;

    /// <summary>true for a folder the tool created while restoring.</summary>
    public static bool IsRestoredCopy(string folderName)
        => folderName.EndsWith(RestoredSuffix, StringComparison.OrdinalIgnoreCase);
}
