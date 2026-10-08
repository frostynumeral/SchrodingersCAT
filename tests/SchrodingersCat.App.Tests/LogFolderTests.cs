using SchrodingersCat.App.Services;
using SchrodingersCat.App.ViewModels;
using SchrodingersCat.Core.Xliff;

namespace SchrodingersCat.App.Tests;

public sealed class LogFolderTests
{
    [Fact]
    public async Task OpenLogFolder_ShowsTheFolderOfTheLogFile()
    {
        var dialogs = new FakeDialogs(null);
        var vm = new MainWindowViewModel(new Xliff20Serializer(), dialogs);

        await vm.OpenLogFolderCommand.ExecuteAsync(null);

        Assert.Equal([$"open folder: {Path.GetDirectoryName(RollingFileLoggerProvider.DefaultPath)}"], dialogs.Asked);
        Assert.Null(vm.Notice);
    }

    [Fact]
    public async Task WhenTheFolderCantBeShown_TheNoticeSaysWhereTheLogsAre()
    {
        var vm = new MainWindowViewModel(new Xliff20Serializer(), new FakeDialogs(null) { FolderOpens = false });

        await vm.OpenLogFolderCommand.ExecuteAsync(null);

        Assert.Equal($"Couldn't open the log folder. The log files are in {MainWindowViewModel.LogFolder}", vm.Notice);
    }
}
