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

/// <summary>Ein Desktop-Symbol und seine Stelle.</summary>
/// <param name="Name">Eindeutiger Name im Desktop-Ordner, z. B. "Rechnung.pdf" oder
/// "::{645FF040-5081-101B-9F08-00AA002F954E}" (Papierkorb) - unabhaengig von der Sprache.</param>
/// <param name="Anzeige">Was unter dem Symbol steht, z. B. "Papierkorb" - fuer das Protokoll
/// und als Notbehelf beim Zuordnen.</param>
/// <param name="X">Position in Bildpunkten des Desktops (linke obere Ecke = 0,0).</param>
public sealed record DesktopIcon(string Name, string Anzeige, int X, int Y);

/// <summary>Die gesicherte Anordnung des Desktops.</summary>
public sealed class DesktopLayout
{
    public int Version { get; set; } = 1;
    public string Rechner { get; set; } = "";
    public string Benutzer { get; set; } = "";
    public DateTime Erstellt { get; set; }
    /// <summary>Groesse der Desktop-Flaeche ueber alle Monitore, in Bildpunkten.</summary>
    public int Breite { get; set; }
    public int Hoehe { get; set; }
    /// <summary>"Symbole automatisch anordnen" war an.</summary>
    public bool AutomatischAnordnen { get; set; }
    /// <summary>"Symbole am Raster ausrichten" war an.</summary>
    public bool AmRasterAusrichten { get; set; }
    public List<DesktopIcon> Symbole { get; set; } = new();
}

/// <summary>Ein Symbol der Sicherung und wohin es auf diesem Desktop kommt.</summary>
/// <param name="Index">Stelle des Symbols in der Liste des aktuellen Desktops.</param>
public sealed record IconZiel(DesktopIcon Gesichert, int Index, int X, int Y);

/// <summary>Welches gesicherte Symbol wohin kommt - und welche es hier nicht gibt.</summary>
public sealed record IconPlan(List<IconZiel> Zuordnung, List<DesktopIcon> Fehlend, bool Skaliert);

/// <summary>Was beim Zurueckschreiben geschah.</summary>
public sealed record IconErgebnis(int Gesetzt, int Gesamt, List<DesktopIcon> Fehlend, bool Skaliert,
                                  int BreiteJetzt, int HoeheJetzt, bool AnordnenAusgeschaltet,
                                  bool RasterUmgestellt, bool AnordnenWiederAn);

/// <summary>
/// Der Desktop, wie ihn der Explorer gerade zeigt. Unter Windows
/// <see cref="ExplorerDesktop"/>; austauschbar, damit sich der Ablauf ohne
/// Explorer pruefen laesst.
/// </summary>
public interface IDesktopView : IDisposable
{
    (int Breite, int Hoehe) Groesse();
    bool AutomatischAnordnen { get; set; }
    bool AmRasterAusrichten { get; set; }
    /// <summary>Alle Symbole mit ihrer Stelle. Die Reihenfolge gilt fuer <see cref="Setzen"/>.</summary>
    IReadOnlyList<DesktopIcon> Symbole();
    /// <summary>Setzt das Symbol mit diesem Index (aus dem letzten <see cref="Symbole"/>) an eine Stelle.</summary>
    void Setzen(int index, int x, int y);
}

/// <summary>
/// Sichert die Stellen der Desktop-Symbole und setzt sie spaeter zurueck -
/// wie das Programm DesktopOK.
///
/// Gelesen und gesetzt wird ueber die Schnittstelle, die der Explorer selbst
/// fuer seinen Desktop anbietet (IFolderView), nicht durch Mitlesen in seinem
/// Speicher. Zugeordnet wird ueber den Namen im Desktop-Ordner ("Rechnung.pdf",
/// beim Papierkorb seine Kennung) - der ist unabhaengig von der Sprache und
/// davon, ob Windows Dateiendungen anzeigt.
/// </summary>
public static class DesktopIcons
{
    /// <summary>Unterordner im Backup-Ziel.</summary>
    public const string FolderName = "Desktop-Symbole";
    public const string FileName = "Symbolpositionen.json";

