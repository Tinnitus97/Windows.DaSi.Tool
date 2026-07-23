using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using WindowsDaSiTool.Services;

namespace WindowsDaSiTool.Views;

/// <summary>
/// Kleiner Ersatz fuer System.Windows.MessageBox mit Catppuccin-Optik.
/// Wird immer vom UI-Thread aus aufgerufen.
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
            Foreground = new SolidColorBrush(Color.Parse("#CDD6F4")),
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
            Background = new SolidColorBrush(Color.Parse("#1E1E2E"))
        };

        if (yesNo)
        {
            var yes = MakeButton(Loc.Tr("Ja","Yes"), "#A6E3A1", "#11111B");
            var no = MakeButton(Loc.Tr("Nein","No"), "#313244", "#CDD6F4");
            yes.Click += (_, _) => { tcs.TrySetResult(true); dialog.Close(); };
            no.Click += (_, _) => { tcs.TrySetResult(false); dialog.Close(); };
            buttons.Children.Add(no);
            buttons.Children.Add(yes);
        }
        else
        {
            var ok = MakeButton("OK", "#89B4FA", "#11111B");
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

    private static Button MakeButton(string content, string bg, string fg) => new()
    {
        Content = content,
        Width = 90,
        Height = 32,
        Background = new SolidColorBrush(Color.Parse(bg)),
        Foreground = new SolidColorBrush(Color.Parse(fg)),
        HorizontalContentAlignment = HorizontalAlignment.Center,
        CornerRadius = new Avalonia.CornerRadius(5)
    };
}
