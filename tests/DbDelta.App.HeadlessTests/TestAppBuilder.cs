using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;

[assembly: AvaloniaTestApplication(typeof(DbDelta.App.HeadlessTests.TestAppBuilder))]

namespace DbDelta.App.HeadlessTests;

public sealed class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>()
                  .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public sealed class TestApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
    }
}
