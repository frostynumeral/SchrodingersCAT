using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace SchrodingersCat.App.Services;

/// <summary>
/// Writes log lines to a file, by default <c>SchrodingersCAT/logs/schrodingerscat.log</c> in the user's local application
/// data folder, so a user can send it in when the app crashes. When the file would grow past <c>maxFileBytes</c> it
/// becomes <c>schrodingerscat.1.log</c>, the older ones move up one, and only <c>fileCount</c> files are kept.
/// </summary>
/// <remarks>
/// Each line is appended and the file closed again before <see cref="ILogger.Log{TState}"/> returns, so the last lines
/// before a crash are in the file, and a log viewer can always read it. Logging never throws: a line that can't be written
/// is lost, and the next one tries again. If rotating fails (on Windows, a viewer may hold a file open), the current file
/// stays and the line goes into it, past the limit.
/// <para>
/// Several app instances share the file: each line is written holding <c>schrodingerscat.log.lock</c> exclusively, so
/// lines from different processes never overwrite each other or rotate at the same time. Lines carry the process id.
/// </para>
/// </remarks>
public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    public const long DefaultMaxFileBytes = 1024 * 1024;

    public const int DefaultFileCount = 5;

    // How long a line waits for another process to finish its own: 1 s.
    private const int LockAttempts = 100;
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(10);

    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly string ProcessId = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);

    private readonly Lock _gate = new();
    private readonly long _maxFileBytes;
    private readonly int _fileCount;
    private bool _disposed;

    public RollingFileLoggerProvider(
        string path, LogLevel minimumLevel = LogLevel.Information, long maxFileBytes = DefaultMaxFileBytes, int fileCount = DefaultFileCount)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFileBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(fileCount, 1);

        FilePath = Path.GetFullPath(path);
        MinimumLevel = minimumLevel;
        _maxFileBytes = maxFileBytes;
        _fileCount = fileCount;
    }

    // Local, not roaming: logs describe this machine. DoNotVerify: a folder that doesn't exist yet (e.g. ~/.local/share on
    // a fresh Linux account) would give "", and the log would land in the working directory. The temporary folder is for
    // systems that have no such folder at all.
    public static string DefaultPath
    {
        get
        {
            var data = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
            return Path.Combine(data.Length > 0 ? data : Path.GetTempPath(), "SchrodingersCAT", "logs", "schrodingerscat.log");
        }
    }

    /// <summary>The current file; the older ones are <see cref="PathOf"/> 1, 2, ….</summary>
    public string FilePath { get; }

    public LogLevel MinimumLevel { get; }

    private string LockPath => FilePath + ".lock";

    // Where the current file goes while the older ones move up.
    private string RotatingPath => FilePath + ".rotating";

    /// <summary>The file <paramref name="age"/> rotations old: 0 is <see cref="FilePath"/>, 1 is <c>name.1.log</c>.</summary>
    public string PathOf(int age) =>
        age == 0 ? FilePath : Path.ChangeExtension(FilePath, null) + "." + age.ToString(CultureInfo.InvariantCulture) + Path.GetExtension(FilePath);

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName ?? "");

    /// <summary>Drops the lines logged from now on (the file is never held open between lines).</summary>
    public void Dispose()
    {
        lock (_gate)
            _disposed = true;
    }

    private void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var line = new StringBuilder()
            .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
            .Append(" [").Append(LevelName(level)).Append("] [").Append(ProcessId).Append("] ")
            .Append(category).Append(": ").Append(message);
        if (exception is not null)
            line.Append('\n').Append(Describe(exception));
        var bytes = Utf8.GetBytes(line.ToString().ReplaceLineEndings("\n") + "\n");

        lock (_gate)
        {
            if (_disposed)
                return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                using var otherProcesses = LockOtherProcessesOut();
                // A line longer than the limit still goes into a file of its own.
                if (new FileInfo(FilePath) is { Exists: true, Length: > 0 } current && current.Length + bytes.Length > _maxFileBytes)
                    TryRotate();
                // Append seeks to the end once, when it opens; holding the lock, that is the end when it writes.
                using var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                stream.Write(bytes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // The line is lost; the next one tries again.
            }
        }
    }

    // Null when the lock file can't be had in time: writing without it is better than not writing (a crash is waiting).
    // Only another process holding it is worth waiting for; any other failure (a read-only or full disk) won't pass, and
    // waiting would cost every line a second, on the UI thread too.
    private FileStream? LockOtherProcessesOut()
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex) when (attempt < LockAttempts && IsHeldElsewhere(ex))
            {
                Thread.Sleep(LockRetryDelay);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    // Windows: a sharing or lock violation. Elsewhere .NET emulates FileShare.None with flock, which fails with
    // EWOULDBLOCK (11 on Linux, 35 on macOS and the BSDs) as the HResult of a plain IOException.
    internal static bool IsHeldElsewhere(IOException exception) =>
        exception.GetType() == typeof(IOException)
        && (OperatingSystem.IsWindows()
            ? exception.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021)
            : exception.HResult == (OperatingSystem.IsLinux() ? 11 : 35));

    // Moves the current file aside first, so if that fails nothing has changed and the line goes in past the limit. If
    // moving the older ones up then fails, the current file goes back. A file left aside (a crash in the middle) finishes
    // its rotation first; until it can, nothing rotates. Nothing is lost but the oldest file.
    private void TryRotate()
    {
        if (File.Exists(RotatingPath) && !TryFinishRotation())
            return;
        try
        {
            File.Move(FilePath, RotatingPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }
        if (TryFinishRotation())
            return;
        try
        {
            File.Move(RotatingPath, FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // It waits aside for the next rotation; the line goes into a fresh current file.
        }
    }

    private bool TryFinishRotation()
    {
        try
        {
            if (_fileCount == 1)
            {
                File.Delete(RotatingPath);
                return true;
            }
            for (var age = _fileCount - 2; age >= 1; age--)
            {
                if (File.Exists(PathOf(age)))
                    File.Move(PathOf(age), PathOf(age + 1), overwrite: true);
            }
            File.Move(RotatingPath, PathOf(1), overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // A crash's exception is the one line that matters, so an exception that can't describe itself still gets one.
    private static string Describe(Exception exception)
    {
        try
        {
            return exception.ToString();
        }
        catch (Exception ex)
        {
            return exception.GetType().FullName + " (its description threw " + ex.GetType().FullName + ")";
        }
    }

    private static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => level.ToString(),
    };

    private sealed class FileLogger(RollingFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= provider.MinimumLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;
            ArgumentNullException.ThrowIfNull(formatter);
            string message;
            try
            {
                message = formatter(state, exception);
            }
            catch (Exception ex) // logging never throws, and the exception still deserves its line
            {
                message = "(the message couldn't be formatted: " + ex.GetType().FullName + ")";
            }
            provider.Write(logLevel, category, message, exception);
        }
    }
}
