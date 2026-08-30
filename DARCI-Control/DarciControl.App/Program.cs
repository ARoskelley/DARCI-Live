using Avalonia;

namespace DarciControl.App;

internal static class Program
{
    // Avalonia requires this to run before anything touches the toolkit, so keep it free of app logic.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
