using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SchrodingersCat.App.Views;

/// <summary>A yes/no question. Closing it any other way (title bar, Escape) returns default, which is false.</summary>
public partial class ConfirmDialog : Window
{
    // For the XAML previewer.
    public ConfirmDialog()
        : this("Confirm", "Are you sure?", "OK")
    {
    }

    public ConfirmDialog(string title, string message, string confirmLabel)
    {
        InitializeComponent();
        Title = title;
        Message.Text = message;
        ConfirmButton.Content = confirmLabel;
    }

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
