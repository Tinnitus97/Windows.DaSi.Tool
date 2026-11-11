using System;
using System.Text.RegularExpressions;

namespace WindowsDaSiTool.Services;

/// <summary>
/// Uebertraegt Pfade aus dem gesicherten Profil in das Profil, in das
/// zurueckgeschrieben wird.
///
/// Gesichert wurde etwa "C:\Users\Besitzer\Pictures\Urlaub.jpg", zurueck
/// geht es zum Benutzer "Anwender". Wer den gespeicherten Pfad unveraendert
/// benutzt, legt auf dem neuen Rechner einen Ordner "C:\Users\Besitzer" an -
/// ein Profil, das es dort nicht gibt.
///
/// Ohne Abhaengigkeiten, damit sich die Regel pruefen laesst.
/// </summary>
public static class ProfilePaths
{
    // "C:\Users\<Name>\<Rest>" - fuer Sicherungen, die das gesicherte Profil
    // noch nicht mit abgelegt haben.
    private static readonly Regex UnterUsers =
        new(@"^(?:[A-Za-z]:)?\\Users\\[^\\]+\\(.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Der Pfad im Zielprofil, der <paramref name="original"/> im gesicherten
    /// Profil entspricht - oder null, wenn der Pfad in keinem Benutzerprofil lag
    /// (etwa ein Windows-Hintergrund unter C:\Windows\Web).
    /// </summary>
    /// <param name="original">Der gesicherte Pfad, z. B. "C:\Users\Besitzer\Pictures\a.jpg".</param>
    /// <param name="originalProfile">Das gesicherte Profil, falls bekannt ("C:\Users\Besitzer").</param>
    /// <param name="targetProfile">Das Profil, in das zurueckgeschrieben wird ("C:\Users\Anwender").</param>
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

        // Im Trennzeichen des Zielprofils zusammensetzen - unter Windows "\".
        char trenner = targetProfile.Contains('\\') || !targetProfile.Contains('/') ? '\\' : '/';
        return targetProfile.TrimEnd('\\', '/') + trenner + rest.Replace('\\', trenner);
    }

    private static string Win(string p) => p.Replace('/', '\\');
}
