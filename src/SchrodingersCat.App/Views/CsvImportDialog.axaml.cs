using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
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

    private void OnImport(object? sender, RoutedEventArgs e)
    {
        if (((CsvImportViewModel)DataContext!).TryImport() is { } import)
            Close(import);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
