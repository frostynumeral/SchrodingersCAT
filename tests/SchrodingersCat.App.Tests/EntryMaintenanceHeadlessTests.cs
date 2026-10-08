using Avalonia;
using Avalonia.Controls;
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
using SchrodingersCat.Core.Models;
using SchrodingersCat.Core.Xliff;
using SchrodingersCat.Memory;

namespace SchrodingersCat.App.Tests;

/// <summary>Step 2 of <c>docs/design/tm-maintenance.md</c> in the real window and the real entry editor.</summary>
public sealed class EntryMaintenanceHeadlessTests : IDisposable
{
    private readonly TestFiles _files = new();

    public void Dispose() => _files.Dispose();

    [AvaloniaFact]
    public async Task AMatchsButtons_EditAndDeleteItsEntry_CtrlShiftNDeletesToo()
    {
        var path = _files.Write("matches.xlf", MatchesTests.WithMatches);
        var dialogs = new FakeDialogs(path) { Confirm = true, EditEntry = _ => InlineContent.FromText("Nyomd meg az OK-t.") };
        var (window, vm) = Show(dialogs);
        await vm.LoadAsync(path);
        var tm = new InMemoryTranslationMemory();
        tm.AddRange([
            new TmEntry(InlineContent.FromText("Press OK."), InlineContent.FromText("Nyomd meg: OK.")),
            new TmEntry(InlineContent.FromText("Press OK now."), InlineContent.FromText("Most nyomd meg: OK.")),
        ]);
        await vm.UseTranslationMemoryAsync(new OpenTranslationMemory(tm, "Project TM", "en", "hu"));
        Click(window, TargetFrames(window)[2]);
        await WaitFor(() => MatchRows(window).Count == 3);
        var editor = Assert.Single(VisibleEditors(window));

        // Best first: the TM's 96%, the file's 90%, which has no entry to change, then the TM's other match.
        Assert.Equal([true, false, true], MatchRows(window).Select(r => RowButton(r, "deleteEntry").IsEffectivelyVisible));

        Click(window, RowButton(MatchRows(window)[0], "editEntry"));
        await WaitFor(() => tm.Entries().Any(e => e.Target.ToPlainText() == "Nyomd meg az OK-t."));
        Assert.Contains("edit entry: Press OK.", dialogs.Asked);
        await WaitFor(() => MatchRows(window).Any(r => Text(r, "matchTarget") == "Nyomd meg az OK-t."));
        Assert.True(editor.Editor.TextArea.IsKeyboardFocusWithin);

        Click(window, RowButton(MatchRows(window)[2], "deleteEntry"));
        await WaitFor(() => tm.Count == 1);
        Assert.Contains("confirm: Delete entry", dialogs.Asked);
        await WaitFor(() => MatchRows(window).Count == 2);
        Assert.Equal("Deleted the entry from Project TM.", window.FindControl<SelectableTextBlock>("NoticeText")!.Text);

        // Ctrl+Shift+1 from inside the editor.
        Press(window, PhysicalKey.Digit1, RawInputModifiers.Control | RawInputModifiers.Shift);
        await WaitFor(() => tm.Count == 0);
        await WaitFor(() => MatchRows(window).Count == 1);
        Assert.True(editor.Editor.TextArea.IsKeyboardFocusWithin);
    }

    [AvaloniaFact]
    public async Task TheEntryEditor_CtrlEnterSaves_WithChipsAndTheGridsRules()
    {
        var entry = new TmEntry(
            new InlineContent([new InlineText("Press "), Open("1"), new InlineText("OK"), Close("1"), new InlineText(".")]),
            new InlineContent([new InlineText("Nyomd: "), Open("1"), new InlineText("OK"), Close("1")]));
        var (owner, answer, dialog) = ShowEditor(entry);
        Assert.Equal(2, Chips(dialog.FindControl<InlineContentBlock>("SourceBlock")!).Count);
        var editor = dialog.FindControl<TargetEditor>("Editor")!;
        Assert.True(editor.Editor.TextArea.IsKeyboardFocusWithin);

        dialog.KeyTextInput("!");
        Press(dialog, PhysicalKey.Enter, RawInputModifiers.Control);
        await WaitFor(() => answer.IsCompleted);

        Assert.Equal(new InlineContent([new InlineText("Nyomd: "), Open("1"), new InlineText("OK"), Close("1"), new InlineText("!")]), await answer);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task TheEntryEditor_RefusesToSaveNothing_AndEscapeCancels()
    {
        var (owner, answer, dialog) = ShowEditor(new TmEntry(InlineContent.FromText("Save"), InlineContent.FromText("Mentés")));

        for (var i = 0; i < "Mentés".Length; i++)
            Press(dialog, PhysicalKey.Backspace);
        dialog.FindControl<Button>("SaveButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.False(answer.IsCompleted);
        Assert.True(dialog.FindControl<TextBlock>("ProblemText")!.IsVisible);

        Press(dialog, PhysicalKey.Escape);
        await WaitFor(() => answer.IsCompleted);
        Assert.Null(await answer);
        owner.Close();
    }

    private static (Window Owner, Task<InlineContent?> Answer, EntryEditorDialog Dialog) ShowEditor(TmEntry entry)
    {
        var owner = new Window();
        owner.Show();
        var answer = new AvaloniaDialogService(owner).EditEntryAsync(new EntryEditorViewModel(entry, "TM"));
        Dispatcher.UIThread.RunJobs();
        return (owner, answer, owner.OwnedWindows.OfType<EntryEditorDialog>().Single());
    }

    private static (MainWindow Window, MainWindowViewModel ViewModel) Show(FakeDialogs dialogs)
    {
        var vm = new MainWindowViewModel(new Xliff20Serializer(), dialogs);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, vm);
    }

    private static List<Border> MatchRows(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        return window.FindControl<ItemsControl>("MatchList")!.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("match")).ToList();
    }

    private static List<Border> TargetFrames(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        return window.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("targetFrame")).ToList();
    }

    private static List<TargetEditor> VisibleEditors(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        return window.GetVisualDescendants().OfType<TargetEditor>().Where(e => e.IsEffectivelyVisible).ToList();
    }

    private static Button RowButton(Border row, string role) => row.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains(role));

    private static string Text(Border row, string role) =>
        string.Concat(row.GetVisualDescendants().OfType<InlineContentBlock>().Single(b => b.Classes.Contains(role)).Inlines!.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text));

    private static List<Border> Chips(InlineContentBlock block) =>
        block.Inlines!.OfType<Avalonia.Controls.Documents.InlineUIContainer>().Select(c => Assert.IsType<Border>(c.Child)).ToList();

    private static void Press(TopLevel window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPressQwerty(key, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Click(Window window, Control control)
    {
        var point = control.TranslatePoint(new Point(4, 4), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The window did not reach the expected state.");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static InlineCode Open(string id) => new(id, InlineCodeKind.Opening);

    private static InlineCode Close(string id) => new(id, InlineCodeKind.Closing);
}
