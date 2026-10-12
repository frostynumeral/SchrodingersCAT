using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using SchrodingersCat.Memory;

namespace SchrodingersCat.App.ViewModels;

/// <summary>An encoding the CSV import dialog offers.</summary>
public sealed record CsvEncodingChoice(string Label, Encoding Encoding);

/// <summary>A row of the CSV import dialog's preview: its cells, as many as the widest previewed row has.</summary>
public sealed record CsvPreviewRow(IReadOnlyList<string> Cells);

/// <summary>
/// The CSV import dialog (section 4 of <c>docs/design/termbase.md</c>, built on <see cref="TermCsv"/>'s steps): the
/// encoding (asked when the file isn't UTF-8 and has no BOM), the first rows decoded with it, whether the first row is
/// a header, and which columns hold the source, the target, the note and the status, guessed from the header.
/// </summary>
public sealed partial class CsvImportViewModel : ObservableObject
{
    /// <summary>How many rows the preview shows.</summary>
    public const int PreviewRows = 8;

    /// <summary>The label of "no column" in the note and status lists.</summary>
    public const string NoColumn = "(none)";

    private readonly byte[] _bytes;
    private readonly string _sourceLanguage;
    private readonly string _targetLanguage;
    private readonly Func<Action, Task> _runOffUiThread;

    // The table parsed with the chosen encoding; null while parsing, or if it can't be.
    private CsvTable? _table;

    // Counts the parses, so only the latest applies.
    private int _generation;

    // The columns were guessed once, from the first table parsed.
    private bool _guessed;

