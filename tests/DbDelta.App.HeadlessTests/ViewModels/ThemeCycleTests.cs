using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using DbDelta.App.ViewModels;
using DbDelta.Persistence.Json;
using FluentAssertions;
using Xunit;

namespace DbDelta.App.HeadlessTests.ViewModels;

/// <summary>
/// The topbar theme button cycles Light → Dark → System → Light and remembers
/// the choice across restarts.
/// </summary>
/// <remarks>
/// These tests pin the mapping and the persistence. What they CANNOT pin is
/// whether <see cref="ThemeVariant.Default"/> actually tracks the Windows
/// light/dark setting — the headless platform reports its own theme — so that
/// half is verified by running the real app.
/// </remarks>
public class ThemeCycleTests : IDisposable
{
    private readonly string _dir;
    private readonly string _file;

    public ThemeCycleTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"dbdelta-theme-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "ui-settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private MainWindowViewModel BuildVm(AppTheme initial = AppTheme.Light) =>
        new(new AppStateViewModel(),
            JsonRecentProjectsStore.CreateDefault(),
            credentials: null,
            uiSettings: new JsonUiSettingsStore(_file),
            initialTheme: initial);

    [AvaloniaFact]
    public async Task Cycle_goes_light_then_dark_then_system_then_back_to_light()
    {
        MainWindowViewModel vm = BuildVm(AppTheme.Light);

        await vm.CycleThemeCommand.ExecuteAsync(null);
        vm.Theme.Should().Be(AppTheme.Dark);

        await vm.CycleThemeCommand.ExecuteAsync(null);
        vm.Theme.Should().Be(AppTheme.System);

        await vm.CycleThemeCommand.ExecuteAsync(null);
        vm.Theme.Should().Be(AppTheme.Light);
    }

    [AvaloniaTheory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.System)]
    public void Exactly_one_icon_is_visible_per_theme(AppTheme theme)
    {
        MainWindowViewModel vm = BuildVm(theme);

        bool[] visible = [vm.IsThemeLight, vm.IsThemeDark, vm.IsThemeSystem];
        visible.Count(v => v).Should().Be(1);
    }

    [AvaloniaFact]
    public async Task Icon_flags_follow_the_cycle()
    {
        MainWindowViewModel vm = BuildVm(AppTheme.Light);
        vm.IsThemeLight.Should().BeTrue();

        await vm.CycleThemeCommand.ExecuteAsync(null);
        vm.IsThemeDark.Should().BeTrue();
        vm.IsThemeLight.Should().BeFalse();

        await vm.CycleThemeCommand.ExecuteAsync(null);
        vm.IsThemeSystem.Should().BeTrue();
        vm.IsThemeDark.Should().BeFalse();
    }

    // The button is a single icon with three meanings, so the tooltip is the
    // only thing telling the user which state they are actually in.
    [AvaloniaTheory]
    [InlineData(AppTheme.Light, "chiaro")]
    [InlineData(AppTheme.Dark, "scuro")]
    [InlineData(AppTheme.System, "sistema")]
    public void Tooltip_names_the_current_theme(AppTheme theme, string expected) => BuildVm(theme).ThemeTooltip.Should().Contain(expected);

    [AvaloniaTheory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.System)]
    public void Theme_maps_to_the_matching_avalonia_variant(AppTheme theme)
    {
        ThemeVariant expected = theme switch
        {
            AppTheme.Light => ThemeVariant.Light,
            AppTheme.Dark => ThemeVariant.Dark,
            AppTheme.System => ThemeVariant.Default,
            _ => ThemeVariant.Default,
        };

        MainWindowViewModel.ToVariant(theme).Should().Be(expected);
    }

    // System must map to Default and NOT to a hardcoded Light: Default is the
    // only value that lets Avalonia resolve the variant from the OS.
    [AvaloniaFact]
    public void System_maps_to_default_not_light() => MainWindowViewModel.ToVariant(AppTheme.System).Should().NotBe(ThemeVariant.Light);

    [AvaloniaFact]
    public async Task Cycling_applies_the_variant_to_the_running_application()
    {
        Application app = Application.Current!;
        ThemeVariant? previous = app.RequestedThemeVariant;
        try
        {
            MainWindowViewModel vm = BuildVm(AppTheme.Light);

            await vm.CycleThemeCommand.ExecuteAsync(null);

            app.RequestedThemeVariant.Should().Be(ThemeVariant.Dark);
        }
        finally
        {
            app.RequestedThemeVariant = previous;
        }
    }

    [AvaloniaFact]
    public async Task Chosen_theme_survives_a_restart()
    {
        MainWindowViewModel vm = BuildVm(AppTheme.Light);

        await vm.CycleThemeCommand.ExecuteAsync(null);

        AppTheme reloaded = await new JsonUiSettingsStore(_file).LoadThemeAsync(TestContext.Current.CancellationToken);
        reloaded.Should().Be(AppTheme.Dark);
    }

    // The theme is cosmetic; an unwritable settings file must not surface as a
    // crash when the user clicks the button.
    [AvaloniaFact]
    public async Task Cycling_still_switches_the_theme_when_the_settings_file_cannot_be_written()
    {
        MainWindowViewModel vm = new(
            new AppStateViewModel(),
            JsonRecentProjectsStore.CreateDefault(),
            credentials: null,
            uiSettings: new JsonUiSettingsStore(Path.Combine(_dir, "no-such-dir", "ui-settings.json")),
            initialTheme: AppTheme.Light);

        await vm.CycleThemeCommand.ExecuteAsync(null);

        vm.Theme.Should().Be(AppTheme.Dark);
    }
}
