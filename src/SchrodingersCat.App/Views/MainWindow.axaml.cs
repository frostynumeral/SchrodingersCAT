using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SchrodingersCat.App.Controls;
using SchrodingersCat.App.Services;
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
        QaList.Tapped += OnQaListTapped;
        // The list item handles Enter itself (selecting), so a plain subscription would never see it.
        QaList.AddHandler(KeyDownEvent, OnQaListKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel viewModel)
                viewModel.Qa.EditorRequested += (_, _) => FocusEditorSoon();
        };
    }

    // The segment shortcuts (design section 4), Ctrl+1 … Ctrl+9 (insert a match), Ctrl+Shift+1 … Ctrl+Shift+9
    // (delete a match's entry), Ctrl+K (concordance), Ctrl+T (insert a term), Ctrl+E (add a term) and F8 (check the
    // document) are seen
    // before the editor: AvaloniaEdit would take Ctrl+Up/Down, Ctrl+Enter and Ctrl+Insert for itself, so they can't be
    // window key bindings, which only get unhandled keys.
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

        if (e.Key == Key.T && e.KeyModifiers == KeyModifiers.Control)
        {
            e.Handled = true;
            OpenTermPicker(viewModel);
            return;
        }

        if (e.Key == Key.E && e.KeyModifiers == KeyModifiers.Control)
        {
            e.Handled = true;
            AddTermAsync(viewModel).Forget();
            return;
        }

        if (e.Key == Key.F8 && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            viewModel.Qa.RunCommand.Execute(null);
            // Not while a picker is open: it has the keyboard, and would stay open without its keys.
            if (!this.GetVisualDescendants().OfType<TargetEditor>().Any(t => t.IsPickerOpen))
                FocusReportSoon();
            return;
        }

        (ICommand? command, object? parameter) = (e.Key, e.KeyModifiers) switch
        {
            (Key.Enter, KeyModifiers.Control) => (viewModel.ConfirmSegmentCommand, null),
            (Key.Down, KeyModifiers.Control) => (viewModel.NextSegmentCommand, null),
            (Key.Up, KeyModifiers.Control) => (viewModel.PreviousSegmentCommand, null),
            (Key.Down, KeyModifiers.Control | KeyModifiers.Shift) => (viewModel.NextSegmentWithIssuesCommand, null),
            (Key.Insert, KeyModifiers.Control) => (viewModel.CopySourceCommand, null),
            ( >= Key.D1 and <= Key.D9, KeyModifiers.Control) => (viewModel.InsertMatchCommand, e.Key - Key.D0),
            ( >= Key.NumPad1 and <= Key.NumPad9, KeyModifiers.Control) => (viewModel.InsertMatchCommand, e.Key - Key.NumPad0),
            ( >= Key.D1 and <= Key.D9, KeyModifiers.Control | KeyModifiers.Shift) => (viewModel.DeleteMatchCommand, e.Key - Key.D0),
            _ => ((ICommand?)null, (object?)null),
        };
        if (command is null)
            return;

        e.Handled = true;
        if (command.CanExecute(parameter))
            command.Execute(parameter);
    }

    // Ctrl+T (section 6 of docs/design/termbase.md): the picker of the selected segment's terms, in its editor, from
    // wherever the keyboard is (a source block, the QA list). With nothing to offer, the status line says why.
    private void OpenTermPicker(MainWindowViewModel viewModel)
    {
        if (viewModel.TermPickerChoices() is not { Count: > 0 } choices
            || this.GetVisualDescendants().OfType<TargetEditor>().FirstOrDefault(t => t.IsEffectivelyVisible) is not { } editor)
            return;

        editor.FocusEditor();
        editor.OpenTermPicker(choices);
    }

    // Ctrl+E and Termbase → Add term… (section 6 of docs/design/termbase.md): the entry dialog with the selected row's
    // source selection and the editor's selection filled in (codes as spaces); afterwards the keyboard goes back to the
    // editor.
    private async Task AddTermAsync(MainWindowViewModel viewModel)
    {
        var source = SourceSelection(viewModel.SelectedSegment);
        var target = this.GetVisualDescendants().OfType<TargetEditor>().FirstOrDefault(t => t.IsEffectivelyVisible)?.Model is { HasSelection: true } model
            ? ConcordanceViewModel.TextOf(model.SelectedContent())
            : null;
        await viewModel.AddTermCommand.ExecuteAsync(new TermSelection(source, target));
        FocusEditorSoon();
    }

    private void OnAddTerm(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
            AddTermAsync(viewModel).Forget();
    }

    // The selected text of a source block, as the concordance searches it (codes as spaces): the selected row's if it
    // has one, else the focused block's, else any.
    private string? SourceSelection(SegmentViewModel? selected)
    {
        var block = SourceBlocks().OrderByDescending(b => b.DataContext == selected && selected is not null).ThenByDescending(b => b.IsKeyboardFocusWithin).FirstOrDefault();
        return block is null ? null : ConcordanceViewModel.TextOf(Selection(block));
    }

    // The source blocks with a selection.
    private List<InlineContentBlock> SourceBlocks() =>
        [.. this.GetVisualDescendants().OfType<InlineContentBlock>().Where(b => b.Classes.Contains("source") && b.SelectionStart != b.SelectionEnd)];

    private static Core.Models.InlineContent Selection(InlineContentBlock block) =>
        ContentRuns.Slice(block.Runs ?? [], Math.Min(block.SelectionStart, block.SelectionEnd), Math.Max(block.SelectionStart, block.SelectionEnd));

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
        var sources = SourceBlocks();
        var block = sources.FirstOrDefault(b => b.IsKeyboardFocusWithin) ?? sources.FirstOrDefault();
        return block is null ? null : (ConcordanceViewModel.TextOf(Selection(block)), false);
    }

    // A click on a report entry (not on its Dismiss button) opens its segment; a click beside the entries, nothing.
    private void OnQaListTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is null
            && source.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is QaReportEntry entry)
            OpenQaEntry(entry);
    }

    private void OnQaListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None || QaList.SelectedItem is not QaReportEntry entry)
            return;

        e.Handled = true;
        OpenQaEntry(entry);
    }

    private void OpenQaEntry(QaReportEntry entry)
    {
        if (DataContext is MainWindowViewModel viewModel)
            viewModel.Qa.OpenCommand.Execute(entry);
    }

    // F8: the keyboard goes to the report's first entry, so Up/Down and Enter work through it. The tab's content is
    // attached only once the tab has switched.
    private void FocusReportSoon() =>
        Dispatcher.UIThread.Post(
            () =>
            {
                if (QaList.Items.OfType<QaReportEntry>().FirstOrDefault() is not { } first)
                    return;
                QaList.SelectedItem = first;
                QaList.ContainerFromItem(first)?.Focus(NavigationMethod.Directional);
            },
            DispatcherPriority.Loaded);

    // After the selection moved, the selected row's editor exists only once the list has laid it out.
    private void FocusEditorSoon() =>
        Dispatcher.UIThread.Post(
            () => this.GetVisualDescendants().OfType<TargetEditor>().FirstOrDefault(t => t.IsEffectivelyVisible)?.FocusEditor(),
            DispatcherPriority.Loaded);

    // Escape in the search box: back to typing the translation.
    private void OnConcordanceBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None)
            return;

        e.Handled = true;
        this.GetVisualDescendants().OfType<TargetEditor>().FirstOrDefault(t => t.IsEffectivelyVisible)?.FocusEditor();
    }

    // Closing is synchronous, but asking about unsaved changes and closing the TM and the termbase are not: cancel this
    // close, ask, let the TM finish its queued stores (and a termbase that is loading stop), and close again (for real
    // this time) only if the view model allows it.
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closeConfirmed || e.Cancel || DataContext is not MainWindowViewModel viewModel
            || (!viewModel.IsDirty && viewModel.TranslationMemory is null && viewModel.Termbase is null && viewModel.LoadingTermbase is null))
            return;

        e.Cancel = true;
        ConfirmAndCloseAsync(viewModel).Forget();
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
