namespace DbDelta.App.ViewModels;

/// <summary>
/// Pairs a raw <c>DifferenceDto.Status</c> value (or <c>null</c> for "all")
/// with its Italian display label, used by the status-filter picker.
/// </summary>
public sealed record StatusFilterOption(string? Value, string Label);
