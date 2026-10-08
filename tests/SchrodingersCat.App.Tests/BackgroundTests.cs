using Microsoft.Extensions.Logging;
using SchrodingersCat.App.Services;

namespace SchrodingersCat.App.Tests;

public sealed class BackgroundTests : IDisposable
{
    private readonly TestFiles _files = new();

    public void Dispose() => _files.Dispose();

    [Fact]
    public async Task FailedWork_IsLoggedAsAnError_WithItsException()
    {
        var path = _files.PathOf("app.log");
        using var file = new RollingFileLoggerProvider(path);
        var failing = Task.Run(() => throw new InvalidOperationException("clipboard gone"), TestContext.Current.CancellationToken);

        failing.Forget(file.CreateLogger("Background"), "PasteAsync()");
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing);
        await WaitForAsync(() => File.Exists(path) && File.ReadAllText(path).Contains("clipboard gone\n", StringComparison.Ordinal));

        var text = File.ReadAllText(path);
        Assert.Contains("[ERR]", text, StringComparison.Ordinal);
        Assert.Contains("Background: PasteAsync() failed\nSystem.InvalidOperationException: clipboard gone\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkThatSucceedsOrIsCancelled_LogsNothing()
    {
        var path = _files.PathOf("app.log");
        using var file = new RollingFileLoggerProvider(path, LogLevel.Trace);
        var logger = file.CreateLogger("Background");

        Task.CompletedTask.Forget(logger, "done");
        var cancelled = Task.FromCanceled(new CancellationToken(canceled: true));
        cancelled.Forget(logger, "cancelled");
        await Task.WhenAny(cancelled);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.False(File.Exists(path));
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10, TestContext.Current.CancellationToken);
    }
}
