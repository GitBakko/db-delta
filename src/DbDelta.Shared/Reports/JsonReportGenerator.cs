using System.Text.Json;
using DbDelta.Core.Diff;
using DbDelta.Shared.Dtos;

namespace DbDelta.Shared.Reports;

/// <summary>
/// Renders a <see cref="ComparisonResult"/> to a stable, pretty-printed JSON
/// document by going through <see cref="Mapper.ToDto"/> first. The DTO shape
/// is the wire contract for the report — Core stays free of JSON concerns.
/// </summary>
public sealed class JsonReportGenerator
{
    private static readonly JsonSerializerOptions Serializer = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Options that mirror <see cref="Serializer"/> for the deserialisation
    /// direction — exposed so tests (and any downstream consumer) can round-trip
    /// through the exact same casing policy.
    /// </summary>
    public static JsonSerializerOptions DeserializerOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public string Generate(ComparisonResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        ComparisonResultDto dto = Mapper.ToDto(result);
        return JsonSerializer.Serialize(dto, Serializer);
    }
}
