using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SchrodingersCat.App.Controls;
using SchrodingersCat.App.Services;
using SchrodingersCat.App.ViewModels;
using SchrodingersCat.App.Views;
using SchrodingersCat.Core.Models;
using SchrodingersCat.Memory;
using static SchrodingersCat.App.Tests.Headless;
using static SchrodingersCat.App.Tests.TermbaseFiles;

namespace SchrodingersCat.App.Tests;

/// <summary>
/// Step 5 of <c>docs/design/termbase.md</c> in the real window: the Termbase menu, the terms list, the marks in the
/// source, the term picker (Ctrl+T) and inserting.
/// </summary>
public sealed class TermbaseHeadlessTests : IDisposable
{
    /// <summary>u1 has "file manager" around a code, u2 "file" inside a term annotation and "folder" in another, u3 no terms.</summary>
    private const string Document = """
        <xliff xmlns="urn:oasis:names:tc:xliff:document:2.0" version="2.0" srcLang="en" trgLang="hu">
          <file id="f1">
            <unit id="u1"><segment><source>Save the file <ph id="1"/>manager settings.</source></segment></unit>
            <unit id="u2"><segment><source>Open the <mrk id="m1" type="term">file</mrk> in the <mrk id="m2" type="term">folder</mrk>.</source></segment></unit>
            <unit id="u3"><segment><source>Nothing here.</source></segment></unit>
          </file>
        </xliff>
        """;

    private readonly TestFiles _files = new();

    public void Dispose() => _files.Dispose();

