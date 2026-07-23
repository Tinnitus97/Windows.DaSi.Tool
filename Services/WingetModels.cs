using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace WindowsDaSiTool.Services;

public sealed class WingetPackage
{
    [JsonPropertyName("PackageIdentifier")]
    public string PackageIdentifier { get; set; } = "";

    [JsonPropertyName("Version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; set; }
}

public sealed class WingetSourceDetails
{
    [JsonPropertyName("Argument")]  public string Argument { get; set; } = "https://cdn.winget.microsoft.com/cache";
    [JsonPropertyName("Identifier")] public string Identifier { get; set; } = "Microsoft.Winget.Source_8wekyb3d8bbwe";
    [JsonPropertyName("Name")]       public string Name { get; set; } = "winget";
    [JsonPropertyName("Type")]       public string Type { get; set; } = "Microsoft.PreIndexed.Package";
}

public sealed class WingetSource
{
    [JsonPropertyName("Packages")]      public List<WingetPackage> Packages { get; set; } = new();
    [JsonPropertyName("SourceDetails")] public WingetSourceDetails SourceDetails { get; set; } = new();
}

public sealed class WingetExport
{
    [JsonPropertyName("$schema")]      public string Schema { get; set; } = "https://aka.ms/winget-packages.schema.json";
    [JsonPropertyName("CreationDate")] public string CreationDate { get; set; } = "";
    [JsonPropertyName("Sources")]      public List<WingetSource> Sources { get; set; } = new();
}
