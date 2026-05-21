using Avalonia.Data.Converters;
using DbDelta.Core.Abstractions;
using System.Globalization;

namespace DbDelta.App.ViewModels;

/// <summary>
/// Value converters for <see cref="AuthenticationMode"/> used in
/// <c>ProjectSetupDialog.axaml</c>.
/// </summary>
public sealed class AuthModeConverter : IValueConverter
{
    /// <summary>
    /// Singleton used as <c>{x:Static vm:AuthModeConverter.Instance}</c>.
    /// Converts <see cref="AuthenticationMode"/> ↔ ComboBox <c>SelectedIndex</c>
    /// (0 = SqlServer, 1 = WindowsIntegrated).
    /// </summary>
    public static readonly AuthModeConverter Instance = new();

    /// <summary>
    /// Returns <see langword="true"/> when the mode is SqlServer.
    /// Used for IsVisible bindings on credentials fields.
    /// </summary>
    public static readonly IValueConverter IsSqlServer = new FuncValueConverter<AuthenticationMode, bool>(
        static m => m == AuthenticationMode.SqlServer);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is AuthenticationMode mode ? (mode == AuthenticationMode.SqlServer ? 0 : (object?)1) : 0;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int idx && idx == 1
            ? AuthenticationMode.WindowsIntegrated
            : AuthenticationMode.SqlServer;
}
