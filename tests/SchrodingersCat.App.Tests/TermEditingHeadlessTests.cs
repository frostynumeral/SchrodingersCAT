using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SchrodingersCat.App.Controls;
using SchrodingersCat.App.Services;
using SchrodingersCat.App.ViewModels;
using SchrodingersCat.App.Views;
using SchrodingersCat.Core.Xliff;
using SchrodingersCat.Memory;
using static SchrodingersCat.App.Tests.Headless;
using static SchrodingersCat.App.Tests.TermbaseFiles;

namespace SchrodingersCat.App.Tests;

/// <summary>
/// Step 6 of <c>docs/design/termbase.md</c> in the real window, with the real dialogs: Ctrl+E and the entry dialog, a
/// term row's context menu, the Termbase menu, and the CSV import dialog.
/// </summary>
public sealed class TermEditingHeadlessTests : IDisposable
{
    private const string Document = """
        <xliff xmlns="urn:oasis:names:tc:xliff:document:2.0" version="2.0" srcLang="en" trgLang="hu">
          <file id="f1">
            <unit id="u1"><segment><source>Close the folder.</source></segment></unit>
            <unit id="u2"><segment><source>Open the file.</source></segment></unit>
            <unit id="u3"><segment><source>Save the file manager settings.</source></segment></unit>
          </file>
        </xliff>
        """;

    private readonly TestFiles _files = new();

    public void Dispose() => _files.Dispose();

