using System;
using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace WindowsDaSiTool.Services;

/// <summary>
/// Liest und schreibt die Werte von <see cref="DesignEinstellungen"/> in der
/// Registry - unter einem hereingereichten Wurzelschluessel, damit es der
/// Zweig des angemeldeten Benutzers sein kann (HKEY_USERS\&lt;SID&gt;) statt
/// des Kontos, unter dem das Tool laeuft.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DesignRegistry
{
    /// <summary>(Typ, Wert) oder null, wenn es den Wert nicht gibt oder sein Typ nicht vorgesehen ist.</summary>
    public static (string Typ, string Wert)? Lesen(RegistryKey wurzel, string schluessel, string name)
    {
        using var k = wurzel.OpenSubKey(schluessel);
        if (k is null) return null;
        RegistryValueKind art;
        try { art = k.GetValueKind(name); }
        catch { return null; }
        // %SystemRoot% & Co. bleiben stehen - aufgeloest wird erst beim Benutzen.
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

    /// <summary>Schreibt einen gesicherten Wert zurueck. Wirft, wenn es nicht geht.</summary>
    public static void Schreiben(RegistryKey wurzel, DesignEintrag e)
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
