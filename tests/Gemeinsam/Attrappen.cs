using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DaSiPruefung;

/// <summary>Shared helpers of both test projects.</summary>
internal static class Attrappen
{
    /// <summary>The repository folder (where WindowsDaSiTool.csproj lives).</summary>
    public static string Repository()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "WindowsDaSiTool.csproj"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("Repository nicht gefunden.");
    }

    /// <summary>The built winget fake (tests/WingetAttrappe) - null if it is not built.</summary>
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
    /// Sets up a scenario for the fake: a new folder, szenario.txt, an
    /// environment variable for the started processes.
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

    /// <summary>What the fake has been asked so far - one line per call.</summary>
    public static List<string> Calls(string ordner)
    {
        var p = Path.Combine(ordner, "aufrufe.txt");
        return File.Exists(p) ? File.ReadAllLines(p).ToList() : new List<string>();
    }
}
