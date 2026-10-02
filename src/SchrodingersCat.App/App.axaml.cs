using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SchrodingersCat.App.Services;
using SchrodingersCat.App.ViewModels;
using SchrodingersCat.App.Views;
using SchrodingersCat.Core.Xliff;

namespace SchrodingersCat.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // The headless tests have no desktop lifetime and create their own windows.
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            window.DataContext = new MainWindowViewModel(new Xliff20Serializer(), new AvaloniaDialogService(window));
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
