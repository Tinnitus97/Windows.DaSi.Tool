using System;

namespace WindowsDaSiTool.Services;

/// <summary>
/// Entscheidet, ob eine Protokollzeile einen Fehlschlag meldet.
///
/// Eigene Datei, damit die Regel ohne Oberflaeche pruefbar ist - von ihr
/// haengt ab, ob eine Aktion in Bilanz und Protokolldatei als [OK] oder als
/// [FEHLER] erscheint.
/// </summary>
public static class ActionOutcome
{
    /// <summary>
    /// true fuer jede Zeile, die ein Dienst mit "[FEHLER]" bzw. "[ERROR]"
    /// kennzeichnet - auch eingerueckt ("    -> [FEHLER] ...") oder mit
    /// vorangestelltem Zeilenumbruch.
    ///
    /// Zeilen aus der Ausgabe von robocopy selbst ("FEHLER 5 (0x00000005)
    /// ...") tragen keine eckigen Klammern und zaehlen bewusst NICHT: Einzelne
    /// gesperrte Dateien sind bei einem Profil normal. Scheitert robocopy
    /// wirklich, meldet der Dienst den Fehlercode ohnehin selbst mit
    /// "[FEHLER]".
    /// </summary>
    public static bool IsErrorLine(string? text)
        => !string.IsNullOrEmpty(text)
           && (text.Contains("[FEHLER]", StringComparison.Ordinal)
               || text.Contains("[ERROR]", StringComparison.Ordinal));
}
