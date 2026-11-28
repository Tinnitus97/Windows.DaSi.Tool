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

    // true = the log follows to the end automatically. Switched off when the
    // user scrolls up, switched on again when scrolled back to the end.
    private bool _autoScroll = true;
    private bool _programScroll;

    public MainWindow()
    {
        InitializeComponent();

        _logScroll = this.FindControl<ScrollViewer>("LogScroll");
        _logText = this.FindControl<SelectableTextBlock>("TBLog");

        if (_logScroll is not null)
        {
            // Detect user scrolling: an offset change that does NOT come from us.
            _logScroll.PropertyChanged += (_, ev) =>
            {
                if (ev.Property != ScrollViewer.OffsetProperty) return;
                if (_programScroll) return;

                var sv = _logScroll!;
                var distanceFromBottom = sv.Extent.Height - (sv.Offset.Y + sv.Viewport.Height);
                _autoScroll = distanceFromBottom <= 12;
            };

            // Mouse wheel up stops the automatic jump to the end right away.
            _logScroll.AddHandler(PointerWheelChangedEvent, (_, e) =>
            {
                if (e.Delta.Y > 0) _autoScroll = false;
            }, RoutingStrategies.Tunnel);
        }

        // Jump to the end on every new log line - only while auto-scroll is on.
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

            // Ask ONLY if an action is really running. This used to check
            // "!vm.UiEnabled" - but the UI is also locked during the self-update. Then
            // closing asked about a hard abort although nothing was running; a "No"
            // kept the window open while the swap script was already waiting for it
            // to end.
            if (!vm.IsActionRunning || _forceClose) return;

            // An action is still running -> ask
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
            // An error while closing must not end the app with a crash.
            Program.WriteCrashLog("OnWindowClosing", ex);
            _forceClose = true;
            try { Close(); } catch { }
        }
    }
}
