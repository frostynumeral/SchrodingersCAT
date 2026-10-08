using System.Collections.ObjectModel;
using System.Data.Common;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SchrodingersCat.Core.Models;
using SchrodingersCat.Memory;

namespace SchrodingersCat.App.ViewModels;

/// <summary>
/// The Concordance tab: searches the open TM for a word or phrase (how was it translated before?) and inserts a
/// result's translation at the editor's caret. Searches run when asked (Enter, Ctrl+K), never as you type.
/// </summary>
public sealed partial class ConcordanceViewModel : ObservableObject
{
    private const string Hint = "Type a word or phrase and press Enter, or select text and press Ctrl+K.";

    private readonly MainWindowViewModel _owner;

    // Cancels the running search when another starts or the TM changes. (The search owns its token source.)
    private Action? _cancel;

    // What the results are for, so they can be searched again after the TM changed.
    private (string Query, bool InTargets)? _searched;

    public ConcordanceViewModel(MainWindowViewModel owner) => _owner = owner ?? throw new ArgumentNullException(nameof(owner));

    [ObservableProperty]
    public partial string Query { get; set; } = "";

    /// <summary>Search the translations rather than the sources.</summary>
    [ObservableProperty]
    public partial bool SearchTargets { get; set; }

    [ObservableProperty]
    public partial string? Status { get; private set; } = Hint;

    /// <summary>The best entries found, best first.</summary>
    public ObservableCollection<ConcordanceResultViewModel> Results { get; } = [];

    /// <summary>The latest search; tests await it.</summary>
    internal Task SearchTask { get; private set; } = Task.CompletedTask;

    /// <summary>Searches for <paramref name="text"/> (Ctrl+K on a selection): the translations if it came from a target.</summary>
    public void SearchFor(string text, bool inTargets)
    {
        ArgumentNullException.ThrowIfNull(text);

        Query = text.Trim();
        SearchTargets = inTargets;
        SearchTask = SearchAsync(Query, SearchTargets);
    }

    [RelayCommand]
    private void Search() => SearchTask = SearchAsync(Query, SearchTargets);

    /// <summary>
    /// Called after an entry was edited or deleted: searches again for what the results show (not for what the box
    /// may hold by now), so they show what the TM holds.
    /// </summary>
    internal void Refresh()
    {
        if (_searched is { } searched)
            SearchTask = SearchAsync(searched.Query, searched.InTargets);
    }

    /// <summary>Called when the TM changes: stops a running search (so closing the TM doesn't wait for it) and clears the results.</summary>
    internal void Reset()
    {
        _cancel?.Invoke();
        _cancel = null;
        _searched = null;
        Results.Clear();
        Status = Hint;
    }

    /// <summary>
    /// Inserts a result's translation at the selected segment's caret, as text: the selection is kept (a term selected
    /// for Ctrl+K isn't replaced), and codes, which belong to another sentence, become their text equivalent or a space.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanInsert))]
    private void Insert(ConcordanceResultViewModel result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (_owner.SelectedSegment is not { } segment)
            return;

        var model = segment.EditModel;
        model.MoveCaret(model.Caret);
        var outcome = model.Paste(InlineContent.FromText(TextOf(result.Hit.Entry.Target)), from: null);
        segment.EditStatus = outcome.IsRefused ? outcome.Message : "Inserted the translation from the concordance.";
    }

    private bool CanInsert(ConcordanceResultViewModel? result) => result is not null && _owner.SelectedSegment is not null;

    /// <summary>
    /// Content as plain text for searching or inserting: standalone codes become their <see cref="InlineCode.Equiv"/>
    /// or a space, so words on either side of a line break don't run together; paired codes and annotations go.
    /// </summary>
    public static string TextOf(InlineContent content)
    {
        ArgumentNullException.ThrowIfNull(content);

        return string.Concat(content.Select(e => e switch
        {
            InlineText text => text.Text,
            InlineCode { Kind: InlineCodeKind.Standalone } code => string.IsNullOrEmpty(code.Equiv) ? " " : code.Equiv,
            _ => "",
        }));
    }

    private async Task SearchAsync(string text, bool inTargets)
    {
        _cancel?.Invoke();
        _cancel = null;
        _searched = null;
        Results.Clear();

        if (_owner.TranslationMemory is not { } memory)
        {
            Status = "Open a translation memory to search it.";
            return;
        }

        var query = ConcordanceQuery.Parse(text);
        if (query.IsEmpty)
        {
            Status = "Type a word or phrase to search for.";
            return;
        }

        using var search = new CancellationTokenSource();
        Action cancel = search.Cancel;
        _cancel = cancel;
        _searched = (text, inTargets);
        Status = "Searching…";
        try
        {
            var result = await memory.SearchAsync(query, inTargets ? ConcordanceSide.Target : ConcordanceSide.Source, cancellationToken: search.Token);
            if (search.IsCancellationRequested || _owner.TranslationMemory != memory)
                return;

            foreach (var hit in result.Hits)
                Results.Add(new ConcordanceResultViewModel(hit, memory.Name, _owner.EditEntryCommand, _owner.DeleteEntryCommand));
            Status = result.Total switch
            {
                0 => $"No hits for '{query.Text.Trim()}'.",
                1 => "1 entry.",
                var total when total > result.Hits.Count => $"{total} entries; showing the best {result.Hits.Count}.",
                var total => $"{total} entries.",
            };
        }
        catch (OperationCanceledException) when (search.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is TmFormatException or DbException or IOException or ObjectDisposedException)
        {
            Status = null;
            _owner.ReportError($"Could not search {memory.Name}: {ex.Message}");
        }
        finally
        {
            if (_cancel == cancel)
                _cancel = null;
        }
    }
}
