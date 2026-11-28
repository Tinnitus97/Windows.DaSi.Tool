using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;

namespace WindowsDaSiTool.Services;

/// <summary>A desktop icon and its position.</summary>
/// <param name="Name">Unique name in the desktop folder, e.g. "Rechnung.pdf" or
/// "::{645FF040-5081-101B-9F08-00AA002F954E}" (Recycle Bin) - independent of the language.</param>
/// <param name="Anzeige">The label below the icon, e.g. "Papierkorb" - for the log and as a
/// fallback when matching.</param>
/// <param name="X">Position in desktop pixels (top left corner = 0,0).</param>
public sealed record DesktopIcon(string Name, string Anzeige, int X, int Y);

/// <summary>The backed-up arrangement of the desktop.</summary>
public sealed class DesktopLayout
{
    public int Version { get; set; } = 1;
    public string Rechner { get; set; } = "";
    public string Benutzer { get; set; } = "";
    public DateTime Erstellt { get; set; }
    /// <summary>Size of the desktop area across all monitors, in pixels.</summary>
    public int Breite { get; set; }
    public int Hoehe { get; set; }
    /// <summary>"Auto arrange icons" was on.</summary>
    public bool AutomatischAnordnen { get; set; }
    /// <summary>"Align icons to grid" was on.</summary>
    public bool AmRasterAusrichten { get; set; }
    public List<DesktopIcon> Symbole { get; set; } = new();
}

/// <summary>An icon from the backup and where it goes on this desktop.</summary>
/// <param name="Index">Position of the icon in the current desktop's list.</param>
public sealed record IconZiel(DesktopIcon Gesichert, int Index, int X, int Y);

/// <summary>Which backed-up icon goes where - and which ones do not exist here.</summary>
public sealed record IconPlan(List<IconZiel> Zuordnung, List<DesktopIcon> Fehlend, bool Skaliert);

/// <summary>What happened while restoring.</summary>
/// <param name="Eingeschaltet">System icons ("This PC" ...) that had to be turned on first.</param>
public sealed record IconErgebnis(int Gesetzt, int Gesamt, List<DesktopIcon> Fehlend, bool Skaliert,
                                  int BreiteJetzt, int HoeheJetzt, bool AnordnenAusgeschaltet,
                                  bool RasterUmgestellt, bool AnordnenWiederAn,
                                  IReadOnlyList<string>? Eingeschaltet = null);

/// <summary>
/// The desktop as Explorer currently shows it. On Windows
/// <see cref="ExplorerDesktop"/>; replaceable, so the flow can be tested
/// without Explorer.
/// </summary>
public interface IDesktopView : IDisposable
{
    (int Breite, int Hoehe) Size();
    bool AutomatischAnordnen { get; set; }
    bool AmRasterAusrichten { get; set; }
    /// <summary>All icons with their positions. The order is what <see cref="Set"/> refers to.</summary>
    IReadOnlyList<DesktopIcon> Icons();
    /// <summary>Moves the icon with this index (from the last <see cref="Icons"/>) to a position.</summary>
    void Set(int index, int x, int y);
    /// <summary>Makes Explorer reload the desktop (like F5).</summary>
    void Refresh();
}

/// <summary>
/// Backs up the positions of the desktop icons and restores them later -
/// like the program DesktopOK.
///
/// Reading and setting go through the interface Explorer itself offers for
/// its desktop (IFolderView), not by peeking into its memory. Icons are
/// matched by their name in the desktop folder ("Rechnung.pdf", for the
/// Recycle Bin its ID) - which does not depend on the language or on
/// whether Windows shows file extensions.
/// </summary>
public static class DesktopIcons
{
    /// <summary>Subfolder in the backup target.</summary>
    public const string FolderName = "Desktop-Symbole";

