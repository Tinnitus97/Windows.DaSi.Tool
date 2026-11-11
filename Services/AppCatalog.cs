using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WindowsDaSiTool.Services;

/// <summary>Die Punkte der Oberflaeche - beim Sichern wie beim Wiederherstellen.</summary>
public enum Punkt
{
    Profil, AppData, Cloud,
    Firefox, Edge, Chrome, Brave, Opera, Thunderbird,
    Winget, Wlan, Drucker, Hintergrund, DesktopShot
}

/// <summary>Ein Programm, dessen Profil gesichert wird.</summary>
/// <param name="ProfilRel">Ort des Profils im Benutzerprofil, z. B. "AppData\Roaming\Mozilla\Firefox".</param>
/// <param name="Prozess">Prozessname ohne ".exe" - wird vor dem Kopieren beendet.</param>
/// <param name="WingetId">Paket fuer die Neuinstallation beim Wiederherstellen, falls bekannt.</param>
/// <param name="Chromium">Chromium-Browser: die Zwischenspeicher bleiben draussen.</param>
public sealed record AppEintrag(Punkt Punkt, string Name, string ProfilRel, string Prozess, string? WingetId, bool Chromium)
{
    /// <summary>Unterordner in der Sicherung, z. B. "Firefox-Profil".</summary>
    public string BackupOrdner => Name + "-Profil";
}

/// <summary>
/// Wo die Programme ihre Profile ablegen, und welche Punkte gerade sinnvoll
/// waehlbar sind.
///
/// Ohne Abhaengigkeiten, damit sich die Regeln pruefen lassen.
/// </summary>
public static class AppCatalog
{
    public static readonly AppEintrag Firefox = new(Punkt.Firefox, "Firefox", @"AppData\Roaming\Mozilla\Firefox", "firefox", "Mozilla.Firefox.de", false);
    public static readonly AppEintrag Edge = new(Punkt.Edge, "Edge", @"AppData\Local\Microsoft\Edge\User Data", "msedge", null, true);
    public static readonly AppEintrag Chrome = new(Punkt.Chrome, "Chrome", @"AppData\Local\Google\Chrome\User Data", "chrome", "Google.Chrome", true);
    public static readonly AppEintrag Brave = new(Punkt.Brave, "Brave", @"AppData\Local\BraveSoftware\Brave-Browser\User Data", "brave", "Brave.Brave", true);
    public static readonly AppEintrag Thunderbird = new(Punkt.Thunderbird, "Thunderbird", @"AppData\Roaming\Thunderbird", "thunderbird", "Mozilla.Thunderbird.de", false);

    /// <summary>Alle Programme mit genau einem Profilordner (Opera hat mehrere, siehe <see cref="Opera"/>).</summary>
    public static readonly IReadOnlyList<AppEintrag> Programme = new[] { Firefox, Edge, Chrome, Brave, Thunderbird };

    /// <summary>
    /// Setzt einen Ort aus dem Katalog an ein Profil an. Die Teile werden
    /// einzeln angehaengt - unter Windows dasselbe wie Path.Combine mit "\",
    /// und auch in der Pruefumgebung ein echter Pfad.
    /// </summary>
    public static string Pfad(string basis, string rel)
        => Path.Combine(new[] { basis }.Concat(rel.Split('\\', StringSplitOptions.RemoveEmptyEntries)).ToArray());

    // ------------------------------------------------------------ Opera

    /// <summary>
    /// Opera gibt es in mehreren Fassungen - Opera, Opera GX, Beta, Developer,
    /// Air ... Jede legt ihr Profil in einen eigenen Ordner unter
    /// "AppData\Roaming\Opera Software" ("Opera Stable", "Opera GX Stable" ...).
    /// Erkannt wird jede Fassung an ihren Profildateien statt an einer festen
    /// Liste - so ist auch eine kuenftige Fassung dabei.
    /// </summary>
    public static class Opera
    {
        public const string Rel = @"AppData\Roaming\Opera Software";
        public const string BackupOrdner = "Opera-Profil";
        public const string Prozess = "opera";

