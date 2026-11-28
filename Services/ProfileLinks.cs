using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WindowsDaSiTool.Services;

/// <summary>
/// Finds folders in the profile that are actually links (junctions,
/// symbolic links) and point OUT of the profile.
///
/// The profile backup deliberately does not follow links (/XJ): the old
/// references like "My Pictures" in Documents point back into the profile,
/// and robocopy would run in circles or see the same files twice. But if a
/// link points outside - "Dokumente\Spiele" to "D:\Spiele" -, its content
/// is missing from the backup. That must not go unnoticed.
///
/// No dependencies, so the search can be tested.
/// </summary>
public static class ProfileLinks
{
    /// <summary>A link in the profile and where it points.</summary>
    public sealed record Link(string Path, string Target);

    /// <summary>At most this many folders are inspected - a huge profile should not hold up the backup.</summary>
    public const int MaxFolders = 200_000;

    /// <summary>
    /// All links in the profile that do NOT point into the profile itself.
    /// AppData and the excluded folders are not searched, and links are not
    /// entered.
    /// </summary>
    /// <param name="userDir">The profile to back up.</param>
    /// <param name="skip">Folders the backup leaves out anyway (full paths).</param>
    /// <param name="truncated">true if the search stopped at <see cref="MaxFolders"/>.</param>
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
            catch { continue; }   // not readable - robocopy reports that itself

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
                catch { /* target not readable */ }

                // Without a target it is not a link but another kind of reparse point
                // (e.g. a cloud placeholder) - do not report it.
                if (ziel is null) continue;
                // If it points into the profile, its content is in the backup anyway.
                if (IsInside(Normal(ziel), profil)) continue;
                gefunden.Add(new Link(u, ziel));
            }
        }
        return gefunden;
    }

    private static string Normal(string p)
    {
        try { p = Path.GetFullPath(p); } catch { /* leave it */ }
        return p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsInside(string pfad, string basis)
        => pfad.Equals(basis, StringComparison.OrdinalIgnoreCase)
           || pfad.StartsWith(basis + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
