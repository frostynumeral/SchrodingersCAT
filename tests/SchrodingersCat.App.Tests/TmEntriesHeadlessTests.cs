using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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

/// <summary>Step 3 of <c>docs/design/tm-maintenance.md</c>: the real entries window.</summary>
public sealed class TmEntriesHeadlessTests : IDisposable
{
    private readonly TestFiles _files = new();

    public void Dispose() => _files.Dispose();

    [AvaloniaFact]
    public async Task TheWindow_FiltersOnEnter_DeletesWithTheDeleteKey_AndEditsWithEnter()
    {
        var dialogs = new FakeDialogs(null) { Confirm = true, EditEntry = _ => InlineContent.FromText("Nyisd meg!") };
        var (vm, tm) = await OpenAsync(dialogs, [
            Entry("Save the file.", "Mentsd a fájlt."),
            Entry("Open the file.", "Nyisd meg a fájlt."),
            Entry("Close it.", "Zárd be."),
        ]);
        await vm.ManageEntriesCommand.ExecuteAsync(null);
        var window = new TmEntriesWindow(dialogs.ShownEntries!);
        window.Show();
        await WaitFor(() => Rows(window).Count == 3);
        Assert.Equal("3 entries", window.FindControl<TextBlock>("HeaderText")!.Text);

        window.FindControl<TextBox>("SearchBox")!.Focus();
        window.KeyTextInput("file");
        Press(window, PhysicalKey.Enter);
        await WaitFor(() => Rows(window).Count == 2);
        Assert.Equal("2 of 3 entries", window.FindControl<TextBlock>("HeaderText")!.Text);

        // Select the first row (Open the file.), then Delete.
        Click(window, Rows(window)[0]);
        Press(window, PhysicalKey.Delete);
        await WaitFor(() => tm.Count == 2);
        await WaitFor(() => Rows(window).Count == 1);
        Assert.Contains("confirm: Delete entries", dialogs.Asked);

        // Enter edits the selected row (as a double-click does, which the headless platform can't produce).
        Click(window, Rows(window)[0]);
        Press(window, PhysicalKey.Enter);
        await WaitFor(() => tm.Entries().Any(e => e.Target.ToPlainText() == "Nyisd meg!"));
        Assert.Contains("edit entry: Save the file.", dialogs.Asked);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ScrollingToTheEnd_LoadsTheNextPage()
    {
        var dialogs = new FakeDialogs(null);
        var (vm, _) = await OpenAsync(dialogs, [.. Enumerable.Range(0, 300).Select(i => Entry($"Sentence {i}.", $"Mondat {i}."))]);
        await vm.ManageEntriesCommand.ExecuteAsync(null);
        var entries = dialogs.ShownEntries!;
        var window = new TmEntriesWindow(entries);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(TmEntriesViewModel.PageSize, entries.Entries.Count);

        var viewer = window.FindControl<ListBox>("EntryList")!.GetVisualDescendants().OfType<ScrollViewer>().First();
        viewer.Offset = new Vector(0, viewer.Extent.Height);
        await WaitFor(() => entries.Entries.Count == 300);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheDialogService_ShowsOneWindow_AndClosingTheTmClosesIt()
    {
        var owner = new Window();
        owner.Show();
        var service = new AvaloniaDialogService(owner);
        var (vm, _) = await OpenAsync(new FakeDialogs(null), [Entry("Save", "Mentés")]);
        var entries = new TmEntriesViewModel(vm, vm.TranslationMemory!, service);

        service.ShowTmEntries(entries);
        service.ShowTmEntries(entries);
        Dispatcher.UIThread.RunJobs();
        var window = Assert.Single(owner.OwnedWindows.OfType<TmEntriesWindow>());

        entries.Close(); // what closing or replacing the TM does
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.IsVisible);
        owner.Close();
    }

    private async Task<(MainWindowViewModel ViewModel, ITranslationMemory Memory)> OpenAsync(FakeDialogs dialogs, TmEntry[] entries)
    {
        var vm = new MainWindowViewModel(new Xliff20Serializer(), dialogs);
        await vm.LoadAsync(_files.Write("doc.xlf", TestFiles.Sample));
        var tm = new InMemoryTranslationMemory();
        tm.AddRange(entries);
        await vm.UseTranslationMemoryAsync(new OpenTranslationMemory(tm, "TM", "en", "hu"));
        return (vm, tm);
    }

    private static List<ListBoxItem> Rows(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        return window.FindControl<ListBox>("EntryList")!.GetVisualDescendants().OfType<ListBoxItem>().ToList();
    }

    private static TmEntry Entry(string source, string target) => new(InlineContent.FromText(source), InlineContent.FromText(target));

    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPressQwerty(key, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Click(Window window, Control control)
    {
        var point = control.TranslatePoint(new Point(8, 8), window)!.Value;
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
}