        /// <summary>Daran erkennt man ein Chromium-Profil (je nach Opera-Fassung direkt oder in "Default").</summary>
        private static readonly string[] Merkmale = { "Local State", "Preferences", "Bookmarks" };

        /// <summary>Die Profilordner aller Opera-Fassungen unter <paramref name="wurzel"/>, nach Anzeigename.</summary>
        public static List<string> Varianten(string wurzel)
        {
            var liste = new List<string>();
            try
            {
                if (!Directory.Exists(wurzel)) return liste;
                foreach (var d in Directory.GetDirectories(wurzel))
                {
                    var name = Path.GetFileName(d);
                    if (!name.StartsWith("Opera", StringComparison.OrdinalIgnoreCase)) continue;
                    if (Merkmale.Any(m => File.Exists(Path.Combine(d, m)) || File.Exists(Path.Combine(d, "Default", m))))
                        liste.Add(name);
                }
            }
            catch { /* nicht lesbar - dann eben keine */ }
            // Nach Anzeigename: "Opera, Opera Beta, Opera GX" statt nach Ordnernamen.
            return liste.OrderBy(Anzeigename, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>"Opera GX Stable" -> "Opera GX", "Opera Stable" -> "Opera", "Opera Beta" bleibt.</summary>
        public static string Anzeigename(string variante)
            => variante.EndsWith(" Stable", StringComparison.OrdinalIgnoreCase) ? variante[..^" Stable".Length] : variante;

        /// <summary>winget-Paket fuer die Neuinstallation - nur fuer die Fassungen, deren Paket sicher bekannt ist.</summary>
        public static string? WingetId(string variante) => Anzeigename(variante).ToLowerInvariant() switch
        {
            "opera" => "Opera.Opera",
            "opera gx" => "Opera.OperaGX",
            _ => null
        };
    }

    // ------------------------------------------------------------ Verfuegbarkeit

    /// <summary>
    /// Ist der Punkt beim SICHERN waehlbar? null = ja, sonst der Grund.
    ///
    /// Gesperrt werden nur die Programmsicherungen: ohne Profil beim gewaehlten
    /// Benutzer gibt es nichts zu sichern. Ist (noch) kein vorhandenes Profil
    /// gewaehlt, wird nichts gesperrt - dann fehlt die Grundlage dafuer.
    /// </summary>
    public static (string De, string En)? BeimSichern(Punkt p, string? profil)
    {
        if (string.IsNullOrWhiteSpace(profil) || !Directory.Exists(profil)) return null;

        if (p == Punkt.Opera)
        {
            var wurzel = Pfad(profil, Opera.Rel);
            return Opera.Varianten(wurzel).Count > 0 ? null
                : ($"Opera ist bei diesem Benutzer nicht eingerichtet - kein Profil unter {wurzel}.",
                   $"Opera is not set up for this user - no profile under {wurzel}.");
        }
        var app = Programme.FirstOrDefault(a => a.Punkt == p);
        if (app is null) return null;
        var ort = Pfad(profil, app.ProfilRel);
        return Directory.Exists(ort) ? null
            : ($"{app.Name} ist bei diesem Benutzer nicht eingerichtet - kein Profil unter {ort}.",
               $"{app.Name} is not set up for this user - no profile under {ort}.");
    }

    /// <summary>
    /// Ist der Punkt beim WIEDERHERSTELLEN waehlbar? null = ja, sonst der Grund.
    /// Gesperrt wird alles, was in der Sicherung nicht enthalten ist. Ist
    /// (noch) kein vorhandener Sicherungsordner gewaehlt, wird nichts gesperrt.
    /// </summary>
    public static (string De, string En)? BeimWiederherstellen(Punkt p, string? sicherung)
        => BeimWiederherstellen(sicherung)[p];

    /// <summary>
    /// Alle Punkte auf einmal - jeder wird dabei genau einmal nachgesehen, auch
    /// wenn die Sicherung auf einem langsamen Netzlaufwerk liegt.
    /// </summary>
    public static Dictionary<Punkt, (string De, string En)?> BeimWiederherstellen(string? sicherung)
    {
        var punkte = Enum.GetValues<Punkt>();
        var erg = punkte.ToDictionary(p => p, _ => ((string De, string En)?)null);
        if (string.IsNullOrWhiteSpace(sicherung) || !Directory.Exists(sicherung)) return erg;

        var da = punkte.ToDictionary(p => p, p => Enthaelt(sicherung, p));
        // Enthaelt der Ordner gar nichts davon, ist er keine Sicherung - meist
        // ist der Ordner darueber gewaehlt (D:\DaSi statt D:\DaSi\DASI_...).
        // Dann ein gemeinsamer Hinweis statt vierzehn einzelner Gruende.
        bool keine = !da.Values.Any(x => x);
        foreach (var p in punkte)
            if (!da[p]) erg[p] = keine ? KeineSicherung : Fehlt(p);
        return erg;
    }

    private static readonly (string De, string En) KeineSicherung =
        ("Der gewählte Ordner enthält keine Sicherung. Bitte den Sicherungsordner selbst wählen (z. B. DASI_<PC>_<Datum>).",
         "The selected folder contains no backup. Please select the backup folder itself (e.g. DASI_<PC>_<date>).");

    private static (string De, string En) Fehlt(Punkt p)
    {
        var was = p switch
        {
            Punkt.Profil => ("kein Benutzerprofil", "no user profile"),
            Punkt.AppData => ("kein AppData", "no AppData"),
            Punkt.Cloud => ("keine Cloud-Ordner", "no cloud folders"),
            Punkt.Winget => ("keine Programmliste (Winget)", "no program list (winget)"),
            Punkt.Wlan => ("keine WLAN-Profile", "no WiFi profiles"),
            Punkt.Drucker => ("keine Drucker", "no printers"),
            Punkt.Hintergrund => ("kein Hintergrundbild", "no wallpaper"),
            Punkt.DesktopShot => ("keinen Desktop-Screenshot", "no desktop screenshot"),
            Punkt.Opera => ("kein Opera-Profil", "no Opera profile"),
            _ => ($"kein {p}-Profil", $"no {p} profile"),
        };
        return ($"Die gewählte Sicherung enthält {was.Item1}.", $"The selected backup contains {was.Item2}.");
    }

    /// <summary>Liegt der Punkt in der Sicherung?</summary>
    private static bool Enthaelt(string sicherung, Punkt p)
    {
        var profil = Path.Combine(sicherung, "Benutzerprofil");
        return p switch
        {
            Punkt.Profil => Directory.Exists(profil),
            Punkt.AppData => Directory.Exists(Path.Combine(profil, "AppData")),
            Punkt.Cloud => CloudProviders.Detect(profil).Count > 0,
            Punkt.Opera => Opera.Varianten(Path.Combine(sicherung, Opera.BackupOrdner)).Count > 0,
            Punkt.Winget => File.Exists(Path.Combine(sicherung, "Winget", "Export.json"))
                            || File.Exists(Path.Combine(sicherung, "Export.json")),
            Punkt.Wlan => HatDateien(Path.Combine(sicherung, "WLAN-Profile"), "*.xml"),
            Punkt.Drucker => File.Exists(Path.Combine(sicherung, "Drucker", "Drucker.printerExport")),
            Punkt.Hintergrund => HatDateien(Path.Combine(sicherung, "Hintergrundbild"), "*", ohne: ".txt"),
            Punkt.DesktopShot => DesktopScreenshot.FindImages(Path.Combine(sicherung, DesktopScreenshot.FolderName)).Count > 0,
            _ => Programme.FirstOrDefault(a => a.Punkt == p) is { } app && Directory.Exists(Path.Combine(sicherung, app.BackupOrdner)),
        };
    }

    private static bool HatDateien(string ordner, string muster, string? ohne = null)
    {
        try
        {
            return Directory.Exists(ordner) && Directory.EnumerateFiles(ordner, muster)
                .Any(f => ohne is null || !f.EndsWith(ohne, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }
}
