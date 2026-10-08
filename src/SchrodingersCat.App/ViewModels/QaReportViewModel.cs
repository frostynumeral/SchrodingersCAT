using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SchrodingersCat.App.Services;
using SchrodingersCat.Core.Checks;
using SchrodingersCat.Core.Models;

namespace SchrodingersCat.App.ViewModels;

/// <summary>A row of the QA report: a check's heading, or one issue.</summary>
public abstract class QaReportRow : ObservableObject
{
    /// <summary>An issue, which can be selected and opened, not a heading.</summary>
    public bool IsEntry => this is QaReportEntry;
}

/// <summary>A check's heading in the report, with how many issues it found.</summary>
public sealed class QaReportHeader(string check, int count) : QaReportRow
{
    public string Check { get; } = check;

    public int Count { get; } = count;

    public string Text => $"{Check} ({Count})";
}

/// <summary>
/// One issue in the QA report, kept as it was when the report ran: it shows as fixed (struck through) once the issue
/// is gone, and greyed once dismissed, until the report runs again.
/// </summary>
public sealed partial class QaReportEntry : QaReportRow
{
    private readonly InlineContent? _targetAtRun;

    /// <param name="kind">The quality check that found it; null for the code check.</param>
    /// <param name="acrossSegments">Found by a document check (untranslated, inconsistent), not on the segment's badge.</param>
    public QaReportEntry(SegmentViewModel segment, IssueSeverity severity, string message, QaIssueKind? kind, bool acrossSegments)
    {
        Segment = segment ?? throw new ArgumentNullException(nameof(segment));
        Severity = severity;
        Message = message ?? throw new ArgumentNullException(nameof(message));
        Kind = kind;
        AcrossSegments = acrossSegments;
        _targetAtRun = segment.Target;
    }

    public SegmentViewModel Segment { get; }

    public IssueSeverity Severity { get; }

    public string Message { get; }

    public QaIssueKind? Kind { get; }

    /// <summary>Found by a document check, so not among the segment's own issues.</summary>
    public bool AcrossSegments { get; }

    public bool IsError => Severity == IssueSeverity.Error;

    public bool IsWarning => Severity == IssueSeverity.Warning;

    /// <summary>Only a quality check's issue can be dismissed (code errors decide the save prompt and the TM store).</summary>
    public bool CanDismiss => Kind is not null;

    [ObservableProperty]
    public partial bool IsDismissed { get; private set; }

    /// <summary>The issue is gone since the report ran (for a document check: the segment's target has changed).</summary>
    [ObservableProperty]
    public partial bool IsFixed { get; private set; }

    /// <param name="stillThere">For a segment's own issue: whether the segment still has it.</param>
    internal void Update(bool stillThere)
    {
        IsDismissed = Kind is { } kind && Segment.IsDismissed(kind, Message);
        IsFixed = AcrossSegments
            ? (Kind == QaIssueKind.Untranslated ? Segment.Target is not null : Segment.Target != _targetAtRun)
            : !stillThere;
    }

    /// <summary>
    /// Updates a segment's entries. Identical issues (the same number missing twice) count one by one: as many entries
    /// stay open as the segment still has such issues.
    /// </summary>
    internal static void UpdateAll(SegmentViewModel segment, IEnumerable<QaReportEntry> entries)
    {
        var left = segment.AllIssues.GroupBy(i => (i.Qa?.Kind, i.Message)).ToDictionary(g => g.Key, g => g.Count());
        foreach (var entry in entries)
        {
            var key = (entry.Kind, entry.Message);
            var stillThere = !entry.AcrossSegments && left.GetValueOrDefault(key) > 0;
            if (stillThere)
                left[key]--;
            entry.Update(stillThere);
        }
    }
}

/// <summary>
/// The QA tab (step 4 of <c>docs/design/qa-checks.md</c>): F8 checks the whole document, the code check, the segment
/// checks and the document checks, and lists every issue grouped by check. Opening an entry selects its segment;
/// entries can be dismissed and restored. The list stays as it was until F8 runs again.
/// </summary>
public sealed partial class QaReportViewModel(MainWindowViewModel owner, IDialogService dialogs) : ObservableObject
{
    private const string Hint = "Press F8 to check the whole document.";

    // The segments the report follows, to update their entries as they change.
    private readonly Dictionary<SegmentViewModel, List<QaReportEntry>> _entries = [];

    public ObservableCollection<QaReportRow> Rows { get; } = [];

    [ObservableProperty]
    public partial string Status { get; private set; } = Hint;

    /// <summary>Raised when an entry is opened, after its segment is selected: the view gives the editor the keyboard.</summary>
    public event EventHandler? EditorRequested;

