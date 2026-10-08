using System.Globalization;
using Microsoft.Extensions.Logging;
using SchrodingersCat.App.Services;

namespace SchrodingersCat.App.Tests;

public sealed class RollingFileLoggerTests : IDisposable
{
    private readonly TestFiles _files = new();

    public void Dispose() => _files.Dispose();

    private string LogPath => _files.PathOf(Path.Combine("logs", "app.log"));

    private static string[] Lines(string path) => File.ReadAllLines(path);

    // "a" for a line whose message is "a xxx…".
    private static string FirstWord(string line) => line.Split("C: ")[1].Split(' ')[0];

    // What a [LoggerMessage] method does, without the source generator.
    private static void Write(ILogger logger, LogLevel level, string message, Exception? exception = null) =>
        logger.Log(level, default, message, exception, (state, _) => state);

    [Fact]
    public void ALine_HasTheTimeLevelProcessCategoryAndMessage_AndIsInTheFileAtOnce()
    {
        using var provider = new RollingFileLoggerProvider(LogPath);

        Write(provider.CreateLogger("Some.Category"), LogLevel.Warning, "Disk C: is full");

        // Not disposed yet: each line is flushed as it's written, so a crash can't lose it.
        var line = Assert.Single(Lines(LogPath));
        Assert.Matches(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3} [+-]\d\d:\d\d \[WRN\] \[\d+\] Some\.Category: Disk C: is full$", line);
        Assert.Contains($"[{Environment.ProcessId}]", line, StringComparison.Ordinal);
    }

    [Fact]
    public void AnException_FollowsItsLine_WithItsStackTrace()
    {
        using var provider = new RollingFileLoggerProvider(LogPath);
        Exception thrown;
        try
        {
            throw new InvalidOperationException("boom");
        }
        catch (InvalidOperationException ex)
        {
            thrown = ex;
        }

        Write(provider.CreateLogger("C"), LogLevel.Critical, "Crashed", thrown);

        var text = File.ReadAllText(LogPath);
        Assert.Contains("[CRT]", text, StringComparison.Ordinal);
        Assert.Contains("C: Crashed\nSystem.InvalidOperationException: boom\n", text, StringComparison.Ordinal);
        Assert.Contains(nameof(AnException_FollowsItsLine_WithItsStackTrace), text, StringComparison.Ordinal);
    }

    [Fact]
    public void LevelsBelowTheMinimum_AreLeftOut()
    {
        using var provider = new RollingFileLoggerProvider(LogPath, LogLevel.Warning);
        var logger = provider.CreateLogger("C");

        Assert.False(logger.IsEnabled(LogLevel.Information));
        Assert.False(logger.IsEnabled(LogLevel.None));
        Write(logger, LogLevel.Information, "left out");
        Write(logger, LogLevel.Error, "kept");

        Assert.EndsWith("C: kept", Assert.Single(Lines(LogPath)), StringComparison.Ordinal);
    }

    [Fact]
    public void AFullFile_Rotates_AndOnlyTheNewestFilesAreKept()
    {
        using var provider = new RollingFileLoggerProvider(LogPath, maxFileBytes: 200, fileCount: 3);
        var logger = provider.CreateLogger("C");

        for (var i = 0; i < 20; i++)
            Write(logger, LogLevel.Information, $"message {i:D2} {new string('x', 60)}");

        Assert.Equal(_files.PathOf(Path.Combine("logs", "app.1.log")), provider.PathOf(1));
        Assert.False(File.Exists(provider.PathOf(3)));
        var kept = Enumerable.Range(0, 3).Reverse().SelectMany(age => Lines(provider.PathOf(age))).ToList();
        foreach (var age in Enumerable.Range(0, 3))
            Assert.InRange(new FileInfo(provider.PathOf(age)).Length, 1, 200);
        // The newest messages, in order, with none lost between the files.
        var numbers = kept.Select(line => int.Parse(line.Split("message ")[1][..2], CultureInfo.InvariantCulture)).ToList();
        Assert.Equal(19, numbers[^1]);
        Assert.Equal(Enumerable.Range(numbers[0], numbers.Count), numbers);
    }

