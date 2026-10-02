using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
        Assert.True(targets[2].IsReadOnly); // inline codes

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

    private static (MainWindow Window, MainWindowViewModel ViewModel) Show(string path)
    {
        var vm = new MainWindowViewModel(new Xliff20Serializer(), new FakeFileDialogs(path));
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
