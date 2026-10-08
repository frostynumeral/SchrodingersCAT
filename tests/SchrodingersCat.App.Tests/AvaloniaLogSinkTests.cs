using Avalonia.Controls;
using Avalonia.Logging;
using Microsoft.Extensions.Logging;
using SchrodingersCat.App.Services;

namespace SchrodingersCat.App.Tests;

public sealed class AvaloniaLogSinkTests : IDisposable
{
    private readonly TestFiles _files = new();

    public void Dispose() => _files.Dispose();

    [Fact]
    public void AnEvent_KeepsItsLevelAndArea_AndReadsAsAvaloniasOwn()
    {
        var path = _files.PathOf("app.log");
        using (var file = new RollingFileLoggerProvider(path, LogLevel.Trace))
        using (var loggers = LoggerFactory.Create(builder => builder.AddProvider(file).SetMinimumLevel(LogLevel.Trace)))
        {
            var sink = new AvaloniaLogSink(loggers);

            sink.Log(LogEventLevel.Information, LogArea.Binding, null, "below the minimum");
            sink.Log(LogEventLevel.Error, LogArea.Binding, new TextBlock { Name = "StatusText" }, "Error binding {Property} to {Path}", "Text", "A.B");
            sink.Log(LogEventLevel.Fatal, LogArea.Visual, 42, "Rendering failed");
        }

        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        Assert.EndsWith("[ERR] [" + Environment.ProcessId + "] Avalonia.Binding: Error binding 'Text' to 'A.B' (TextBlock #StatusText)", lines[0], StringComparison.Ordinal);
        Assert.EndsWith("[CRT] [" + Environment.ProcessId + "] Avalonia.Visual: Rendering failed (Int32 #42)", lines[1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{A} and {B}", "'1' and ''")]
    [InlineData("unclosed {A", "unclosed '1'")]
    [InlineData("{{A}}", "{A}}")]
    public void Templates_AreFilledLikeAvalonias_EvenWhenMalformed(string template, string expected) =>
        Assert.Equal(expected, AvaloniaLogSink.Render(template, [1], source: null));

    [Fact]
    public void AValueWhoseToStringThrows_DoesntLoseTheLine() =>
        Assert.Equal("value '(Thrower: ToString threw InvalidOperationException)'", AvaloniaLogSink.Render("value {V}", [new Thrower()], source: null));

    private sealed class Thrower
    {
        public override string ToString() => throw new InvalidOperationException();
    }
}
