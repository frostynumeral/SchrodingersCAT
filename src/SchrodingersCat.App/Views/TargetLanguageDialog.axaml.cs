using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SchrodingersCat.App.Views;

/// <summary>
/// Asks for a target language code. Save is enabled only for a code without a problem; closing it any other way
/// (title bar, Escape, Cancel) returns null.
/// </summary>
public partial class TargetLanguageDialog : Window
{
    private readonly Func<string, string?> _problem;

    // For the XAML previewer.
    public TargetLanguageDialog()
        : this("en", _ => null)
    {
    }

    public TargetLanguageDialog(string sourceLanguage, Func<string, string?> problem)
    {
        _problem = problem ?? throw new ArgumentNullException(nameof(problem));
        InitializeComponent();
        Message.Text = $"This file doesn't say which language it is translated into (the source is {sourceLanguage}). "
            + "XLIFF needs it to save the translations. Enter the target language code:";
        LanguageBox.TextChanged += (_, _) => Validate();
        Opened += (_, _) => LanguageBox.Focus();
        Validate();
    }

    // No message for the empty box: nothing has been typed yet.
    private void Validate()
    {
        var text = LanguageBox.Text ?? "";
        var problem = _problem(text);
        OkButton.IsEnabled = problem is null;
        Problem.Text = text.Length == 0 ? "" : problem;
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        if (_problem(LanguageBox.Text ?? "") is null)
            Close(LanguageBox.Text!.Trim());
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
