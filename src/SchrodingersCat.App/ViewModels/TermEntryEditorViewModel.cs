using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SchrodingersCat.Memory;

namespace SchrodingersCat.App.ViewModels;

/// <summary>One term in the entry dialog: its text, status and note.</summary>
public sealed partial class TermRowViewModel : ObservableObject
{
    /// <summary>The statuses the row's list offers, in <see cref="TermStatus"/>'s order (<see cref="StatusIndex"/>).</summary>
    public static IReadOnlyList<string> StatusNames { get; } = ["Unspecified", "Preferred", "Admitted", "Forbidden"];

    public TermRowViewModel(string text = "", TermStatus status = TermStatus.Unspecified, string? note = null)
    {
        Text = text ?? throw new ArgumentNullException(nameof(text));
        Status = status;
        Note = note;
    }

    [ObservableProperty]
    public partial string Text { get; set; }

    /// <summary>Unspecified for a new term, so a term typed without a status isn't exported as preferred (section 1).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusIndex))]
    public partial TermStatus Status { get; set; }

    /// <summary><see cref="Status"/> as a place in <see cref="StatusNames"/>, for the row's list.</summary>
    public int StatusIndex
    {
        get => (int)Status;
        set
        {
            if (Enum.IsDefined((TermStatus)value))
                Status = (TermStatus)value;
        }
    }

    [ObservableProperty]
    public partial string? Note { get; set; }

    /// <summary>The row as a term: trimmed, a blank note none; null if the text is blank.</summary>
    internal Term? ToTerm() => string.IsNullOrWhiteSpace(Text)
        ? null
        : new Term(Text.Trim(), Status) { Note = string.IsNullOrWhiteSpace(Note) ? null : Note.Trim() };
}

/// <summary>A choice in the entry dialog when adding a term: make a new entry, or add to one that has the term already.</summary>
/// <param name="Stored">The entry to add to; null for a new entry.</param>
public sealed record ExistingEntryChoice(StoredTermEntry? Stored, string Label)
{
    public static ExistingEntryChoice NewEntry { get; } = new(null, "Make a new entry");

    /// <summary>"file, data file → fájl, állomány", forbidden translations left out.</summary>
    public static ExistingEntryChoice Of(StoredTermEntry stored)
    {
        ArgumentNullException.ThrowIfNull(stored);

        var source = string.Join(", ", stored.Entry.Source.Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)));
        var translations = TermTips.Ordered(stored.Entry).Where(t => !t.IsForbidden).Select(t => t.Text).ToList();
        return new(stored, $"Add to “{source}” → {(translations.Count == 0 ? "no translation yet" : string.Join(", ", translations))}");
    }
}

/// <summary>
/// What the entry dialog asks to store: a new entry (<see cref="Id"/> null), an entry's new content, or (with
/// <see cref="IsAddition"/>) terms and a note to add to an entry (<see cref="MergeInto"/>). The date and user are the
/// window's to stamp.
/// </summary>
public sealed record TermEntryEdit(TermEntryId? Id, TermEntry Entry, bool IsAddition)
{
    /// <summary>
    /// The entry with <paramref name="addition"/>'s terms and note added (step 6's rule): its source and target terms
    /// the entry doesn't have yet (<see cref="TermEntry.SameTerm"/>) appended as synonyms and translations, in their
    /// order; the terms it has keep their stored status and note (Edit changes those); a note it doesn't contain yet
    /// appended on a new line. The entry as it is if there's nothing to add.
    /// </summary>
    public static TermEntry MergeInto(TermEntry entry, TermEntry addition)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(addition);

        var source = New(entry.Source, addition.Source);
        var target = New(entry.Target, addition.Target);
        var note = addition.Note is { } added && !string.IsNullOrWhiteSpace(added) && !(entry.Note ?? "").Contains(added, StringComparison.Ordinal)
            ? string.IsNullOrWhiteSpace(entry.Note) ? added : $"{entry.Note}\n{added}"
            : entry.Note;
        return source.Count == 0 && target.Count == 0 && note == entry.Note
            ? entry
            : entry with { Source = [.. entry.Source, .. source], Target = [.. entry.Target, .. target], Note = note };
    }

    /// <summary>Whether two entries hold the same terms and note, whatever their dates and users.</summary>
    public static bool SameContent(TermEntry a, TermEntry b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return a with { Changed = default, ChangedBy = null } == b with { Changed = default, ChangedBy = null };
    }

    // The added terms the entry doesn't have, each once.
    private static List<Term> New(IReadOnlyList<Term> have, IReadOnlyList<Term> added)
    {
        var list = new List<Term>();
        foreach (var term in added)
        {
            if (!have.Concat(list).Any(t => TermEntry.SameTerm(t.Text, term.Text)))
                list.Add(term);
        }

        return list;
    }
}

