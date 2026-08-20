using System.Text;
using DbDelta.Core.ObjectModel;

namespace DbDelta.Core.ScriptGen;

/// <summary>
/// Emits ALTER TABLE ADD CONSTRAINT … FOREIGN KEY statements, including
/// cascading options + NOCHECK / NOT FOR REPLICATION flags.
/// </summary>
public sealed class ForeignKeyScriptEmitter
{
    public string EmitAdd(string schema, string table, ForeignKey fk)
    {
        ArgumentNullException.ThrowIfNull(fk);
        StringBuilder sb = new();
        sb.Append("ALTER TABLE ").Append(Sql.Q(schema, table)).Append(' ');
        if (fk.IsDisabled)
        {
            sb.Append("WITH NOCHECK ");
        }
        // A name SQL Server minted is derived from that constraint's own
        // object_id, so copying it pins on the target a name the next
        // comparison can never reproduce. Left out, the target server mints its
        // own — which is what happened on the source. The one exception is
        // below: a disabled key has to be nameable to be disabled.
        sb.Append("ADD ").Append(NameClause(fk)).Append("FOREIGN KEY (")
          .Append(string.Join(", ", fk.Columns.Select(c => $"{Sql.Q(c)}")))
          .Append(") REFERENCES ").Append(Sql.Q(fk.ReferencedSchema, fk.ReferencedTable)).Append(" (")
          .Append(string.Join(", ", fk.ReferencedColumns.Select(c => $"{Sql.Q(c)}")))
          .Append(')');

        if (fk.OnDelete != ReferentialAction.NoAction)
        {
            sb.Append(" ON DELETE ").Append(FormatAction(fk.OnDelete));
        }
        if (fk.OnUpdate != ReferentialAction.NoAction)
        {
            sb.Append(" ON UPDATE ").Append(FormatAction(fk.OnUpdate));
        }
        if (fk.IsNotForReplication)
        {
            sb.Append(" NOT FOR REPLICATION");
        }
        sb.Append(';');

        if (fk.IsDisabled)
        {
            sb.Append('\n')
              .Append("ALTER TABLE ").Append(Sql.Q(schema, table))
              .Append(" NOCHECK CONSTRAINT ").Append(Sql.Q(fk.Name)).Append(';');
        }

        return sb.ToString();
    }

    /// <summary>
    /// <c>CONSTRAINT [name] </c>, or nothing when SQL Server minted the name
    /// itself — mirrors <c>TableScriptEmitter.NameClause</c>.
    /// </summary>
    /// <remarks>
    /// ponytail: a DISABLED key keeps its minted name, because
    /// <c>NOCHECK CONSTRAINT</c> takes one and the only name available is the
    /// source's. Emitting it unnamed and skipping the disable would silently
    /// change enforcement, which is worse than the churn this leaves behind on
    /// that one shape. The way out is a name of our own — a deploy-time
    /// rename — not a smarter clause here.
    /// </remarks>
    private static string NameClause(ForeignKey fk) =>
        fk.IsSystemNamed && !fk.IsDisabled ? string.Empty : $"CONSTRAINT {Sql.Q(fk.Name)} ";

    private static string FormatAction(ReferentialAction action) => action switch
    {
        ReferentialAction.Cascade => "CASCADE",
        ReferentialAction.SetNull => "SET NULL",
        ReferentialAction.SetDefault => "SET DEFAULT",
        ReferentialAction.NoAction => "NO ACTION",
        _ => "NO ACTION",
    };
}
