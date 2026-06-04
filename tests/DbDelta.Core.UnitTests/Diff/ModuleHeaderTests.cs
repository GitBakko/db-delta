using DbDelta.Core.Diff;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.Diff;

public class ModuleHeaderTests
{
    private const string Banner =
        "-- =============================================\r\n" +
        "-- Author:      Someone\r\n" +
        "-- Create date: 2020-01-01\r\n" +
        "-- =============================================\r\n";

    // ----- EnsureCreateOrAlter ---------------------------------------------

    [Fact]
    public void EnsureCreateOrAlter_inserts_OR_ALTER_after_plain_CREATE()
    {
        ModuleHeader.EnsureCreateOrAlter("CREATE VIEW dbo.v AS SELECT 1")
            .Should().Be("CREATE OR ALTER VIEW dbo.v AS SELECT 1");
    }

    [Fact]
    public void EnsureCreateOrAlter_leaves_existing_CREATE_OR_ALTER_unchanged()
    {
        const string body = "CREATE OR ALTER VIEW dbo.v AS SELECT 1";
        ModuleHeader.EnsureCreateOrAlter(body).Should().BeSameAs(body);
    }

    [Fact]
    public void EnsureCreateOrAlter_preserves_comment_banner_before_header()
    {
        string body = Banner + "CREATE PROCEDURE [dbo].[u] AS SELECT 1";
        ModuleHeader.EnsureCreateOrAlter(body)
            .Should().Be(Banner + "CREATE OR ALTER PROCEDURE [dbo].[u] AS SELECT 1");
    }

    [Fact]
    public void EnsureCreateOrAlter_preserves_whitespace_run_between_tokens()
    {
        ModuleHeader.EnsureCreateOrAlter("CREATE   VIEW  dbo.v AS SELECT 1")
            .Should().Be("CREATE OR ALTER   VIEW  dbo.v AS SELECT 1");
    }

    [Fact]
    public void EnsureCreateOrAlter_preserves_short_PROC_keyword()
    {
        ModuleHeader.EnsureCreateOrAlter("CREATE PROC dbo.u AS SELECT 1")
            .Should().Be("CREATE OR ALTER PROC dbo.u AS SELECT 1");
    }

    [Fact]
    public void EnsureCreateOrAlter_tolerates_block_comment_before_header()
    {
        const string body = "/* generated\r\n   by tooling */ CREATE FUNCTION dbo.f() RETURNS INT AS BEGIN RETURN 1 END";
        ModuleHeader.EnsureCreateOrAlter(body)
            .Should().Be("/* generated\r\n   by tooling */ CREATE OR ALTER FUNCTION dbo.f() RETURNS INT AS BEGIN RETURN 1 END");
    }

    [Fact]
    public void EnsureCreateOrAlter_returns_body_unchanged_when_no_header_found()
    {
        const string body = "SELECT 1 AS NotAModule";
        ModuleHeader.EnsureCreateOrAlter(body).Should().BeSameAs(body);
    }

    // ----- CanonicalizeObjectName ------------------------------------------

    [Fact]
    public void CanonicalizeObjectName_skips_banner_and_canonicalizes_header()
    {
        string body = Banner + "CREATE VIEW [dbo].[OldName] AS SELECT 1";
        ModuleHeader.CanonicalizeObjectName(body, "dbo", "v")
            .Should().Be(Banner + "CREATE VIEW [dbo].[v] AS SELECT 1");
    }

    [Fact]
    public void CanonicalizeObjectName_drops_OR_ALTER_behind_banner()
    {
        string body = Banner + "CREATE OR ALTER VIEW dbo.v AS SELECT 1";
        ModuleHeader.CanonicalizeObjectName(body, "dbo", "v")
            .Should().Be(Banner + "CREATE VIEW [dbo].[v] AS SELECT 1");
    }

    // ----- AlignNameToCatalog ----------------------------------------------

    [Fact]
    public void AlignNameToCatalog_rewrites_stale_name_behind_banner()
    {
        string body = Banner + "CREATE TRIGGER [dbo].[trgOld] ON [dbo].[T] AFTER INSERT AS SELECT 1";
        ModuleHeader.AlignNameToCatalog(body, "dbo", "trgNew")
            .Should().Be(Banner + "CREATE TRIGGER [dbo].[trgNew] ON [dbo].[T] AFTER INSERT AS SELECT 1");
    }

    [Fact]
    public void AlignNameToCatalog_returns_body_verbatim_when_name_matches()
    {
        string body = Banner + "CREATE VIEW [dbo].[v] AS SELECT 1";
        ModuleHeader.AlignNameToCatalog(body, "dbo", "v").Should().BeSameAs(body);
    }

    // ----- ToCreateOrAlterScript -------------------------------------------

    [Fact]
    public void ToCreateOrAlterScript_aligns_name_upgrades_verb_and_appends_semicolon()
    {
        ModuleHeader.ToCreateOrAlterScript("  CREATE VIEW [dbo].[vOld] AS SELECT 1", "dbo", "v")
            .Should().Be("CREATE OR ALTER VIEW [dbo].[v] AS SELECT 1;");
    }

    [Fact]
    public void ToCreateOrAlterScript_preserves_banner_and_existing_semicolon()
    {
        string body = Banner + "CREATE VIEW dbo.v AS SELECT 1;";
        ModuleHeader.ToCreateOrAlterScript(body, "dbo", "v")
            .Should().Be(Banner + "CREATE OR ALTER VIEW dbo.v AS SELECT 1;");
    }
}
