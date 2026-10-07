using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SchrodingersCat.App.ViewModels;

namespace SchrodingersCat.App.Views;

public partial class MainWindow : Window
{
    private bool _closeConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnShortcut, RoutingStrategies.Tunnel);
    }

    // The segment shortcuts (design section 4) and Ctrl+1 … Ctrl+9 (insert a match) are seen before the editor:
    // AvaloniaEdit would take Ctrl+Up/Down, Ctrl+Enter and Ctrl+Insert for itself, so they can't be window key
    // bindings, which only get unhandled keys.
    private void OnShortcut(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
            return;

        (ICommand? command, object? parameter) = (e.Key, e.KeyModifiers) switch
        {
            (Key.Enter, KeyModifiers.Control) => (viewModel.ConfirmSegmentCommand, null),
            (Key.Down, KeyModifiers.Control) => (viewModel.NextSegmentCommand, null),
            (Key.Up, KeyModifiers.Control) => (viewModel.PreviousSegmentCommand, null),
            (Key.Down, KeyModifiers.Control | KeyModifiers.Shift) => (viewModel.NextSegmentWithIssuesCommand, null),
            (Key.Insert, KeyModifiers.Control) => (viewModel.CopySourceCommand, null),
            (>= Key.D1 and <= Key.D9, KeyModifiers.Control) => (viewModel.InsertMatchCommand, e.Key - Key.D0),
            (>= Key.NumPad1 and <= Key.NumPad9, KeyModifiers.Control) => (viewModel.InsertMatchCommand, e.Key - Key.NumPad0),
            _ => ((ICommand?)null, (object?)null),
        };
        if (command is null)
            return;

        e.Handled = true;
        if (command.CanExecute(parameter))
            command.Execute(parameter);
    }

    // Closing is synchronous, but asking about unsaved changes and closing the TM are not: cancel this close, ask,
    // let the TM finish its queued stores, and close again (for real this time) only if the view model allows it.
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closeConfirmed || e.Cancel || DataContext is not MainWindowViewModel viewModel
            || (!viewModel.IsDirty && viewModel.TranslationMemory is null))
            return;

        e.Cancel = true;
        _ = ConfirmAndCloseAsync(viewModel);
    }

    private async Task ConfirmAndCloseAsync(MainWindowViewModel viewModel)
    {
        if (!await viewModel.ConfirmCloseAsync())
            return;

        await viewModel.ShutDownAsync();
        _closeConfirmed = true;
        Close();
    }
}
