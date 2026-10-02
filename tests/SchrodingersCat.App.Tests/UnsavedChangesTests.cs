using SchrodingersCat.App.Services;
using SchrodingersCat.App.ViewModels;
using SchrodingersCat.Core.Xliff;

namespace SchrodingersCat.App.Tests;

/// <summary>Unsaved edits are never lost without asking: opening another file, closing, and "Save as".</summary>
public sealed class UnsavedChangesTests : IDisposable
{
    private readonly TestFiles _files = new();
    private readonly Xliff20Serializer _serializer = new();

    public void Dispose() => _files.Dispose();

    [Fact]
    public async Task Open_WithoutChanges_DoesNotAsk()
    {
        var (vm, dialogs, _) = await OpenSampleAsync();

        await vm.OpenCommand.ExecuteAsync(null);

        Assert.Equal(["open"], dialogs.Asked);
    }

    [Fact]
    public async Task Open_WithChanges_Save_SavesAndThenOpens()
    {
        var (vm, dialogs, path) = await OpenEditedSampleAsync();
        var other = _files.Write("other.xlf", TestFiles.Sample.Replace("Hello world.", "Other file."));
        dialogs.Choice = UnsavedChangesChoice.Save;
        dialogs.OpenPath = other;

        await vm.OpenCommand.ExecuteAsync(null);

        Assert.Equal(["save changes to sample.xlf?", "open"], dialogs.Asked);
        Assert.Equal("Helló világ.", await FirstTargetAsync(path));
        Assert.Equal(other, vm.FilePath);
        Assert.Equal("Other file.", vm.Segments[0].SourceText);
    }

