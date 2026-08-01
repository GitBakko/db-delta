using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.Options;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.Diff;

public class ModuleDiffTests
{
    // Module diff tests pass ComparisonOptions.None on purpose: BodyNormalizer
    // is invoked unconditionally inside ClassifyModule (any future IgnoreWhitespace
    // flag must therefore not gate the whitespace test below). Sibling table-diff
    // tests use ComparisonOptions.Default — that divergence is intentional.
    private static Database Db(params View[] views) =>
        new("Db", Schemas: [new Schema("dbo")], Tables: [], Views: views, Procedures: []);

    [Fact]
    public void View_only_in_A_is_OnlyInA()
    {
        Database a = Db(new View("dbo", "vCustomer", "SELECT 1", IsEncrypted: false));
        Database b = Db();
        ComparisonResult result = new ComparisonEngine().Compare(a, b, ComparisonOptions.None);
        DifferencePair pair = result.Differences.Single(p => p.Identity.Kind == "View");
        pair.Status.Should().Be(DifferenceStatus.OnlyInA);
    }

    [Fact]
    public void View_only_in_B_is_OnlyInB()
    {
        Database a = Db();
        Database b = Db(new View("dbo", "vCustomer", "SELECT 1", IsEncrypted: false));
        DifferencePair pair = new ComparisonEngine().Compare(a, b, ComparisonOptions.None)
            .Differences.Single(p => p.Identity.Kind == "View");
        pair.Status.Should().Be(DifferenceStatus.OnlyInB);
    }

    [Fact]
    public void Identical_view_bodies_are_Identical()
    {
        Database a = Db(new View("dbo", "v", "SELECT 1 AS Id", IsEncrypted: false));
        Database b = Db(new View("dbo", "v", "SELECT 1 AS Id", IsEncrypted: false));
        ComparisonResult result = new ComparisonEngine().Compare(a, b, ComparisonOptions.None);
        result.Differences
            .Where(p => p.Identity.Kind == "View")
            .Should().OnlyContain(p => p.Status == DifferenceStatus.Identical);
    }

    [Fact]
    public void View_body_differing_only_in_whitespace_is_Identical()
    {
        Database a = Db(new View("dbo", "v", "SELECT 1\r\nAS Id", IsEncrypted: false));
        Database b = Db(new View("dbo", "v", "SELECT  1 AS Id", IsEncrypted: false));
        ComparisonResult result = new ComparisonEngine().Compare(a, b, ComparisonOptions.None);
        result.Differences.Single(p => p.Identity.Kind == "View")
            .Status.Should().Be(DifferenceStatus.Identical);
    }

    [Fact]
    public void View_body_differing_substantively_is_Different()
    {
        Database a = Db(new View("dbo", "v", "SELECT 1 AS Id", IsEncrypted: false));
        Database b = Db(new View("dbo", "v", "SELECT 2 AS Id", IsEncrypted: false));
        ComparisonResult result = new ComparisonEngine().Compare(a, b, ComparisonOptions.None);
        result.Differences.Single(p => p.Identity.Kind == "View")
            .Status.Should().Be(DifferenceStatus.Different);
    }

    [Fact]
    public void View_renamed_via_sp_rename_with_stale_embedded_name_is_Identical()
    {
        // sp_rename updated the catalog name to "v" on both sides, but the stored
        // definition on side A still carries the pre-rename name. The modules are
        // semantically identical — only the frozen embedded name differs, which
        // must NOT be reported as a difference (regression: BCEVOLUTION_* views).
        Database a = Db(new View("dbo", "v", "CREATE VIEW [dbo].[OldName] AS SELECT 1 AS Id", IsEncrypted: false));
        Database b = Db(new View("dbo", "v", "CREATE VIEW [dbo].[v] AS SELECT 1 AS Id", IsEncrypted: false));
        new ComparisonEngine().Compare(a, b, ComparisonOptions.None)
            .Differences.Single(p => p.Identity.Kind == "View")
            .Status.Should().Be(DifferenceStatus.Identical);
    }

    [Fact]
    public void View_with_stale_embedded_name_AND_a_real_body_change_stays_Different()
    {
        // Name reconciliation must not mask a genuine body difference.
        Database a = Db(new View("dbo", "v", "CREATE VIEW [dbo].[OldName] AS SELECT 1 AS Id", IsEncrypted: false));
        Database b = Db(new View("dbo", "v", "CREATE VIEW [dbo].[v] AS SELECT 2 AS Id", IsEncrypted: false));
        new ComparisonEngine().Compare(a, b, ComparisonOptions.None)
            .Differences.Single(p => p.Identity.Kind == "View")
            .Status.Should().Be(DifferenceStatus.Different);
    }