    /// <param name="runOffUiThread">Parses in the background: <see cref="Task.Run(Action)"/> in the app.</param>
    public CsvImportViewModel(byte[] bytes, string sourceLanguage, string targetLanguage, Func<Action, Task> runOffUiThread)
    {
        _bytes = bytes ?? throw new ArgumentNullException(nameof(bytes));
        _sourceLanguage = sourceLanguage ?? throw new ArgumentNullException(nameof(sourceLanguage));
        _targetLanguage = targetLanguage ?? throw new ArgumentNullException(nameof(targetLanguage));
        _runOffUiThread = runOffUiThread ?? throw new ArgumentNullException(nameof(runOffUiThread));

        HasBom = bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])
            || bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]) || bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]);
        var detected = TermCsv.DetectEncoding(bytes);
        if (detected is null)
            EncodingMessage = "This file isn't UTF-8. Choose its encoding, and check the preview.";
        else if (HasBom)
            EncodingMessage = $"The file says it is {(detected is UnicodeEncoding ? "UTF-16" : "UTF-8")}.";

        // A BOM decides whatever is chosen (TermCsv.Parse), so the choice shows what it says.
        _selectedEncoding = detected switch
        {
            UnicodeEncoding => Encodings[1],
            null => Encodings.First(e => e.Encoding.CodePage == 1252),
            _ => Encodings[0],
        };
        Parsing = ParseAsync();
    }

    /// <summary>
    /// UTF-8 and UTF-16, then the legacy code pages Excel saves CSV in for European languages (Windows, and ISO 8859-2).
    /// </summary>
    public IReadOnlyList<CsvEncodingChoice> Encodings { get; } =
    [
        new("UTF-8", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)),
        new("UTF-16", new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true)),
        new("Central European (Windows-1250)", TermCsv.LegacyEncoding(1250)),
        new("Cyrillic (Windows-1251)", TermCsv.LegacyEncoding(1251)),
        new("Western European (Windows-1252)", TermCsv.LegacyEncoding(1252)),
        new("Greek (Windows-1253)", TermCsv.LegacyEncoding(1253)),
        new("Turkish (Windows-1254)", TermCsv.LegacyEncoding(1254)),
        new("Baltic (Windows-1257)", TermCsv.LegacyEncoding(1257)),
        new("Central European (ISO-8859-2)", TermCsv.LegacyEncoding(28592)),
    ];

    /// <summary>The file starts with a BOM, which decides the encoding: the choice is then disabled.</summary>
    public bool HasBom { get; }

    public bool CanChooseEncoding => !HasBom;

    /// <summary>What the dialog says about the encoding: that it has to be chosen, or what the BOM says.</summary>
    public string? EncodingMessage { get; }

    private CsvEncodingChoice _selectedEncoding;

    /// <summary>The encoding; choosing another parses the file again, in the background.</summary>
    public CsvEncodingChoice SelectedEncoding
    {
        get => _selectedEncoding;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (SetProperty(ref _selectedEncoding, value))
                Parsing = ParseAsync();
        }
    }

    /// <summary>The latest parse; tests await it.</summary>
    internal Task Parsing { get; private set; }

    /// <summary>The file is being parsed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanImport))]
    public partial bool IsParsing { get; private set; }

    /// <summary>The first row holds column names, not terms (the default, as most files have one).</summary>
    [ObservableProperty]
    public partial bool FirstRowIsHeader { get; set; } = true;

    /// <summary>The columns: the header's names, or "Column 1", "Column 2", … without a header.</summary>
    public ObservableCollection<string> Columns { get; } = [];

    /// <summary><see cref="NoColumn"/>, then <see cref="Columns"/>: for the note and status, which a file may not have.</summary>
    public ObservableCollection<string> OptionalColumns { get; } = [NoColumn];

    /// <summary>The source terms' column (in <see cref="Columns"/>).</summary>
    [ObservableProperty]
    public partial int SourceColumn { get; set; }

    /// <summary>The translations' column (in <see cref="Columns"/>).</summary>
    [ObservableProperty]
    public partial int TargetColumn { get; set; } = 1;

    /// <summary>The note's column in <see cref="OptionalColumns"/>: 0 for none.</summary>
    [ObservableProperty]
    public partial int NoteColumn { get; set; }

    /// <summary>The status's column in <see cref="OptionalColumns"/>: 0 for none.</summary>
    [ObservableProperty]
    public partial int StatusColumn { get; set; }

    /// <summary>The first rows, decoded with the chosen encoding.</summary>
    public ObservableCollection<CsvPreviewRow> Preview { get; } = [];

    /// <summary>What the dialog says about the columns: that the guess failed, so they should be checked.</summary>
    [ObservableProperty]
    public partial string? ColumnMessage { get; private set; }

    /// <summary>Why the file can't be imported as chosen (it doesn't decode, the columns clash); cleared by a change that fixes it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanImport))]
    public partial string? Problem { get; private set; }

    /// <summary>How many rows the file has, for the dialog.</summary>
    [ObservableProperty]
    public partial string? Summary { get; private set; }

    public bool CanImport => !IsParsing && _table is not null && Problem is null;

    /// <summary>The import of the parsed table with the chosen columns, or null with <see cref="Problem"/> set.</summary>
    public TermImport? TryImport()
    {
        if (IsParsing || _table is null)
            return null;
        if (ColumnProblem() is { } problem)
        {
            Problem = problem;
            return null;
        }

        return TermCsv.Import(_table, new CsvColumns(SourceColumn, TargetColumn, Optional(NoteColumn), Optional(StatusColumn)), FirstRowIsHeader);
    }

    partial void OnFirstRowIsHeaderChanged(bool value) => ShowColumns();

    partial void OnSourceColumnChanged(int value) => CheckColumns();

    partial void OnTargetColumnChanged(int value) => CheckColumns();

    partial void OnNoteColumnChanged(int value) => CheckColumns();

    partial void OnStatusColumnChanged(int value) => CheckColumns();

    // The whole file, off the UI thread: a client's spreadsheet can be large. A parse superseded meanwhile drops its result.
    private async Task ParseAsync()
    {
        var generation = ++_generation;
        var encoding = SelectedEncoding.Encoding;
        _table = null;
        IsParsing = true;
        CsvTable? table = null;
        string? problem = null;
        await _runOffUiThread(() =>
        {
            try
            {
                table = TermCsv.Parse(_bytes, encoding);
            }
            catch (TermbaseFormatException ex)
            {
                problem = ex.Message;
            }
        });
        if (generation != _generation)
            return;

        _table = table;
        IsParsing = false;
        Preview.Clear();
        if (table is null)
        {
            Summary = null;
            Problem = problem;
            return;
        }

        var width = table.Rows.Take(PreviewRows).Select(r => r.Count).DefaultIfEmpty(0).Max();
        foreach (var row in table.Rows.Take(PreviewRows))
            Preview.Add(new CsvPreviewRow([.. Enumerable.Range(0, width).Select(i => i < row.Count ? row[i] : "")]));
        Summary = $"{table.Rows.Count} {(table.Rows.Count == 1 ? "row" : "rows")}, separated by {SeparatorName(table.Separator)}.";
        ShowColumns();
        Problem = null;
        CheckColumns();
    }

    // The column lists for the table, and once, the columns guessed from its header.
    private void ShowColumns()
    {
        if (_table is not { } table)
            return;

        var (source, target, note, status) = (SourceColumn, TargetColumn, NoteColumn, StatusColumn);
        var header = table.Rows.Count > 0 ? table.Rows[0] : [];
        var count = Math.Max(2, table.Rows.Select(r => r.Count).DefaultIfEmpty(0).Max());
        Columns.Clear();
        OptionalColumns.Clear();
        OptionalColumns.Add(NoColumn);
        for (var i = 0; i < count; i++)
        {
            var name = FirstRowIsHeader && i < header.Count && !string.IsNullOrWhiteSpace(header[i]) ? $"{i + 1}: {header[i].Trim()}" : $"Column {i + 1}";
            Columns.Add(name);
            OptionalColumns.Add(name);
        }

        if (!_guessed)
        {
            _guessed = true;
            if (TermCsv.GuessColumns(header, _sourceLanguage, _targetLanguage) is { } guess)
            {
                (source, target, note, status) = (guess.Source, guess.Target, guess.Note is { } n ? n + 1 : 0, guess.Status is { } s ? s + 1 : 0);
            }
            else
            {
                (source, target) = (0, 1);
                ColumnMessage = $"Couldn't tell the {_sourceLanguage} and {_targetLanguage} columns from the first row; check them.";
            }
        }

        // Clearing the lists cleared the selections; they come back where they were.
        SourceColumn = Math.Min(source, count - 1);
        TargetColumn = Math.Min(target, count - 1);
        NoteColumn = Math.Min(note, count);
        StatusColumn = Math.Min(status, count);
    }

    private void CheckColumns()
    {
        if (_table is not null)
            Problem = ColumnProblem();
    }

    private string? ColumnProblem()
    {
        if (SourceColumn < 0 || TargetColumn < 0)
            return "Choose the source and target columns.";
        if (SourceColumn == TargetColumn)
            return "The source and the translations can't be in the same column.";
        return null;
    }

    private static int? Optional(int column) => column > 0 ? column - 1 : null;

    private static string SeparatorName(char separator) => separator switch
    {
        '\t' => "tabs",
        ';' => "semicolons",
        _ => "commas",
    };
}