    [AvaloniaFact]
    public async Task TheTermsList_ShowsTheSelectedSegmentsTerms()
    {
        var (window, vm) = await OpenAsync();
        var pane = window.FindControl<Border>("TermsPane")!;
        Assert.False(pane.IsVisible); // no termbase: the matches keep the room

        await OpenTermbaseAsync(window, vm);
        Assert.True(pane.IsVisible);
        Assert.Equal("client (en → hu)", window.FindControl<TextBlock>("TermbaseName")!.Text);
        Assert.Equal("Select a segment to see its terms.", window.FindControl<TextBlock>("TermsStatus")!.Text);

        Click(window, TargetFrames(window)[1]);
        var row = Assert.Single(TermRows(window));
        Assert.Equal("file", One<TextBlock>(row, "termSource").Text);
        var translations = All<TextBlock>(row, "termTranslation");
        Assert.Equal(["fájl", "állomány", "akta"], translations.Select(t => t.Text));
        Assert.Equal([true, false, false], translations.Select(t => t.Classes.Contains("preferred")));
        var forbidden = translations[2];
        Assert.Contains("forbidden", forbidden.Classes);
        Assert.Contains(forbidden.TextDecorations!, d => d.Location == TextDecorationLocation.Strikethrough);
        Assert.Equal("forbidden: akta", AutomationProperties.GetName(forbidden));
        Assert.Equal("Computer files only.", ToolTip.GetTip(row));
        Assert.False(window.FindControl<TextBlock>("TermsStatus")!.IsVisible);

        Click(window, TargetFrames(window)[0]);
        Assert.Equal(["file manager", "file"], TermRows(window).Select(r => One<TextBlock>(r, "termSource").Text));
        Click(window, TargetFrames(window)[2]);
        Assert.Empty(TermRows(window));
        Assert.Equal("No terms found.", window.FindControl<TextBlock>("TermsStatus")!.Text);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TermsAreUnderlinedInTheSource_SolidUnlikeTermAnnotations_WithTheTranslationsInATooltip()
    {
        var (window, vm) = await OpenAsync();
        await UseTermbaseAsync(vm);
        var sources = All<InlineContentBlock>(window, "source");

        // u2: the termbase's "file" is solid, though it's a term annotation too; the annotation "folder" stays dotted.
        var runs = sources[1].Inlines!.OfType<Run>().ToList();
        var file = Assert.Single(runs, r => r.Text == "file");
        Assert.Null(Assert.Single(file.TextDecorations!).StrokeDashArray);
        var folder = Assert.Single(runs, r => r.Text == "folder");
        Assert.NotNull(Assert.Single(folder.TextDecorations!).StrokeDashArray);

        // u1: "file manager" around a code: its text is underlined, the chip isn't part of it.
        var underlined = sources[0].Inlines!.OfType<Run>().Where(r => r.TextDecorations is not null).Select(r => r.Text);
        Assert.Equal("file manager", string.Concat(underlined));
        Assert.Single(Chips(sources[0]));

        // Hovering over the term shows its translations; elsewhere nothing.
        MoveOver(window, sources[1], "Open the ".Length + 1);
        Assert.Contains("→ fájl, állomány", ToolTip.GetTip(sources[1]) as string, StringComparison.Ordinal);
        MoveOver(window, sources[1], 1);
        Assert.Null(ToolTip.GetTip(sources[1]));

        // Closing the termbase takes the marks away.
        window.FindControl<MenuItem>("CloseTermbaseItem")!.Command!.Execute(null);
        await WaitFor(() => vm.Termbase is null && vm.TermCheck.IsCompleted);
        Assert.NotNull(Assert.Single(sources[1].Inlines!.OfType<Run>().Single(r => r.Text == "file").TextDecorations!).StrokeDashArray);
        Assert.DoesNotContain(sources[0].Inlines!.OfType<Run>(), r => r.TextDecorations is not null);
        window.Close();
    }

    [AvaloniaFact]
    public async Task CtrlT_PicksATerm_WithTypeAhead_AndInsertsItAtTheCaret()
    {
        var (window, vm) = await OpenAsync();
        await UseTermbaseAsync(vm);
        Click(window, TargetFrames(window)[1]);
        var editor = Assert.Single(VisibleEditors(window));

        Press(window, PhysicalKey.T, RawInputModifiers.Control);
        Assert.True(editor.TermPicker.IsOpen);
        var list = (ListBox)editor.TermPicker.Child!.GetVisualDescendants().Single(v => v is ListBox);
        Assert.Equal("TermList", list.Name);
        Assert.Equal(["fájl", "állomány"], list.Items.Cast<TermChoice>().Select(c => c.Text));
        Assert.Equal("fájl", ((TermChoice)list.SelectedItem!).Text);

        window.KeyTextInput("áll");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["állomány"], list.Items.Cast<TermChoice>().Select(c => c.Text));
        Press(window, PhysicalKey.Enter);
        Assert.False(editor.TermPicker.IsOpen);
        Assert.Equal("állomány", vm.Segments[1].TargetText);
        Assert.Equal("Inserted the term “állomány”.", StatusText(window));
        Assert.True(editor.Editor.TextArea.IsKeyboardFocusWithin);

        // Down, then Enter: the second choice, at the caret.
        Type(window, " ");
        Press(window, PhysicalKey.T, RawInputModifiers.Control);
        Press(window, PhysicalKey.ArrowDown);
        Press(window, PhysicalKey.Enter);
        Assert.Equal("állomány állomány", vm.Segments[1].TargetText);

        // Escape closes it without inserting.
        Press(window, PhysicalKey.T, RawInputModifiers.Control);
        Press(window, PhysicalKey.Escape);
        Assert.False(editor.TermPicker.IsOpen);
        Assert.Equal("állomány állomány", vm.Segments[1].TargetText);
        Assert.True(editor.Editor.TextArea.IsKeyboardFocusWithin);

        // F8 leaves the open picker the keyboard.
        Press(window, PhysicalKey.T, RawInputModifiers.Control);
        Press(window, PhysicalKey.F8);
        await SettleAsync();
        Assert.False(window.FindControl<ListBox>("QaList")!.IsKeyboardFocusWithin);
        Assert.True(editor.TermPicker.IsOpen);
        Press(window, PhysicalKey.Escape);
        Assert.False(editor.TermPicker.IsOpen);

        // Nothing to pick: the status line says why.
        Click(window, TargetFrames(window)[2]);
        Press(window, PhysicalKey.T, RawInputModifiers.Control);
        Assert.Equal("No terms found in this segment's source.", StatusText(window));
        Assert.False(Assert.Single(VisibleEditors(window)).TermPicker.IsOpen);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheInsertButton_InsertsAtTheCaret_AndCtrl1StillInsertsTheMatch()
    {
        var (window, vm) = await OpenAsync();
        await UseTermbaseAsync(vm);
        var memory = new InMemoryTranslationMemory();
        memory.Add(new TmEntry(InlineContent.FromText("Open the file in the folders."), InlineContent.FromText("Nyisd meg a fájlt a mappákban.")));
        await vm.UseTranslationMemoryAsync(new OpenTranslationMemory(memory, "TM", "en", "hu"));
        Click(window, TargetFrames(window)[1]);
        await WaitFor(() => vm.Matches.Count == 1);
        var editor = Assert.Single(VisibleEditors(window));
        Type(window, "Nyisd meg: ");

        Click(window, One<Button>(Assert.Single(TermRows(window)), "insertTerm"));
        Assert.Equal("Nyisd meg: fájl", vm.Segments[1].TargetText);
        Assert.True(editor.Editor.TextArea.IsKeyboardFocusWithin);

        Press(window, PhysicalKey.Digit1, RawInputModifiers.Control);
        Assert.Equal("Nyisd meg a fájlt a mappákban.", vm.Segments[1].TargetText);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheMenu_EnablesCloseOnlyWithATermbase_AndClosingTheWindowClosesIt()
    {
        var settings = new JsonSettingsStore(_files.PathOf("settings.json"));
        var (window, vm) = await OpenAsync(settings);
        // Items of a closed submenu aren't kept up to date, so their commands are asked.
        var closeItem = window.FindControl<MenuItem>("CloseTermbaseItem")!;
        Assert.False(closeItem.Command!.CanExecute(null));
        Assert.True(window.FindControl<MenuItem>("NewTermbaseItem")!.Command!.CanExecute(null));

        var path = await OpenTermbaseAsync(window, vm);
        Assert.True(closeItem.Command.CanExecute(null));
        var closed = TrackClosed(window);

        window.Close();
        await WaitFor(closed);

        Assert.Null(vm.Termbase);
        Assert.Equal(path, settings.Load().TermbasePath);
    }

    // ---------------------------------------------------------------- helpers

    private async Task<(MainWindow Window, MainWindowViewModel ViewModel)> OpenAsync(ISettingsStore? settings = null)
    {
        var (window, vm) = Show(new FakeDialogs(null) { TermbaseToOpen = Write(_files.PathOf("client.sctb"), "en", "hu", Client) }, settings);
        await vm.LoadAsync(_files.Write("terms.xlf", Document));
        Dispatcher.UIThread.RunJobs();
        return (window, vm);
    }

    // Termbase → Open…, loaded in the background, and the document checked again.
    private static async Task<string> OpenTermbaseAsync(MainWindow window, MainWindowViewModel vm)
    {
        window.FindControl<MenuItem>("OpenTermbaseItem")!.Command!.Execute(null);
        await WaitFor(() => vm.Termbase is not null && vm.TermCheck.IsCompleted);
        return vm.Termbase!.Path!;
    }

    private async Task UseTermbaseAsync(MainWindowViewModel vm)
    {
        var termbase = OpenTermbase.Create(_files.PathOf("in-use.sctb"), "en", "hu");
        termbase.Termbase.AddRange(Client);
        await vm.UseTermbaseAsync(termbase);
        await WaitFor(() => vm.TermCheck.IsCompleted);
    }

    private static List<Border> TermRows(Window window) => All<Border>(window.FindControl<ItemsControl>("TermList")!, "term");

    // Moves the mouse over a character of the block's text. Headless drawing records no glyphs, so text isn't hit by
    // the pointer: the event is raised on the block, as the window would with real drawing.
    private static void MoveOver(Window window, InlineContentBlock block, int index)
    {
        var bounds = block.TextLayout.HitTestTextPosition(index);
        var point = block.TranslatePoint(new Point(bounds.X + 1 + block.Padding.Left, bounds.Y + (bounds.Height / 2) + block.Padding.Top), window)!.Value;
        block.RaiseEvent(new PointerEventArgs(
            InputElement.PointerMovedEvent, block, new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true), window, point,
            timestamp: 0, new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other), KeyModifiers.None));
        Dispatcher.UIThread.RunJobs();
    }

    private static void Type(Window window, string text)
    {
        window.KeyTextInput(text);
        Dispatcher.UIThread.RunJobs();
    }

    private static string? StatusText(Window window) => window.FindControl<TextBlock>("StatusText")!.Text;

    private static async Task SettleAsync()
    {
        for (var i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}
