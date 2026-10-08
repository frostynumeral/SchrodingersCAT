using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SchrodingersCat.App.Controls;
using SchrodingersCat.App.ViewModels;

namespace SchrodingersCat.App.Views;

public partial class MainWindow : Window
{
    private bool _closeConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnShortcut, RoutingStrategies.Tunnel);
        ConcordanceBox.KeyDown += OnConcordanceBoxKeyDown;
    }

    // The segment shortcuts (design section 4) and Ctrl+1 … Ctrl+9 (insert a match) are seen before the editor:
    // AvaloniaEdit would take Ctrl+Up/Down, Ctrl+Enter and Ctrl+Insert for itself, so they can't be window key
    // bindings, which only get unhandled keys.
    private void OnShortcut(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
            return;

        if (e.Key == Key.K && e.KeyModifiers == KeyModifiers.Control)
        {
            e.Handled = true;
            SearchSelection(viewModel);
            return;
        }

        (ICommand? command, object? parameter) = (e.Key, e.KeyModifiers) switch
        {
            (Key.Enter, KeyModifiers.Control) => (viewModel.ConfirmSegmentCommand, null),
            (Key.Down, KeyModifiers.Control) => (viewModel.NextSegmentCommand, null),
            (Key.Up, KeyModifiers.Control) => (viewModel.PreviousSegmentCommand, null),
            (Key.Down, KeyModifiers.Control | KeyModifiers.Shift) => (viewModel.NextSegmentWithIssuesCommand, null),
            (Key.Insert, KeyModifiers.Control) => (viewModel.CopySourceCommand, null),
            (>= Key.D1 and <= Key.D9, KeyModifiers.Control) => (viewModel.InsertMatchCommand, e.Key - Key.D0),
            (>= Key.NumPad1 and <= Key.NumPad9, KeyModifiers.Control) => (viewModel.InsertMatchCommand, e.Key - Key.NumPad0),
            _ => ((ICommand?)null, (object?)null),
        };
        if (command is null)
            return;

        e.Handled = true;
        if (command.CanExecute(parameter))
            command.Execute(parameter);
    }

    // Ctrl+K: the concordance for the selected text, the editor's (searching translations) or a source's (searching
    // sources); without a selection, the search box. Codes in the selection become spaces.
    private void SearchSelection(MainWindowViewModel viewModel)
    {
        viewModel.PaneTab = 1;
        if (SelectedText() is { } selection && !string.IsNullOrWhiteSpace(selection.Text))
        {
            viewModel.Concordance.SearchFor(selection.Text, selection.InTargets);
            return;
        }

        // The tab's content is attached only once the tab has switched.
        Dispatcher.UIThread.Post(() => ConcordanceBox.Focus(), DispatcherPriority.Loaded);
    }

    private (string Text, bool InTargets)? SelectedText()
    {
        if (FocusManager?.GetFocusedElement() is Visual focused
            && focused.FindAncestorOfType<TargetEditor>(includeSelf: true) is { Model: { HasSelection: true } model })
            return (ConcordanceViewModel.TextOf(model.SelectedContent()), true);

        // A source's selection: the focused block's, else any (clicking a source selects its row, and the editor
        // then takes the keyboard, so the block may not have it).
        var sources = this.GetVisualDescendants().OfType<InlineContentBlock>().Where(b => b.Classes.Contains("source")).ToList();
        var block = sources.FirstOrDefault(b => b.IsKeyboardFocusWithin && b.SelectionStart != b.SelectionEnd)
            ?? sources.FirstOrDefault(b => b.SelectionStart != b.SelectionEnd);
        if (block is null)
            return null;

        var content = ContentRuns.Slice(block.Runs ?? [], Math.Min(block.SelectionStart, block.SelectionEnd), Math.Max(block.SelectionStart, block.SelectionEnd));
        return (ConcordanceViewModel.TextOf(content), false);
    }

    // Escape in the search box: back to typing the translation.
    private void OnConcordanceBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None)
            return;

        e.Handled = true;
        this.GetVisualDescendants().OfType<TargetEditor>().FirstOrDefault(t => t.IsEffectivelyVisible)?.FocusEditor();
    }

    // Closing is synchronous, but asking about unsaved changes and closing the TM are not: cancel this close, ask,
    // let the TM finish its queued stores, and close again (for real this time) only if the view model allows it.
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closeConfirmed || e.Cancel || DataContext is not MainWindowViewModel viewModel
            || (!viewModel.IsDirty && viewModel.TranslationMemory is null))
            return;

        e.Cancel = true;
        _ = ConfirmAndCloseAsync(viewModel);
    }

    private async Task ConfirmAndCloseAsync(MainWindowViewModel viewModel)
    {
        if (!await viewModel.ConfirmCloseAsync())
            return;

        await viewModel.ShutDownAsync();
        _closeConfirmed = true;
        Close();
    }
}
