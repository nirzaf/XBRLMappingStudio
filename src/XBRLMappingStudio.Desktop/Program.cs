using Avalonia;
using XBRLMappingStudio.Worker;

namespace XBRLMappingStudio.Desktop;

internal sealed class Program
{
    // Avalonia UI entry point (UI01). Initialization is configured via
    // AppBuilder; do not edit this code or use it before Avalonia is fully initialized.
    public static void Main(string[] args)
    {
        if (PdfExtractionWorkerProtocol.IsInternalCommand(args))
        {
            Environment.ExitCode = PdfExtractionWorkerProtocol.RunAsync().GetAwaiter().GetResult();
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
