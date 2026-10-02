using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.LogicalTree;
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

namespace SchrodingersCat.App.Tests;

/// <summary>Drives the real window on Avalonia's headless platform: bindings, templates, key bindings and all.</summary>
public sealed class MainWindowHeadlessTests : IDisposable
{
    private readonly TestFiles _files = new();

    public void Dispose() => _files.Dispose();

    [AvaloniaFact]
    public async Task OpenEditSave_ThroughTheWindow()
    {
        var path = _files.Write("sample.xlf", TestFiles.Sample);
        var (window, vm) = Show(path);

        window.FindControl<Button>("OpenButton")!.Command!.Execute(null);
        await WaitFor(() => vm.Segments.Count == 3);

        var targets = TargetBoxes(window);
        Assert.Equal(3, targets.Count);
        Assert.Equal("Viszlát.", targets[1].Text);
        Assert.False(targets[2].IsVisible); // inline codes: shown as chips instead

        // Type into the first target like a user would, then save with Ctrl+S.
        targets[0].Focus();
        window.KeyTextInput("Helló világ.");
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsDirty);
        Assert.Equal("sample.xlf • — SchrödingersCAT", window.Title);

        window.KeyPressQwerty(PhysicalKey.S, RawInputModifiers.Control);
        await WaitFor(() => !vm.IsDirty);