    [AvaloniaFact]
    public async Task CtrlE_OpensTheDialogWithTheSelections_CtrlEnterSaves_AndTheTermShowsAtOnce()
    {
        var (window, vm) = await OpenAsync();
        Click(window, TargetFrames(window)[0]);
        var editor = Assert.Single(VisibleEditors(window));
        Type(window, "Zárd be a mappa ablakát.");
        editor.Editor.Select("Zárd be a ".Length, "mappa".Length);
        var source = All<InlineContentBlock>(window, "source")[0];
        source.SelectionStart = "Close the ".Length;
        source.SelectionEnd = "Close the folder".Length;

        Press(window, PhysicalKey.E, RawInputModifiers.Control);
        var dialog = await DialogAsync<TermEntryDialog>(window);

        Assert.Equal("Add term", dialog.Title);
        Assert.Equal(["folder", "mappa"], TermBoxes(dialog).Select(b => b.Text));
        await WaitFor(() => TermBoxes(dialog)[0].IsKeyboardFocusWithin); // both filled: the first
        All<ComboBox>(dialog, "termStatus")[1].SelectedIndex = (int)TermStatus.Preferred;
        Press(dialog, PhysicalKey.Enter, RawInputModifiers.Control);
        await WaitFor(() => vm.Termbase!.Index.Count == 3);

        var entry = vm.Termbase!.Index.Entries().Last().Entry;
        Assert.Equal(("folder", "mappa", TermStatus.Preferred), (entry.Source[0].Text, entry.Target[0].Text, entry.Target[0].Status));
        await WaitFor(() => TermRows(window).Count == 1);
        Assert.Equal("folder", One<TextBlock>(TermRows(window)[0], "termSource").Text);
        var folder = Assert.Single(All<InlineContentBlock>(window, "source")[0].Inlines!.OfType<Run>(), r => r.Text == "folder");
        Assert.NotNull(folder.TextDecorations);
        Assert.Equal("Added “folder” → “mappa” to client.", window.FindControl<SelectableTextBlock>("NoticeText")!.Text);
        await WaitFor(() => editor.Editor.TextArea.IsKeyboardFocusWithin);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheDialog_EscapeCancels_AndARefusedSaveStaysOpen()
    {
        var (window, vm) = await OpenAsync();
        Click(window, TargetFrames(window)[1]);

        Press(window, PhysicalKey.E, RawInputModifiers.Control);
        var dialog = await DialogAsync<TermEntryDialog>(window);
        Assert.Equal(["", ""], TermBoxes(dialog).Select(b => b.Text));

        // No source term: refused, and the dialog stays open with the problem shown.
        dialog.FindControl<Button>("SaveButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.True(dialog.IsVisible);
        Assert.Equal("An entry needs a source term.", dialog.FindControl<TextBlock>("ProblemText")!.Text);

        Press(dialog, PhysicalKey.Escape);
        await WaitFor(() => !window.OwnedWindows.Any());
        Assert.Equal(2, vm.Termbase!.Index.Count);
        window.Close();
    }

    // The dialog lists the entries with that source term, the first chosen: the translation goes into it.
    [AvaloniaFact]
    public async Task TheDialog_OffersTheEntryThatHasTheTerm_AndAddsTheTranslationToIt()
    {
        var (window, vm) = await OpenAsync();
        Click(window, TargetFrames(window)[1]);
        var source = All<InlineContentBlock>(window, "source")[1];
        source.SelectionStart = "Open the ".Length;
        source.SelectionEnd = "Open the file".Length;

        Press(window, PhysicalKey.E, RawInputModifiers.Control);
        var dialog = await DialogAsync<TermEntryDialog>(window);
        var existing = dialog.FindControl<ListBox>("ExistingList")!;
        Assert.True(existing.IsEffectivelyVisible);
        Assert.Equal(1, existing.SelectedIndex);
        TermBoxes(dialog)[1].Text = "dosszié";
        Dispatcher.UIThread.RunJobs();
        Assert.StartsWith("Adds “dosszié” to this entry.", dialog.FindControl<TextBlock>("MergeSummaryText")!.Text, StringComparison.Ordinal);
        Press(dialog, PhysicalKey.Enter, RawInputModifiers.Control);

        await WaitFor(() => vm.Termbase!.Index.Find(new TermEntryId(1))!.Target.Count == 4);
        Assert.Equal(2, vm.Termbase!.Index.Count);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ATermRowsContextMenu_EditsInTheDialog_AndDeletesAfterAsking()
    {
        var (window, vm) = await OpenAsync();
        Click(window, TargetFrames(window)[2]);
        Assert.Equal(["file manager", "file"], TermRows(window).Select(r => One<TextBlock>(r, "termSource").Text));

        // Edit "file": renamed, so it's no longer in this source.
        ClickMenuItem(TermRows(window)[1], "editTermEntry");
        var dialog = await DialogAsync<TermEntryDialog>(window);
        Assert.Equal("Edit term entry", dialog.Title);
        Assert.Equal(["file", "állomány", "fájl", "akta"], TermBoxes(dialog).Select(b => b.Text));
        Assert.Equal([0, 2, 1, 3], All<ComboBox>(dialog, "termStatus").Select(c => c.SelectedIndex));
        TermBoxes(dialog)[0].Text = "document";
        Press(dialog, PhysicalKey.Enter, RawInputModifiers.Control);
        await WaitFor(() => TermRows(window).Count == 1);
        Assert.Equal("document", vm.Termbase!.Index.Find(new TermEntryId(1))!.Source[0].Text);

        // Delete "file manager", after the confirmation.
        ClickMenuItem(TermRows(window)[0], "deleteTermEntry");
        var confirm = await DialogAsync<ConfirmDialog>(window);
        Assert.Equal("Delete term entry", confirm.Title);
        confirm.FindControl<Button>("ConfirmButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitFor(() => vm.Termbase.Index.Count == 1);
        await WaitFor(() => TermRows(window).Count == 0);
        Assert.Equal("Deleted the entry “file manager” from client.", window.FindControl<SelectableTextBlock>("NoticeText")!.Text);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheTermbaseMenu_AddImportAndExport_WorkOnlyWithATermbase()
    {
        var (window, vm) = Show(new FakeDialogs(null));
        await vm.LoadAsync(_files.Write("terms.xlf", Document));
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.FindControl<MenuItem>("AddTermItem")!.IsEnabled);
        Assert.False(window.FindControl<MenuItem>("ImportTermsItem")!.Command!.CanExecute(null));
        Assert.False(window.FindControl<MenuItem>("ExportTermsItem")!.Command!.CanExecute(null));

        await vm.UseTermbaseAsync(CreateTermbase());
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.FindControl<MenuItem>("AddTermItem")!.IsEnabled);
        Assert.Equal("Ctrl+E", window.FindControl<MenuItem>("AddTermItem")!.InputGesture!.ToString());
        Assert.True(window.FindControl<MenuItem>("ImportTermsItem")!.Command!.CanExecute(null));
        Assert.True(window.FindControl<MenuItem>("ExportTermsItem")!.Command!.CanExecute(null));
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheCsvDialog_ShowsThePreview_ChangesTheEncoding_AndImports()
    {
        var owner = new Window();
        owner.Show();
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Samples", "termbase", "windows-1250.csv"));
        var answer = new AvaloniaDialogService(owner).ChooseCsvImportAsync(new CsvImportViewModel(bytes, "en", "hu", Task.Run));
        var dialog = await DialogAsync<CsvImportDialog>(owner);
        await WaitFor(() => All<TextBlock>(dialog, "previewCell").Count > 0);
        Assert.True(dialog.FindControl<TextBlock>("EncodingMessageText")!.IsVisible);
        Assert.DoesNotContain(All<TextBlock>(dialog, "previewCell"), c => c.Text == "fűzfa");

        var encodings = dialog.FindControl<ComboBox>("EncodingBox")!;
        encodings.SelectedItem = ((CsvImportViewModel)dialog.DataContext!).Encodings.Single(e => e.Label.Contains("1250", StringComparison.Ordinal));
        await WaitFor(() => All<TextBlock>(dialog, "previewCell").Any(c => c.Text == "fűzfa"));
        Assert.Equal(3, dialog.FindControl<ComboBox>("NoteColumnBox")!.SelectedIndex);
        dialog.FindControl<Button>("ImportButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        await WaitFor(() => answer.IsCompleted);
        Assert.Equal(["willow", "guard"], (await answer)!.Entries.Select(e => e.Source[0].Text));
        owner.Close();
    }

    // ---------------------------------------------------------------- helpers

    // The window with the real dialogs, the document, and the termbase.
    private async Task<(MainWindow Window, MainWindowViewModel ViewModel)> OpenAsync()
    {
        var window = new MainWindow();
        var vm = new MainWindowViewModel(new Xliff20Serializer(), new AvaloniaDialogService(window));
        window.DataContext = vm;
        window.Show();
        await vm.LoadAsync(_files.Write("terms.xlf", Document));
        await vm.UseTermbaseAsync(CreateTermbase());
        await WaitFor(() => vm.TermCheck.IsCompleted);
        return (window, vm);
    }

    private OpenTermbase CreateTermbase()
    {
        var termbase = OpenTermbase.Create(_files.PathOf("client.sctb"), "en", "hu");
        termbase.Termbase.AddRange(Client);
        return termbase;
    }

    private static async Task<T> DialogAsync<T>(Window owner) where T : Window
    {
        await WaitFor(() => owner.OwnedWindows.OfType<T>().Any());
        return owner.OwnedWindows.OfType<T>().Single();
    }

    private static List<TextBox> TermBoxes(Window dialog) => All<TextBox>(dialog, "termText");

    private static List<Border> TermRows(Window window) => All<Border>(window.FindControl<ItemsControl>("TermList")!, "term");

    // Opens the row's context menu and clicks the item with this class.
    private static void ClickMenuItem(Border row, string role)
    {
        var menu = row.ContextMenu!;
        menu.Open(row);
        Dispatcher.UIThread.RunJobs();
        var item = menu.Items.OfType<MenuItem>().Single(i => i.Classes.Contains(role));
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static void Type(Window window, string text)
    {
        window.KeyTextInput(text);
        Dispatcher.UIThread.RunJobs();
    }
}