/// <summary>
/// The entry dialog (section 6 of <c>docs/design/termbase.md</c>): adding a term (Ctrl+E), with the selected source
/// and target text filled in and the entries that have that source term already offered, or editing an entry (the
/// terms list's context menu). Source terms and translations each with a status and a note, and the entry's note.
/// </summary>
public sealed partial class TermEntryEditorViewModel : ObservableObject
{
    // The termbase as the dialog opened, for the entries that have the first source term already; null when editing.
    private readonly TermIndex? _snapshot;

    // The first source term the existing entries were found for.
    private string? _existingFor;

    private TermEntryEditorViewModel(TermIndex? snapshot, TermEntryId? id, TermEntry? original, string termbaseName, string sourceLanguage, string targetLanguage)
    {
        ArgumentNullException.ThrowIfNull(termbaseName);
        ArgumentNullException.ThrowIfNull(sourceLanguage);
        ArgumentNullException.ThrowIfNull(targetLanguage);

        _snapshot = snapshot;
        Id = id;
        Original = original;
        TermbaseName = termbaseName;
        SourceLanguage = sourceLanguage;
        TargetLanguage = targetLanguage;
        SourceTerms.CollectionChanged += OnRowsChanged;
        TargetTerms.CollectionChanged += OnRowsChanged;
    }

    /// <summary>Adding a term: one source row and one translation row with the selected texts (perhaps empty).</summary>
    public static TermEntryEditorViewModel ForAdd(TermIndex snapshot, string termbaseName, string sourceLanguage, string targetLanguage, string sourceText, string targetText)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(sourceText);
        ArgumentNullException.ThrowIfNull(targetText);

