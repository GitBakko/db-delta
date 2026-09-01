using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.ScriptGen;

namespace DbDelta.ScriptGen.GoldenTests;

public class ViewGoldenTests
{
    private readonly ViewScriptEmitter _emitter = new();

    [Fact]
    public Task Add_view_emits_CREATE_OR_ALTER()
    {
        View v = new("dbo", "vCustomer", "CREATE VIEW dbo.vCustomer AS SELECT Id, Name FROM dbo.Customer;", IsEncrypted: false);
        DifferencePair pair = new(v.Identity, DifferenceStatus.OnlyInA, v, null);
        string ddl = _emitter.Emit(pair);
        return Verify(ddl);
    }

    [Fact]
    public Task Drop_view_emits_a_bare_DROP_VIEW()
    {
        View v = new("dbo", "vCustomer", "CREATE VIEW dbo.vCustomer AS SELECT 1 AS X;", IsEncrypted: false);
        DifferencePair pair = new(v.Identity, DifferenceStatus.OnlyInB, null, v);
        string ddl = _emitter.Emit(pair);
        return Verify(ddl);
    }

    [Fact]
    public Task Different_view_emits_CREATE_OR_ALTER_with_new_body()
    {
        View a = new("dbo", "vCustomer", "CREATE VIEW dbo.vCustomer AS SELECT Id, Name FROM dbo.Customer;", IsEncrypted: false);
        View b = new("dbo", "vCustomer", "CREATE VIEW dbo.vCustomer AS SELECT Id FROM dbo.Customer;", IsEncrypted: false);
        DifferencePair pair = new(a.Identity, DifferenceStatus.Different, a, b);
        string ddl = _emitter.Emit(pair);
        return Verify(ddl);
    }

    [Fact]
    public Task Different_view_with_stale_embedded_name_targets_catalog_identity()
    {
        // Side-A definition still carries a pre-sp_rename name in its CREATE header.
        // The emitted DDL must target the catalog identity [dbo].[vCustomer], never
        // the stale name (which would create/alter the wrong object on deploy).
        View a = new("dbo", "vCustomer", "CREATE VIEW [dbo].[vOldName] AS SELECT Id, Name FROM dbo.Customer;", IsEncrypted: false);
        View b = new("dbo", "vCustomer", "CREATE VIEW [dbo].[vCustomer] AS SELECT Id FROM dbo.Customer;", IsEncrypted: false);
        DifferencePair pair = new(a.Identity, DifferenceStatus.Different, a, b);
        string ddl = _emitter.Emit(pair);
        return Verify(ddl);
    }

    [Fact]
    public Task View_with_comment_banner_emits_banner_then_CREATE_OR_ALTER()
    {
        // SSMS-template banner before CREATE: the emitter must keep the banner
        // verbatim and still upgrade the verb (regression: banner'd bodies were
        // emitted as plain CREATE, failing on deploy when the view exists).
        View v = new(
            "dbo",
            "vCustomer",
            "-- =============================================\r\n-- Author:      Someone\r\n-- =============================================\r\nCREATE VIEW dbo.vCustomer AS SELECT Id FROM dbo.Customer;",
            IsEncrypted: false);
        DifferencePair pair = new(v.Identity, DifferenceStatus.OnlyInA, v, null);
        string ddl = _emitter.Emit(pair);
        return Verify(ddl);
    }

    [Fact]
    public Task Encrypted_view_emits_a_comment_warning_and_no_DDL()
    {
        View v = new("dbo", "vSecret", Body: null, IsEncrypted: true);
        DifferencePair pair = new(v.Identity, DifferenceStatus.OnlyInA, v, null);
        string ddl = _emitter.Emit(pair);
        return Verify(ddl);
    }
}
