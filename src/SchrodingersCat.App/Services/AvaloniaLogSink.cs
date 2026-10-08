using System.Text;
using Avalonia;
using Avalonia.Logging;
using Microsoft.Extensions.Logging;

namespace SchrodingersCat.App.Services;

/// <summary>
/// Passes Avalonia's own log events (<see cref="Logger.Sink"/>) on to <see cref="ILogger"/>s, at their level and in the
/// category <c>Avalonia.&lt;area&gt;</c> (e.g. <c>Avalonia.Binding</c>). The message reads as Avalonia's
/// <c>LogToTrace</c> writes it: values in quotes, then the type and name of the element it's about.
/// </summary>
public sealed partial class AvaloniaLogSink(ILoggerFactory loggers, LogEventLevel minimumLevel = LogEventLevel.Warning) : ILogSink
{
    private readonly ILoggerFactory _loggers = loggers ?? throw new ArgumentNullException(nameof(loggers));

    public bool IsEnabled(LogEventLevel level, string area) => level >= minimumLevel;

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate) =>
        Log(level, area, source, messageTemplate, []);

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
    {
        if (!IsEnabled(level, area))
            return;
        var logger = _loggers.CreateLogger("Avalonia." + area);
        var logLevel = LevelOf(level);
        if (logger.IsEnabled(logLevel))
        {
            var message = Render(messageTemplate, propertyValues, source);
            Event(logger, logLevel, message);
        }
    }

    internal static LogLevel LevelOf(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => LogLevel.Trace,
        LogEventLevel.Debug => LogLevel.Debug,
        LogEventLevel.Information => LogLevel.Information,
        LogEventLevel.Warning => LogLevel.Warning,
        LogEventLevel.Error => LogLevel.Error,
        _ => LogLevel.Critical,
    };

    /// <summary>Fills each <c>{Name}</c> with the next value in quotes (<c>{{</c> is a brace), as Avalonia does.</summary>
    internal static string Render(string template, object?[]? values, object? source)
    {
        var text = new StringBuilder(template.Length + 32);
        var next = 0;
        for (var i = 0; i < template.Length; i++)
        {
            if (template[i] != '{')
                text.Append(template[i]);
            else if (i + 1 < template.Length && template[i + 1] == '{')
                text.Append(template[++i]);
            else
            {
                text.Append('\'').Append(values is not null && next < values.Length ? Describe(values[next++]) : null).Append('\'');
                var end = template.IndexOf('}', i);
                i = end < 0 ? template.Length : end;
            }
        }
        if (source is not null)
        {
            text.Append(" (").Append(source.GetType().Name).Append(" #")
                .Append(source is StyledElement { Name: { } name } ? name : source.GetHashCode()).Append(')');
        }
        return text.ToString();
    }

    // A value's ToString runs arbitrary code; a log line must not throw because of it.
    private static string? Describe(object? value)
    {
        try
        {
            return value?.ToString();
        }
        catch (Exception ex)
        {
            return "(" + value!.GetType().Name + ": ToString threw " + ex.GetType().Name + ")";
        }
    }

    [LoggerMessage(Message = "{Message}")]
    private static partial void Event(ILogger logger, LogLevel level, string message);
}
