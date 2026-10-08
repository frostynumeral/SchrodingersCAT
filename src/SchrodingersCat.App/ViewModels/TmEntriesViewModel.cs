using System.Collections;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SchrodingersCat.App.Services;
using SchrodingersCat.Memory;

namespace SchrodingersCat.App.ViewModels;

/// <summary>A choice in a filter's list: a value, or null for "any".</summary>
public sealed record FilterChoice(string Label, string? Value)
{
    public override string ToString() => Label;
}

/// <summary>
/// The window for browsing a TM and deleting in bulk (section 4 of <c>docs/design/tm-maintenance.md</c>): filters, a
/// list loaded a page at a time, newest first, and editing and deleting entries.
/// </summary>
/// <remarks>
/// The list refreshes when a filter changes, or on Enter in the text box, never as you type (like the concordance).
/// Changes made elsewhere (the pane, the concordance) arrive through <see cref="MainWindowViewModel.EntriesChanged"/>;
/// translations stored with Ctrl+Enter show after the next refresh.
/// </remarks>
public sealed partial class TmEntriesViewModel : ObservableObject
{
    /// <summary>How many entries a page loads.</summary>
    public const int PageSize = 200;

    private static readonly FilterChoice AnyDocument = new("All documents", null);
    private static readonly FilterChoice AnyUser = new("All users", null);

    private readonly MainWindowViewModel _owner;
    private readonly IDialogService _dialogs;

    // What the list shows, and where the next page starts.
    private EntryFilter _applied = EntryFilter.All;
    private TmEntryId? _last;
    private bool _complete = true;
    private int _generation;
    private bool _loadingMore;

    // Set while OpenAsync fills the lists, so selecting their first item doesn't refresh.
    private bool _opening;

