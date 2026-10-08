using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace SchrodingersCat.App.Services;

/// <summary>
/// Work an event handler starts and can't await. Its exception would stay in a <see cref="Task"/> nobody looks at (the
/// window just wouldn't close, a paste would do nothing), and <see cref="TaskScheduler.UnobservedTaskException"/> sees it
/// only if the GC happens to finalize the task, so <see cref="Forget"/> logs it as an Error. It doesn't crash the app: these
/// are clipboard and closing chores, and the log line is what a crash report needs.
/// </summary>
internal static partial class Background
{
    /// <param name="work">How the log names the work: by default, the expression that started it.</param>
    public static void Forget(this Task task, [CallerArgumentExpression(nameof(task))] string work = "") =>
        task.Forget(App.Loggers.CreateLogger(typeof(Background).FullName!), work);

    public static void Forget(this Task task, ILogger logger, string work)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(logger);
        task.ContinueWith(
            failed =>
            {
                var exceptions = failed.Exception!;
                Exception exception = exceptions.InnerExceptions.Count == 1 ? exceptions.InnerExceptions[0] : exceptions;
                Failed(logger, exception, work);
            },
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "{Work} failed")]
    private static partial void Failed(ILogger logger, Exception exception, string work);
}