    /// <summary>
    /// The system icons from "Desktop icon settings" with their IDs - that is
    /// their name in the desktop folder ("::{...}"), independent of language
    /// and user name. The user folder, for instance, carries the user's name:
    /// "Anwender" on the old, "Anwender123" on the new computer.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (string De, string En)> Systemsymbole =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["{20D04FE0-3AEA-1069-A2D8-08002B30309D}"] = ("Dieser PC", "This PC"),
            ["{59031A47-3F72-44A7-89C5-5595FE6B30EE}"] = ("Benutzerordner", "User's files"),
            ["{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}"] = ("Netzwerk", "Network"),
            ["{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}"] = ("Systemsteuerung", "Control Panel"),
            ["{645FF040-5081-101B-9F08-00AA002F954E}"] = ("Papierkorb", "Recycle Bin"),
        };

    /// <summary>"::{20D04FE0-...}" -> "{20D04FE0-...}" if it is a system icon; otherwise null.</summary>
    public static string? SystemIconId(string name)
    {
        if (string.IsNullOrEmpty(name) || !name.StartsWith("::", StringComparison.Ordinal)) return null;
        var kennung = name[2..];
        return Systemsymbole.ContainsKey(kennung) ? kennung.ToUpperInvariant() : null;
    }

    /// <summary>
    /// Splits missing icons by whether they come from the user profile: if
    /// their file is in the backup's desktop folder (or it is a system icon),
    /// it comes back with the profile. The others lived elsewhere - usually on
    /// the public desktop for all users, where programs put their shortcut when
    /// they are installed. Those appear only together with the program.
    ///
    /// Without a desktop folder in the backup (only icon positions backed up)
    /// this cannot be told - then they all count as profile icons.
    /// </summary>
    public static (List<DesktopIcon> ImProfil, List<DesktopIcon> Fremd) ByOrigin(
        IReadOnlyList<DesktopIcon> fehlend, string desktopDerSicherung)
    {
        if (!Directory.Exists(desktopDerSicherung)) return (fehlend.ToList(), new List<DesktopIcon>());
        var imProfil = new List<DesktopIcon>();
        var fremd = new List<DesktopIcon>();
        foreach (var f in fehlend)
        {
            bool liegtDa = f.Name.StartsWith("::", StringComparison.Ordinal);
            if (!liegtDa && f.Name.Length > 0 && f.Name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && f.Name.IndexOf('\\') < 0)
            {
                var pfad = Path.Combine(desktopDerSicherung, f.Name);
                liegtDa = File.Exists(pfad) || Directory.Exists(pfad);
            }
            (liegtDa ? imProfil : fremd).Add(f);
        }
        return (imProfil, fremd);
    }

    /// <summary>
    /// The system icons that were on the desktop at backup time but are missing
    /// here - on a freshly set up computer, "This PC" and the user folder are
    /// usually turned off.
    /// </summary>
    public static List<string> MissingSystemIcons(DesktopLayout gesichert, IReadOnlyList<DesktopIcon> jetzt)
    {
        var da = new HashSet<string>(jetzt.Select(j => SystemIconId(j.Name)).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        return gesichert.Symbole.Select(s => SystemIconId(s.Name)).OfType<string>()
                        .Where(k => !da.Contains(k))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
    }
    public const string FileName = "Symbolpositionen.json";

    /// <summary>How the desktop is opened - on Windows, the Explorer desktop.</summary>
    public static Func<IDesktopView> OeffneDesktop { get; set; } = () =>
        OperatingSystem.IsWindows()
            ? ExplorerDesktop.Open()
            : throw new PlatformNotSupportedException("Desktop-Symbole gibt es nur unter Windows.");

    /// <summary>
    /// Tries again as long as <paramref name="nochmal"/> considers the error
    /// temporary - for at most <paramref name="hoechstens"/>.
    /// </summary>
    internal static T WithWaiting<T>(Func<T> versuch, TimeSpan hoechstens, Func<Exception, bool> nochmal, TimeSpan? pause = null)
    {
        var uhr = Stopwatch.StartNew();
        while (true)
        {
            try { return versuch(); }
            catch (Exception ex) when (nochmal(ex) && uhr.Elapsed < hoechstens)
            {
                Thread.Sleep(pause ?? TimeSpan.FromMilliseconds(500));
            }
        }
    }

    public static string FilePath(string sicherung) => Path.Combine(sicherung, FolderName, FileName);

    public static bool Exists(string sicherung)
    {
        try { return File.Exists(FilePath(sicherung)); } catch { return false; }
    }

    // ------------------------------------------------------------ File

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        // Keep umlauts readable ("Präsentation.pptx" instead of "Präsentation.pptx").
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Save(string ordner, DesktopLayout layout)
    {
        Directory.CreateDirectory(ordner);
        var datei = Path.Combine(ordner, FileName);
        File.WriteAllText(datei, JsonSerializer.Serialize(layout, Json));
        return datei;
    }

    /// <summary>Reads a backed-up arrangement - null if there is no (readable) one.</summary>
    public static DesktopLayout? Load(string datei)
    {
        try
        {
            if (!File.Exists(datei)) return null;
            var layout = JsonSerializer.Deserialize<DesktopLayout>(File.ReadAllText(datei), Json);
            return layout is { Symbole: not null } ? layout : null;
        }
        catch { return null; }
    }

    // ------------------------------------------------------------ Matching

    /// <summary>
    /// Matches each backed-up icon to one on this desktop and converts its
    /// position.
    ///
    /// First by the name in the desktop folder, then - for shortcuts that may
    /// have a different name here - by the displayed name. Each icon is
    /// assigned at most once.
    ///
    /// If the desktop has a different size here (another monitor, another
    /// resolution), the positions are scaled in proportion: what was top left
    /// is top left again, what was at the right edge is at the right edge
    /// again.
    /// </summary>
    public static IconPlan Plan(DesktopLayout gesichert, IReadOnlyList<DesktopIcon> jetzt, int breite, int hoehe)
    {
        bool skalieren = gesichert.Breite > 0 && gesichert.Hoehe > 0 && breite > 0 && hoehe > 0
                         && (gesichert.Breite != breite || gesichert.Hoehe != hoehe);
        var vergeben = new bool[jetzt.Count];
        var zuordnung = new List<IconZiel>();
        var fehlend = new List<DesktopIcon>();

        int Find(Func<DesktopIcon, bool> passt)
        {
            for (int i = 0; i < jetzt.Count; i++)
                if (!vergeben[i] && passt(jetzt[i])) return i;
            return -1;
        }

        // First all by the unique name, then the rest by the displayed one -
        // otherwise a display name could snatch the real partner of a later icon.
        var offen = new List<DesktopIcon>();
        var treffer = new Dictionary<DesktopIcon, int>(ReferenceEqualityComparer.Instance);
        foreach (var s in gesichert.Symbole)
        {
            int i = Find(j => string.Equals(j.Name, s.Name, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) { vergeben[i] = true; treffer[s] = i; } else offen.Add(s);
        }
        foreach (var s in offen)
        {
            int i = string.IsNullOrWhiteSpace(s.Anzeige) ? -1
                : Find(j => string.Equals(j.Anzeige, s.Anzeige, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) { vergeben[i] = true; treffer[s] = i; }
        }

        foreach (var s in gesichert.Symbole)
        {
            if (!treffer.TryGetValue(s, out var i)) { fehlend.Add(s); continue; }
            int x = s.X, y = s.Y;
            if (skalieren)
            {
                x = (int)Math.Round(s.X * (double)breite / gesichert.Breite);
                y = (int)Math.Round(s.Y * (double)hoehe / gesichert.Hoehe);
            }
            if (breite > 0) x = Math.Clamp(x, 0, breite - 1);
            if (hoehe > 0) y = Math.Clamp(y, 0, hoehe - 1);
            zuordnung.Add(new IconZiel(s, i, x, y));
        }
        return new IconPlan(zuordnung, fehlend, skalieren);
    }

    // ------------------------------------------------------------ Flow

    /// <summary>Reads the current arrangement of the desktop.</summary>
    public static DesktopLayout Read(string rechner, string benutzer) => OnOwnThread(() =>
    {
        using var desktop = OeffneDesktop();
        var (b, h) = desktop.Size();
        return new DesktopLayout
        {
            Rechner = rechner,
            Benutzer = benutzer,
            Erstellt = DateTime.Now,
            Breite = b,
            Hoehe = h,
            AutomatischAnordnen = desktop.AutomatischAnordnen,
            AmRasterAusrichten = desktop.AmRasterAusrichten,
            Symbole = desktop.Icons().Where(s => s.Name.Length > 0).ToList(),
        };
    });

    /// <summary>
    /// Moves the backed-up icons to their positions.
    ///
    /// If icons are still missing, it waits up to <paramref name="warten"/> for
    /// them: right after the profile is restored, Explorer needs a moment until
    /// the new files show up on the desktop.
    /// </summary>
    /// <param name="einschalten">
    /// Turns on system icons that were backed up but are missing here (IDs
    /// "{...}"); true if that worked. null: turn nothing on.
    /// </param>
    public static IconErgebnis Restore(DesktopLayout gesichert, TimeSpan warten,
                                                Func<IReadOnlyList<string>, bool>? einschalten = null) => OnOwnThread(() =>
    {
        using var desktop = OeffneDesktop();
        var (b, h) = desktop.Size();

        // Turn on the system icons first - Explorer shows them after a moment, and
        // the waiting loop below picks them up.
        IReadOnlyList<string>? eingeschaltet = null;
        var fehlendeSystem = MissingSystemIcons(gesichert, desktop.Icons());
        if (fehlendeSystem.Count > 0 && einschalten is not null)
        {
            try
            {
                if (einschalten(fehlendeSystem))
                {
                    eingeschaltet = fehlendeSystem;
                    desktop.Refresh();
                }
            }
            catch { /* then they stay off - they are reported as missing */ }
        }

        var plan = Plan(gesichert, desktop.Icons(), b, h);

        var uhr = Stopwatch.StartNew();
        int ohneFortschritt = 0;
        while (plan.Fehlend.Count > 0 && uhr.Elapsed < warten && ohneFortschritt < 4)
        {
            Thread.Sleep(1000);
            var neu = Plan(gesichert, desktop.Icons(), b, h);
            ohneFortschritt = neu.Fehlend.Count < plan.Fehlend.Count ? 0 : ohneFortschritt + 1;
            plan = neu;
        }

        // With "Auto arrange", Windows puts every icon straight back into its slot
        // in the row - positions would have no effect.
        bool anordnenAus = false;
        if (desktop.AutomatischAnordnen) { desktop.AutomatischAnordnen = false; anordnenAus = true; }
        // Grid as at backup time: otherwise freely placed icons would snap to the
        // grid, or aligned ones would sit slightly off.
        bool raster = false;
        if (desktop.AmRasterAusrichten != gesichert.AmRasterAusrichten)
        {
            desktop.AmRasterAusrichten = gesichert.AmRasterAusrichten;
            raster = true;
        }

        int gesetzt = 0;
        foreach (var z in plan.Zuordnung)
        {
            try { desktop.Set(z.Index, z.X, z.Y); gesetzt++; }
            catch { /* a single icon - carry on with the others */ }
        }

        // If "Auto arrange" was on at backup time, Windows arranges the icons
        // itself - so switch it back on here.
        bool wiederAn = false;
        if (gesichert.AutomatischAnordnen) { desktop.AutomatischAnordnen = true; wiederAn = true; }

        return new IconErgebnis(gesetzt, gesichert.Symbole.Count, plan.Fehlend, plan.Skaliert, b, h,
                                anordnenAus && !wiederAn, raster, wiederAn, eingeschaltet);
    });

    /// <summary>
    /// Explorer's COM objects belong on an STA thread, and the measurements
    /// should be real pixels (not scaled to 100 %).
    /// </summary>
    private static T OnOwnThread<T>(Func<T> arbeit)
    {
        T? ergebnis = default;
        ExceptionDispatchInfo? fehler = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (OperatingSystem.IsWindows()) ExplorerDesktop.PerMonitorDpi();
                ergebnis = arbeit();
            }
            catch (Exception ex) { fehler = ExceptionDispatchInfo.Capture(ex); }
        }) { IsBackground = true, Name = "Desktop-Symbole" };
        if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        fehler?.Throw();
        return ergebnis!;
    }
}