    [Fact]
    public async Task Open_WithChanges_DontSave_OpensWithoutSaving()
    {
        var (vm, dialogs, path) = await OpenEditedSampleAsync();
        dialogs.Choice = UnsavedChangesChoice.DontSave;

        await vm.OpenCommand.ExecuteAsync(null);

        Assert.Equal(["save changes to sample.xlf?", "open"], dialogs.Asked);
        Assert.Null(await FirstTargetAsync(path));
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task Open_WithChanges_Cancel_KeepsTheEditsAndShowsNoPicker()
    {
        var (vm, dialogs, path) = await OpenEditedSampleAsync();
        dialogs.Choice = UnsavedChangesChoice.Cancel;

        await vm.OpenCommand.ExecuteAsync(null);

        Assert.Equal(["save changes to sample.xlf?"], dialogs.Asked);
        Assert.True(vm.IsDirty);
        Assert.Equal("Helló világ.", vm.Segments[0].TargetText);
        Assert.Null(await FirstTargetAsync(path));
    }

    [Fact]
    public async Task Open_WithChanges_SaveFails_AbortsTheOpen()
    {
        var (vm, dialogs, _) = await OpenEditedSampleAsync(TestFiles.Sample.Replace(" trgLang=\"hu\"", "").Replace("<target>Viszlát.</target>", ""));
        dialogs.Choice = UnsavedChangesChoice.Save;

        await vm.OpenCommand.ExecuteAsync(null);

        Assert.Equal(["save changes to sample.xlf?"], dialogs.Asked);
        Assert.StartsWith("Could not save sample.xlf:", vm.ErrorMessage);
        Assert.True(vm.IsDirty);
    }

    [Fact]
    public async Task ConfirmClose_WithoutChanges_AllowsWithoutAsking()
    {
        var (vm, dialogs, _) = await OpenSampleAsync();

        Assert.True(await vm.ConfirmCloseAsync());
        Assert.Empty(dialogs.Asked);
    }

    [Theory]
    [InlineData(UnsavedChangesChoice.Save, true, "Helló világ.")]
    [InlineData(UnsavedChangesChoice.DontSave, true, null)]
    [InlineData(UnsavedChangesChoice.Cancel, false, null)]
    public async Task ConfirmClose_WithChanges_FollowsTheChoice(UnsavedChangesChoice choice, bool mayClose, string? savedTarget)
    {
        var (vm, dialogs, path) = await OpenEditedSampleAsync();
        dialogs.Choice = choice;

        Assert.Equal(mayClose, await vm.ConfirmCloseAsync());
        Assert.Equal(["save changes to sample.xlf?"], dialogs.Asked);
        Assert.Equal(savedTarget, await FirstTargetAsync(path));
    }

    [Fact]
    public async Task ConfirmClose_WithChanges_SaveFails_KeepsTheWindowOpen()
    {
        var (vm, dialogs, _) = await OpenEditedSampleAsync(TestFiles.Sample.Replace(" trgLang=\"hu\"", "").Replace("<target>Viszlát.</target>", ""));
        dialogs.Choice = UnsavedChangesChoice.Save;

        Assert.False(await vm.ConfirmCloseAsync());
        Assert.NotNull(vm.ErrorMessage);
        Assert.True(vm.IsDirty);
    }

    [Fact]
    public async Task SaveAs_WritesTheNewFileAndSwitchesToIt()
    {
        var (vm, dialogs, path) = await OpenEditedSampleAsync();
        var copy = _files.PathOf("copy.xlf");
        dialogs.SavePath = copy;

        await vm.SaveAsCommand.ExecuteAsync(null);

        Assert.Equal(["save as (suggested sample.xlf)"], dialogs.Asked);
        Assert.Equal(copy, vm.FilePath);
        Assert.False(vm.IsDirty);
        Assert.Equal("copy.xlf — SchrödingersCAT", vm.Title);
        Assert.Equal("Helló világ.", await FirstTargetAsync(copy));
        Assert.Null(await FirstTargetAsync(path)); // the original is untouched

        // Later saves go to the new file.
        vm.Segments[0].TargetText = "Szia világ.";
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Szia világ.", await FirstTargetAsync(copy));
        Assert.Null(await FirstTargetAsync(path));
    }

    [Fact]
    public async Task SaveAs_Cancelled_ChangesNothing()
    {
        var (vm, dialogs, path) = await OpenEditedSampleAsync();
        dialogs.SavePath = null;

        await vm.SaveAsCommand.ExecuteAsync(null);

        Assert.Equal(path, vm.FilePath);
        Assert.True(vm.IsDirty);
    }

    [Fact]
    public async Task SaveAs_Failure_KeepsTheOldPathAndShowsTheError()
    {
        var (vm, dialogs, path) = await OpenEditedSampleAsync();
        dialogs.SavePath = Path.Combine(_files.PathOf("no-such-directory"), "copy.xlf");

        await vm.SaveAsCommand.ExecuteAsync(null);

        Assert.Equal(path, vm.FilePath);
        Assert.True(vm.IsDirty);
        Assert.StartsWith("Could not save copy.xlf:", vm.ErrorMessage);
    }

    [Fact]
    public void SaveAs_NeedsAnOpenDocument()
    {
        var vm = new MainWindowViewModel(_serializer, new FakeDialogs(null));

        Assert.False(vm.SaveAsCommand.CanExecute(null));
    }

    private async Task<(MainWindowViewModel Vm, FakeDialogs Dialogs, string Path)> OpenSampleAsync(string content = TestFiles.Sample)
    {
        var path = _files.Write("sample.xlf", content);
        var dialogs = new FakeDialogs(path);
        var vm = new MainWindowViewModel(_serializer, dialogs);
        await vm.LoadAsync(path);
        return (vm, dialogs, path);
    }

    private async Task<(MainWindowViewModel Vm, FakeDialogs Dialogs, string Path)> OpenEditedSampleAsync(string content = TestFiles.Sample)
    {
        var opened = await OpenSampleAsync(content);
        opened.Vm.Segments[0].TargetText = "Helló világ.";
        return opened;
    }

    private async Task<string?> FirstTargetAsync(string path) =>
        (await _serializer.ReadFileAsync(path, TestContext.Current.CancellationToken)).Units.First().Segments[0].Target?.ToPlainText();
}