        var saved = await new Xliff20Serializer().ReadFileAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal("Helló világ.", saved.Units.First().Segments[0].Target!.ToPlainText());
        Assert.False(window.FindControl<Border>("ErrorBanner")!.IsVisible);
    }

    [AvaloniaFact]
    public async Task OpenFailure_IsShownInTheWindow()
    {
        var (window, vm) = Show(_files.Write("broken.xlf", "<xliff"));

        window.KeyPressQwerty(PhysicalKey.O, RawInputModifiers.Control);
        await WaitFor(() => vm.ErrorMessage is not null);

        Assert.True(window.FindControl<Border>("ErrorBanner")!.IsVisible);
        Assert.StartsWith("Could not open broken.xlf:", window.FindControl<SelectableTextBlock>("ErrorText")!.Text);
        Assert.Empty(TargetBoxes(window));
    }

    [AvaloniaTheory]
    [InlineData(UnsavedChangesChoice.Cancel, false, null)]
    [InlineData(UnsavedChangesChoice.DontSave, true, null)]
    [InlineData(UnsavedChangesChoice.Save, true, "Helló világ.")]
    public async Task ClosingWithUnsavedChanges_AsksFirst(UnsavedChangesChoice choice, bool closes, string? savedTarget)
    {
        var path = _files.Write("sample.xlf", TestFiles.Sample);
        var dialogs = new FakeDialogs(path) { Choice = choice };
        var (window, vm) = Show(dialogs);
        await vm.LoadAsync(path);
        vm.Segments[0].TargetText = "Helló világ.";
        var closed = false;
        window.Closed += (_, _) => closed = true;

        window.Close();
        await WaitFor(() => dialogs.Asked.Count > 0);
        await WaitFor(() => closed == closes);

        Assert.Equal(["save changes to sample.xlf?"], dialogs.Asked);
        Assert.Equal(closes, closed);
        var saved = await new Xliff20Serializer().ReadFileAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(savedTarget, saved.Units.First().Segments[0].Target?.ToPlainText());
    }

    [AvaloniaFact]
    public async Task ClosingWithoutChanges_DoesNotAsk()
    {
        var path = _files.Write("sample.xlf", TestFiles.Sample);
        var dialogs = new FakeDialogs(path);
        var (window, vm) = Show(dialogs);
        await vm.LoadAsync(path);
        var closed = false;
        window.Closed += (_, _) => closed = true;

        window.Close();
        await WaitFor(() => closed);

        Assert.Empty(dialogs.Asked);
    }

    [AvaloniaFact]
    public async Task CtrlO_WithUnsavedChanges_Cancel_ShowsNoPicker()
    {
        var path = _files.Write("sample.xlf", TestFiles.Sample);
        var dialogs = new FakeDialogs(path) { Choice = UnsavedChangesChoice.Cancel };
        var (window, vm) = Show(dialogs);
        await vm.LoadAsync(path);
        vm.Segments[0].TargetText = "Helló világ.";

        window.KeyPressQwerty(PhysicalKey.O, RawInputModifiers.Control);
        await WaitFor(() => dialogs.Asked.Count > 0);

        Assert.Equal(["save changes to sample.xlf?"], dialogs.Asked);
        Assert.True(vm.IsDirty);
    }

    [AvaloniaFact]
    public async Task CtrlShiftS_SavesAs()
    {
        var path = _files.Write("sample.xlf", TestFiles.Sample);
        var copy = _files.PathOf("copy.xlf");
        var dialogs = new FakeDialogs(path) { SavePath = copy };
        var (window, vm) = Show(dialogs);
        await vm.LoadAsync(path);

        window.KeyPressQwerty(PhysicalKey.S, RawInputModifiers.Control | RawInputModifiers.Shift);
        await WaitFor(() => vm.FilePath == copy);

        Assert.True(File.Exists(copy));
        Assert.Equal("copy.xlf — SchrödingersCAT", window.Title);
    }

    // The real prompt window, through the real dialog service.
    [AvaloniaTheory]
    [InlineData("SaveButton", UnsavedChangesChoice.Save)]
    [InlineData("DontSaveButton", UnsavedChangesChoice.DontSave)]
    [InlineData("CancelButton", UnsavedChangesChoice.Cancel)]
    public async Task UnsavedChangesDialog_ReturnsTheClickedChoice(string button, UnsavedChangesChoice expected)
    {
        var (owner, prompt, dialog) = ShowPrompt();

        dialog.FindControl<Button>(button)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitFor(() => prompt.IsCompleted);

        Assert.Equal(expected, await prompt);
        Assert.Contains("sample.xlf", dialog.FindControl<TextBlock>("Message")!.Text);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task UnsavedChangesDialog_ClosedWithoutAnswer_MeansCancel()
    {
        var (owner, prompt, dialog) = ShowPrompt();

        dialog.Close(); // title bar close button
        await WaitFor(() => prompt.IsCompleted);

        Assert.Equal(UnsavedChangesChoice.Cancel, await prompt);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task UnsavedChangesDialog_Escape_MeansCancel()
    {
        var (owner, prompt, dialog) = ShowPrompt();

        dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        await WaitFor(() => prompt.IsCompleted);

        Assert.Equal(UnsavedChangesChoice.Cancel, await prompt);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task InlineCodes_AreShownAsChips()
    {
        var path = _files.Write("tagged.xlf", """
            <xliff xmlns="urn:oasis:names:tc:xliff:document:2.0" version="2.0" srcLang="en" trgLang="hu">
              <file id="f1">
                <unit id="u1">
                  <originalData>
                    <data id="d1">&lt;b class="warn"&gt;</data>
                    <data id="d2">&lt;/b&gt;</data>
                  </originalData>
                  <segment>
                    <source>Hello <ph id="1" disp="{0}" equiv="Ann" canDelete="no"/>, press <pc id="2" subType="xlf:b" type="fmt" dataRefStart="d1" dataRefEnd="d2">OK</pc>.</source>
                    <target>Szia <ph id="1" disp="{0}" equiv="Ann" canDelete="no"/>, nyomd meg: <pc id="2" subType="xlf:b" type="fmt" dataRefStart="d1" dataRefEnd="d2">OK</pc>.</target>
                  </segment>
                </unit>
              </file>
            </xliff>
            """);
        var (window, vm) = Show(path);

        window.KeyPressQwerty(PhysicalKey.O, RawInputModifiers.Control);
        await WaitFor(() => vm.Segments.Count == 1);

        var source = ContentBlock(window, "source");
        Assert.Equal(["{0} 🔒", "⟨b", "b⟩"], Chips(source).Select(Label));
        Assert.Equal("Hello {0}, press ⟨bOKb⟩.", string.Concat(source.Inlines!.Select(InlineText)));

        var (placeholder, opening, closing) = (Chips(source)[0], Chips(source)[1], Chips(source)[2]);
        Assert.Equal(["chip", "standalone", "protected"], placeholder.Classes);
        Assert.Equal(["chip", "opening"], opening.Classes);
        Assert.Equal(["chip", "closing"], closing.Classes);
        Assert.Equal("Code 1\nReads as: Ann\nCan't be deleted.", ToolTip.GetTip(placeholder));
        Assert.Equal("Start of code 2\n<b class=\"warn\">", ToolTip.GetTip(opening));
        Assert.Equal("End of code 2\n</b>", ToolTip.GetTip(closing));

        // The target can't be edited as text yet: no text box, but the same chips, read-only.
        Assert.False(TargetBoxes(window).Single().IsVisible);
        var target = ContentBlock(window, "target");
        Assert.True(target.IsEffectivelyVisible);
        Assert.Equal(["{0} 🔒", "⟨b", "b⟩"], Chips(target).Select(Label));
        Assert.Equal("Szia {0}, nyomd meg: ⟨bOKb⟩.", string.Concat(target.Inlines!.Select(InlineText)));
    }

    private static InlineContentBlock ContentBlock(Window window, string role)
    {
        Dispatcher.UIThread.RunJobs();
        return window.GetVisualDescendants().OfType<InlineContentBlock>().Single(b => b.Classes.Contains(role));
    }

    private static List<Border> Chips(InlineContentBlock block) =>
        block.Inlines!.OfType<InlineUIContainer>().Select(c => Assert.IsType<Border>(c.Child)).ToList();

    private static string Label(Border chip) =>
        string.Join(" ", chip.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text));

    private static string InlineText(Inline inline) => inline switch
    {
        Run run => run.Text ?? "",
        InlineUIContainer { Child: Border chip } => chip.GetLogicalDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("label")).Text ?? "",
        _ => throw new InvalidOperationException($"Unexpected inline {inline.GetType().Name}."),
    };

    private static (Window Owner, Task<UnsavedChangesChoice> Prompt, UnsavedChangesDialog Dialog) ShowPrompt()
    {
        var owner = new Window();
        owner.Show();
        var prompt = new AvaloniaDialogService(owner).AskToSaveChangesAsync("sample.xlf");
        Dispatcher.UIThread.RunJobs();
        return (owner, prompt, owner.OwnedWindows.OfType<UnsavedChangesDialog>().Single());
    }

    private static (MainWindow Window, MainWindowViewModel ViewModel) Show(string path) => Show(new FakeDialogs(path));

    private static (MainWindow Window, MainWindowViewModel ViewModel) Show(FakeDialogs dialogs)
    {
        var vm = new MainWindowViewModel(new Xliff20Serializer(), dialogs);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, vm);
    }

    private static List<TextBox> TargetBoxes(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        return window.GetVisualDescendants().OfType<TextBox>().Where(t => t.Classes.Contains("target")).ToList();
    }

    // Commands run async file I/O; keep the dispatcher going until the effect is visible.
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
