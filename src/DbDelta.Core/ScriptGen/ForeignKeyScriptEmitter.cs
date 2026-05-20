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
        sb.Append("ALTER TABLE [").Append(schema).Append("].[").Append(table).Append("] ");
        if (fk.IsDisabled)
        {
            sb.Append("WITH NOCHECK ");
        }
        sb.Append("ADD CONSTRAINT [").Append(fk.Name).Append("] FOREIGN KEY (")
          .Append(string.Join(", ", fk.Columns.Select(c => $"[{c}]")))
          .Append(") REFERENCES [").Append(fk.ReferencedSchema).Append("].[")
          .Append(fk.ReferencedTable).Append("] (")
          .Append(string.Join(", ", fk.ReferencedColumns.Select(c => $"[{c}]")))
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
              .Append("ALTER TABLE [").Append(schema).Append("].[").Append(table).Append("] ")
              .Append("NOCHECK CONSTRAINT [").Append(fk.Name).Append("];");
        }

        return sb.ToString();
    }

    private static string FormatAction(ReferentialAction action) => action switch
    {
        ReferentialAction.Cascade => "CASCADE",
        ReferentialAction.SetNull => "SET NULL",
        ReferentialAction.SetDefault => "SET DEFAULT",
        ReferentialAction.NoAction => "NO ACTION",
        _ => "NO ACTION",
    };
}
