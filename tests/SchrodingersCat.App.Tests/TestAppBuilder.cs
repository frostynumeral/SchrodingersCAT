using Avalonia;
using Avalonia.Headless;
using SchrodingersCat.App.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace SchrodingersCat.App.Tests;

/// <summary>Runs the real <see cref="App"/> (styles, theme) on Avalonia's headless platform: no display needed, so it works in CI.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
