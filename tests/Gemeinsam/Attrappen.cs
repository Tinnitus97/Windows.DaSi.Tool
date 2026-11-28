using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DaSiPruefung;

/// <summary>Gemeinsame Hilfen beider Pruefprojekte.</summary>
internal static class Attrappen
{
    /// <summary>Der Ordner des Repositories (dort liegt WindowsDaSiTool.csproj).</summary>
    public static string Repository()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "WindowsDaSiTool.csproj"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("Repository nicht gefunden.");
    }

    /// <summary>Die gebaute winget-Attrappe (tests/WingetAttrappe) - null, wenn sie nicht gebaut ist.</summary>
    public static string? WingetFake()
    {
        var basis = Path.Combine(Repository(), "tests", "WingetAttrappe", "bin");
        foreach (var konfig in new[] { "Release", "Debug" })
            foreach (var name in new[] { "winget.exe", "winget" })
            {
                var p = Path.Combine(basis, konfig, "net10.0-windows", name);
                if (File.Exists(p)) return p;
            }
        return null;
    }

    /// <summary>
    /// Richtet ein Szenario fuer die Attrappe ein: neuer Ordner, szenario.txt,
    /// Umgebungsvariable fuer die gestarteten Prozesse.
    /// </summary>
    public static string WingetScenario(string wurzel, IDictionary<string, string> werte)
    {
        var ordner = Path.Combine(wurzel, "winget-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(ordner);
        WriteScenario(ordner, werte);
        Environment.SetEnvironmentVariable("DASI_WINGET_ATTRAPPE", ordner);
        return ordner;
    }

    public static void WriteScenario(string ordner, IDictionary<string, string> werte)
        => File.WriteAllLines(Path.Combine(ordner, "szenario.txt"),
                              werte.Select(kv => kv.Key + "=" + kv.Value.Replace("\n", "\\n")));

    /// <summary>Was die Attrappe bisher gefragt wurde - je Aufruf eine Zeile.</summary>
    public static List<string> Calls(string ordner)
    {
        var p = Path.Combine(ordner, "aufrufe.txt");
        return File.Exists(p) ? File.ReadAllLines(p).ToList() : new List<string>();
    }
}
