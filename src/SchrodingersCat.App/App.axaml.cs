using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SchrodingersCat.App.Services;
using SchrodingersCat.App.ViewModels;
using SchrodingersCat.App.Views;
using SchrodingersCat.Core.Xliff;

namespace SchrodingersCat.App;

public partial class App : Application
{
    /// <summary>Where the app's classes log: the log file once <see cref="Program.Main"/> has set it, nowhere in tests.</summary>
    internal static ILoggerFactory Loggers { get; set; } = NullLoggerFactory.Instance;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // The headless tests have no desktop lifetime and create their own windows.
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            var viewModel = new MainWindowViewModel(
                new Xliff20Serializer(Loggers.CreateLogger<Xliff20Serializer>()), new AvaloniaDialogService(window), new JsonSettingsStore(JsonSettingsStore.DefaultPath));
            window.DataContext = viewModel;
            desktop.MainWindow = window;
            viewModel.RestoreTranslationMemory(); // the TM that was open last time (decision 1)
        }

        base.OnFrameworkInitializationCompleted();
    }
}
