using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using WindowsDaSiTool.Services;

namespace WindowsDaSiTool.Views;

/// <summary>
/// Kleiner Ersatz fuer System.Windows.MessageBox mit Catppuccin-Optik.
/// Die Farben kommen aus den Theme-Stilen (App.axaml, "dialog") und folgen
/// damit dem hellen bzw. dunklen Modus. Wird immer vom UI-Thread aus aufgerufen.
/// </summary>
public static class MessageBox
{
    public static async Task ShowInfo(Window owner, string title, string message)
        => await Show(owner, title, message, yesNo: false);

    public static async Task<bool> ShowYesNo(Window owner, string title, string message)
        => await Show(owner, title, message, yesNo: true);

    private static async Task<bool> Show(Window owner, string title, string message, bool yesNo)
    {
        var tcs = new TaskCompletionSource<bool>();

        var text = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Classes = { "dialogText" },
            Margin = new Avalonia.Thickness(0, 0, 0, 16)
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };

        var dialog = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Classes = { "dialog" }
        };

        if (yesNo)
        {
            var yes = MakeButton(Loc.Tr("Ja","Yes"), "yes");
            var no = MakeButton(Loc.Tr("Nein","No"));
            yes.Click += (_, _) => { tcs.TrySetResult(true); dialog.Close(); };
            no.Click += (_, _) => { tcs.TrySetResult(false); dialog.Close(); };
            buttons.Children.Add(no);
            buttons.Children.Add(yes);
        }
        else
        {
            var ok = MakeButton("OK", "ok");
            ok.Click += (_, _) => { tcs.TrySetResult(true); dialog.Close(); };
            buttons.Children.Add(ok);
        }

        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Children = { text, buttons }
        };

        dialog.Closed += (_, _) => tcs.TrySetResult(false);

        await dialog.ShowDialog(owner);
        return await tcs.Task;
    }

    /// <param name="art">"yes", "ok", "cancel" oder nichts (neutral) - siehe Button.dialog in App.axaml.</param>
    private static Button MakeButton(string content, string? art = null)
    {
        var b = new Button { Content = content, Width = 90, Height = 32, Classes = { "dialog" } };
        if (art is not null) b.Classes.Add(art);
        return b;
    }
}
