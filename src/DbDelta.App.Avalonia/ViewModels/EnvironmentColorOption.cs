namespace DbDelta.App.ViewModels;

public sealed record EnvironmentColorOption(string Label, string Hex);

public static class EnvironmentColorPalette
{
    public static IReadOnlyList<EnvironmentColorOption> All { get; } =
    [
        new("Cobalt",  "#0054BD"),
        new("Violet",  "#6649BE"),
        new("Emerald", "#007339"),
        new("Amber",   "#AE5C00"),
        new("Crimson", "#B31220"),
        new("Sky",     "#5BB9D1"),
        new("Rose",    "#D957A6"),
        new("Neutral", "#9097A0"),
    ];
}
