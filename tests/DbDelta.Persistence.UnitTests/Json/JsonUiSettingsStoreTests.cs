using DbDelta.Persistence.Json;
using FluentAssertions;
using Xunit;

namespace DbDelta.Persistence.UnitTests.Json;

public class JsonUiSettingsStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _file;

    public JsonUiSettingsStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"dbdelta-ui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "ui-settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private JsonUiSettingsStore CreateStore() => new(_file);

    [Fact]
    public async Task Load_returns_System_when_file_absent()
    {
        AppTheme theme = await CreateStore().LoadThemeAsync(CancellationToken.None);
        theme.Should().Be(AppTheme.System);
    }

    [Fact]
    public async Task Save_then_Load_round_trips_the_theme()
    {
        JsonUiSettingsStore store = CreateStore();
        await store.SaveThemeAsync(AppTheme.Dark, CancellationToken.None);
        AppTheme theme = await store.LoadThemeAsync(CancellationToken.None);
        theme.Should().Be(AppTheme.Dark);
    }

    [Fact]
    public async Task Save_overwrites_a_previously_saved_theme()
    {
        JsonUiSettingsStore store = CreateStore();
        await store.SaveThemeAsync(AppTheme.Dark, CancellationToken.None);
        await store.SaveThemeAsync(AppTheme.Light, CancellationToken.None);
        AppTheme theme = await store.LoadThemeAsync(CancellationToken.None);
        theme.Should().Be(AppTheme.Light);
    }

    // A hand-edited or half-written settings file must not take the app down on
    // startup: the theme is a cosmetic preference, and losing it is strictly
    // better than refusing to launch.
    [Fact]
    public async Task Load_returns_System_when_file_is_not_valid_json()
    {
        await File.WriteAllTextAsync(_file, "{ this is not json", TestContext.Current.CancellationToken);
        AppTheme theme = await CreateStore().LoadThemeAsync(CancellationToken.None);
        theme.Should().Be(AppTheme.System);
    }

    [Fact]
    public async Task Load_returns_System_when_theme_value_is_unknown()
    {
        await File.WriteAllTextAsync(_file, /*lang=json,strict*/ """{"schemaVersion":1,"theme":"Solarized"}""", TestContext.Current.CancellationToken);
        AppTheme theme = await CreateStore().LoadThemeAsync(CancellationToken.None);
        theme.Should().Be(AppTheme.System);
    }

    // Guards the forward-compat rule the other stores follow: a file written by
    // a newer DbDelta is ignored rather than half-read.
    [Fact]
    public async Task Load_returns_System_when_schema_version_is_from_the_future()
    {
        await File.WriteAllTextAsync(_file, /*lang=json,strict*/ """{"schemaVersion":99,"theme":"Dark"}""", TestContext.Current.CancellationToken);
        AppTheme theme = await CreateStore().LoadThemeAsync(CancellationToken.None);
        theme.Should().Be(AppTheme.System);
    }

    [Fact]
    public async Task Save_leaves_no_temp_file_behind()
    {
        await CreateStore().SaveThemeAsync(AppTheme.Dark, CancellationToken.None);
        Directory.GetFiles(_dir).Should().ContainSingle().Which.Should().Be(_file);
    }
}