    /// <summary>Wie der Desktop geoeffnet wird - unter Windows der Explorer-Desktop.</summary>
    public static Func<IDesktopView> OeffneDesktop { get; set; } = () =>
        OperatingSystem.IsWindows()
            ? new ExplorerDesktop()
            : throw new PlatformNotSupportedException("Desktop-Symbole gibt es nur unter Windows.");

    public static string Datei(string sicherung) => Path.Combine(sicherung, FolderName, FileName);

    public static bool Vorhanden(string sicherung)
    {
        try { return File.Exists(Datei(sicherung)); } catch { return false; }
    }

    // ------------------------------------------------------------ Datei

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        // Umlaute lesbar lassen ("Präsentation.pptx" statt "Präsentation.pptx").
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Speichern(string ordner, DesktopLayout layout)
    {
        Directory.CreateDirectory(ordner);
        var datei = Path.Combine(ordner, FileName);
        File.WriteAllText(datei, JsonSerializer.Serialize(layout, Json));
        return datei;
    }

    /// <summary>Liest eine gesicherte Anordnung - null, wenn es keine (lesbare) gibt.</summary>
    public static DesktopLayout? Laden(string datei)
    {
        try
        {
            if (!File.Exists(datei)) return null;
            var layout = JsonSerializer.Deserialize<DesktopLayout>(File.ReadAllText(datei), Json);
            return layout is { Symbole: not null } ? layout : null;
        }
        catch { return null; }
    }

    // ------------------------------------------------------------ Zuordnen

