using Avalonia.Controls;
using SchrodingersCat.App.ViewModels;

namespace SchrodingersCat.App.Views;

public partial class MainWindow : Window
{
    private bool _closeConfirmed;

    public MainWindow() => InitializeComponent();

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
