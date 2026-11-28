using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using WindowsDaSiTool.Services;

namespace WindowsDaSiTool.Views;

/// <summary>
/// Selection of the folders under C:\ - laid out like the winget selection.
/// At the top the box "Windows-Standardordner ausblenden" (hide Windows
/// default folders, checked from the start): Windows, Program Files, Users
/// &amp; co. only show up once it is unchecked. Next to each folder its size
/// appears as soon as it is calculated.
/// "Übernehmen" (Apply) only pre-selects the choice - the backup only runs
/// with "Ausgewählte Aktionen starten" (Start selected actions) in the main
/// window.
/// Returns the chosen folders, or null when cancelled.
/// </summary>
public static class OrdnerAuswahlDialog
{
    /// <param name="vorgewaehlt">The folders already pre-selected (names) - empty: the default.</param>
    public static async Task<List<LaufwerksOrdner.Eintrag>?> Show(Window owner, IReadOnlyList<LaufwerksOrdner.Eintrag> eintraege, bool wiederherstellen,
                                                                  IReadOnlyCollection<string>? vorgewaehlt = null)
    {
        var vorher = new HashSet<string>(vorgewaehlt ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var tcs = new TaskCompletionSource<List<LaufwerksOrdner.Eintrag>?>();
        var stop = new CancellationTokenSource();
        var wurzel = LaufwerksOrdner.Wurzel;

        var zeilen = new List<Zeile>();
        var stack = new StackPanel { Spacing = 3 };
        foreach (var e in eintraege)
        {
            var groesse = new TextBlock { Text = "", Classes = { "dialogText" }, FontSize = 11, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center, Margin = new Avalonia.Thickness(12, 0, 0, 0) };
            var name = new TextBlock
            {
                Text = e.Windows ? e.Name + Loc.Tr("  (Windows)", "  (Windows)") : e.Name,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
            };
            var inhalt = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Grid.SetColumn(groesse, 1);
            inhalt.Children.Add(name);
            inhalt.Children.Add(groesse);
            var cb = new CheckBox
            {
                Content = inhalt,
                // Already pre-selected: checked again. Otherwise on restore everything
                // backed up is pre-selected, on backup nothing: the user decides what goes
                // along.
                IsChecked = vorher.Count > 0 ? vorher.Contains(e.Name) : wiederherstellen && !e.Windows,
                Classes = { "dialogItem" },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Tag = e,
            };
            ToolTip.SetTip(cb, wiederherstellen ? $"{wurzel}{e.Name}" : e.Pfad);
            zeilen.Add(new Zeile(cb, e, groesse));
            stack.Children.Add(cb);
        }
        var leer = new TextBlock
        {
            Text = Loc.Tr($"Unter {wurzel} gibt es außer den Windows-Ordnern keine Ordner.", $"Apart from the Windows folders there are no folders under {wurzel}."),
            Classes = { "dialogText" }, FontSize = 12, Opacity = 0.7, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };
        stack.Children.Add(leer);

        var liste = new Border
        {
            Classes = { "dialogList" },
            Child = new ScrollViewer
            {
                Content = stack,
                Padding = new Avalonia.Thickness(8),
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
            }
        };

        var ausblenden = new CheckBox
        {
            Content = Loc.Tr("Windows-Standardordner ausblenden (Windows, Programme, Benutzer …)",
                             "Hide the standard Windows folders (Windows, Program Files, Users …)"),
            // If a Windows folder is already pre-selected, it stays visible.
            IsChecked = !eintraege.Any(e => e.Windows && vorher.Contains(e.Name)),
            Classes = { "dialogItem" },
            Margin = new Avalonia.Thickness(0, 0, 0, 8),
        };
        ToolTip.SetTip(ausblenden, string.Join(", ", LaufwerksOrdner.WindowsOrdner.OrderBy(n => n)) + Loc.Tr(" und alles mit $ am Anfang", " and everything starting with $"));

        var summe = new TextBlock { Classes = { "dialogText" }, FontSize = 11, Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center, Margin = new Avalonia.Thickness(10, 0, 0, 0) };

        var btnAll = MakeButton(Loc.Tr("Alle", "All"));
        var btnNone = MakeButton(Loc.Tr("Keine", "None"));
        var btnCancel = MakeButton(Loc.Tr("Abbrechen", "Cancel"), "cancel");
        var btnOk = MakeButton(Loc.Tr("Übernehmen", "Apply"), "yes");
        btnOk.Width = 130;
        btnCancel.Width = 100;

        IEnumerable<Zeile> Visible() => zeilen.Where(z => z.Cb.IsVisible);

        void Summarize()
        {
            var gewaehlt = zeilen.Where(z => z.Cb.IsChecked == true).ToList();
            btnOk.IsEnabled = gewaehlt.Count > 0;
            var bekannt = gewaehlt.Where(z => z.Bytes >= 0).Sum(z => z.Bytes);
            summe.Text = gewaehlt.Count == 0 ? Loc.Tr("Nichts gewählt", "Nothing selected")
                : Loc.Tr($"{gewaehlt.Count} gewählt, ca. {SystemHelpers.FormatBytes(bekannt)}{(gewaehlt.Any(z => z.Bytes < 0) ? " …" : "")}",
                         $"{gewaehlt.Count} selected, ~{SystemHelpers.FormatBytes(bekannt)}{(gewaehlt.Any(z => z.Bytes < 0) ? " …" : "")}");
        }

        // Sizes in the background - only for the visible folders, each once.
        void FetchSizes()
        {
            foreach (var z in Visible().Where(z => !z.Gestartet))
            {
                z.Gestartet = true;
                z.Anzeige.Text = "…";
                var zeile = z;
                _ = Task.Run(() => LaufwerksOrdner.SizeOf(zeile.E.Pfad, stop.Token)).ContinueWith(t =>
                {
                    if (stop.IsCancellationRequested || t.Result < 0) return;
                    Dispatcher.UIThread.Post(() =>
                    {
                        zeile.Bytes = t.Result;
                        zeile.Anzeige.Text = SystemHelpers.FormatBytes(t.Result);
                        Summarize();
                    });
                }, TaskScheduler.Default);
            }
        }

        void Filter()
        {
            bool aus = ausblenden.IsChecked == true;
            foreach (var z in zeilen)
            {
                z.Cb.IsVisible = !(aus && z.E.Windows);
                // Hidden entries must not run along unnoticed.
                if (!z.Cb.IsVisible) z.Cb.IsChecked = false;
            }
            leer.IsVisible = !Visible().Any();
            FetchSizes();
            Summarize();
        }

        foreach (var z in zeilen) z.Cb.IsCheckedChanged += (_, _) => Summarize();
        ausblenden.IsCheckedChanged += (_, _) => Filter();
        btnAll.Click += (_, _) => { foreach (var z in Visible()) z.Cb.IsChecked = true; };
        btnNone.Click += (_, _) => { foreach (var z in zeilen) z.Cb.IsChecked = false; };

        var dialog = new Window
        {
            Title = wiederherstellen ? Loc.Tr($"Ordner nach {wurzel} wiederherstellen", $"Restore folders to {wurzel}")
                                     : Loc.Tr($"Ordner unter {wurzel} sichern", $"Back up folders under {wurzel}"),
            Width = 560,
            Height = 600,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Classes = { "dialog" }
        };

        btnCancel.Click += (_, _) => { tcs.TrySetResult(null); dialog.Close(); };
        btnOk.Click += (_, _) =>
        {
            tcs.TrySetResult(zeilen.Where(z => z.Cb.IsVisible && z.Cb.IsChecked == true).Select(z => z.E).ToList());
            dialog.Close();
        };

        var links = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        links.Children.Add(btnAll);
        links.Children.Add(btnNone);
        links.Children.Add(summe);
        var rechts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, HorizontalAlignment = HorizontalAlignment.Right };
        rechts.Children.Add(btnCancel);
        rechts.Children.Add(btnOk);
        var unten = new Grid { Margin = new Avalonia.Thickness(0, 15, 0, 0), ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(rechts, 1);
        unten.Children.Add(links);
        unten.Children.Add(rechts);

        var titel = new TextBlock { Text = dialog.Title, Classes = { "dialogTitle" }, Margin = new Avalonia.Thickness(0, 0, 0, 5) };
        var text = new TextBlock
        {
            Text = wiederherstellen
                ? Loc.Tr($"Wähle die Ordner, die nach {wurzel} zurückgeschrieben werden sollen. Gibt es einen schon, wird er ergänzt - neuere Dateien dort bleiben. "
                         + "\"Übernehmen\" merkt sie vor; los geht es mit \"Ausgewählte Aktionen starten\".",
                         $"Select the folders to write back to {wurzel}. If one already exists, it is added to - newer files there are kept. "
                         + "\"Apply\" selects them; it starts with \"Start selected actions\".")
                : Loc.Tr($"Wähle die Ordner unter {wurzel}, die gesichert werden sollen. \"Übernehmen\" merkt sie vor; gesichert wird mit \"Ausgewählte Aktionen starten\".",
                         $"Select the folders under {wurzel} to back up. \"Apply\" selects them; they are backed up with \"Start selected actions\"."),
            Classes = { "dialogText" }, FontSize = 12, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Avalonia.Thickness(0, 0, 0, 10)
        };

        var root = new Grid { Margin = new Avalonia.Thickness(15), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto") };
        Grid.SetRow(text, 1);
        Grid.SetRow(ausblenden, 2);
        Grid.SetRow(liste, 3);
        Grid.SetRow(unten, 4);
        root.Children.Add(titel);
        root.Children.Add(text);
        root.Children.Add(ausblenden);
        root.Children.Add(liste);
        root.Children.Add(unten);

        dialog.Content = root;
        dialog.Closed += (_, _) => { stop.Cancel(); tcs.TrySetResult(null); };
        Filter();

        await dialog.ShowDialog(owner);
        return await tcs.Task;
    }

    private sealed class Zeile(CheckBox cb, LaufwerksOrdner.Eintrag e, TextBlock anzeige)
    {
        public CheckBox Cb { get; } = cb;
        public LaufwerksOrdner.Eintrag E { get; } = e;
        public TextBlock Anzeige { get; } = anzeige;
        public bool Gestartet { get; set; }
        /// <summary>-1 as long as the size is not known yet.</summary>
        public long Bytes { get; set; } = -1;
    }

    /// <param name="art">"yes", "cancel" or nothing (neutral) - see Button.dialog in App.axaml.</param>
    private static Button MakeButton(string inhalt, string? art = null)
    {
        var b = new Button { Content = inhalt, Width = 70, Height = 30, CornerRadius = new Avalonia.CornerRadius(4), Classes = { "dialog" } };
        if (art is not null) b.Classes.Add(art);
        return b;
    }
}
