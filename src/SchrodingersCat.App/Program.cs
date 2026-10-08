using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Logging;
using Microsoft.Extensions.Logging;
using Logger = Avalonia.Logging.Logger;
using SchrodingersCat.App.Services;

namespace SchrodingersCat.App;

internal static partial class Program
{
    /// <summary>Sets the lowest level written to the log file (e.g. <c>Debug</c>); the default is <c>Information</c>.</summary>
    public const string LogLevelVariable = "SCHRODINGERSCAT_LOG_LEVEL";

    // Don't use Avalonia, third-party APIs or SynchronizationContext-reliant code before AppMain is called.
    [STAThread]
    public static void Main(string[] args)
    {
        using var logFile = new RollingFileLoggerProvider(RollingFileLoggerProvider.DefaultPath, LogLevelFrom(Environment.GetEnvironmentVariable(LogLevelVariable)));
        using var loggers = LoggerFactory.Create(builder => builder.AddProvider(logFile).SetMinimumLevel(logFile.MinimumLevel));
        var log = loggers.CreateLogger(typeof(Program).FullName!);

        // Exceptions that escape the UI thread's loop come out of StartWithClassicDesktopLifetime, so this sees every crash
        // once, on whichever thread; each line is flushed as it's written, so it's in the file before the process ends.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(log, e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) => UnobservedTaskException(log, e.Exception);

        LogStarted(log);
        App.Loggers = loggers;
        var app = BuildAvaloniaApp();
        Logger.Sink = new AvaloniaLogSink(loggers); // replaces LogToTrace's, which BuildAvaloniaApp has just set
        app.StartWithClassicDesktopLifetime(args);
        Stopped(log);
    }

    // Also used by the visual designer and the headless tests.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    internal static LogLevel LogLevelFrom(string? setting) =>
        Enum.TryParse<LogLevel>(setting, ignoreCase: true, out var level) && Enum.IsDefined(level) ? level : LogLevel.Information;

    private static void LogStarted(ILogger log)
    {
        if (!log.IsEnabled(LogLevel.Information))
            return;
        var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        var os = RuntimeInformation.OSDescription;
        var runtime = RuntimeInformation.FrameworkDescription;
        Started(log, version, os, runtime);
    }

    // Anything can be thrown in other .NET languages, not only an Exception.
    private static void LogCrash(ILogger log, object thrown)
    {
        if (thrown is Exception exception)
            Crashed(log, exception);
        else if (log.IsEnabled(LogLevel.Critical))
        {
            var description = thrown.ToString();
            CrashedWith(log, description);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "SchrödingersCAT {Version} started on {Os}, {Runtime}")]
    private static partial void Started(ILogger logger, string version, string os, string runtime);

    [LoggerMessage(Level = LogLevel.Information, Message = "SchrödingersCAT stopped")]
    private static partial void Stopped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Unhandled exception, the app is closing")]
    private static partial void Crashed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Unhandled exception, the app is closing: {Thrown}")]
    private static partial void CrashedWith(ILogger logger, string? thrown);

    [LoggerMessage(Level = LogLevel.Error, Message = "A task's exception was never observed")]
    private static partial void UnobservedTaskException(ILogger logger, Exception exception);

}
