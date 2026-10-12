using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SchrodingersCat.App.Services;
using SchrodingersCat.Memory;

namespace SchrodingersCat.App.ViewModels;

// The termbase in the editor (step 5 of docs/design/termbase.md): the Termbase menu, remembering and loading it, the
// terms list, Ctrl+T's choices, and re-checking the document when the termbase changes.
public sealed partial class MainWindowViewModel
{
    // The open document's view of the termbase (one per document); null before a document is opened.
    private DocumentTerms? _terms;

    // The termbase being loaded, to cancel it; null when none is.
    private CancellationTokenSource? _termbaseLoad;

    // Counts the whole-document re-checks, so only the latest applies its results.
    private int _termGeneration;

    // The document whose QA report a re-check cleared, to list it again once the re-check is done.
    private DocumentTerms? _refreshReportFor;

    /// <summary>
    /// The open termbase: its terms are marked in the source and listed for the selected segment, and the terminology
    /// check uses them. Null if none is open, also while the first one loads. Changed through the Termbase menu or
    /// <see cref="UseTermbaseAsync"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TermbaseLabel))]
    [NotifyPropertyChangedFor(nameof(TermsStatus))]
    [NotifyPropertyChangedFor(nameof(ShowsTerms))]
    [NotifyCanExecuteChangedFor(nameof(CloseTermbaseCommand))]
    public partial OpenTermbase? Termbase { get; private set; }

    /// <summary>The file name of the termbase being loaded in the background (section 3); null when none is.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TermbaseLabel))]
    [NotifyPropertyChangedFor(nameof(TermsStatus))]
    [NotifyPropertyChangedFor(nameof(ShowsTerms))]
    [NotifyCanExecuteChangedFor(nameof(CloseTermbaseCommand))]
    public partial string? LoadingTermbase { get; private set; }

    /// <summary>The selected segment's terms (section 6), in the order of their first place in its source.</summary>
    public ObservableCollection<TermViewModel> Terms { get; } = [];

    /// <summary>The terms list is shown: a termbase is open or loading. Without one, the matches keep the room.</summary>
    public bool ShowsTerms => Termbase is not null || LoadingTermbase is not null;

    /// <summary>The termbase and its languages for the list's header, why it can't be used for this document, and what's loading.</summary>
    public string? TermbaseLabel
    {
        get
        {
            if (Termbase is not { } termbase)
                return LoadingTermbase is { } loading ? $"Loading {loading}…" : null;

            var label = $"{termbase.Name} ({termbase.SourceLanguage} → {termbase.TargetLanguage})";
            if (_document is { } document && !TermbaseServes)
                label += $" doesn't serve this document ({document.SourceLanguage} → {document.TargetLanguage ?? "?"}).";
            if (LoadingTermbase is { } other)
                label += $" Loading {other}…";
            return label;
        }
    }

    /// <summary>What the terms list says instead of terms; null when it has some (or says enough in its header).</summary>
    public string? TermsStatus
    {
        get
        {
            if (Termbase is null)
                return LoadingTermbase is null ? null : "Loading the termbase…";
            if (SelectedSegment is null)
                return "Select a segment to see its terms.";
            if (!TermbaseServes)
                return null;
            return Terms.Count == 0 ? "No terms found." : null;
        }
    }

    // Decision 11: a termbase serves its own languages only, in one direction; a regional variant of them fully (a
    // termbase has no scores to lower). A document without a target language yet is judged by its source.
    private bool TermbaseServes => Termbase is { } termbase && _document is { } document
        && TmLanguages.Penalty(termbase.SourceLanguage, termbase.TargetLanguage, document.SourceLanguage, document.TargetLanguage) is not null;

    /// <summary>The open document's view of the termbase; tests read it.</summary>
    internal DocumentTerms? DocumentTerms => _terms;

    /// <summary>Runs the whole document's re-check in the background; tests hold it at a gate.</summary>
    internal Func<Action, Task> RunOffUiThread { get; set; } = Task.Run;

    /// <summary>Opens a termbase file in the background; tests hold it at a gate.</summary>
    internal Func<string, CancellationToken, Task<OpenTermbase>> OpenTermbaseFile { get; set; } = OpenTermbase.OpenAsync;

