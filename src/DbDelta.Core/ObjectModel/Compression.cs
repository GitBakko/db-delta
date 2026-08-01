namespace DbDelta.Core.ObjectModel;

/// <summary>
/// Row/page compression, as <c>sys.partitions.data_compression_desc</c> spells
/// it, plus the one rule every caller needs: an absent value and <c>NONE</c> are
/// the same thing.
/// </summary>
/// <remarks>
/// A table read before DbDelta modelled compression has <c>null</c>, a table
/// read from a server that never had it set has <c>"NONE"</c>, and comparing
/// those two as strings reports a difference nobody can deploy away. One place
/// decides, so the diff and the emitters cannot drift apart on it.
/// </remarks>
public static class Compression
{
    /// <summary>What SQL Server calls "not compressed".</summary>
    public const string None = "NONE";

    /// <summary>Uppercases and trims; null, empty and whitespace all become <see cref="None"/>.</summary>
    public static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? None : value.Trim().ToUpperInvariant();

    /// <summary>True when both sides mean the same compression.</summary>
    public static bool Equal(string? a, string? b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.Ordinal);

    /// <summary>True when nothing needs saying in the DDL.</summary>
    public static bool IsNone(string? value) =>
        string.Equals(Normalize(value), None, StringComparison.Ordinal);
}