    public TmEntriesViewModel(MainWindowViewModel owner, OpenTranslationMemory memory, IDialogService dialogs)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        Memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _owner.EntriesChanged += OnEntriesChanged;
    }

    public OpenTranslationMemory Memory { get; }

    public string Title => $"{Memory.Name} ({Memory.SourceLanguage} → {Memory.TargetLanguage}) — SchrödingersCAT";

    /// <summary>Raised when the window should close: its TM was closed or replaced.</summary>
    public event EventHandler? CloseRequested;

    // ---------------------------------------------------------------- filters

    /// <summary>Words to look for, with the concordance's rules; applied on Enter.</summary>
    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    public partial bool SearchTranslations { get; set; }

    public ObservableCollection<FilterChoice> Documents { get; } = [AnyDocument];

    public ObservableCollection<FilterChoice> Users { get; } = [AnyUser];

    [ObservableProperty]
    public partial FilterChoice? SelectedDocument { get; set; } = AnyDocument;

    [ObservableProperty]
    public partial FilterChoice? SelectedUser { get; set; } = AnyUser;

    /// <summary>The first day (local time) of the dates shown.</summary>
    [ObservableProperty]
    public partial DateTime? ChangedFrom { get; set; }

    /// <summary>The last day (local time) of the dates shown, included.</summary>
    [ObservableProperty]
    public partial DateTime? ChangedTo { get; set; }

    [ObservableProperty]
    public partial bool SeveralTranslations { get; set; }

    partial void OnSearchTranslationsChanged(bool value)
    {
        if (!string.IsNullOrWhiteSpace(SearchText))
            Refresh();
    }

    partial void OnSelectedDocumentChanged(FilterChoice? value) => RefreshUnlessOpening();

    partial void OnSelectedUserChanged(FilterChoice? value) => RefreshUnlessOpening();

    partial void OnChangedFromChanged(DateTime? value) => Refresh();

    partial void OnChangedToChanged(DateTime? value) => Refresh();

    partial void OnSeveralTranslationsChanged(bool value) => Refresh();

    // ---------------------------------------------------------------- the list

    /// <summary>The entries loaded so far, newest first.</summary>
    public ObservableCollection<ConcordanceResultViewModel> Entries { get; } = [];

    /// <summary>How many entries the filter finds.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Header))]
    public partial int Total { get; private set; }

    /// <summary>How many entries the TM has.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Header))]
    public partial int MemoryCount { get; private set; }

    /// <summary>"1,234 of 198,000 entries", or the TM's count when nothing is filtered.</summary>
    public string Header => _applied == EntryFilter.All
        ? $"{N(MemoryCount)} {(MemoryCount == 1 ? "entry" : "entries")}"
        : $"{N(Total)} of {N(MemoryCount)} entries";

    /// <summary>What's happening, or what went wrong; null when there's nothing to say.</summary>
    [ObservableProperty]
    public partial string? Status { get; private set; }

    /// <summary>Whether the filter finds anything to delete.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteAllCommand))]
    public partial bool HasEntries { get; private set; }

    /// <summary>The latest load or change; tests await it.</summary>
    internal Task Loading { get; private set; } = Task.CompletedTask;

    /// <summary>Called when the window is first shown: fills the filters' lists, then the first page.</summary>
    public async Task OpenAsync()
    {
        try
        {
            var (documents, users) = await Memory.FilterValuesAsync();
            _opening = true;
            foreach (var document in documents)
                Documents.Add(new FilterChoice(document, document));
            foreach (var user in users)
                Users.Add(new FilterChoice(user, user));
        }
        catch (Exception ex) when (MainWindowViewModel.IsMemoryReportable(ex))
        {
            Status = $"Could not read {Memory.Name}: {ex.Message}";
            return;
        }
        finally
        {
            _opening = false;
        }

        Refresh();
        await Loading;
    }

    /// <summary>Applies the filters (Enter in the text box) and shows the first page.</summary>
    [RelayCommand]
    private void Refresh() => Loading = LoadAsync(reset: true);

    /// <summary>Loads the next page (scrolling near the end); nothing if all are loaded or a page is on its way.</summary>
    [RelayCommand]
    private void LoadMore()
    {
        if (!_complete && !_loadingMore)
            Loading = LoadAsync(reset: false);
    }

    private void RefreshUnlessOpening()
    {
        if (!_opening)
            Refresh();
    }

    private async Task LoadAsync(bool reset)
    {
        if (reset)
        {
            _applied = Filter();
            _generation++;
            _last = null;
            _complete = false;
            Entries.Clear();
            Status = "Loading…";
        }

        var generation = _generation;
        _loadingMore = true;
        try
        {
            var page = await Memory.BrowseAsync(_applied, _last, PageSize);
            var count = await Memory.CountAsync();
            if (generation != _generation)
                return; // a refresh started meanwhile; its page is the one to show

            foreach (var stored in page.Entries)
                Entries.Add(Row(stored));
            _last = page.Entries.Count > 0 ? page.Entries[^1].Id : _last;
            _complete = page.Entries.Count < PageSize;
            Total = page.Total;
            MemoryCount = count;
            HasEntries = Total > 0;
            OnPropertyChanged(nameof(Header));
            Status = Total == 0 ? (MemoryCount == 0 ? $"{Memory.Name} has no entries." : "No entries match the filters.") : null;
        }
        catch (Exception ex) when (MainWindowViewModel.IsMemoryReportable(ex))
        {
            Status = $"Could not read {Memory.Name}: {ex.Message}";
        }
        finally
        {
            if (generation == _generation)
                _loadingMore = false;
        }
    }

    private EntryFilter Filter()
    {
        var query = ConcordanceQuery.Parse(SearchText);
        return new EntryFilter
        {
            Text = query.IsEmpty ? null : query,
            TextSide = SearchTranslations ? ConcordanceSide.Target : ConcordanceSide.Source,
            Document = SelectedDocument?.Value,
            User = SelectedUser?.Value,
            ChangedFrom = ChangedFrom is { } from ? Local(from.Date) : null,
            ChangedBefore = ChangedTo is { } to ? Local(to.Date.AddDays(1)) : null,
            SeveralTranslations = SeveralTranslations,
        };
    }

    private ConcordanceResultViewModel Row(StoredEntry stored) =>
        new(Concordance.Highlight(stored, _applied.Text, _applied.TextSide), origin: null, EditCommand, DeleteCommand);

    // ---------------------------------------------------------------- editing and deleting

    /// <summary>Edits an entry's translation (double-click, Enter), as the pane's Edit does.</summary>
    [RelayCommand]
    private Task EditAsync(StoredEntry? stored) =>
        stored is null ? Task.CompletedTask : _owner.EditEntryCommand.ExecuteAsync(stored);

    /// <summary>Deletes one entry (its context menu).</summary>
    [RelayCommand]
    private Task DeleteAsync(StoredEntry? stored) =>
        stored is null ? Task.CompletedTask : DeleteEntriesAsync([stored]);

    /// <summary>Deletes the selected entries (the Delete key), asking first.</summary>
    [RelayCommand]
    private Task DeleteSelectedAsync(IList? rows) =>
        DeleteEntriesAsync([.. (rows ?? Array.Empty<object>()).OfType<ConcordanceResultViewModel>().Select(r => r.Stored).OfType<StoredEntry>()]);

    private async Task DeleteEntriesAsync(IReadOnlyList<StoredEntry> entries)
    {
        if (entries.Count == 0)
            return;

        var what = entries.Count == 1
            ? $"“{Shorten(entries[0].Entry.Source.ToPlainText())}” → “{Shorten(entries[0].Entry.Target.ToPlainText())}”"
            : $"{N(entries.Count)} entries";
        if (!await _dialogs.ConfirmAsync("Delete entries", $"Delete {what} from {Memory.Name}? This can't be undone.", "Delete"))
            return;

        var change = DeleteThenCompactAsync(
            () => Memory.DeleteAsync([.. entries.Select(e => e.Id)]),
            removed =>
            {
                foreach (var entry in entries)
                    _owner.FollowStoredTarget(entry.Entry, null);
                var ids = entries.Select(e => e.Id).ToHashSet();
                foreach (var row in Entries.Where(r => r.Stored is { } s && ids.Contains(s.Id)).ToList())
                    Entries.Remove(row);
                Total -= removed;
                MemoryCount -= removed;
                HasEntries = Total > 0;
                _owner.OnEntriesChanged(new EntriesChange { Deleted = ids, Source = this });
            });
        Loading = change;
        await change;
    }

    /// <summary>
    /// Deletes every entry the filters find (the ones the list shows, not what the filter boxes may hold by now),
    /// asking first and suggesting an export, as there is no undo.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasEntries))]
    private async Task DeleteAllAsync()
    {
        var filter = _applied;
        var what = filter == EntryFilter.All ? $"all {N(Total)} entries" : $"the {N(Total)} entries the filters find";
        if (!await _dialogs.ConfirmAsync(
                "Delete entries",
                $"Delete {what} from {Memory.Name}? This can't be undone. To keep a copy, export the TM first (Translation memory → Export TMX…).",
                "Delete"))
            return;

        Loading = DeleteAllThenRefreshAsync(filter);
        await Loading;
    }

    private async Task DeleteAllThenRefreshAsync(EntryFilter filter)
    {
        if (await DeleteThenCompactAsync(
                () => Memory.DeleteMatchingAsync(filter),
                _ => _owner.OnEntriesChanged(EntriesChange.Everything with { Source = this })) is { } done)
        {
            await LoadAsync(reset: true);
            Status = done;
        }
    }

    // Deletes, then gives back the space if it's worth it (after a few deletes it isn't, and nothing happens).
    // Returns what it did, also shown as the status; null if the delete failed.
    private async Task<string?> DeleteThenCompactAsync(Func<Task<int>> delete, Action<int> deleted)
    {
        int removed;
        try
        {
            removed = await delete();
        }
        catch (Exception ex) when (MainWindowViewModel.IsMemoryReportable(ex))
        {
            Status = $"Could not delete from {Memory.Name}: {ex.Message}";
            return null;
        }

        deleted(removed);
        var message = $"Deleted {N(removed)} {(removed == 1 ? "entry" : "entries")}.";
        Status = $"{message} Compacting the file…";
        try
        {
            Status = await Memory.CompactAsync() ? $"{message} The file was compacted." : message;
        }
        catch (Exception ex) when (MainWindowViewModel.IsMemoryReportable(ex))
        {
            Status = $"{message} Could not compact {Memory.Name}: {ex.Message}";
        }

        return Status;
    }

    // A change made elsewhere (or an edit made here, through the main window): show it.
    private void OnEntriesChanged(object? sender, EntriesChange change)
    {
        if (change.Source == this)
            return;

        if (change.IsEverything)
        {
            Refresh();
            return;
        }

        foreach (var row in Entries.Where(r => r.Stored is { } s && change.Deleted.Contains(s.Id)).ToList())
            Entries.Remove(row);

        // A deleted entry the filter finds may not be loaded yet, so the counts are read again.
        Loading = change.Updated is { } updated ? ReloadAsync(updated) : RecountAsync();
    }

    private async Task RecountAsync()
    {
        var generation = _generation;
        try
        {
            var total = (await Memory.BrowseAsync(_applied, null, 1)).Total;
            var count = await Memory.CountAsync();
            if (generation != _generation)
                return;

            Total = total;
            MemoryCount = count;
            HasEntries = Total > 0;
        }
        catch (Exception ex) when (MainWindowViewModel.IsMemoryReportable(ex))
        {
            Status = $"Could not read {Memory.Name}: {ex.Message}";
        }
    }

    // The updated entry's row shows its new translation, where it is.
    private async Task ReloadAsync(TmEntryId id)
    {
        try
        {
            var index = Entries.ToList().FindIndex(r => r.Stored?.Id == id);
            if (index < 0 || await Memory.FindAsync(id) is not { } stored)
                return;

            index = Entries.ToList().FindIndex(r => r.Stored?.Id == id);
            if (index >= 0)
                Entries[index] = Row(stored);
        }
        catch (Exception ex) when (MainWindowViewModel.IsMemoryReportable(ex))
        {
            Status = $"Could not read {Memory.Name}: {ex.Message}";
        }
    }

    /// <summary>Asks the window to close (its TM is going) and stops following changes.</summary>
    internal void Close()
    {
        _owner.EntriesChanged -= OnEntriesChanged;
        _generation++;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private static DateTimeOffset Local(DateTime day) => new(day, TimeZoneInfo.Local.GetUtcOffset(day));

    private static string N(int count) => count.ToString("N0", CultureInfo.CurrentCulture);

    private static string Shorten(string text) => text.Length <= 60 ? text : string.Concat(text.AsSpan(0, 59), "…");
}
