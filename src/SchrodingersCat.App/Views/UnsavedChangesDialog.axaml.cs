using Avalonia.Controls;
using Avalonia.Interactivity;
using SchrodingersCat.App.Services;

namespace SchrodingersCat.App.Views;

/// <summary>
/// Save / Don't save / Cancel. Closing it any other way (title bar, Escape) returns default, which is
/// <see cref="UnsavedChangesChoice.Cancel"/>.
/// </summary>
public partial class UnsavedChangesDialog : Window
{
    // For the XAML previewer.
    public UnsavedChangesDialog()
        : this("file.xlf")
    {
    }

    public UnsavedChangesDialog(string fileName)
    {
        InitializeComponent();
        Message.Text = $"Do you want to save the changes to {fileName}? Your changes will be lost if you don't save them.";
    }

    private void OnSave(object? sender, RoutedEventArgs e) => Choose(UnsavedChangesChoice.Save);

    private void OnDontSave(object? sender, RoutedEventArgs e) => Choose(UnsavedChangesChoice.DontSave);

    private void OnCancel(object? sender, RoutedEventArgs e) => Choose(UnsavedChangesChoice.Cancel);

    private void Choose(UnsavedChangesChoice choice) => Close(choice);
}
