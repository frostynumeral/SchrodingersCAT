using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SchrodingersCat.App.ViewModels;
using SchrodingersCat.Memory;

namespace SchrodingersCat.App.Views;

/// <summary>
/// Adds a term or edits a term entry (section 6 of <c>docs/design/termbase.md</c>). Returns what to store, or null if
/// cancelled, however the window was closed.
/// </summary>
public partial class TermEntryDialog : Window
{
    // For the XAML previewer.
    public TermEntryDialog()
        : this(TermEntryEditorViewModel.ForAdd(TermIndex.Empty, "client", "en", "hu", "file", "fájl"))
    {
    }

    public TermEntryDialog(TermEntryEditorViewModel editor)
    {
        InitializeComponent();
        DataContext = editor ?? throw new ArgumentNullException(nameof(editor));
        AddHandler(KeyDownEvent, OnShortcut, RoutingStrategies.Tunnel);
        Opened += (_, _) => Dispatcher.UIThread.Post(FocusFirstEmpty, DispatcherPriority.Loaded);
    }

    private TermEntryEditorViewModel ViewModel => (TermEntryEditorViewModel)DataContext!;

    // Seen before the text boxes, so Ctrl+Enter saves from the note too (which takes Enter as a line break).
    private void OnShortcut(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.Control)
        {
            e.Handled = true;
            Save();
        }
        else if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None
            && !this.GetVisualDescendants().OfType<ComboBox>().Any(c => c.IsDropDownOpen))
        {
            e.Handled = true;
            Close(null);
        }
    }

    private void OnSave(object? sender, RoutedEventArgs e) => Save();

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    // Stays open with the problem shown if the entry can't be stored.
    private void Save()
    {
        if (ViewModel.TrySave() is { } edit)
            Close(edit);
    }

    // The keyboard goes to the first term box that's empty (the translation, when a source was selected), else the first.
    private void FocusFirstEmpty()
    {
        var boxes = this.GetVisualDescendants().OfType<TextBox>().Where(b => b.Classes.Contains("termText")).ToList();
        (boxes.FirstOrDefault(b => string.IsNullOrEmpty(b.Text)) ?? boxes.FirstOrDefault())?.Focus();
    }
}
