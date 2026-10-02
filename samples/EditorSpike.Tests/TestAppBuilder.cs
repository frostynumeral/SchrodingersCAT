using Avalonia;
using Avalonia.Headless;
using EditorSpike.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace EditorSpike.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<SpikeApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