/// <summary>
/// Explorer's desktop, accessed as in Raymond Chen's example "Manipulating
/// the positions of desktop icons": ShellWindows returns the desktop window,
/// its browser the active view, and the view is an IFolderView - with
/// GetItemPosition and SelectAndPositionItems.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ExplorerDesktop : IDesktopView
{
    /// <summary>
    /// Opens Explorer's desktop. While Explorer is (re)starting - after sign-in
    /// or right after the restart for the taskbar - the taskbar is often there
    /// already, but the desktop only seconds later. Wait that long instead of
    /// giving up with "no Explorer desktop".
    /// </summary>
    public static ExplorerDesktop Open(TimeSpan? hoechstens = null)
        => DesktopIcons.WithWaiting(() => new ExplorerDesktop(), hoechstens ?? TimeSpan.FromSeconds(20), ex => ex is KeinDesktopException);

    private readonly IFolderView2 _ansicht;
    private readonly IShellFolder _ordner;
    private readonly IntPtr _fenster;
    private readonly List<IntPtr> _pidls = new();

    public ExplorerDesktop()
    {
        object? shellWindows = null;
        try
        {
            var typ = Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"))
                      ?? throw new InvalidOperationException("ShellWindows ist nicht registriert.");
            shellWindows = Activator.CreateInstance(typ)
                           ?? throw new InvalidOperationException("ShellWindows ließ sich nicht anlegen.");
            object ort = 0;          // CSIDL_DESKTOP
            object? wurzel = null;   // VT_EMPTY
            int hr = ((IShellWindows)shellWindows).FindWindowSW(ref ort, ref wurzel, SWC_DESKTOP, out _, SWFO_NEEDDISPATCH, out var dispatch);
            if (hr != 0 || dispatch is null)
                throw new KeinDesktopException($"Kein Explorer-Desktop gefunden (0x{hr:X8}) - läuft der Explorer?");

            var sid = SID_STopLevelBrowser;
            var iidBrowser = typeof(IShellBrowser).GUID;
            // Registered, but without a view yet: "not there yet" as well.
            int hrDienst = ((IComServiceProvider)dispatch).QueryService(ref sid, ref iidBrowser, out var browser);
            if (hrDienst < 0) throw new KeinDesktopException($"Der Explorer-Desktop ist noch nicht bereit (QueryService 0x{hrDienst:X8}).");
            int hrAnsicht = ((IShellBrowser)browser).QueryActiveShellView(out var ansicht);
            if (hrAnsicht < 0 || ansicht is null) throw new KeinDesktopException($"Der Explorer-Desktop ist noch nicht bereit (QueryActiveShellView 0x{hrAnsicht:X8}).");

            _ansicht = (IFolderView2)ansicht;
            ThrowIfFailed(((IShellView)ansicht).GetWindow(out _fenster), "GetWindow");
            var iidOrdner = typeof(IShellFolder).GUID;
            ThrowIfFailed(_ansicht.GetFolder(ref iidOrdner, out var ordner), "GetFolder");
            _ordner = (IShellFolder)ordner;
        }
        finally
        {
            if (shellWindows is not null) Marshal.ReleaseComObject(shellWindows);
        }
    }

    public (int Breite, int Hoehe) Size()
        => GetClientRect(_fenster, out var r) ? (r.Right - r.Left, r.Bottom - r.Top) : (0, 0);

    public bool AutomatischAnordnen
    {
        get => (Flags() & FWF_AUTOARRANGE) != 0;
        set => ThrowIfFailed(_ansicht.SetCurrentFolderFlags(FWF_AUTOARRANGE, value ? FWF_AUTOARRANGE : 0), "SetCurrentFolderFlags");
    }

    public bool AmRasterAusrichten
    {
        get => (Flags() & FWF_SNAPTOGRID) != 0;
        set => ThrowIfFailed(_ansicht.SetCurrentFolderFlags(FWF_SNAPTOGRID, value ? FWF_SNAPTOGRID : 0), "SetCurrentFolderFlags");
    }

    private uint Flags()
    {
        ThrowIfFailed(_ansicht.GetCurrentFolderFlags(out var f), "GetCurrentFolderFlags");
        return f;
    }

    public IReadOnlyList<DesktopIcon> Icons()
    {
        FreePidls();
        ThrowIfFailed(_ansicht.ItemCount(SVGIO_ALLVIEW, out var anzahl), "ItemCount");
        var liste = new List<DesktopIcon>(anzahl);
        for (int i = 0; i < anzahl; i++)
        {
            if (_ansicht.Item(i, out var pidl) != 0 || pidl == IntPtr.Zero) { _pidls.Add(IntPtr.Zero); liste.Add(new DesktopIcon("", "", 0, 0)); continue; }
            _pidls.Add(pidl);
            _ansicht.GetItemPosition(pidl, out var pt);
            liste.Add(new DesktopIcon(Name(pidl, SHGDN_INFOLDER | SHGDN_FORPARSING), Name(pidl, SHGDN_NORMAL), pt.X, pt.Y));
        }
        return liste;
    }

    /// <summary>
    /// "Refresh" from the desktop's context menu: command 0x7103 sent to the
    /// view. Only then does Explorer show freshly enabled system icons.
    /// </summary>
    public void Refresh()
    {
        const uint WM_COMMAND = 0x0111;
        PostMessageW(_fenster, WM_COMMAND, (IntPtr)0x7103, IntPtr.Zero);
    }

    public void Set(int index, int x, int y)
    {
        var pidl = _pidls[index];
        if (pidl == IntPtr.Zero) throw new InvalidOperationException("Symbol ohne Kennung.");
        ThrowIfFailed(_ansicht.SelectAndPositionItems(1, new[] { pidl }, new[] { new POINT { X = x, Y = y } }, SVSI_POSITIONITEM),
               "SelectAndPositionItems");
    }

    private string Name(IntPtr pidl, uint art)
    {
        var strret = Marshal.AllocCoTaskMem(1024);
        try
        {
            for (int i = 0; i < 1024; i += 8) Marshal.WriteInt64(strret, i, 0);
            if (_ordner.GetDisplayNameOf(pidl, art, strret) != 0) return "";
            if (StrRetToStrW(strret, pidl, out var text) != 0 || text == IntPtr.Zero) return "";
            try { return Marshal.PtrToStringUni(text) ?? ""; }
            finally { Marshal.FreeCoTaskMem(text); }
        }
        finally { Marshal.FreeCoTaskMem(strret); }
    }

    private void FreePidls()
    {
        foreach (var p in _pidls) if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p);
        _pidls.Clear();
    }

    public void Dispose()
    {
        FreePidls();
        try { Marshal.ReleaseComObject(_ordner); } catch { }
        try { Marshal.ReleaseComObject(_ansicht); } catch { }
    }

    private static void ThrowIfFailed(int hr, string was)
    {
        if (hr < 0) throw new COMException($"{was} fehlgeschlagen (0x{hr:X8}).", hr);
    }

    /// <summary>The calling thread works in real pixels (per monitor).</summary>
    internal static void PerMonitorDpi()
    {
        try { SetThreadDpiAwarenessContext(new IntPtr(-4)); } catch { /* older Windows version */ }
    }

    // ------------------------------------------------------------ Windows interfaces

    private const int SWC_DESKTOP = 8;
    private const int SWFO_NEEDDISPATCH = 1;
    private const uint SVGIO_ALLVIEW = 0x2;
    private const uint SVSI_POSITIONITEM = 0x80;
    private const uint FWF_AUTOARRANGE = 0x1;
    private const uint FWF_SNAPTOGRID = 0x4;
    private const uint SHGDN_NORMAL = 0x0;
    private const uint SHGDN_INFOLDER = 0x1;
    private const uint SHGDN_FORPARSING = 0x8000;
    private static readonly Guid SID_STopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("shlwapi.dll")] private static extern int StrRetToStrW(IntPtr pstr, IntPtr pidl, out IntPtr ppsz);

    // Placeholders (_n) keep the order of the methods; only the spelled-out
    // ones are called.

    /// <summary>IShellWindows - a dual interface: the four IDispatch methods follow IUnknown.</summary>
    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellWindows
    {
        void _d1(); void _d2(); void _d3(); void _d4();
        void _get_Count(); void _Item(); void _NewEnum(); void _Register();
        void _RegisterPending(); void _Revoke(); void _OnNavigate(); void _OnActivated();
        [PreserveSig]
        int FindWindowSW(ref object pvarLoc, ref object? pvarLocRoot, int swClass, out int phwnd, int swfwOptions,
                         [MarshalAs(UnmanagedType.IDispatch)] out object? ppdispOut);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IComServiceProvider
    {
        [PreserveSig]
        int QueryService(ref Guid guidService, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppvObject);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        void _GetWindow(); void _ContextSensitiveHelp(); void _InsertMenusSB(); void _SetMenuSB();
        void _RemoveMenusSB(); void _SetStatusTextSB(); void _EnableModelessSB(); void _TranslateAcceleratorSB();
        void _BrowseObject(); void _GetViewStateStream(); void _GetControlWindow(); void _SendControlMsg();
        [PreserveSig] int QueryActiveShellView([MarshalAs(UnmanagedType.Interface)] out object ppshv);
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        [PreserveSig] int GetWindow(out IntPtr phwnd);
    }

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        void _ParseDisplayName(); void _EnumObjects(); void _BindToObject(); void _BindToStorage();
        void _CompareIDs(); void _CreateViewObject(); void _GetAttributesOf(); void _GetUIObjectOf();
        [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint uFlags, IntPtr pName);
    }

    /// <summary>IFolderView2 - with all methods of IFolderView before it.</summary>
    [ComImport, Guid("1AF3A467-214F-4298-908E-06B03E0B39F9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFolderView2
    {
        // IFolderView
        [PreserveSig] int GetCurrentViewMode(out uint pViewMode);
        [PreserveSig] int SetCurrentViewMode(uint viewMode);
        [PreserveSig] int GetFolder(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        [PreserveSig] int Item(int iItemIndex, out IntPtr ppidl);
        [PreserveSig] int ItemCount(uint uFlags, out int pcItems);
        [PreserveSig] int Items(uint uFlags, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        [PreserveSig] int GetSelectionMarkedItem(out int piItem);
        [PreserveSig] int GetFocusedItem(out int piItem);
        [PreserveSig] int GetItemPosition(IntPtr pidl, out POINT ppt);
        [PreserveSig] int GetSpacing(ref POINT ppt);
        [PreserveSig] int GetDefaultSpacing(out POINT ppt);
        [PreserveSig] int GetAutoArrange();
        [PreserveSig] int SelectItem(int iItem, uint dwFlags);
        [PreserveSig] int SelectAndPositionItems(uint cidl, [In, MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl,
                                                 [In, MarshalAs(UnmanagedType.LPArray)] POINT[] apt, uint dwFlags);
        // IFolderView2
        void _SetGroupBy(); void _GetGroupBy(); void _SetViewProperty(); void _GetViewProperty();
        void _SetTileViewProperties(); void _SetExtendedTileViewProperties(); void _SetText();
        [PreserveSig] int SetCurrentFolderFlags(uint dwMask, uint dwFlags);
        [PreserveSig] int GetCurrentFolderFlags(out uint pdwFlags);
    }
}

/// <summary>The Explorer desktop is not there (yet) - if Explorer is running, it will be soon.</summary>
public sealed class KeinDesktopException(string message) : InvalidOperationException(message);
