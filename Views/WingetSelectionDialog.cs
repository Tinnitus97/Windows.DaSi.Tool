using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using WindowsDaSiTool.Services;

namespace WindowsDaSiTool.Views;

/// <summary>
/// Auswahlfenster fuer den Winget-Import (entspricht Show-WingetImportSelection).
/// Liefert die vom Benutzer ausgewaehlten Pakete zurueck, oder null bei Abbruch.
/// Die Farben kommen aus den Theme-Stilen (App.axaml, "dialog") und folgen
/// damit dem hellen bzw. dunklen Modus.
/// </summary>
public static class WingetSelectionDialog
{
    public static async Task<List<WingetPackage>?> Show(Window owner, IReadOnlyList<WingetPackage> packages)
    {
        var tcs = new TaskCompletionSource<List<WingetPackage>?>();

        var checkBoxes = new List<CheckBox>();
        var stack = new StackPanel { Spacing = 3 };

        foreach (var pkg in packages)
        {
            var cb = new CheckBox
            {
                Content = string.IsNullOrWhiteSpace(pkg.PackageIdentifier) ? Loc.Tr("Unbekanntes Paket","Unknown package") : pkg.PackageIdentifier,
                IsChecked = true,
                Classes = { "dialogItem" },
                Tag = pkg
            };
            checkBoxes.Add(cb);
            stack.Children.Add(cb);
        }

        var scroll = new Border
        {
            Classes = { "dialogList" },
            Child = new ScrollViewer
            {
                Content = stack,
                Padding = new Avalonia.Thickness(8),
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
            }
        };

        var btnAll = MakeSmall(Loc.Tr("Alle","All"));
        var btnNone = MakeSmall(Loc.Tr("Keine","None"));
        btnAll.Click += (_, _) => { foreach (var cb in checkBoxes) cb.IsChecked = true; };
        btnNone.Click += (_, _) => { foreach (var cb in checkBoxes) cb.IsChecked = false; };

        var btnCancel = MakeSmall(Loc.Tr("Abbrechen","Cancel"), "cancel");
        var btnInstall = MakeSmall(Loc.Tr("Installieren","Install"), "yes");
        btnInstall.Width = 110;
        btnCancel.Width = 100;

        var dialog = new Window
        {
            Title = Loc.Tr("Winget-Paketauswahl","Winget package selection"),
            Width = 550,
            Height = 600,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Classes = { "dialog" }
        };

        btnCancel.Click += (_, _) => { tcs.TrySetResult(null); dialog.Close(); };
        btnInstall.Click += (_, _) =>
        {
            var selected = checkBoxes
                .Where(cb => cb.IsChecked == true)
                .Select(cb => (WingetPackage)cb.Tag!)
                .ToList();
            tcs.TrySetResult(selected);
            dialog.Close();
        };

        var leftButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        leftButtons.Children.Add(btnAll);
        leftButtons.Children.Add(btnNone);

        var rightButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 5,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        rightButtons.Children.Add(btnCancel);
        rightButtons.Children.Add(btnInstall);

        var bottomBar = new Grid { Margin = new Avalonia.Thickness(0, 15, 0, 0) };
        bottomBar.ColumnDefinitions = new ColumnDefinitions("*,Auto");
        Grid.SetColumn(leftButtons, 0);
        Grid.SetColumn(rightButtons, 1);
        bottomBar.Children.Add(leftButtons);
        bottomBar.Children.Add(rightButtons);

        var root = new Grid { Margin = new Avalonia.Thickness(15) };
        root.RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto");

        var title = new TextBlock
        {
            Text = Loc.Tr("Winget Wiederherstellungsauswahl","Winget restore selection"),
            Classes = { "dialogTitle" },
            Margin = new Avalonia.Thickness(0, 0, 0, 5)
        };
        var subtitle = new TextBlock
        {
            Text = Loc.Tr("Wähle die Programme, die installiert werden sollen:","Select the programs to install:"),
            Classes = { "dialogText" },
            FontSize = 12, Margin = new Avalonia.Thickness(0, 0, 0, 10)
        };
        Grid.SetRow(title, 0);
        Grid.SetRow(subtitle, 1);
        Grid.SetRow(scroll, 2);
        Grid.SetRow(bottomBar, 3);
        root.Children.Add(title);
        root.Children.Add(subtitle);
        root.Children.Add(scroll);
        root.Children.Add(bottomBar);

        dialog.Content = root;
        dialog.Closed += (_, _) => tcs.TrySetResult(null);

        await dialog.ShowDialog(owner);
        return await tcs.Task;
    }

    /// <param name="art">"yes", "cancel" oder nichts (neutral) - siehe Button.dialog in App.axaml.</param>
    private static Button MakeSmall(string content, string? art = null)
    {
        var b = new Button { Content = content, Width = 70, Height = 30, CornerRadius = new Avalonia.CornerRadius(4), Classes = { "dialog" } };
        if (art is not null) b.Classes.Add(art);
        return b;
    }
}
