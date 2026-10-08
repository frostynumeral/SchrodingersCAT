using Avalonia.Controls;
using Avalonia.Interactivity;
using SchrodingersCat.App.ViewModels;
using SchrodingersCat.Core.Checks;

namespace SchrodingersCat.App.Views;

/// <summary>Which quality checks run. Closing it any other way than OK (title bar, Escape) returns default, which is false.</summary>
public partial class QaSettingsDialog : Window
{
    // For the XAML previewer.
    public QaSettingsDialog()
        : this(new QaSettingsViewModel(QaOptions.Default))
    {
    }

    public QaSettingsDialog(QaSettingsViewModel settings)
    {
        InitializeComponent();
        DataContext = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
