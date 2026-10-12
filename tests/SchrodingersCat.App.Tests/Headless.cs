using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SchrodingersCat.App.Controls;
using SchrodingersCat.App.Services;
using SchrodingersCat.App.ViewModels;
using SchrodingersCat.App.Views;
using SchrodingersCat.Core.Xliff;

namespace SchrodingersCat.App.Tests;

/// <summary>Drives windows on Avalonia's headless platform like a user would, and finds what they show.</summary>
internal static class Headless
{
    public static (MainWindow Window, MainWindowViewModel ViewModel) Show(FakeDialogs dialogs, ISettingsStore? settings = null)
    {
        var vm = new MainWindowViewModel(new Xliff20Serializer(), dialogs, settings);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, vm);
    }

    public static void Press(TopLevel window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPressQwerty(key, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Clicks near the control's top left corner, or <paramref name="onText"/> on its first characters.</summary>
    public static void Click(Window window, Control control, bool onText = false)
    {
        var point = control.TranslatePoint(onText ? new Point(8, control.Bounds.Height / 2) : new Point(4, 4), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    // Commands run async file I/O; keep the dispatcher going until the effect is visible.
    public static async Task WaitFor(Func<bool> condition)
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

    /// <summary>Says whether the window has closed since this was called.</summary>
    public static Func<bool> TrackClosed(Window window)
    {
        var closed = false;
        window.Closed += (_, _) => closed = true;
        return () => closed;
    }

    /// <summary>The controls of type <typeparamref name="T"/> under <paramref name="root"/> with the style class <paramref name="role"/>, once the UI is up to date.</summary>
    public static List<T> All<T>(Visual root, string role) where T : StyledElement
    {
        Dispatcher.UIThread.RunJobs();
        return root.GetVisualDescendants().OfType<T>().Where(c => c.Classes.Contains(role)).ToList();
    }

    public static T One<T>(Visual root, string role) where T : StyledElement => Assert.Single(All<T>(root, role));

    public static List<Border> TargetFrames(Window window) => All<Border>(window, "targetFrame");

    public static List<InlineContentBlock> TargetViews(Window window) => All<InlineContentBlock>(window, "target");

    public static List<TargetEditor> VisibleEditors(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        return window.GetVisualDescendants().OfType<TargetEditor>().Where(e => e.IsEffectivelyVisible).ToList();
    }

    public static List<Border> Chips(InlineContentBlock block) =>
        block.Inlines!.OfType<InlineUIContainer>().Select(c => Assert.IsType<Border>(c.Child)).ToList();

    public static List<Border> EditorChips(TargetEditor editor) => All<Border>(editor.Editor, "chip");

    public static string Label(Border chip) =>
        string.Join(" ", chip.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text));

    /// <summary>The block's text, with each chip as its label.</summary>
    public static string PlainText(InlineContentBlock block) => string.Concat((block.Inlines ?? []).Select(InlineText));

    private static string InlineText(Inline inline) => inline switch
    {
        Run run => run.Text ?? "",
        InlineUIContainer { Child: Border chip } => chip.GetLogicalDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("label")).Text ?? "",
        _ => throw new InvalidOperationException($"Unexpected inline {inline.GetType().Name}."),
    };
}
