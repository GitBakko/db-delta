using Avalonia;

namespace DbDelta.App;

internal static class Program
{
    // STAThread is required on Windows for clipboard / drag-and-drop / COM interop.
    [STAThread]
    public static int Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    // Public + static so Avalonia previewer / designer / headless tests can call it.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
                  .UsePlatformDetect()
                  .LogToTrace();
}
