using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

// Scenario: lines "key=value"; "\n" in a value stands for a line break.
// These keys are evaluated (a missing key means the default):
//   version                 output of --version              (v1.12.460)
//   version.code            exit code of --version           (0)
//   source list             output of "source list"          ("winget  https://cdn.winget.microsoft.com/cache")
//   source list.code        exit code                        (0)
//   source reset.code / source update.code                   (0)
//   source list.warte       wait this many milliseconds      (0)
//   export.datei            file copied to -o                (none -> no file)
//   export.ausgabe / export.code
//   show.<Id>.code          (1 = unknown)
//   install.<Id>.code / install.<Id>.ausgabe                 (0)
//   install.*.code          for every other Id
var ordner = Environment.GetEnvironmentVariable("DASI_WINGET_FAKE");
if (string.IsNullOrEmpty(ordner)) { Console.Error.WriteLine("DASI_WINGET_FAKE fehlt"); return 99; }

var szenario = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
var datei = Path.Combine(ordner, "scenario.txt");
if (File.Exists(datei))
    foreach (var zeile in File.ReadAllLines(datei))
    {
        var i = zeile.IndexOf('=');
        if (i > 0) szenario[zeile[..i].Trim()] = zeile[(i + 1)..].Replace("\\n", "\n");
    }

try { File.AppendAllText(Path.Combine(ordner, "calls.txt"), string.Join(" ", args) + Environment.NewLine); } catch { }

string Value(string k, string vorgabe) => szenario.TryGetValue(k, out var v) ? v : vorgabe;
int Code(string k, int vorgabe) => szenario.TryGetValue(k, out var v) && int.TryParse(v, out var c) ? c : vorgabe;
string? ValueAfter(string schalter) { var i = Array.IndexOf(args, schalter); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }

var befehl = args.Length == 0 ? "" : args[0];
switch (befehl)
{
    case "--version":
        Console.WriteLine(Value("version", "v1.12.460"));
        return Code("version.code", 0);

    case "source":
        var was = "source " + (args.Length > 1 ? args[1] : "");
        System.Threading.Thread.Sleep(Code(was + ".warte", 0));
        if (was == "source list")
            Console.WriteLine(Value("source list", "Name   Argument\n-----------------------------------------\nwinget https://cdn.winget.microsoft.com/cache"));
        return Code(was + ".code", 0);

    case "export":
        var ziel = ValueAfter("-o");
        if (ziel is not null && szenario.TryGetValue("export.datei", out var quelle)) File.Copy(quelle, ziel, true);
        Console.WriteLine(Value("export.ausgabe", ""));
        return Code("export.code", 0);

    case "show":
        return Code("show." + ValueAfter("--id") + ".code", 1);

    case "install":
        var id = ValueAfter("--id") ?? "";
        Console.WriteLine(Value("install." + id + ".ausgabe", Value("install.*.ausgabe", "")));
        return Code("install." + id + ".code", Code("install.*.code", 0));
}
Console.Error.WriteLine("Unbekannter Aufruf: " + string.Join(" ", args));
return 98;