    [Fact]
    public void View_with_comment_banner_and_stale_embedded_name_is_Identical()
    {
        // Real-world SSMS template banner before CREATE. The header regex must
        // skip the banner so stale-name canonicalization still applies
        // (regression: banner'd modules diffed as Different on identical bodies).
        const string banner = "-- ===== Author: X / Create date: 2020 =====\r\n";
        Database a = Db(new View("dbo", "v", banner + "CREATE VIEW [dbo].[OldName] AS SELECT 1 AS Id", IsEncrypted: false));
        Database b = Db(new View("dbo", "v", banner + "CREATE VIEW [dbo].[v] AS SELECT 1 AS Id", IsEncrypted: false));
        new ComparisonEngine().Compare(a, b, ComparisonOptions.None)
            .Differences.Single(p => p.Identity.Kind == "View")
            .Status.Should().Be(DifferenceStatus.Identical);
    }

    [Fact]
    public void View_with_CREATE_vs_CREATE_OR_ALTER_same_body_is_Identical()
    {
        Database a = Db(new View("dbo", "v", "CREATE OR ALTER VIEW [dbo].[v] AS SELECT 1 AS Id", IsEncrypted: false));
        Database b = Db(new View("dbo", "v", "CREATE VIEW [dbo].[v] AS SELECT 1 AS Id", IsEncrypted: false));
        new ComparisonEngine().Compare(a, b, ComparisonOptions.None)
            .Differences.Single(p => p.Identity.Kind == "View")
            .Status.Should().Be(DifferenceStatus.Identical);
    }

    [Fact]
    public void View_bodies_differing_only_in_banner_text_stay_Different()
    {
        // Comment-only differences are real content differences and must surface.
        Database a = Db(new View("dbo", "v", "-- Author: A\r\nCREATE VIEW [dbo].[v] AS SELECT 1 AS Id", IsEncrypted: false));
        Database b = Db(new View("dbo", "v", "-- Author: B\r\nCREATE VIEW [dbo].[v] AS SELECT 1 AS Id", IsEncrypted: false));
        new ComparisonEngine().Compare(a, b, ComparisonOptions.None)
            .Differences.Single(p => p.Identity.Kind == "View")
            .Status.Should().Be(DifferenceStatus.Different);
    }

    [Fact]
    public void Procedure_renamed_with_stale_embedded_name_is_Identical()
    {
        Database a = DbWithProcs(new StoredProcedure("dbo", "u", "CREATE PROCEDURE [dbo].[uOld] AS SELECT 1", IsEncrypted: false));
        Database b = DbWithProcs(new StoredProcedure("dbo", "u", "CREATE PROCEDURE [dbo].[u] AS SELECT 1", IsEncrypted: false));
        new ComparisonEngine().Compare(a, b, ComparisonOptions.None)
            .Differences.Single(p => p.Identity.Kind == "Procedure")
            .Status.Should().Be(DifferenceStatus.Identical);
    }

    [Fact]
    public void Encrypted_view_compared_to_encrypted_view_is_Different_because_bodies_are_opaque()
    {
        // We cannot prove equality of opaque bodies; the safe default is Different.
        Database a = Db(new View("dbo", "v", Body: null, IsEncrypted: true));
        Database b = Db(new View("dbo", "v", Body: null, IsEncrypted: true));
        ComparisonResult result = new ComparisonEngine().Compare(a, b, ComparisonOptions.None);
        result.Differences.Single(p => p.Identity.Kind == "View")
            .Status.Should().Be(DifferenceStatus.Different);
    }

    private static Database DbWithProcs(params StoredProcedure[] procs) =>
        new("Db", Schemas: [new Schema("dbo")], Tables: [], Views: [], Procedures: procs);

    [Fact]
    public void Procedure_only_in_A_is_OnlyInA()
    {
        Database a = DbWithProcs(new StoredProcedure("dbo", "uspGet", "BODY", IsEncrypted: false));
        Database b = DbWithProcs();
        DifferencePair pair = new ComparisonEngine().Compare(a, b, ComparisonOptions.None)
            .Differences.Single(p => p.Identity.Kind == "Procedure");
        pair.Status.Should().Be(DifferenceStatus.OnlyInA);
    }

    [Fact]
    public void Procedure_with_identical_bodies_is_Identical()
    {
        Database a = DbWithProcs(new StoredProcedure("dbo", "u", "SELECT 1", IsEncrypted: false));
        Database b = DbWithProcs(new StoredProcedure("dbo", "u", "SELECT 1", IsEncrypted: false));
        new ComparisonEngine().Compare(a, b, ComparisonOptions.None)
            .Differences.Single(p => p.Identity.Kind == "Procedure")
            .Status.Should().Be(DifferenceStatus.Identical);
    }

