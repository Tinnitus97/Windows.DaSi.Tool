using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;

namespace WindowsDaSiTool.Services;

/// <summary>The items of the UI - for backup and restore alike.</summary>
public enum Punkt
{
    Profil, AppData, Cloud,
    Firefox, Edge, Chrome, Brave, Opera, Thunderbird,
    Winget, Wlan, Drucker, Hintergrund, DesktopShot,
    /// <summary>Folders under C:\ (the user's own folders on the system drive).</summary>
    Laufwerk
}

/// <summary>A program whose profile is backed up.</summary>
/// <param name="ProfilRel">Location of the profile inside the user profile, e.g. "AppData\Roaming\Mozilla\Firefox".</param>
/// <param name="Prozess">Process name without ".exe" - it is closed before copying.</param>
/// <param name="WingetId">Package used to reinstall the program on restore, if known.</param>
/// <param name="Chromium">Chromium-based browser: its caches are left out.</param>
public sealed record AppEintrag(Punkt Punkt, string Name, string ProfilRel, string Prozess, string? WingetId, bool Chromium)
{
    /// <summary>Subfolder in the backup, e.g. "Firefox-Profil".</summary>
    public string BackupOrdner => Name + "-Profil";
}

/// <summary>
/// Where the programs keep their profiles, and which items can sensibly be
/// selected right now.
///
/// No dependencies, so the rules can be tested.
/// </summary>
public static class AppCatalog
{
    public static readonly AppEintrag Firefox = new(Punkt.Firefox, "Firefox", @"AppData\Roaming\Mozilla\Firefox", "firefox", "Mozilla.Firefox.de", false);
    public static readonly AppEintrag Edge = new(Punkt.Edge, "Edge", @"AppData\Local\Microsoft\Edge\User Data", "msedge", null, true);
    public static readonly AppEintrag Chrome = new(Punkt.Chrome, "Chrome", @"AppData\Local\Google\Chrome\User Data", "chrome", "Google.Chrome", true);
    public static readonly AppEintrag Brave = new(Punkt.Brave, "Brave", @"AppData\Local\BraveSoftware\Brave-Browser\User Data", "brave", "Brave.Brave", true);
    public static readonly AppEintrag Thunderbird = new(Punkt.Thunderbird, "Thunderbird", @"AppData\Roaming\Thunderbird", "thunderbird", "Mozilla.Thunderbird.de", false);

    /// <summary>All programs with exactly one profile folder (Opera has several, see <see cref="Opera"/>).</summary>
    public static readonly IReadOnlyList<AppEintrag> Programme = new[] { Firefox, Edge, Chrome, Brave, Thunderbird };

    /// <summary>
    /// Appends a catalog location to a profile path. The parts are appended one
    /// by one - on Windows the same as Path.Combine with "\", and a real path in
    /// the test environment as well.
    /// </summary>
    public static string PathIn(string basis, string rel)
        => Path.Combine(new[] { basis }.Concat(rel.Split('\\', StringSplitOptions.RemoveEmptyEntries)).ToArray());

    // ------------------------------------------------------------ Opera

    /// <summary>
    /// Opera comes in several editions - Opera, Opera GX, Beta, Developer,
    /// Air ... Each keeps its profile in its own folder under
    /// "AppData\Roaming\Opera Software" ("Opera Stable", "Opera GX Stable" ...).
    /// Each edition is recognized by its profile files instead of a fixed
    /// list - so a future edition is covered as well.
    /// </summary>
    public static class Opera
    {
        public const string Rel = @"AppData\Roaming\Opera Software";
        public const string BackupOrdner = "Opera-Profil";
        public const string Prozess = "opera";

        /// <summary>Marks a Chromium profile (directly or in "Default", depending on the Opera edition).</summary>
        private static readonly string[] Merkmale = { "Local State", "Preferences", "Bookmarks" };

        /// <summary>The profile folders of all Opera editions under <paramref name="wurzel"/>, sorted by display name.</summary>
        public static List<string> Variants(string wurzel)
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
            catch { /* not readable - then there are none */ }
            // By display name: "Opera, Opera Beta, Opera GX" instead of by folder name.
            return liste.OrderBy(DisplayName, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>"Opera GX Stable" -> "Opera GX", "Opera Stable" -> "Opera", "Opera Beta" stays.</summary>
        public static string DisplayName(string variante)
            => variante.EndsWith(" Stable", StringComparison.OrdinalIgnoreCase) ? variante[..^" Stable".Length] : variante;

        /// <summary>winget package for reinstalling - only for the editions whose package is known for sure.</summary>
        public static string? WingetId(string variante) => DisplayName(variante).ToLowerInvariant() switch
        {
            "opera" => "Opera.Opera",
            "opera gx" => "Opera.OperaGX",
            _ => null
        };
    }

