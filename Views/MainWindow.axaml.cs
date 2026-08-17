using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WindowsDaSiTool.ViewModels;

namespace WindowsDaSiTool.Views;

public partial class MainWindow : Window
{
    private ScrollViewer? _logScroll;
    private SelectableTextBlock? _logText;

    // true = Log folgt automatisch ans Ende. Wird beim manuellen Hochscrollen
    // ausgeschaltet und beim Zurueckscrollen ans Ende wieder eingeschaltet.
    private bool _autoScroll = true;
    private bool _programScroll;

    public MainWindow()
    {
        InitializeComponent();

        _logScroll = this.FindControl<ScrollViewer>("LogScroll");
        _logText = this.FindControl<SelectableTextBlock>("TBLog");

        if (_logScroll is not null)
        {
            // Nutzer-Scrollen erkennen: Offset-Aenderung, die NICHT von uns kommt.
            _logScroll.PropertyChanged += (_, ev) =>
            {
                if (ev.Property != ScrollViewer.OffsetProperty) return;
                if (_programScroll) return;

                var sv = _logScroll!;
                var distanceFromBottom = sv.Extent.Height - (sv.Offset.Y + sv.Viewport.Height);
                _autoScroll = distanceFromBottom <= 12;
            };

            // Mausrad nach oben stoppt das automatische Nachspringen sofort.
            _logScroll.AddHandler(PointerWheelChangedEvent, (_, e) =>
            {
                if (e.Delta.Y > 0) _autoScroll = false;
            }, RoutingStrategies.Tunnel);
        }

        // Bei jeder neuen Log-Zeile ans Ende springen - nur wenn Auto-Scroll aktiv.
        if (_logText is not null)
        {
            _logText.PropertyChanged += (_, e) =>
            {
                if (e.Property != TextBlock.TextProperty) return;

                Dispatcher.UIThread.Post(() =>
                {
                    var sv = _logScroll;
                    if (sv is null || !_autoScroll) return;
                    _programScroll = true;
                    sv.ScrollToEnd();
                    _programScroll = false;
                }, DispatcherPriority.Background);
            };
        }

        Closing += OnWindowClosing;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private bool _forceClose;

    private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        try
        {
            if (DataContext is not MainWindowViewModel vm) return;
            if (vm.UiEnabled || _forceClose) return;

            // Es laeuft noch eine Aktion -> Rueckfrage
            e.Cancel = true;
            var confirmed = await MessageBox.ShowYesNo(this,
                WindowsDaSiTool.Services.Loc.Tr("Abbruch bestätigen", "Confirm cancellation"),
                WindowsDaSiTool.Services.Loc.Tr(
                    "Es läuft gerade eine Aktion!\nSoll diese wirklich hart abgebrochen und das Programm beendet werden?",
                    "An action is currently running!\nDo you really want to abort it and close the program?"));

            if (confirmed)
            {
                vm.RequestCancelAndKill();
                _forceClose = true;
                Close();
            }
        }
        catch (Exception ex)
        {
            // Ein Fehler beim Schliessen darf die App nicht mit einem Crash beenden.
            Program.WriteCrashLog("OnWindowClosing", ex);
            _forceClose = true;
            try { Close(); } catch { }
        }
    }
}
