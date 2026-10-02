using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace EditorSpike;

public partial class SpikeApp : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new SpikeWindow(SampleUnit.Load(rightToLeft: desktop.Args?.Contains("--rtl") == true));

        base.OnFrameworkInitializationCompleted();
    }
}
