using Avalonia.Headless.XUnit;
using DbDelta.App.ViewModels;
using DbDelta.Core.Abstractions;
using FluentAssertions;

namespace DbDelta.App.HeadlessTests;

public class ConnectionEditViewModelTests
{
    private static ConnectionEntry SampleEntry() => new(
        Id: Guid.NewGuid(),
        Name: "Dev",
        ServerName: "srv",
        DatabaseName: "db",
        ConnectionStringTemplate: "Server=srv;Database=db;User Id=sa;Password={PASSWORD};TrustServerCertificate=True",
        EnvironmentTag: "Dev",
        EnvironmentColorHex: "#0054BD",
        IsPinned: false,
        CreatedUtc: DateTime.UtcNow,
        LastUsedUtc: DateTime.UtcNow);

    [AvaloniaFact]
    public void Empty_name_is_invalid()
    {
        ConnectionEditViewModel vm = new(SampleEntry(), "p4ssw0rd") { Name = "" };
        vm.IsValid.Should().BeFalse();
    }

    [AvaloniaFact]
    public void Empty_colour_is_invalid()
    {
        ConnectionEditViewModel vm = new(SampleEntry(), "p4ssw0rd") { EnvironmentColorHex = "" };
        vm.IsValid.Should().BeFalse();
    }

    [AvaloniaFact]
    public void Filled_form_is_valid()
    {
        ConnectionEditViewModel vm = new(SampleEntry(), "p4ssw0rd");
        vm.IsValid.Should().BeTrue();
    }

    [AvaloniaFact]
    public void ToEntry_round_trips_id_created_and_overrides_other_fields()
    {
        ConnectionEntry original = SampleEntry() with { CreatedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        ConnectionEditViewModel vm = new(original, "p4ssw0rd")
        {
            Name = "Renamed",
            EnvironmentTag = "Prod",
            EnvironmentColorHex = "#B31220",
            IsPinned = true,
        };
        ConnectionEntry result = vm.ToEntry();
        result.Id.Should().Be(original.Id);
        result.CreatedUtc.Should().Be(original.CreatedUtc);
        result.Name.Should().Be("Renamed");
        result.EnvironmentTag.Should().Be("Prod");
        result.EnvironmentColorHex.Should().Be("#B31220");
        result.IsPinned.Should().BeTrue();
    }
}
