using System;
using System.Text.RegularExpressions;

namespace WindowsDaSiTool.Services;

/// <summary>
/// Carries paths from the backed-up profile over to the profile being
/// restored into.
///
/// Backed up was, say, "C:\Users\Besitzer\Pictures\Urlaub.jpg", restored to
/// the user "Anwender". Using the stored path as is creates a folder
/// "C:\Users\Besitzer" on the new computer - a profile that does not exist
/// there.
///
/// No dependencies, so the rule can be tested.
/// </summary>
public static class ProfilePaths
{
    // "C:\Users\<name>\<rest>" - for backups that did not store the backed-up
    // profile yet.
    private static readonly Regex UnterUsers =
        new(@"^(?:[A-Za-z]:)?\\Users\\[^\\]+\\(.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// The path in the target profile that corresponds to
    /// <paramref name="original"/> in the backed-up profile - or null if the
    /// path was not inside any user profile (e.g. a Windows wallpaper under
    /// C:\Windows\Web).
    /// </summary>
    /// <param name="original">The backed-up path, e.g. "C:\Users\Besitzer\Pictures\a.jpg".</param>
    /// <param name="originalProfile">The backed-up profile, if known ("C:\Users\Besitzer").</param>
    /// <param name="targetProfile">The profile being restored into ("C:\Users\Anwender").</param>
    public static string? MapIntoProfile(string? original, string? originalProfile, string targetProfile)
    {
        if (string.IsNullOrWhiteSpace(original) || string.IsNullOrWhiteSpace(targetProfile)) return null;
        var pfad = Win(original.Trim());

        string? rest = null;
        if (!string.IsNullOrWhiteSpace(originalProfile))
        {
            var profil = Win(originalProfile.Trim()).TrimEnd('\\');
            if (pfad.StartsWith(profil + "\\", StringComparison.OrdinalIgnoreCase))
                rest = pfad[(profil.Length + 1)..];
        }
        if (rest is null)
        {
            var m = UnterUsers.Match(pfad);
            if (m.Success) rest = m.Groups[1].Value;
        }
        if (string.IsNullOrEmpty(rest)) return null;

        // Join with the target profile's separator - "\" on Windows.
        char trenner = targetProfile.Contains('\\') || !targetProfile.Contains('/') ? '\\' : '/';
        return targetProfile.TrimEnd('\\', '/') + trenner + rest.Replace('\\', trenner);
    }

    private static string Win(string p) => p.Replace('/', '\\');
}
