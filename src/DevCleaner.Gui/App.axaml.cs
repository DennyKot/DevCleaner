using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DevCleaner.Gui.ViewModels;
using DevCleaner.Gui.Views;
using DevCleaner.Infrastructure;

namespace DevCleaner.Gui;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow { DataContext = new MainViewModel(AppHost.Create()) };
        base.OnFrameworkInitializationCompleted();
    }
}