    /// <summary>The latest termbase load; tests await it.</summary>
    internal Task TermbaseLoad { get; private set; } = Task.CompletedTask;

    /// <summary>The latest whole-document re-check after the termbase changed; tests await it.</summary>
    internal Task TermCheck { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// After the termbase, or whether it serves the document, may have changed: the document takes the snapshot to use
    /// (none if it doesn't serve), and if that changed, the marks and the terms list follow and every unit is checked
    /// again in the background (section 5).
    /// </summary>
    private void OnTermsChanged()
    {
        OnPropertyChanged(nameof(TermbaseLabel));
        OnPropertyChanged(nameof(TermsStatus));
        if (_terms is not { } terms)
            return;

        var index = Termbase is { } termbase && TermbaseServes ? termbase.Index : null;
        if (ReferenceEquals(index, terms.Index))
            return;

        terms.Use(index, Termbase?.Name);
        foreach (var segment in Segments)
            segment.OnTermsChanged();
        ShowTerms(SelectedSegment);
        TermCheck = RecheckTermsAsync(terms);
        TermCheck.Forget();
    }

    // Not through UseQaOptions, which saves the settings. A report that has run is listed again afterwards, without
    // switching tabs; until then it is cleared, so it doesn't follow every unit's new result one by one.
    private async Task RecheckTermsAsync(DocumentTerms terms)
    {
        var generation = ++_termGeneration;
        if (Qa.HasRun)
        {
            Qa.Clear();
            _refreshReportFor = terms;
        }

        bool Current() => generation == _termGeneration && ReferenceEquals(terms, _terms);
        await UnitCheck.RecheckAsync([.. _checks], QaOptions, terms.Source, RunOffUiThread, Current);
        if (!Current() || !ReferenceEquals(_refreshReportFor, terms))
            return;

        _refreshReportFor = null;
        Qa.Refresh();
    }

    private void ShowTerms(SegmentViewModel? segment)
    {
        Terms.Clear();
        foreach (var hit in segment?.TermHits ?? [])
            Terms.Add(new TermViewModel(hit, InsertTermCommand));
        OnPropertyChanged(nameof(TermsStatus));
        InsertTermCommand.NotifyCanExecuteChanged();
    }

    /// <summary>A row's Insert button: its first allowed translation goes in at the caret, as text.</summary>
    [RelayCommand(CanExecute = nameof(CanInsertTerm))]
    private void InsertTerm(TermViewModel? term)
    {
        if (term?.FirstAllowed is { } text && SelectedSegment is { } segment)
            segment.InsertTerm(text);
    }

    private bool CanInsertTerm(TermViewModel? term) => term?.FirstAllowed is not null && SelectedSegment is not null;

    /// <summary>
    /// What the term picker (Ctrl+T) offers for the selected segment. If nothing, the status line says why, and the
    /// picker doesn't open.
    /// </summary>
    internal IReadOnlyList<TermChoice> TermPickerChoices()
    {
        if (SelectedSegment is not { } segment)
            return [];

        var choices = segment.TermChoices;
        if (choices.Count > 0)
            return choices;

        segment.EditStatus = Termbase switch
        {
            null when LoadingTermbase is not null => "The termbase is still loading.",
            null => "No termbase is open. Open one with Termbase → Open…",
            { } termbase when !TermbaseServes => $"The termbase “{termbase.Name}” doesn't serve this document's languages.",
            _ when segment.TermHits.Count == 0 => "No terms found in this segment's source.",
            _ => "The terms found have no translations to insert.",
        };
        return [];
    }

    // ---------------------------------------------------------------- the Termbase menu

    /// <summary>
    /// Makes <paramref name="termbase"/> the open termbase (null closes it), re-checks the document with it, and closes
    /// the previous one. <paramref name="remember"/>: open it again next time; false when the app shuts down, so it's
    /// remembered.
    /// </summary>
    public async Task UseTermbaseAsync(OpenTermbase? termbase, bool remember = true)
    {
        var previous = Termbase;
        Termbase = termbase;
        if (remember)
            SaveSettings(s => s with { TermbasePath = termbase?.Path }, "Could not remember the termbase for next time");
        OnTermsChanged();
        if (previous is not null && previous != termbase)
            await previous.DisposeAsync();
    }

    /// <summary>
    /// Called at startup: loads the termbase that was open last time, in the background (section 3). If it can't be
    /// opened, that's reported; a damaged one is forgotten, one that's missing isn't (it may be on a drive that isn't
    /// plugged in).
    /// </summary>
    public void RestoreTermbase()
    {
        if (Termbase is not null || LoadingTermbase is not null || settings?.Load().TermbasePath is not { } path)
            return;

        TermbaseLoad = LoadTermbaseAsync(path, restoring: true);
        TermbaseLoad.Forget();
    }

    // Loads in the background while the termbase already open stays in use, as a file or TM that fails to open leaves
    // the current one. A newer load, Close or shutting down cancels it; a termbase that opened all the same is closed.
    private async Task LoadTermbaseAsync(string path, bool restoring)
    {
        _termbaseLoad?.Cancel();
        using var load = new CancellationTokenSource();
        _termbaseLoad = load;
        LoadingTermbase = Path.GetFileName(path);
        OpenTermbase termbase;
        try
        {
            termbase = await OpenTermbaseFile(path, load.Token);
        }
        catch (OperationCanceledException) when (load.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (IsMemoryReportable(ex))
        {
            if (load.IsCancellationRequested)
                return;

            ErrorMessage = $"Could not open the termbase {Path.GetFileName(path)}: {ex.Message}";
            if (restoring && ex is TermbaseFormatException)
                SaveSettings(s => s with { TermbasePath = null }, "Could not forget the termbase");
            return;
        }
        finally
        {
            if (_termbaseLoad == load)
            {
                _termbaseLoad = null;
                LoadingTermbase = null;
            }
        }

        if (load.IsCancellationRequested)
        {
            await termbase.DisposeAsync();
            return;
        }

        if (!restoring)
            ErrorMessage = null;
        await UseTermbaseAsync(termbase, remember: !restoring);
    }

    // Cancels the load (if any) and waits for it to end, so the file it may have opened is closed.
    private async Task StopLoadingTermbaseAsync()
    {
        _termbaseLoad?.Cancel();
        _termbaseLoad = null;
        LoadingTermbase = null;
        await TermbaseLoad.ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    /// <summary>Creates a termbase for the open document's languages, asking for the target language first if the file has none.</summary>
    [RelayCommand(CanExecute = nameof(HasDocument))]
    private async Task NewTermbaseAsync()
    {
        var document = _document!;
        if (!await EnsureTargetLanguageAsync(required: true)
            || await dialogs.PickTermbaseToCreateAsync($"{document.SourceLanguage}-{document.TargetLanguage}.sctb") is not { } path)
            return;

        // Never replace a termbase: a client's terminology can be in one.
        var name = Path.GetFileName(path);
        if (File.Exists(path))
        {
            ErrorMessage = $"{name} already exists. Open it with Termbase → Open…, or choose another name.";
            return;
        }

        OpenTermbase termbase;
        try
        {
            termbase = OpenTermbase.Create(path, document.SourceLanguage, document.TargetLanguage!);
        }
        catch (Exception ex) when (IsMemoryReportable(ex))
        {
            ErrorMessage = $"Could not create {name}: {ex.Message}";
            return;
        }

        ErrorMessage = null;
        await StopLoadingTermbaseAsync();
        await UseTermbaseAsync(termbase);
    }

    /// <summary>
    /// Opens a termbase file, loading it in the background; the one open stays in use until it has loaded. Another can
    /// be opened meanwhile, which cancels this one.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task OpenTermbaseAsync()
    {
        if (await dialogs.PickTermbaseToOpenAsync() is not { } path)
            return;

        TermbaseLoad = LoadTermbaseAsync(path, restoring: false);
        await TermbaseLoad;
    }

    /// <summary>Closes the termbase, and stops one that is loading.</summary>
    [RelayCommand(CanExecute = nameof(CanCloseTermbase))]
    private async Task CloseTermbaseAsync()
    {
        _termbaseLoad?.Cancel();
        _termbaseLoad = null;
        LoadingTermbase = null;
        await UseTermbaseAsync(null);
    }

    private bool CanCloseTermbase() => Termbase is not null || LoadingTermbase is not null;
}
