using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SchrodingersCat.App.ViewModels;

namespace SchrodingersCat.App.Views;

/// <summary>
/// Browsing a TM and deleting in bulk (section 4 of <c>docs/design/tm-maintenance.md</c>): not modal, so the
/// translator can keep translating beside it. Closes when its TM is closed or replaced.
/// </summary>
public partial class TmEntriesWindow : Window
{
    /// <summary>How close to the end of the list (in pixels) scrolling loads the next page.</summary>
    private const double LoadAhead = 600;

    // For the XAML previewer.
    public TmEntriesWindow()
    {
        InitializeComponent();
    }

    public TmEntriesWindow(TmEntriesViewModel entries)
    {
        InitializeComponent();
        DataContext = entries ?? throw new ArgumentNullException(nameof(entries));
        entries.CloseRequested += OnCloseRequested;
        Closed += (_, _) => entries.CloseRequested -= OnCloseRequested;

        EntryList.AddHandler(ScrollViewer.ScrollChangedEvent, OnScrolled);
        EntryList.AddHandler(KeyDownEvent, OnListKeyDown, RoutingStrategies.Tunnel);
        EntryList.DoubleTapped += (_, _) => EditSelected();
    }

    private TmEntriesViewModel ViewModel => (TmEntriesViewModel)DataContext!;

    private void OnCloseRequested(object? sender, EventArgs e) => Close();

    // Near the end of what's loaded (or when a page doesn't fill the list), the next page.
    private void OnScrolled(object? sender, ScrollChangedEventArgs e)
    {
        if (e.Source is ScrollViewer viewer && viewer.Extent.Height - viewer.Offset.Y - viewer.Viewport.Height < LoadAhead)
            ViewModel.LoadMoreCommand.Execute(null);
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None)
            return;

        if (e.Key == Key.Delete)
        {
            e.Handled = true;
            ViewModel.DeleteSelectedCommand.Execute(EntryList.SelectedItems);
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            EditSelected();
        }
    }

    private void EditSelected()
    {
        if (EntryList.SelectedItem is ConcordanceResultViewModel { Stored: { } stored })
            ViewModel.EditCommand.Execute(stored);
    }
}
