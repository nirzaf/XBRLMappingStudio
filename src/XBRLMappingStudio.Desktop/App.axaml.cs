using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using XBRLMappingStudio.Desktop.ViewModels;
using XBRLMappingStudio.Application;
using AvaloniaApplication = Avalonia.Application;

namespace XBRLMappingStudio.Desktop;

public partial class App : AvaloniaApplication
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            window.DataContext = new MainWindowViewModel(
                new AvaloniaProjectFileDialog(window),
                () => new SuggestionAuditStore(new LocalApplicationDatabase()));
            desktop.MainWindow = window;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
