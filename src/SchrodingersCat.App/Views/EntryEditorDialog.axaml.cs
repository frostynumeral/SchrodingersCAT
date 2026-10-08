using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SchrodingersCat.App.ViewModels;
using SchrodingersCat.Core.Models;
using SchrodingersCat.Memory;

namespace SchrodingersCat.App.Views;

/// <summary>
/// Edits a TM entry's translation (section 3 of <c>docs/design/tm-maintenance.md</c>). Returns the translation to
/// store, or null if cancelled, however the window was closed.
/// </summary>
public partial class EntryEditorDialog : Window
{
    // For the XAML previewer.
    public EntryEditorDialog()
        : this(new EntryEditorViewModel(new TmEntry(InlineContent.FromText("Source"), InlineContent.FromText("Translation")), "TM"))
    {
    }

    public EntryEditorDialog(EntryEditorViewModel editor)
    {
        InitializeComponent();
        DataContext = editor ?? throw new ArgumentNullException(nameof(editor));
        AddHandler(KeyDownEvent, OnShortcut, RoutingStrategies.Tunnel);
        Opened += (_, _) => Editor.FocusEditor();
    }

    private EntryEditorViewModel ViewModel => (EntryEditorViewModel)DataContext!;

    // Seen before the editor, which would take Enter as a line break.
    private void OnShortcut(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.Control)
        {
            e.Handled = true;
            Save();
        }
        else if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && !Editor.CodePicker.IsOpen)
        {
            e.Handled = true;
            Close(null);
        }
    }

    private void OnSave(object? sender, RoutedEventArgs e) => Save();

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    // Stays open with the problem shown if the translation can't be stored.
    private void Save()
    {
        if (ViewModel.TrySave() is { } target)
            Close(target);
        else
            Editor.FocusEditor();
    }
}
