using System;

namespace WindowsDaSiTool.Services;

public enum AppLang { De, En }

/// <summary>
/// Central language handling. Holds the current language and returns the
/// matching text through Tr(de, en). Changes are announced by an event, so
/// the UI can update its labels.
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

    /// <summary>Raised on every language change.</summary>
    public event Action? LanguageChanged;

    /// <summary>Picks the German or the English text depending on the current language.</summary>
    public string Tr(string de, string en) => _lang == AppLang.En ? en : de;
}

/// <summary>Convenient static access: Loc.Tr("...", "...").</summary>
public static class Loc
{
    public static string Tr(string de, string en) => Localizer.I.Tr(de, en);
}