    [Fact]
    public void ALineLongerThanTheLimit_GetsAFileOfItsOwn()
    {
        using var provider = new RollingFileLoggerProvider(LogPath, maxFileBytes: 50, fileCount: 2);
        var logger = provider.CreateLogger("C");

        Write(logger, LogLevel.Information, "first " + new string('a', 100));
        Write(logger, LogLevel.Information, "second " + new string('b', 100));

        Assert.Contains("first", Assert.Single(Lines(provider.PathOf(1))), StringComparison.Ordinal);
        Assert.Contains("second", Assert.Single(Lines(LogPath)), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnwritableFile_LosesTheLine_ButNeverThrows_AndLoggingResumesWhenItCan()
    {
        // The log file's path is a directory, so it can't be opened.
        Directory.CreateDirectory(LogPath);
        using var provider = new RollingFileLoggerProvider(LogPath);
        var logger = provider.CreateLogger("C");

        Write(logger, LogLevel.Error, "lost");

        Directory.Delete(LogPath);
        Write(logger, LogLevel.Error, "kept");
        Assert.EndsWith("C: kept", Assert.Single(Lines(LogPath)), StringComparison.Ordinal);
    }

    [Fact]
    public void AfterDispose_LinesAreDropped()
    {
        var provider = new RollingFileLoggerProvider(LogPath);
        var logger = provider.CreateLogger("C");
        Write(logger, LogLevel.Error, "kept");

        provider.Dispose();
        Write(logger, LogLevel.Error, "dropped");

        Assert.Single(Lines(LogPath));
    }

    // On Windows a log viewer holding the file open without delete sharing makes the move fail the same way.
    [Fact]
    public void ARotationThatCantMoveTheCurrentFile_MovesNothing_AndTheLinesGoInPastTheLimit()
    {
        using var provider = new RollingFileLoggerProvider(LogPath, maxFileBytes: 200, fileCount: 3);
        var logger = provider.CreateLogger("C");
        Write(logger, LogLevel.Information, "a " + new string('x', 60));
        Write(logger, LogLevel.Information, "b " + new string('x', 60));
        var blocked = Directory.CreateDirectory(Path.Combine(LogPath + ".rotating", "in the way"));

        Write(logger, LogLevel.Information, "c " + new string('x', 60));
        Write(logger, LogLevel.Information, "d " + new string('x', 60));

        Assert.Equal(["a"], Lines(provider.PathOf(1)).Select(FirstWord));
        Assert.Equal(["b", "c", "d"], Lines(LogPath).Select(FirstWord));
        Assert.False(File.Exists(provider.PathOf(2)));

        blocked.Parent!.Delete(recursive: true);
        Write(logger, LogLevel.Information, "e " + new string('x', 60));

        Assert.Equal(["a"], Lines(provider.PathOf(2)).Select(FirstWord));
        Assert.Equal(["b", "c", "d"], Lines(provider.PathOf(1)).Select(FirstWord));
        Assert.Equal(["e"], Lines(LogPath).Select(FirstWord));
    }

    [Fact]
    public void ARotationThatCantMoveTheOlderFiles_PutsTheCurrentFileBack_AndLosesNothing()
    {
        using var provider = new RollingFileLoggerProvider(LogPath, maxFileBytes: 200, fileCount: 3);
        var logger = provider.CreateLogger("C");
        Write(logger, LogLevel.Information, "a " + new string('x', 60));
        Write(logger, LogLevel.Information, "b " + new string('x', 60));
        var blocked = Directory.CreateDirectory(Path.Combine(provider.PathOf(2), "in the way"));

        Write(logger, LogLevel.Information, "c " + new string('x', 60));
        Write(logger, LogLevel.Information, "d " + new string('x', 60));

        Assert.Equal(["a"], Lines(provider.PathOf(1)).Select(FirstWord));
        Assert.Equal(["b", "c", "d"], Lines(LogPath).Select(FirstWord));
        Assert.False(File.Exists(LogPath + ".rotating"));

        blocked.Parent!.Delete(recursive: true);
        Write(logger, LogLevel.Information, "e " + new string('x', 60));

        Assert.Equal(["a"], Lines(provider.PathOf(2)).Select(FirstWord));
        Assert.Equal(["b", "c", "d"], Lines(provider.PathOf(1)).Select(FirstWord));
        Assert.Equal(["e"], Lines(LogPath).Select(FirstWord));
    }

    // A crash between moving the current file aside and moving it to .1 leaves it there.
    [Fact]
    public void AFileLeftAsideByAnUnfinishedRotation_IsRotatedFirst_NotOverwritten()
    {
        using var provider = new RollingFileLoggerProvider(LogPath, maxFileBytes: 200, fileCount: 3);
        var logger = provider.CreateLogger("C");
        Write(logger, LogLevel.Information, "a " + new string('x', 60));
        Write(logger, LogLevel.Information, "b " + new string('x', 60));
        File.Move(LogPath, LogPath + ".rotating");
        Write(logger, LogLevel.Information, "c " + new string('x', 60));

        Write(logger, LogLevel.Information, "d " + new string('x', 60));

        // "b" finished its rotation first, so "a" is the one dropped as the oldest.
        Assert.Equal(["b"], Lines(provider.PathOf(2)).Select(FirstWord));
        Assert.Equal(["c"], Lines(provider.PathOf(1)).Select(FirstWord));
        Assert.Equal(["d"], Lines(LogPath).Select(FirstWord));
        Assert.False(File.Exists(LogPath + ".rotating"));
    }

    // Also shows that this platform's "held by another process" is recognised: otherwise the line wouldn't wait.
    [Fact]
    public void ALockHeldByAnotherProcess_IsWaitedFor_ThenTheLineGoesInWithoutIt()
    {
        using var provider = new RollingFileLoggerProvider(LogPath);
        var logger = provider.CreateLogger("C");
        Write(logger, LogLevel.Information, "first");
        using (new FileStream(LogPath + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Write(logger, LogLevel.Information, "second");
            Assert.InRange(clock.Elapsed, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(30));
        }

        Assert.EndsWith("C: second", Lines(LogPath)[1], StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyALockHeldElsewhere_IsWorthWaitingFor()
    {
        Assert.False(RollingFileLoggerProvider.IsHeldElsewhere(new FileNotFoundException()));
        Assert.False(RollingFileLoggerProvider.IsHeldElsewhere(new IOException("Read-only file system", 30)));
    }

    [Fact]
    public void TwoWritersOfTheSameFile_LikeTwoAppInstances_LoseNoLines_WhileRotating()
    {
        using var first = new RollingFileLoggerProvider(LogPath, maxFileBytes: 2048, fileCount: 1000);
        using var second = new RollingFileLoggerProvider(LogPath, maxFileBytes: 2048, fileCount: 1000);
        ILogger[] loggers = [first.CreateLogger("C"), second.CreateLogger("C")];

        Parallel.For(0, 400, i => Write(loggers[i % 2], LogLevel.Information, $"line {i} {new string('z', 40)}"));

        var lines = Directory.GetFiles(Path.GetDirectoryName(LogPath)!, "*.log").SelectMany(Lines).ToList();
        Assert.All(lines, line => Assert.Matches(@"C: line \d+ z{40}$", line));
        Assert.Equal(Enumerable.Range(0, 400), lines.Select(line => int.Parse(line.Split("line ")[1].Split(' ')[0], CultureInfo.InvariantCulture)).Order());
        Assert.All(Directory.GetFiles(Path.GetDirectoryName(LogPath)!, "*.log"), path => Assert.InRange(new FileInfo(path).Length, 1, 2048));
    }

    [Fact]
    public void AnExceptionThatCantDescribeItself_OrAMessageThatCantBeFormatted_StillGetsItsLine()
    {
        using var provider = new RollingFileLoggerProvider(LogPath);
        var logger = provider.CreateLogger("C");

        var undescribable = new UndescribableException();
        logger.Log(LogLevel.Critical, default, "state", undescribable, (_, _) => "Crashed");
        logger.Log<string>(LogLevel.Error, default, "state", null, (_, _) => throw new FormatException());

        var lines = Lines(LogPath);
        Assert.Equal(3, lines.Length);
        Assert.EndsWith("C: Crashed", lines[0], StringComparison.Ordinal);
        Assert.Equal($"{typeof(UndescribableException).FullName} (its description threw System.InvalidOperationException)", lines[1]);
        Assert.EndsWith("C: (the message couldn't be formatted: System.FormatException)", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public void LinesFromManyThreads_NeverInterleave()
    {
        using var provider = new RollingFileLoggerProvider(LogPath, maxFileBytes: 4096, fileCount: 100);
        var logger = provider.CreateLogger("C");

        Parallel.For(0, 400, i => Write(logger, LogLevel.Information, $"thread line {i} {new string('y', 40)}"));

        var lines = Directory.GetFiles(Path.GetDirectoryName(LogPath)!, "*.log").SelectMany(Lines).ToList();
        Assert.Equal(400, lines.Count);
        Assert.All(lines, line => Assert.Matches(@"C: thread line \d+ y{40}$", line));
        Assert.Equal(400, lines.Select(line => line.Split("thread line ")[1].Split(' ')[0]).Distinct().Count());
    }

    [Theory]
    [InlineData(null, LogLevel.Information)]
    [InlineData("", LogLevel.Information)]
    [InlineData("debug", LogLevel.Debug)]
    [InlineData("Warning", LogLevel.Warning)]
    [InlineData("nonsense", LogLevel.Information)]
    [InlineData("42", LogLevel.Information)]
    public void TheLogLevelVariable_IsReadLeniently(string? setting, LogLevel expected) =>
        Assert.Equal(expected, Program.LogLevelFrom(setting));

    private sealed class UndescribableException : Exception
    {
        public override string ToString() => throw new InvalidOperationException();
    }
}