    // ------------------------------------------------------------ Availability

    /// <summary>
    /// Does this PC have a Wi-Fi adapter? Replaceable, so availability can be
    /// tested without a real adapter.
    /// </summary>
    public static Func<bool> HatWlanAdapter { get; set; } = WifiAdapterPresent;

    /// <summary>
    /// A Wi-Fi adapter as Windows reports it - enabled, even in airplane mode
    /// or without a connection. An adapter disabled in Device Manager does not
    /// count: without it, "netsh wlan" neither returns profiles nor accepts
    /// any. If it cannot be determined, nothing is locked.
    /// </summary>
    public static bool WifiAdapterPresent()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                                   .Any(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);
        }
        catch { return true; }
    }

    private static readonly (string De, string En) KeinWlanSichern =
        ("Dieser PC hat keinen WLAN-Adapter (oder er ist deaktiviert) - es gibt keine WLAN-Profile zu sichern.",
         "This PC has no WiFi adapter (or it is disabled) - there are no WiFi profiles to back up.");

    private static readonly (string De, string En) KeinWlanZurueck =
        ("Dieser PC hat keinen WLAN-Adapter (oder er ist deaktiviert) - die WLAN-Profile aus der Sicherung lassen sich hier nicht einspielen.",
         "This PC has no WiFi adapter (or it is disabled) - the WiFi profiles from the backup cannot be imported here.");

    /// <summary>
    /// Can the item be selected for BACKUP? null = yes, otherwise the reason.
    ///
    /// Only program backups are locked: without a profile for the selected user
    /// there is nothing to back up. If no existing profile is selected (yet),
    /// nothing is locked - there is nothing to base it on.
    /// </summary>
    public static (string De, string En)? ForBackup(Punkt p, string? profil)
    {
        // Wi-Fi belongs to the computer, not to the user: no adapter, no profiles.
        if (p == Punkt.Wlan) return HatWlanAdapter() ? null : KeinWlanSichern;
        // Folders under C:\ belong to the computer, not to the user.
        if (p == Punkt.Laufwerk)
            return Directory.Exists(DriveFolders.Wurzel) ? null
                : ($"Das Laufwerk {DriveFolders.Wurzel} gibt es hier nicht.", $"The drive {DriveFolders.Wurzel} does not exist here.");

        if (string.IsNullOrWhiteSpace(profil) || !Directory.Exists(profil)) return null;

        if (p == Punkt.Opera)
        {
            var wurzel = PathIn(profil, Opera.Rel);
            return Opera.Variants(wurzel).Count > 0 ? null
                : ($"Opera ist bei diesem Benutzer nicht eingerichtet - kein Profil unter {wurzel}.",
                   $"Opera is not set up for this user - no profile under {wurzel}.");
        }
        var app = Programme.FirstOrDefault(a => a.Punkt == p);
        if (app is null) return null;
        var ort = PathIn(profil, app.ProfilRel);
        return Directory.Exists(ort) ? null
            : ($"{app.Name} ist bei diesem Benutzer nicht eingerichtet - kein Profil unter {ort}.",
               $"{app.Name} is not set up for this user - no profile under {ort}.");
    }

    /// <summary>
    /// Can the item be selected for RESTORE? null = yes, otherwise the reason.
    /// Everything the backup does not contain is locked. If no existing backup
    /// folder is selected (yet), nothing is locked.
    /// </summary>
    public static (string De, string En)? ForRestore(Punkt p, string? sicherung)
        => ForRestore(sicherung)[p];

    /// <summary>
    /// All items at once - each one is looked up exactly once, even if the
    /// backup lives on a slow network drive.
    /// </summary>
    public static Dictionary<Punkt, (string De, string En)?> ForRestore(string? sicherung)
    {
        var punkte = Enum.GetValues<Punkt>();
        var erg = punkte.ToDictionary(p => p, _ => ((string De, string En)?)null);
        if (!string.IsNullOrWhiteSpace(sicherung) && Directory.Exists(sicherung))
        {
            var da = punkte.ToDictionary(p => p, p => Contains(sicherung, p));
            // If the folder contains none of these, it is not a backup - usually the
            // folder above it was picked (D:\DaSi instead of D:\DaSi\DASI_...).
            // Then one shared hint instead of fourteen separate reasons.
            bool keine = !da.Values.Any(x => x);
            foreach (var p in punkte)
                if (!da[p]) erg[p] = keine ? KeineSicherung : MissingReason(p);
        }

        // Even if the backup contains Wi-Fi profiles, importing them still needs
        // an adapter - "netsh wlan add profile" fails otherwise. If they are
        // missing from the backup, that remains the reason.
        if (erg[Punkt.Wlan] is null && !HatWlanAdapter()) erg[Punkt.Wlan] = KeinWlanZurueck;
        return erg;
    }

    /// <summary>
    /// Does the folder already hold a backup? Any item or a log written by the
    /// tool is enough.
    /// </summary>
    public static bool IsBackup(string? ordner)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ordner) || !Directory.Exists(ordner)) return false;
            if (Enum.GetValues<Punkt>().Any(p => Contains(ordner, p))) return true;
            return Directory.EnumerateFiles(ordner, "Sicherung-Protokoll_*.txt").Any()
                || Directory.EnumerateFiles(ordner, "Wiederherstellung-Protokoll_*.txt").Any();
        }
        catch { return false; }
    }

    private static readonly (string De, string En) KeineSicherung =
        ("Der gewählte Ordner enthält keine Sicherung. Bitte den Sicherungsordner selbst wählen (z. B. DASI_<PC>_<Datum>).",
         "The selected folder contains no backup. Please select the backup folder itself (e.g. DASI_<PC>_<date>).");

    private static (string De, string En) MissingReason(Punkt p)
    {
        var was = p switch
        {
            Punkt.Profil => ("kein Benutzerprofil", "no user profile"),
            Punkt.AppData => ("kein AppData", "no AppData"),
            Punkt.Cloud => ("keine Cloud-Ordner", "no cloud folders"),
            Punkt.Winget => ("keine Programmliste (Winget)", "no program list (winget)"),
            Punkt.Wlan => ("keine WLAN-Profile", "no WiFi profiles"),
            Punkt.Drucker => ("keine Drucker", "no printers"),
            Punkt.Hintergrund => ("kein Design (weder Hintergrund noch Design-Einstellungen)", "no design (neither wallpaper nor design settings)"),
            Punkt.DesktopShot => ("weder Desktop-Symbole noch Screenshot", "neither desktop icons nor a screenshot"),
            Punkt.Laufwerk => ($"keine Ordner von {DriveFolders.Wurzel}", $"no folders from {DriveFolders.Wurzel}"),
            Punkt.Opera => ("kein Opera-Profil", "no Opera profile"),
            _ => ($"kein {p}-Profil", $"no {p} profile"),
        };
        return ($"Die gewählte Sicherung enthält {was.Item1}.", $"The selected backup contains {was.Item2}.");
    }

    /// <summary>Design &amp; Desktop, part Screenshot: are there images in the backup?</summary>
    public static bool HasDesktopScreenshot(string sicherung)
        => DesktopScreenshot.FindImages(Path.Combine(sicherung, DesktopScreenshot.FolderName)).Count > 0;

    /// <summary>Design &amp; Desktop, part Icons + taskbar: icon positions or taskbar in the backup?</summary>
    public static bool HasDesktopIcons(string sicherung) => DesktopIcons.Exists(sicherung) || Taskbar.Exists(sicherung);

    /// <summary>Is there a wallpaper image in the backup?</summary>
    public static bool HasWallpaper(string sicherung)
        => HasFiles(Path.Combine(sicherung, "Hintergrundbild"), "*", ohne: ".txt");

    /// <summary>Is the item in the backup?</summary>
    private static bool Contains(string sicherung, Punkt p)
    {
        var profil = Path.Combine(sicherung, "Benutzerprofil");
        return p switch
        {
            Punkt.Profil => Directory.Exists(profil),
            Punkt.AppData => Directory.Exists(Path.Combine(profil, "AppData")),
            Punkt.Cloud => CloudProviders.Detect(profil).Count > 0,
            Punkt.Opera => Opera.Variants(Path.Combine(sicherung, Opera.BackupOrdner)).Count > 0,
            Punkt.Winget => File.Exists(Path.Combine(sicherung, "Winget", "Export.json"))
                            || File.Exists(Path.Combine(sicherung, "Export.json")),
            Punkt.Wlan => HasFiles(Path.Combine(sicherung, "WLAN-Profile"), "*.xml"),
            Punkt.Drucker => File.Exists(Path.Combine(sicherung, "Drucker", "Drucker.printerExport")),
            Punkt.Hintergrund => HasWallpaper(sicherung) || DesignSettings.Exists(sicherung),
            Punkt.DesktopShot => HasDesktopScreenshot(sicherung) || HasDesktopIcons(sicherung),
            Punkt.Laufwerk => DriveFolders.Exists(sicherung),
            _ => Programme.FirstOrDefault(a => a.Punkt == p) is { } app && Directory.Exists(Path.Combine(sicherung, app.BackupOrdner)),
        };
    }

    private static bool HasFiles(string ordner, string muster, string? ohne = null)
    {
        try
        {
            return Directory.Exists(ordner) && Directory.EnumerateFiles(ordner, muster)
                .Any(f => ohne is null || !f.EndsWith(ohne, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }
}
