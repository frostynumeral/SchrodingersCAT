using System.Text;
using SchrodingersCat.App.Services;
using SchrodingersCat.App.ViewModels;
using SchrodingersCat.Core.Xliff;
using SchrodingersCat.Memory;
using static SchrodingersCat.App.Tests.TermbaseFiles;

namespace SchrodingersCat.App.Tests;

/// <summary>
/// Step 6 of <c>docs/design/termbase.md</c>: Termbase → Import… and Export… with step 4's formats, and the CSV import
/// dialog's view model. Real termbase files, and the samples in <c>samples/termbase</c>.
/// </summary>
public sealed class TermExchangeTests : IAsyncDisposable
{
    private const string Document = """
        <xliff xmlns="urn:oasis:names:tc:xliff:document:2.0" version="2.0" srcLang="en" trgLang="hu">
          <file id="f1">
            <unit id="u1"><segment><source>Open the folder.</source><target>Nyisd meg a könyvtárat.</target></segment></unit>
            <unit id="u2"><segment><source>Nothing here.</source><target>Semmi.</target></segment></unit>
          </file>
        </xliff>
        """;

    private readonly TestFiles _files = new();
    private readonly List<MainWindowViewModel> _viewModels = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var vm in _viewModels)
            await vm.ShutDownAsync();
        _files.Dispose();
    }

    // ---------------------------------------------------------------- import

    [Fact]
    public async Task ImportingTbx_AddsTheEntriesInOneChange_StampsTheDate_ReportsTheRest_AndRechecks()
    {
        var (vm, dialogs) = await OpenWithTermbaseAsync();
        dialogs.TermsToImport = Sample("tbx2-basic.tbx");
        var before = DateTimeOffset.UtcNow;

        await vm.ImportTermsCommand.ExecuteAsync(null);
        await vm.TermCheck;

        Assert.Equal(
            "Imported 4 entries from tbx2-basic.tbx into client. Skipped 2 entries: Entry 3: no en term.; Entry 5: no hu term."
            + " 1 notice: Entry 2: the status \"obsoleteTerm-admn-sts\" of \"fájl\" isn't known; the term is imported without one."
            + " Dropped: langSet xml:lang=\"de\" ×1, partOfSpeech ×1, source ×1, subjectField ×1.",
            vm.Notice);
        var entries = vm.Termbase!.Index.Entries().Select(e => e.Entry).ToList();
        Assert.Equal(5, entries.Count);
        // The file's own date and user stay; an entry without one gets the import's date, not the translator's name.
        Assert.Equal((new DateTimeOffset(2025, 3, 14, 9, 26, 53, TimeSpan.Zero), "bela"), (entries[1].Changed, entries[1].ChangedBy));
        Assert.True(entries[2].Changed >= before.AddSeconds(-1));
        Assert.Null(entries[2].ChangedBy);
        // "folder" → "mappa" (preferred), "könyvtár" forbidden: u1 now has a terminology warning.
        Assert.Contains(vm.Segments[0].Issues, i => i.Qa?.Message.Contains("“folder”", StringComparison.Ordinal) == true);
    }

    [Theory]
    [InlineData("tbx2-utf16.tbx")]
    [InlineData("tbx2-latin1.tbx")]
    [InlineData("tbx3-basic-dca.tbx")]
    [InlineData("tbx3-basic-dct.tbx")]
    public async Task EveryTbxSample_Imports(string sample)
    {
        var (vm, dialogs) = await OpenWithTermbaseAsync();
        dialogs.TermsToImport = Sample(sample);
        var expected = await ReadTbxAsync(Sample(sample));

        await vm.ImportTermsCommand.ExecuteAsync(null);

        Assert.StartsWith($"Imported {expected.Entries.Count} {(expected.Entries.Count == 1 ? "entry" : "entries")} from {sample} into client.", vm.Notice, StringComparison.Ordinal);
        Assert.Equal(expected.Entries.Count + 1, vm.Termbase!.Index.Count);
        Assert.Null(vm.ErrorMessage);
    }

    // The format comes from the extension, else from the first character: TBX starts with '<'.
    [Fact]
    public async Task AFileWithAnotherExtension_IsTakenForTbxOrCsv_ByItsFirstCharacter()
    {
        var (vm, dialogs) = await OpenWithTermbaseAsync();
        dialogs.TermsToImport = _files.Write("terms.dat", File.ReadAllText(Sample("tbx3-basic-dca.tbx")));
        await vm.ImportTermsCommand.ExecuteAsync(null);
        Assert.DoesNotContain("CSV import", dialogs.Asked);
        Assert.StartsWith("Imported", vm.Notice, StringComparison.Ordinal);

        dialogs.TermsToImport = _files.Write("terms.export", "en,hu\nprinter,nyomtató\n");
        await vm.ImportTermsCommand.ExecuteAsync(null);
        Assert.Contains("CSV import", dialogs.Asked);
        Assert.StartsWith("Imported 1 entry from terms.export into client.", vm.Notice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportingCsv_GoesThroughTheDialog_AndCancellingImportsNothing()
    {
        var (vm, dialogs) = await OpenWithTermbaseAsync();
        dialogs.TermsToImport = Sample("formulas-and-pipes.csv");
        dialogs.ChooseCsvImport = _ => Task.FromResult<TermImport?>(null);
        var index = vm.Termbase!.Index;

        await vm.ImportTermsCommand.ExecuteAsync(null);
        Assert.Same(index, vm.Termbase.Index);
        Assert.Null(vm.Notice);

        dialogs.ChooseCsvImport = null; // the guessed columns
        await vm.ImportTermsCommand.ExecuteAsync(null);
        Assert.Equal(
            "Imported 5 entries from formulas-and-pipes.csv into client. Skipped 1 row: Row 6: no source term."
            + " 1 notice: Row 5: the status \"approved\" isn't known; the terms are imported without one.",
            vm.Notice);
        Assert.Contains(vm.Termbase.Index.Entries(), e => e.Entry.Source[0].Text == "=SUM(A1)");
    }

    [Fact]
    public async Task AFileThatIsntTbx_GoesToTheBanner()
    {
        var (vm, dialogs) = await OpenWithTermbaseAsync();
        dialogs.TermsToImport = _files.Write("broken.tbx", "<martif><text><body><termEntry>");

        await vm.ImportTermsCommand.ExecuteAsync(null);

        Assert.StartsWith("Could not import broken.tbx: ", vm.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(1, vm.Termbase!.Index.Count);
    }

    [Fact]
    public async Task AFileWithNoEntries_SaysSo()
    {
        var (vm, dialogs) = await OpenWithTermbaseAsync();
        dialogs.TermsToImport = _files.Write("empty.csv", "en,hu\n,nyomtató\n");

        await vm.ImportTermsCommand.ExecuteAsync(null);

        Assert.Equal("Found no entries to import in empty.csv. Skipped 1 row: Row 2: no source term.", vm.Notice);
    }

    // ---------------------------------------------------------------- the CSV dialog

    // windows-1250.csv isn't UTF-8: the dialog asks, and the text comes out right once 1250 is chosen.
    [Fact]
    public async Task TheCsvDialog_AsksForTheEncoding_OfAFileThatIsntUtf8()
    {
        var import = new CsvImportViewModel(File.ReadAllBytes(Sample("windows-1250.csv")), "en", "hu", Task.Run);
        await import.Parsing;
        Assert.Equal("This file isn't UTF-8. Choose its encoding, and check the preview.", import.EncodingMessage);
        Assert.True(import.CanChooseEncoding);
        Assert.Equal("Western European (Windows-1252)", import.SelectedEncoding.Label);
        // Windows-1252 decodes those bytes as other letters: the preview shows that.
        Assert.DoesNotContain(import.Preview.SelectMany(r => r.Cells), c => c == "fűzfa");

        import.SelectedEncoding = import.Encodings.Single(e => e.Label == "Central European (Windows-1250)");
        await import.Parsing;

        Assert.Equal(["English", "magyar", "Note"], import.Preview[0].Cells);
        Assert.Equal(["willow", "fűzfa", ""], import.Preview[1].Cells);
        Assert.Equal((0, 1, 3), (import.SourceColumn, import.TargetColumn, import.NoteColumn)); // "magyar" is Hungarian's native name
        Assert.Null(import.ColumnMessage);
        Assert.True(import.CanImport);
        var imported = import.TryImport()!;
        Assert.Equal(["őr"], imported.Entries[1].Target.Select(t => t.Text));
        Assert.Equal("Őrség: guard duty", imported.Entries[1].Note);
    }

    [Fact]
    public async Task TheCsvDialog_ShowsWhyAFileDoesntDecode()
    {
        var bytes = Encoding.UTF8.GetBytes("en;hu\nfile;fájl\n").Concat(new byte[] { 0xFF, 0x0A }).ToArray();
        var import = new CsvImportViewModel(bytes, "en", "hu", Task.Run);
        import.SelectedEncoding = import.Encodings[0]; // UTF-8, strictly
        await import.Parsing;

        Assert.NotNull(import.Problem);
        Assert.False(import.CanImport);
        Assert.Null(import.TryImport());
    }

    [Fact]
    public async Task TheCsvDialog_GuessesTheColumns_AndABomDecidesTheEncoding()
    {
        var import = new CsvImportViewModel(File.ReadAllBytes(Sample("utf8-semicolon.csv")), "en", "hu", Task.Run);
        await import.Parsing;
        Assert.False(import.HasBom);
        Assert.Null(import.EncodingMessage);
        Assert.Equal("UTF-8", import.SelectedEncoding.Label);
        Assert.Equal(["1: en", "2: hu", "3: Comment", "Column 4"], import.Columns); // a row has an extra cell
        Assert.Equal((0, 1, 3, 0), (import.SourceColumn, import.TargetColumn, import.NoteColumn, import.StatusColumn));
        Assert.Equal("First line\nsecond line", import.TryImport()!.Entries[0].Note);

        var utf16 = new CsvImportViewModel(File.ReadAllBytes(Sample("utf16-tab.txt")), "en", "hu", Task.Run);
        await utf16.Parsing;
        Assert.True(utf16.HasBom);
        Assert.False(utf16.CanChooseEncoding);
        Assert.Equal("UTF-16", utf16.SelectedEncoding.Label);
        Assert.Equal((0, 1, 0, 3), (utf16.SourceColumn, utf16.TargetColumn, utf16.NoteColumn, utf16.StatusColumn));
        Assert.Equal(TermStatus.Preferred, utf16.TryImport()!.Entries[0].Source[0].Status);
    }

    [Fact]
    public async Task TheCsvDialog_WithoutAHeader_ImportsTheFirstRow_AndRefusesOneColumnForBoth()
    {
        var import = new CsvImportViewModel(Encoding.UTF8.GetBytes("file,fájl\nprinter,nyomtató\n"), "en", "hu", Task.Run);
        await import.Parsing;
        Assert.Equal("Couldn't tell the en and hu columns from the first row; check them.", import.ColumnMessage);
        Assert.Equal((0, 1), (import.SourceColumn, import.TargetColumn));

        import.FirstRowIsHeader = false;
        Assert.Equal(["Column 1", "Column 2"], import.Columns);
        Assert.Equal(2, import.TryImport()!.Entries.Count);

        import.TargetColumn = 0;
        Assert.Equal("The source and the translations can't be in the same column.", import.Problem);
        Assert.False(import.CanImport);
        Assert.Null(import.TryImport());
        import.TargetColumn = 1;
        Assert.Null(import.Problem);
    }

    // ---------------------------------------------------------------- export

    [Theory]
    [InlineData(TermExportFormat.Tbx2, "client.tbx")]
    [InlineData(TermExportFormat.Tbx3, "client3.tbx")]
    public async Task ExportingTbx_WritesWhatImportingReadsBack(TermExportFormat format, string file)
    {
        var (vm, dialogs) = await OpenWithTermbaseAsync(Client);
        dialogs.TermsToExport = new TermExportTarget(_files.PathOf(file), format);

        await vm.ExportTermsCommand.ExecuteAsync(null);

        Assert.Contains("export terms (suggested client.tbx)", dialogs.Asked);
        Assert.Equal($"Exported 2 entries from client to {file}.", vm.Notice);
        var read = await ReadTbxAsync(_files.PathOf(file));
        Assert.Equal(vm.Termbase!.Index.Entries().Select(e => e.Entry), read.Entries);
        Assert.Contains(format == TermExportFormat.Tbx3 ? "<tbx" : "<martif", File.ReadAllText(_files.PathOf(file)), StringComparison.Ordinal);
        Assert.False(File.Exists(_files.PathOf(file) + ".tmp"));
    }

    [Fact]
    public async Task ExportingCsv_SaysWhatItCouldntHold()
    {
        var (vm, dialogs) = await OpenWithTermbaseAsync(Client);
        dialogs.TermsToExport = new TermExportTarget(_files.PathOf("client.csv"), TermExportFormat.Csv);

        await vm.ExportTermsCommand.ExecuteAsync(null);

        Assert.StartsWith("Exported 2 entries from client to client.csv. Exported without what CSV can't hold: 1 entry (", vm.Notice, StringComparison.Ordinal);
        var table = TermCsv.Parse(File.ReadAllBytes(_files.PathOf("client.csv")));
        var read = TermCsv.Import(table, TermCsv.GuessColumns(table.Rows[0], "en", "hu")!);
        Assert.Equal(["file", "file manager"], read.Entries.Select(e => e.Source[0].Text));
        Assert.Equal(["állomány", "fájl", "akta"], read.Entries[0].Target.Select(t => t.Text));
    }

    // Atomic, as the TMX export: a failed export leaves the existing file as it was.
    [Fact]
    public async Task AFailedExport_LeavesTheExistingFile()
    {
        var (vm, dialogs) = await OpenWithTermbaseAsync(Client);
        var path = _files.Write("client.tbx", "the previous export");
        TestFiles.BlockSaving(path);
        dialogs.TermsToExport = new TermExportTarget(path, TermExportFormat.Tbx2);

        await vm.ExportTermsCommand.ExecuteAsync(null);

        Assert.StartsWith("Could not export to client.tbx: ", vm.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal("the previous export", File.ReadAllText(path));
    }

    [Fact]
    public async Task ImportAndExport_FollowTheTermbase()
    {
        var (vm, _) = await OpenWithTermbaseAsync();
        Assert.True(vm.ImportTermsCommand.CanExecute(null));
        Assert.True(vm.ExportTermsCommand.CanExecute(null));

        await vm.CloseTermbaseCommand.ExecuteAsync(null);

        Assert.False(vm.ImportTermsCommand.CanExecute(null));
        Assert.False(vm.ExportTermsCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("terms.csv", TermExportFormat.Csv)]
    [InlineData("terms.CSV", TermExportFormat.Csv)]
    [InlineData("terms.tbx", TermExportFormat.Tbx2)]
    [InlineData("terms", TermExportFormat.Tbx2)]
    public void WithoutAChosenFileType_TheExtensionDecides(string path, TermExportFormat format) =>
        Assert.Equal(format, AvaloniaDialogService.FormatOf(path));

    // ---------------------------------------------------------------- helpers

    private static string Sample(string name) => Path.Combine(AppContext.BaseDirectory, "Samples", "termbase", name);

    private static async Task<TermImport> ReadTbxAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return await Tbx.ReadAsync(stream, "en", "hu", cancellationToken: TestContext.Current.CancellationToken);
    }

    private async Task<(MainWindowViewModel ViewModel, FakeDialogs Dialogs)> OpenWithTermbaseAsync(params TermEntry[] entries)
    {
        var dialogs = new FakeDialogs(null);
        var vm = new MainWindowViewModel(new Xliff20Serializer(), dialogs);
        _viewModels.Add(vm);
        await vm.LoadAsync(_files.Write("doc.xlf", Document));
        var termbase = OpenTermbase.Create(_files.PathOf("client.sctb"), "en", "hu");
        termbase.Termbase.AddRange(entries.Length == 0 ? [Entry("printer", ("nyomtató", TermStatus.Preferred))] : entries);
        await vm.UseTermbaseAsync(termbase);
        await vm.TermCheck;
        return (vm, dialogs);
    }
}
