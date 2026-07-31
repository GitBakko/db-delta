using Avalonia;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;

[assembly: AvaloniaTestApplication(typeof(DbDelta.App.HeadlessTests.TestAppBuilder))]

namespace DbDelta.App.HeadlessTests;

public sealed class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>()
                  .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

/// <summary>
/// Mirrors <c>App.axaml</c> closely enough to instantiate real views.
/// </summary>
/// <remarks>
/// A bare FluentTheme is enough for a control that only uses built-in
/// resources, but any view referencing a design-system brush
/// (<c>BgMutedBrush</c>, …) throws KeyNotFoundException at construction. The
/// dictionaries are loaded here so view-level tests exercise the same markup
/// the app runs, rather than a stripped-down variant that can pass while the
/// real window fails.
/// </remarks>
public sealed class TestApp : Application
{
    private const string Base = "avares://DbDelta.App";

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri(Base))
        {
            Source = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml"),
        });
        foreach (string dict in (string[])["Tokens", "Themes", "Templates"])
        {
            Resources.MergedDictionaries.Add(new ResourceInclude(new Uri(Base))
            {
                Source = new Uri($"{Base}/Styles/{dict}.axaml"),
            });
        }
        Styles.Add(new StyleInclude(new Uri(Base))
        {
            Source = new Uri($"{Base}/Styles/AppStyles.axaml"),
        });
    }
}
