using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace WindowsDaSiTool.Services;

/// <summary>
/// Reads and writes the values of <see cref="DesignEinstellungen"/> in the
/// registry - below a root key passed in, so it can be the key of the
/// signed-in user (HKEY_USERS\&lt;SID&gt;) instead of the account the tool
/// runs under.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DesignRegistry
{
    /// <summary>(type, value) or null if the value does not exist or its type is not supported.</summary>
    public static (string Typ, string Wert)? Read(RegistryKey wurzel, string schluessel, string name)
    {
        using var k = wurzel.OpenSubKey(schluessel);
        if (k is null) return null;
        RegistryValueKind art;
        try { art = k.GetValueKind(name); }
        catch { return null; }
        // %SystemRoot% etc. are kept as they are - they are expanded only when used.
        var v = k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return (art, v) switch
        {
            (RegistryValueKind.DWord, int i) => ("DWord", i.ToString(CultureInfo.InvariantCulture)),
            (RegistryValueKind.String, string s) => ("String", s),
            (RegistryValueKind.ExpandString, string s) => ("ExpandString", s),
            (RegistryValueKind.Binary, byte[] b) => ("Binary", Convert.ToBase64String(b)),
            _ => null,
        };
    }

    /// <summary>What applying the values resulted in.</summary>
    public sealed record Ergebnis(List<DesignEintrag> Gesetzt, List<string> OhneDatei, List<string> NichtSchreibbar)
    {
        public bool Zeiger => Gesetzt.Any(e => e.Gruppe == "Mauszeiger");
    }

    /// <summary>
    /// Writes the backed-up values and notifies Windows and the running
    /// programs. Pointers whose file does not exist here are skipped.
    /// </summary>
    /// <param name="eigenerBenutzer">Does the tool run as the user of <paramref name="wurzel"/>?
    /// Windows loads the mouse pointers from the PROCESS's registry - otherwise only from the next sign-in on.</param>
    public static Ergebnis Apply(RegistryKey wurzel, IEnumerable<DesignEintrag> werte, bool eigenerBenutzer)
    {
        var r = new Ergebnis(new List<DesignEintrag>(), new List<string>(), new List<string>());
        foreach (var e in werte)
        {
            if (DesignEinstellungen.PointerMissing(e, p => File.Exists(Environment.ExpandEnvironmentVariables(p))))
            {
                r.OhneDatei.Add(e.Name);
                continue;
            }
            try { Write(wurzel, e); r.Gesetzt.Add(e); }
            catch { r.NichtSchreibbar.Add(e.Name.Length == 0 ? e.Schluessel : e.Name); }
        }
        if (r.Zeiger && eigenerBenutzer)
            SystemParametersInfo(SPI_SETCURSORS, 0, null, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
        foreach (var bereich in new[] { "ImmersiveColorSet", "WindowsThemeElement", "TraySettings" })
            SendMessageTimeoutW(HWND_BROADCAST, WM_SETTINGCHANGE, UIntPtr.Zero, bereich, SMTO_ABORTIFHUNG, 2000, out _);
        return r;
    }

    private const uint SPI_SETCURSORS = 0x0057, SPIF_UPDATEINIFILE = 0x01, SPIF_SENDCHANGE = 0x02;
    private static readonly IntPtr HWND_BROADCAST = new(0xFFFF);
    private const uint WM_SETTINGCHANGE = 0x001A, SMTO_ABORTIFHUNG = 0x0002;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint uAction, uint uParam, string? lpvParam, uint fuWinIni);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, uint msg, UIntPtr wParam, string lParam,
                                                     uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);

    /// <summary>Writes one backed-up value back. Throws if that is not possible.</summary>
    public static void Write(RegistryKey wurzel, DesignEintrag e)
    {
        using var k = wurzel.CreateSubKey(e.Schluessel, writable: true)
                      ?? throw new InvalidOperationException($"Schlüssel nicht anlegbar: {e.Schluessel}");
        switch (e.Typ)
        {
            case "DWord":
                k.SetValue(e.Name, int.Parse(e.Wert, NumberStyles.Integer, CultureInfo.InvariantCulture), RegistryValueKind.DWord);
                break;
            case "String":
                k.SetValue(e.Name, e.Wert, RegistryValueKind.String);
                break;
            case "ExpandString":
                k.SetValue(e.Name, e.Wert, RegistryValueKind.ExpandString);
                break;
            case "Binary":
                k.SetValue(e.Name, Convert.FromBase64String(e.Wert), RegistryValueKind.Binary);
                break;
            default:
                throw new InvalidOperationException($"Unbekannter Typ: {e.Typ}");
        }
    }
}
