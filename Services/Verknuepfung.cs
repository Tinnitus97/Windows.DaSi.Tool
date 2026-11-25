using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Text;

namespace WindowsDaSiTool.Services;

/// <summary>
/// Liest und aendert eine Verknuepfung (.lnk) ueber die Schnittstelle von
/// Windows (IShellLink): Ziel, Arbeitsordner, Symbol und die App-Kennung
/// (AppUserModelID), unter der die Taskleiste das Programm fuehrt.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Verknuepfung
{
    /// <summary>Was in der Verknuepfung steht. Ziel ist leer bei Verknuepfungen auf Shell-Objekte (Datei-Explorer).</summary>
    public sealed record Inhalt(string Ziel, string Arbeitsordner, string Symbol, int SymbolIndex, string? AppId);

    public static Inhalt? Lesen(string lnk)
    {
        try
        {
            var link = (IShellLinkW)new CShellLink();
            try
            {
                ((IPersistFile)link).Load(lnk, 0 /* STGM_READ */);
                var ziel = new StringBuilder(1024);
                link.GetPath(ziel, ziel.Capacity, IntPtr.Zero, 0);
                var ordner = new StringBuilder(1024);
                link.GetWorkingDirectory(ordner, ordner.Capacity);
                var symbol = new StringBuilder(1024);
                link.GetIconLocation(symbol, symbol.Capacity, out var index);
                return new Inhalt(ziel.ToString(), ordner.ToString(), symbol.ToString(), index, AppIdLesen((IPropertyStore)link));
            }
            finally { Marshal.ReleaseComObject(link); }
        }
        catch { return null; }
    }

    /// <summary>
    /// Setzt ein neues Ziel. Arbeitsordner und Symbol ziehen mit, wenn sie auf
    /// den alten Ort zeigten; ebenso die App-Kennung, wenn sie die alte
    /// Installation meinte (Firefox und Thunderbird fuehren den Schluessel ihres
    /// Installationsordners als Kennung).
    /// </summary>
    public static bool ZielSetzen(string lnk, string neuesZiel)
    {
        try
        {
            var link = (IShellLinkW)new CShellLink();
            try
            {
                ((IPersistFile)link).Load(lnk, 2 /* STGM_READWRITE */);
                var altZiel = new StringBuilder(1024);
                link.GetPath(altZiel, altZiel.Capacity, IntPtr.Zero, 0);
                var altOrdner = Path.GetDirectoryName(altZiel.ToString()) ?? "";
                var neuOrdner = Path.GetDirectoryName(neuesZiel) ?? "";

                link.SetPath(neuesZiel);
                var ordner = new StringBuilder(1024);
                link.GetWorkingDirectory(ordner, ordner.Capacity);
                if (ordner.Length == 0 || ProfileExcludes.SameFolder(ordner.ToString(), altOrdner))
                    link.SetWorkingDirectory(neuOrdner);
                var symbol = new StringBuilder(1024);
                link.GetIconLocation(symbol, symbol.Capacity, out var index);
                if (symbol.Length > 0 && !File.Exists(Environment.ExpandEnvironmentVariables(symbol.ToString())))
                    link.SetIconLocation(Path.Combine(neuOrdner, Path.GetFileName(symbol.ToString())) is var s && File.Exists(s) ? s : neuesZiel, index);

                var store = (IPropertyStore)link;
                if (AppIdLesen(store) is { } id && altOrdner.Length > 0
                    && id.Equals(MozillaProfile.Schluessel(altOrdner), StringComparison.OrdinalIgnoreCase))
                    AppIdSetzen(store, MozillaProfile.Schluessel(MozillaProfile.EchterPfad(neuOrdner)));

                ((IPersistFile)link).Save(lnk, true);
                return true;
            }
            finally { Marshal.ReleaseComObject(link); }
        }
        catch { return false; }
    }

    // ------------------------------------------------------------ App-Kennung (System.AppUserModel.ID)

    private static PROPERTYKEY AppIdSchluessel = new() { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };
    private const ushort VT_LPWSTR = 31;

    private static string? AppIdLesen(IPropertyStore store)
    {
        var wert = new PROPVARIANT();
        try
        {
            store.GetValue(ref AppIdSchluessel, out wert);
            return wert.vt == VT_LPWSTR && wert.p != IntPtr.Zero ? Marshal.PtrToStringUni(wert.p) : null;
        }
        catch { return null; }
        finally { PropVariantClear(ref wert); }
    }

    private static void AppIdSetzen(IPropertyStore store, string id)
    {
        var wert = new PROPVARIANT { vt = VT_LPWSTR, p = Marshal.StringToCoTaskMemUni(id) };
        try
        {
            store.SetValue(ref AppIdSchluessel, ref wert);
            store.Commit();
        }
        finally { PropVariantClear(ref wert); }
    }

    /// <summary>Nur fuer die Pruefung: eine Verknuepfung anlegen.</summary>
    internal static void Anlegen(string lnk, string ziel, string? appId = null)
    {
        var link = (IShellLinkW)new CShellLink();
        try
        {
            link.SetPath(ziel);
            link.SetWorkingDirectory(Path.GetDirectoryName(ziel) ?? "");
            link.SetIconLocation(ziel, 0);
            if (appId is not null) AppIdSetzen((IPropertyStore)link, appId);
            ((IPersistFile)link).Save(lnk, true);
        }
        finally { Marshal.ReleaseComObject(link); }
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT pvar);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Explicit)]
    private struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr p;
        [FieldOffset(8)] public long zahl;   // haelt die Groesse (16 Bytes unter 64 Bit)
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    private interface IPropertyStore
    {
        void GetCount(out uint cProps);
        void GetAt(uint iProp, out PROPERTYKEY pkey);
        void GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        void SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
        void Commit();
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
