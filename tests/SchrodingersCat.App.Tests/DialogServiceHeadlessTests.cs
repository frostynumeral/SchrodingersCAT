using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using SchrodingersCat.App.Services;
using SchrodingersCat.App.ViewModels;
using SchrodingersCat.App.Views;
using static SchrodingersCat.App.Tests.Headless;

namespace SchrodingersCat.App.Tests;

/// <summary>The real dialogs, through the real dialog service, on Avalonia's headless platform.</summary>
public sealed class DialogServiceHeadlessTests : IDisposable
{
    private readonly TestFiles _files = new();

    public void Dispose() => _files.Dispose();

    [AvaloniaTheory]
    [InlineData("SaveButton", UnsavedChangesChoice.Save)]
    [InlineData("DontSaveButton", UnsavedChangesChoice.DontSave)]
    [InlineData("CancelButton", UnsavedChangesChoice.Cancel)]
    public async Task UnsavedChangesDialog_ReturnsTheClickedChoice(string button, UnsavedChangesChoice expected)
    {
        var (owner, prompt, dialog) = AskToSaveChanges();

        dialog.FindControl<Button>(button)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitFor(() => prompt.IsCompleted);

        Assert.Equal(expected, await prompt);
        Assert.Contains("sample.xlf", dialog.FindControl<TextBlock>("Message")!.Text);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task UnsavedChangesDialog_ClosedWithoutAnswer_MeansCancel()
    {
        var (owner, prompt, dialog) = AskToSaveChanges();

        dialog.Close(); // title bar close button
        await WaitFor(() => prompt.IsCompleted);

        Assert.Equal(UnsavedChangesChoice.Cancel, await prompt);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task UnsavedChangesDialog_Escape_MeansCancel()
    {
        var (owner, prompt, dialog) = AskToSaveChanges();

        dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        await WaitFor(() => prompt.IsCompleted);

        Assert.Equal(UnsavedChangesChoice.Cancel, await prompt);
        owner.Close();
    }

    [AvaloniaTheory]
    [InlineData("ConfirmButton", true)]
    [InlineData("CancelButton", false)]
    public async Task ConfirmDialog_ReturnsTheClickedChoice(string button, bool expected)
    {
        var (owner, answer, dialog) = Confirm();
        Assert.Equal("Replace", dialog.FindControl<Button>("ConfirmButton")!.Content);

        dialog.FindControl<Button>(button)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitFor(() => answer.IsCompleted);

        Assert.Equal(expected, await answer);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task ConfirmDialog_ClosedWithoutAnswer_MeansNo()
    {
        var (owner, answer, dialog) = Confirm();

        dialog.Close();
        await WaitFor(() => answer.IsCompleted);

        Assert.False(await answer);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task TargetLanguageDialog_AcceptsOnlyAValidCode()
    {
        var (owner, answer, dialog) = AskTargetLanguage();
        var box = dialog.FindControl<TextBox>("LanguageBox")!;
        var ok = dialog.FindControl<Button>("OkButton")!;
        Assert.False(ok.IsEnabled);
        Assert.True(box.IsFocused);

        dialog.KeyTextInput("EN");
        Dispatcher.UIThread.RunJobs();
        Assert.False(ok.IsEnabled);
        Assert.Equal("That's the source language (en).", dialog.FindControl<TextBlock>("Problem")!.Text);

        box.Text = "hu ";
        Dispatcher.UIThread.RunJobs();
        Assert.True(ok.IsEnabled);
        Press(dialog, PhysicalKey.Enter);
        await WaitFor(() => answer.IsCompleted);

        Assert.Equal("hu", await answer);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task TargetLanguageDialog_Escape_MeansCancel()
    {
        var (owner, answer, dialog) = AskTargetLanguage();

        Press(dialog, PhysicalKey.Escape);
        await WaitFor(() => answer.IsCompleted);

        Assert.Null(await answer);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task OpenFolder_CreatesTheFolder_AndOneThatCantBeCreated_IsFalseNotACrash()
    {
        var owner = new Window();
        owner.Show();
        var dialogs = new AvaloniaDialogService(owner);
        var nested = _files.PathOf(Path.Combine("logs", "deeper"));

        // Whether a file manager opened is the platform's business (the headless one has none).
        await dialogs.OpenFolderAsync(nested);

        Assert.True(Directory.Exists(nested));
        var underAFile = Path.Combine(_files.Write("a file", ""), "logs");
        Assert.False(await dialogs.OpenFolderAsync(underAFile));
        owner.Close();
    }

    private static (Window Owner, Task<UnsavedChangesChoice> Answer, UnsavedChangesDialog Dialog) AskToSaveChanges() =>
        Ask<UnsavedChangesDialog, UnsavedChangesChoice>(d => d.AskToSaveChangesAsync("sample.xlf"));

    private static (Window Owner, Task<bool> Answer, ConfirmDialog Dialog) Confirm() =>
        Ask<ConfirmDialog, bool>(d => d.ConfirmAsync("Copy source", "Replace?", "Replace"));

    private static (Window Owner, Task<string?> Answer, TargetLanguageDialog Dialog) AskTargetLanguage() =>
        Ask<TargetLanguageDialog, string?>(d => d.AskTargetLanguageAsync("en", t => LanguageTag.Problem(t, "en")));

    /// <summary>Shows an owner window, asks through the dialog service, and finds the dialog that opened.</summary>
    private static (Window Owner, Task<T> Answer, TDialog Dialog) Ask<TDialog, T>(Func<AvaloniaDialogService, Task<T>> ask)
        where TDialog : Window
    {
        var owner = new Window();
        owner.Show();
        var answer = ask(new AvaloniaDialogService(owner));
        Dispatcher.UIThread.RunJobs();
        return (owner, answer, owner.OwnedWindows.OfType<TDialog>().Single());
    }
}