    /// <summary>
    /// Ordnet jedem gesicherten Symbol eines auf diesem Desktop zu und rechnet
    /// seine Stelle um.
    ///
    /// Zuerst ueber den Namen im Desktop-Ordner, dann - fuer Sicherungen von
    /// Verknuepfungen, die hier anders heissen koennten - ueber den angezeigten
    /// Namen. Jedes Symbol wird hoechstens einmal vergeben.
    ///
    /// Ist der Desktop hier anders gross (anderer Monitor, andere Aufloesung),
    /// werden die Stellen im selben Verhaeltnis umgerechnet: Was links oben lag,
    /// liegt wieder links oben, was am rechten Rand lag, wieder am rechten Rand.
    /// </summary>
    public static IconPlan Plan(DesktopLayout gesichert, IReadOnlyList<DesktopIcon> jetzt, int breite, int hoehe)
    {
        bool skalieren = gesichert.Breite > 0 && gesichert.Hoehe > 0 && breite > 0 && hoehe > 0
                         && (gesichert.Breite != breite || gesichert.Hoehe != hoehe);
        var vergeben = new bool[jetzt.Count];
        var zuordnung = new List<IconZiel>();
        var fehlend = new List<DesktopIcon>();

        int Finde(Func<DesktopIcon, bool> passt)
        {
            for (int i = 0; i < jetzt.Count; i++)
                if (!vergeben[i] && passt(jetzt[i])) return i;
            return -1;
        }

        // Erst alle ueber den eindeutigen Namen, dann den Rest ueber den
        // angezeigten - sonst schnappte ein Anzeigename einem spaeteren
        // Symbol seinen eigentlichen Partner weg.
        var offen = new List<DesktopIcon>();
        var treffer = new Dictionary<DesktopIcon, int>(ReferenceEqualityComparer.Instance);
        foreach (var s in gesichert.Symbole)
        {
            int i = Finde(j => string.Equals(j.Name, s.Name, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) { vergeben[i] = true; treffer[s] = i; } else offen.Add(s);
        }
        foreach (var s in offen)
        {
            int i = string.IsNullOrWhiteSpace(s.Anzeige) ? -1
                : Finde(j => string.Equals(j.Anzeige, s.Anzeige, StringComparison.OrdinalIgnoreCase));
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

    // ------------------------------------------------------------ Ablauf

    /// <summary>Liest die aktuelle Anordnung des Desktops.</summary>
    public static DesktopLayout Lesen(string rechner, string benutzer) => ImEigenenThread(() =>
    {
        using var desktop = OeffneDesktop();
        var (b, h) = desktop.Groesse();
        return new DesktopLayout
        {
            Rechner = rechner,
            Benutzer = benutzer,
            Erstellt = DateTime.Now,
            Breite = b,
            Hoehe = h,
            AutomatischAnordnen = desktop.AutomatischAnordnen,
            AmRasterAusrichten = desktop.AmRasterAusrichten,
            Symbole = desktop.Symbole().Where(s => s.Name.Length > 0).ToList(),
        };
    });

    /// <summary>
    /// Setzt die gesicherten Symbole an ihre Stelle.
    ///
    /// Fehlen noch Symbole, wird bis <paramref name="warten"/> darauf gewartet:
    /// Direkt nach dem Zurueckschreiben des Profils braucht der Explorer einen
    /// Moment, bis die neuen Dateien auf dem Desktop erscheinen.
    /// </summary>
    public static IconErgebnis Wiederherstellen(DesktopLayout gesichert, TimeSpan warten) => ImEigenenThread(() =>
    {
        using var desktop = OeffneDesktop();
        var (b, h) = desktop.Groesse();
        var plan = Plan(gesichert, desktop.Symbole(), b, h);

        var uhr = Stopwatch.StartNew();
        int ohneFortschritt = 0;
        while (plan.Fehlend.Count > 0 && uhr.Elapsed < warten && ohneFortschritt < 4)
        {
            Thread.Sleep(1000);
            var neu = Plan(gesichert, desktop.Symbole(), b, h);
            ohneFortschritt = neu.Fehlend.Count < plan.Fehlend.Count ? 0 : ohneFortschritt + 1;
            plan = neu;
        }

        // Mit "Automatisch anordnen" setzt Windows jedes Symbol sofort wieder
        // an seinen Platz in der Reihe - Positionen haetten keine Wirkung.
        bool anordnenAus = false;
        if (desktop.AutomatischAnordnen) { desktop.AutomatischAnordnen = false; anordnenAus = true; }
        // Raster wie beim Sichern: Sonst rueckten frei platzierte Symbole auf
        // das Raster, oder ausgerichtete laegen leicht daneben.
        bool raster = false;
        if (desktop.AmRasterAusrichten != gesichert.AmRasterAusrichten)
        {
            desktop.AmRasterAusrichten = gesichert.AmRasterAusrichten;
            raster = true;
        }

        int gesetzt = 0;
        foreach (var z in plan.Zuordnung)
        {
            try { desktop.Setzen(z.Index, z.X, z.Y); gesetzt++; }
            catch { /* einzelnes Symbol - die anderen trotzdem */ }
        }

        // War beim Sichern "Automatisch anordnen" an, ordnet Windows selbst -
        // dann auch hier wieder so.
        bool wiederAn = false;
        if (gesichert.AutomatischAnordnen) { desktop.AutomatischAnordnen = true; wiederAn = true; }

        return new IconErgebnis(gesetzt, gesichert.Symbole.Count, plan.Fehlend, plan.Skaliert, b, h,
                                anordnenAus && !wiederAn, raster, wiederAn);
    });

    /// <summary>
    /// COM-Objekte des Explorers gehoeren in einen STA-Thread, und die Masse
    /// sollen echte Bildpunkte sein (nicht auf 100 % umgerechnet).
    /// </summary>
    private static T ImEigenenThread<T>(Func<T> arbeit)
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
/// Der Desktop des Explorers, angesprochen wie in Raymond Chens Beispiel
/// "Manipulating the positions of desktop icons": ShellWindows liefert das
/// Desktop-Fenster, dessen Browser die aktive Ansicht, und die Ansicht ist
/// ein IFolderView - mit GetItemPosition und SelectAndPositionItems.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ExplorerDesktop : IDesktopView
{
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
                throw new InvalidOperationException($"Kein Explorer-Desktop gefunden (0x{hr:X8}) - läuft der Explorer?");

            var sid = SID_STopLevelBrowser;
            var iidBrowser = typeof(IShellBrowser).GUID;
            Pruefe(((IComServiceProvider)dispatch).QueryService(ref sid, ref iidBrowser, out var browser), "QueryService");
            Pruefe(((IShellBrowser)browser).QueryActiveShellView(out var ansicht), "QueryActiveShellView");

            _ansicht = (IFolderView2)ansicht;
            Pruefe(((IShellView)ansicht).GetWindow(out _fenster), "GetWindow");
            var iidOrdner = typeof(IShellFolder).GUID;
            Pruefe(_ansicht.GetFolder(ref iidOrdner, out var ordner), "GetFolder");
            _ordner = (IShellFolder)ordner;
        }
        finally
        {
            if (shellWindows is not null) Marshal.ReleaseComObject(shellWindows);
        }
    }

    public (int Breite, int Hoehe) Groesse()
        => GetClientRect(_fenster, out var r) ? (r.Right - r.Left, r.Bottom - r.Top) : (0, 0);

    public bool AutomatischAnordnen
    {
        get => (Flags() & FWF_AUTOARRANGE) != 0;
        set => Pruefe(_ansicht.SetCurrentFolderFlags(FWF_AUTOARRANGE, value ? FWF_AUTOARRANGE : 0), "SetCurrentFolderFlags");
    }

    public bool AmRasterAusrichten
    {
        get => (Flags() & FWF_SNAPTOGRID) != 0;
        set => Pruefe(_ansicht.SetCurrentFolderFlags(FWF_SNAPTOGRID, value ? FWF_SNAPTOGRID : 0), "SetCurrentFolderFlags");
    }

    private uint Flags()
    {
        Pruefe(_ansicht.GetCurrentFolderFlags(out var f), "GetCurrentFolderFlags");
        return f;
    }

    public IReadOnlyList<DesktopIcon> Symbole()
    {
        FreiePidls();
        Pruefe(_ansicht.ItemCount(SVGIO_ALLVIEW, out var anzahl), "ItemCount");
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

    public void Setzen(int index, int x, int y)
    {
        var pidl = _pidls[index];
        if (pidl == IntPtr.Zero) throw new InvalidOperationException("Symbol ohne Kennung.");
        Pruefe(_ansicht.SelectAndPositionItems(1, new[] { pidl }, new[] { new POINT { X = x, Y = y } }, SVSI_POSITIONITEM),
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

    private void FreiePidls()
    {
        foreach (var p in _pidls) if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p);
        _pidls.Clear();
    }

    public void Dispose()
    {
        FreiePidls();
        try { Marshal.ReleaseComObject(_ordner); } catch { }
        try { Marshal.ReleaseComObject(_ansicht); } catch { }
    }

    private static void Pruefe(int hr, string was)
    {
        if (hr < 0) throw new COMException($"{was} fehlgeschlagen (0x{hr:X8}).", hr);
    }

    /// <summary>Der aufrufende Thread rechnet in echten Bildpunkten (pro Monitor).</summary>
    internal static void PerMonitorDpi()
    {
        try { SetThreadDpiAwarenessContext(new IntPtr(-4)); } catch { /* aeltere Windows-Fassung */ }
    }

    // ------------------------------------------------------------ Windows-Schnittstellen

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
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("shlwapi.dll")] private static extern int StrRetToStrW(IntPtr pstr, IntPtr pidl, out IntPtr ppsz);

    // Platzhalter (_n) halten die Reihenfolge der Methoden ein; aufgerufen
    // werden nur die ausgeschriebenen.

    /// <summary>IShellWindows - ein duales Interface: nach IUnknown kommen die vier von IDispatch.</summary>
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

    /// <summary>IFolderView2 - mit allen Methoden von IFolderView davor.</summary>
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