    [Fact]
    public void Procedure_with_substantively_different_bodies_is_Different()
    {
        Database a = DbWithProcs(new StoredProcedure("dbo", "u", "SELECT 1", IsEncrypted: false));
        Database b = DbWithProcs(new StoredProcedure("dbo", "u", "SELECT 2", IsEncrypted: false));
        new ComparisonEngine().Compare(a, b, ComparisonOptions.None)
            .Differences.Single(p => p.Identity.Kind == "Procedure")
            .Status.Should().Be(DifferenceStatus.Different);
    }

    /// <summary>
    /// The same view, one side re-indented and the other flattened onto one
    /// line, is Identical.
    /// </summary>
    /// <remarks>
    /// Taken from a real pair on 192.168.3.243 that Redgate reported as
    /// modified and DbDelta did not, which read like a false negative until the
    /// two definitions were put side by side and turned out to be the same
    /// statement. SQL Server stores whatever text it was handed, so a module
    /// re-deployed from a reformatted script drifts in whitespace without
    /// changing meaning — and a diff that cannot be flattened by applying its
    /// own script is a diff that never goes away. Deliberate since a58b4bb: if
    /// this test is ever "fixed" to expect Different, that decision is being
    /// reversed, not a bug repaired.
    /// </remarks>
    [Fact]
    public void A_view_reformatted_but_not_changed_is_identical()
    {
        const string indented = """
            CREATE VIEW dbo.VwAudit AS
            SELECT
                a.Id,
                a.IdTenant,
                CONVERT(NVARCHAR(36), a.BatchId) AS BatchId
            FROM dbo.AuditLog a
            LEFT JOIN dbo.Users u ON u.Id = a.IdUser;
            """;
        const string flattened =
            "CREATE VIEW dbo.VwAudit AS SELECT a.Id, a.IdTenant, "
            + "CONVERT(NVARCHAR(36), a.BatchId) AS BatchId "
            + "FROM dbo.AuditLog a LEFT JOIN dbo.Users u ON u.Id = a.IdUser;";

        Database a = Db(new View("dbo", "VwAudit", indented, IsEncrypted: false));
        Database b = Db(new View("dbo", "VwAudit", flattened, IsEncrypted: false));

        new ComparisonEngine().Compare(a, b, ComparisonOptions.None)
            .Differences.Single(p => p.Identity.Kind == "View")
            .Status.Should().Be(DifferenceStatus.Identical);
    }

    /// <summary>
    /// The mirror image of the test above, and the other half of the same parity
    /// run: SpClonaApplicationMenu had a byte-identical body on both sides and
    /// QUOTED_IDENTIFIER 1 on one, 0 on the other. We called it identical.
    /// </summary>
    /// <remarks>
    /// The setting is compiled INTO the module, not taken from the session that
    /// runs it. With QUOTED_IDENTIFIER OFF a "quoted" token is a string literal
    /// rather than an identifier; with ANSI_NULLS OFF, <c>= NULL</c> matches NULLs
    /// instead of yielding UNKNOWN. Same text, different object.
    /// </remarks>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void A_module_compiled_under_different_set_options_is_different(
        bool quotedIdentifier, bool ansiNulls)
    {
        const string body = "CREATE VIEW dbo.v AS SELECT 1 AS Id";
        Database a = Db(new View("dbo", "v", body, IsEncrypted: false));
        Database b = Db(new View(
            "dbo", "v", body, IsEncrypted: false,
            UsesQuotedIdentifier: quotedIdentifier, UsesAnsiNulls: ansiNulls));

        new ComparisonEngine().Compare(a, b, ComparisonOptions.None)
            .Differences.Single(p => p.Identity.Kind == "View")
            .Status.Should().Be(DifferenceStatus.Different);
    }

    [Fact]
    public void Matching_set_options_leave_an_identical_module_identical()
    {
        const string body = "CREATE VIEW dbo.v AS SELECT 1 AS Id";
        Database a = Db(new View("dbo", "v", body, IsEncrypted: false, UsesQuotedIdentifier: false));
        Database b = Db(new View("dbo", "v", body, IsEncrypted: false, UsesQuotedIdentifier: false));

        new ComparisonEngine().Compare(a, b, ComparisonOptions.None)
            .Differences.Single(p => p.Identity.Kind == "View")
            .Status.Should().Be(DifferenceStatus.Identical);
    }
}
