using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using SchrodingersCat.App.Services;
using SchrodingersCat.App.ViewModels;

namespace SchrodingersCat.App.Views;

/// <summary>
/// Asks how to read a CSV file (section 4 of <c>docs/design/termbase.md</c>): its encoding and columns, with its first
/// rows. Returns the import, or null if cancelled, however the window was closed.
/// </summary>
public partial class CsvImportDialog : Window
{
    // For the XAML previewer.
    public CsvImportDialog()
        : this(new CsvImportViewModel(Encoding.UTF8.GetBytes("en;hu\nfile;fájl\n"), "en", "hu", Task.Run))
    {
    }

    public CsvImportDialog(CsvImportViewModel import)
    {
        InitializeComponent();
        DataContext = import ?? throw new ArgumentNullException(nameof(import));
    }

    // The import runs off the UI thread (Import is disabled meanwhile); the dialog closes with it.
    private void OnImport(object? sender, RoutedEventArgs e) => ImportAsync().Forget();

    private async Task ImportAsync()
    {
        if (await ((CsvImportViewModel)DataContext!).ImportAsync() is { } import && IsVisible)
            Close(import);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
