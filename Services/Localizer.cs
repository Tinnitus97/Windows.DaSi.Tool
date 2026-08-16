using System;

namespace WindowsDaSiTool.Services;

public enum AppLang { De, En }

/// <summary>
/// Zentrale Sprachverwaltung. Haelt die aktuelle Sprache und liefert ueber
/// Tr(de, en) den passenden Text. Aenderungen melden sich per Event, damit
/// die UI ihre Beschriftungen aktualisieren kann.
/// </summary>
public sealed class Localizer
{
    public static Localizer I { get; } = new();

    private AppLang _lang = AppLang.De;

    public AppLang Lang
    {
        get => _lang;
        set
        {
            if (_lang == value) return;
            _lang = value;
            LanguageChanged?.Invoke();
        }
    }

    /// <summary>Ereignis, das bei jedem Sprachwechsel ausgeloest wird.</summary>
    public event Action? LanguageChanged;

    /// <summary>Waehlt je nach aktueller Sprache den deutschen oder englischen Text.</summary>
    public string Tr(string de, string en) => _lang == AppLang.En ? en : de;
}

/// <summary>Bequemer statischer Zugriff: Loc.Tr("...", "...").</summary>
public static class Loc
{
    public static string Tr(string de, string en) => Localizer.I.Tr(de, en);
}
