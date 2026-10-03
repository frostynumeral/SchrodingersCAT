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

    // The segment shortcuts (design section 4) are seen before the editor: AvaloniaEdit would take Ctrl+Up/Down,
    // Ctrl+Enter and Ctrl+Insert for itself, so they can't be window key bindings, which only get unhandled keys.
    private void OnShortcut(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
            return;

        ICommand? command = (e.Key, e.KeyModifiers) switch
        {
            (Key.Enter, KeyModifiers.Control) => viewModel.ConfirmSegmentCommand,
            (Key.Down, KeyModifiers.Control) => viewModel.NextSegmentCommand,
            (Key.Up, KeyModifiers.Control) => viewModel.PreviousSegmentCommand,
            (Key.Down, KeyModifiers.Control | KeyModifiers.Shift) => viewModel.NextSegmentWithIssuesCommand,
            (Key.Insert, KeyModifiers.Control) => viewModel.CopySourceCommand,
            _ => null,
        };
        if (command is null)
            return;

        e.Handled = true;
        if (command.CanExecute(null))
            command.Execute(null);
    }

    // Closing is synchronous, but asking about unsaved changes is not: cancel this close, ask, and close again
    // (for real this time) only if the view model allows it.
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closeConfirmed || e.Cancel || DataContext is not MainWindowViewModel { IsDirty: true } viewModel)
            return;

        e.Cancel = true;
        _ = ConfirmAndCloseAsync(viewModel);
    }

    private async Task ConfirmAndCloseAsync(MainWindowViewModel viewModel)
    {
        if (!await viewModel.ConfirmCloseAsync())
            return;

        _closeConfirmed = true;
        Close();
    }
}