    /// <summary>F8: checks the document and shows the report.</summary>
    [RelayCommand]
    private void Run()
    {
        owner.PaneTab = 2;
        Clear();
        if (owner.Document is not { } document)
        {
            Status = "Open a document to check it.";
            return;
        }

        var segments = owner.Segments.ToList();
        var bySegment = segments.ToDictionary(s => s.Segment);
        var entries = new List<(int Order, string Check, QaReportEntry Entry)>();
        for (var i = 0; i < segments.Count; i++)
        {
            foreach (var issue in segments[i].AllIssues)
                entries.Add((i, issue.Check, new QaReportEntry(segments[i], issue.Severity, issue.Message, issue.Qa?.Kind, acrossSegments: false)));
        }

        var order = segments.Select((s, i) => (s, i)).ToDictionary(p => p.s, p => p.i);
        foreach (var issue in DocumentChecker.Check(document, owner.QaOptions))
        {
            if (bySegment.TryGetValue(issue.Segment, out var segment))
                entries.Add((order[segment], QaChecks.Label(issue.Kind), new QaReportEntry(segment, issue.Severity, issue.Message, issue.Kind, acrossSegments: true)));
        }

        // The code check's group first, then the quality checks in their usual order; segments in document order.
        var checks = new[] { QaChecks.CodesLabel }.Concat(QaChecks.All.Select(QaChecks.Label)).ToList();
        foreach (var group in entries.GroupBy(e => e.Check).OrderBy(g => checks.IndexOf(g.Key)))
        {
            Rows.Add(new QaReportHeader(group.Key, group.Count()));
            foreach (var (_, _, entry) in group.OrderBy(e => e.Order))
            {
                Rows.Add(entry);
                if (!_entries.TryGetValue(entry.Segment, out var list))
                {
                    _entries[entry.Segment] = list = [];
                    entry.Segment.PropertyChanged += OnSegmentChanged;
                }

                list.Add(entry);
            }
        }

        foreach (var (segment, list) in _entries)
            QaReportEntry.UpdateAll(segment, list);
        UpdateStatus();
    }

    /// <summary>Selects the entry's segment, and asks the view to give its editor the keyboard (click, Enter).</summary>
    [RelayCommand]
    private void Open(QaReportEntry? entry)
    {
        if (entry is null)
            return;

        owner.SelectedSegment = entry.Segment;
        EditorRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Dismisses a quality issue, or restores a dismissed one.</summary>
    [RelayCommand(CanExecute = nameof(CanToggleDismissed))]
    private void ToggleDismissed(QaReportEntry? entry)
    {
        if (entry?.Kind is { } kind)
            entry.Segment.SetDismissed(kind, entry.Message, !entry.IsDismissed);
    }

    private static bool CanToggleDismissed(QaReportEntry? entry) => entry?.CanDismiss == true;

    /// <summary>The settings: which quality checks run. Changing them checks every segment again, and the report if it ran.</summary>
    [RelayCommand]
    private async Task SettingsAsync()
    {
        var choices = new QaSettingsViewModel(owner.QaOptions);
        if (!await dialogs.EditQaSettingsAsync(choices))
            return;

        var ran = _entries.Count > 0 || Rows.Count > 0 || Status != Hint;
        // The report runs again anyway: following every segment's re-check first would only cost time.
        Clear();
        owner.UseQaOptions(choices.ToOptions());
        if (ran && owner.Document is not null)
            Run();
    }

    /// <summary>Forgets the report (another document was opened).</summary>
    internal void Clear()
    {
        foreach (var segment in _entries.Keys)
            segment.PropertyChanged -= OnSegmentChanged;
        _entries.Clear();
        Rows.Clear();
        Status = Hint;
    }

    private void OnSegmentChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not SegmentViewModel segment || e.PropertyName is not (nameof(SegmentViewModel.AllIssues) or nameof(SegmentViewModel.Dismissals) or nameof(SegmentViewModel.TargetText)))
            return;

        QaReportEntry.UpdateAll(segment, _entries[segment]);
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var entries = Rows.OfType<QaReportEntry>().ToList();
        if (entries.Count == 0)
        {
            Status = "No issues found.";
            return;
        }

        var open = entries.Count(e => !e.IsFixed && !e.IsDismissed);
        var parts = new List<string>
        {
            $"{entries.Count} {(entries.Count == 1 ? "issue" : "issues")} in {entries.Select(e => e.Segment).Distinct().Count()} segments",
        };
        if (entries.Count(e => e.IsFixed) is var fixedCount and > 0)
            parts.Add($"{fixedCount} fixed");
        if (entries.Count(e => e.IsDismissed) is var dismissed and > 0)
            parts.Add($"{dismissed} dismissed");
        Status = string.Join(", ", parts) + (open < entries.Count ? $"; {open} left. Press F8 to check again." : ".");
    }
}

/// <summary>One quality check in the settings: whether it runs.</summary>
public sealed partial class QaCheckChoice(QaIssueKind kind, bool isEnabled) : ObservableObject
{
    public QaIssueKind Kind { get; } = kind;

    public string Label => QaChecks.Label(Kind);

    [ObservableProperty]
    public partial bool IsEnabled { get; set; } = isEnabled;
}

/// <summary>The QA settings dialog: a check box per quality check (decision 1: all on by default, each switchable).</summary>
public sealed class QaSettingsViewModel(QaOptions options)
{
    public IReadOnlyList<QaCheckChoice> Choices { get; } = [.. QaChecks.All.Select(k => new QaCheckChoice(k, options.IsEnabled(k)))];

    public QaOptions ToOptions() => new() { Disabled = Choices.Where(c => !c.IsEnabled).Select(c => c.Kind).ToHashSet() };
}