        var editor = new TermEntryEditorViewModel(snapshot, null, null, termbaseName, sourceLanguage, targetLanguage);
        editor.SourceTerms.Add(new TermRowViewModel(sourceText));
        editor.TargetTerms.Add(new TermRowViewModel(targetText));
        editor.FindExisting();
        return editor;
    }

    /// <summary>Editing a stored entry: a row per term, and an empty translation row if it has none.</summary>
    public static TermEntryEditorViewModel ForEdit(TermEntryId id, TermEntry entry, string termbaseName, string sourceLanguage, string targetLanguage)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var editor = new TermEntryEditorViewModel(null, id, entry, termbaseName, sourceLanguage, targetLanguage) { EntryNote = entry.Note };
        foreach (var term in entry.Source)
            editor.SourceTerms.Add(new TermRowViewModel(term.Text, term.Status, term.Note));
        foreach (var term in entry.Target)
            editor.TargetTerms.Add(new TermRowViewModel(term.Text, term.Status, term.Note));
        if (editor.TargetTerms.Count == 0)
            editor.TargetTerms.Add(new TermRowViewModel());
        return editor;
    }

    /// <summary>The entry being edited; null when adding.</summary>
    public TermEntryId? Id { get; }

    /// <summary>The entry as it was stored when the dialog opened; null when adding.</summary>
    public TermEntry? Original { get; }

    public bool IsAdding => Id is null;

    public string Title => IsAdding ? "Add term" : "Edit term entry";

    public string TermbaseName { get; }

    public string SourceLanguage { get; }

    public string TargetLanguage { get; }

    /// <summary>The termbase and its languages, in one line.</summary>
    public string Header => $"Termbase “{TermbaseName}” ({SourceLanguage} → {TargetLanguage})";

    public string SourceHeader => $"Source terms ({SourceLanguage})";

    public string TargetHeader => $"Translations ({TargetLanguage})";

    /// <summary>The term and its synonyms.</summary>
    public ObservableCollection<TermRowViewModel> SourceTerms { get; } = [];

    /// <summary>The translations.</summary>
    public ObservableCollection<TermRowViewModel> TargetTerms { get; } = [];

    /// <summary>About the concept: definition, context, the client's instruction.</summary>
    [ObservableProperty]
    public partial string? EntryNote { get; set; }

    /// <summary>
    /// When adding: a new entry, then the entries that have the first source term already (section 6), so the
    /// translation can go into one of them; empty if there are none.
    /// </summary>
    public ObservableCollection<ExistingEntryChoice> Existing { get; } = [];

    public bool HasExisting => Existing.Count > 0;

    /// <summary>Where the terms go: a new entry, or one of <see cref="Existing"/> (the first, when there are some).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MergeSummary))]
    public partial ExistingEntryChoice? SelectedExisting { get; set; }

    /// <summary>What adding to the chosen entry does: which terms it adds, or that it has them all.</summary>
    public string? MergeSummary
    {
        get
        {
            if (SelectedExisting?.Stored is not { } stored)
                return HasExisting ? "Makes a new entry beside those that have this term (a homonym)." : null;
            if (Typed() is not { } typed)
                return null;

            var merged = TermEntryEdit.MergeInto(stored.Entry, typed);
            var added = merged.Source.Skip(stored.Entry.Source.Count).Concat(merged.Target.Skip(stored.Entry.Target.Count)).Select(t => $"“{t.Text}”").ToList();
            if (added.Count == 0)
                return merged.Note == stored.Entry.Note ? "This entry has these terms already." : "Adds the note to this entry.";
            return $"Adds {string.Join(", ", added)} to this entry{(merged.Note == stored.Entry.Note ? "" : ", and the note")}. The terms it has keep their status and note.";
        }
    }

    /// <summary>Why the last attempt to save was refused; cleared by the next change.</summary>
    [ObservableProperty]
    public partial string? Problem { get; private set; }

    [RelayCommand]
    private void AddSynonym() => SourceTerms.Add(new TermRowViewModel());

    [RelayCommand]
    private void AddTranslation() => TargetTerms.Add(new TermRowViewModel());

    [RelayCommand]
    private void RemoveRow(TermRowViewModel? row)
    {
        if (row is not null && !SourceTerms.Remove(row))
            TargetTerms.Remove(row);
    }

    /// <summary>
    /// What to store, or null with <see cref="Problem"/> set if it can't be: terms are trimmed and blank rows left out;
    /// an entry needs a source term (an entry without translations is allowed, as an import keeps one); a side may
    /// not have the same term twice; adding to an entry must add something.
    /// </summary>
    public TermEntryEdit? TrySave()
    {
        var source = SourceTerms.Select(r => r.ToTerm()).OfType<Term>().ToList();
        var target = TargetTerms.Select(r => r.ToTerm()).OfType<Term>().ToList();
        if (source.Count == 0)
        {
            Problem = IsAdding
                ? "An entry needs a source term."
                : "An entry needs a source term. To remove it, delete the entry instead.";
            return null;
        }

        if ((Duplicate(source) ?? Duplicate(target)) is { } duplicate)
        {
            Problem = $"“{duplicate.First}” and “{duplicate.Second}” are the same term; keep one.";
            return null;
        }

        var entry = new TermEntry(source, target, string.IsNullOrWhiteSpace(EntryNote) ? null : EntryNote.Trim(), default, null);
        if (!IsAdding)
        {
            Problem = null;
            return new TermEntryEdit(Id, entry, IsAddition: false);
        }

        if (SelectedExisting?.Stored is not { } stored)
        {
            Problem = null;
            return new TermEntryEdit(null, entry, IsAddition: false);
        }

        if (TermEntryEdit.MergeInto(stored.Entry, entry) == stored.Entry)
        {
            Problem = "This entry has these terms already. Choose another entry, or make a new one.";
            return null;
        }

        Problem = null;
        return new TermEntryEdit(stored.Id, entry, IsAddition: true);
    }

    // The rows as an entry, if they have a source term; for the merge summary.
    private TermEntry? Typed()
    {
        var source = SourceTerms.Select(r => r.ToTerm()).OfType<Term>().ToList();
        return source.Count == 0
            ? null
            : new TermEntry(source, [.. TargetTerms.Select(r => r.ToTerm()).OfType<Term>()], string.IsNullOrWhiteSpace(EntryNote) ? null : EntryNote.Trim(), default, null);
    }

    private static (string First, string Second)? Duplicate(List<Term> terms)
    {
        for (var i = 0; i < terms.Count; i++)
        {
            for (var j = i + 1; j < terms.Count; j++)
            {
                if (TermEntry.SameTerm(terms[i].Text, terms[j].Text))
                    return (terms[i].Text, terms[j].Text);
            }
        }

        return null;
    }

    partial void OnEntryNoteChanged(string? value) => OnChanged();

    partial void OnSelectedExistingChanged(ExistingEntryChoice? value) => Problem = null;

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var row in e.OldItems?.OfType<TermRowViewModel>() ?? [])
            row.PropertyChanged -= OnRowChanged;
        foreach (var row in e.NewItems?.OfType<TermRowViewModel>() ?? [])
            row.PropertyChanged += OnRowChanged;
        if (sender == SourceTerms)
            FindExisting();
        OnChanged();
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is TermRowViewModel row && row == SourceTerms.FirstOrDefault() && e.PropertyName == nameof(TermRowViewModel.Text))
            FindExisting();
        OnChanged();
    }

    private void OnChanged()
    {
        Problem = null;
        OnPropertyChanged(nameof(MergeSummary));
    }

    // When adding: the entries with the first source term, found again when it changes. The first is chosen, since a
    // translation for a term the termbase has usually belongs to its entry; a new entry (a homonym) is a choice away.
    private void FindExisting()
    {
        if (_snapshot is null)
            return;

        var term = SourceTerms.FirstOrDefault()?.Text ?? "";
        if (term == _existingFor)
            return;

        _existingFor = term;
        Existing.Clear();
        var found = _snapshot.WithSourceTerm(term);
        if (found.Count > 0)
        {
            Existing.Add(ExistingEntryChoice.NewEntry);
            foreach (var stored in found)
                Existing.Add(ExistingEntryChoice.Of(stored));
        }

        OnPropertyChanged(nameof(HasExisting));
        SelectedExisting = Existing.Count > 1 ? Existing[1] : null;
    }
}
