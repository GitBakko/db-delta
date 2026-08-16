using System.Text;
using DbDelta.Core.ObjectModel;

namespace DbDelta.Core.ScriptGen;

/// <summary>
/// Emits standalone CREATE INDEX / DROP INDEX statements. Called by
/// <see cref="ScriptGenerator"/> after all tables have been created.
/// </summary>
/// <remarks>
/// This emitter speaks rowstore only. Anything it is asked to CREATE or REBUILD
/// that is not <c>CLUSTERED</c> / <c>NONCLUSTERED</c> raises
/// <see cref="UnscriptableIndexException"/> rather than writing a statement that
/// would compile and build the wrong index. <see cref="EmitDrop"/> is exempt:
/// <c>DROP INDEX</c> is valid for every type, and refusing to drop what the
/// source no longer has would block a convergence the target can complete.
/// </remarks>
public sealed class IndexScriptEmitter
{
    public string EmitCreate(string schema, string table, TableIndex ix)
    {
        ArgumentNullException.ThrowIfNull(ix);
        UnscriptableIndexException.ThrowIfNotRowstore(schema, table, ix);
        StringBuilder sb = new();
        sb.Append("CREATE ");
        if (ix.IsUnique)
        {
            sb.Append("UNIQUE ");
        }
        sb.Append(ix.IsClustered ? "CLUSTERED " : "NONCLUSTERED ");
        sb.Append("INDEX ").Append(Sql.Q(ix.Name)).Append(" ON ")
          .Append(Sql.Q(schema, table)).Append(" (");
        sb.Append(string.Join(", ", ix.KeyColumns.Select(k =>
            $"{Sql.Q(k.Name)} {(k.IsDescending ? "DESC" : "ASC")}")));
        sb.Append(')');

        if (ix.IncludedColumns.Count > 0)
        {
            sb.Append(" INCLUDE (");
            sb.Append(string.Join(", ", ix.IncludedColumns.Select(c => $"{Sql.Q(c)}")));
            sb.Append(')');
        }

        if (!string.IsNullOrEmpty(ix.FilterExpression))
        {
            sb.Append(" WHERE ").Append(ix.FilterExpression);
        }

        if (!Compression.IsNone(ix.DataCompression))
        {
            sb.Append(" WITH (DATA_COMPRESSION = ").Append(Compression.Normalize(ix.DataCompression)).Append(')');
        }

        sb.Append(';');
        return sb.ToString();
    }

    public string EmitDrop(string schema, string table, TableIndex ix)
    {
        ArgumentNullException.ThrowIfNull(ix);
        return $"DROP INDEX {Sql.Q(ix.Name)} ON {Sql.Q(schema, table)};";
    }

    /// <summary>
    /// Changes an existing index's compression in place.
    /// </summary>
    /// <remarks>
    /// A REBUILD rather than a DROP + CREATE, which is what a shape change gets:
    /// the columns are identical, so tearing the index down would take the same
    /// minutes of work AND leave the table unindexed in the middle of them, for a
    /// setting that a rebuild changes on its own.
    /// </remarks>
    public string EmitRebuildForCompression(string schema, string table, TableIndex ix)
    {
        ArgumentNullException.ThrowIfNull(ix);
        // A columnstore reports COLUMNSTORE / COLUMNSTORE_ARCHIVE here, which is
        // not a DATA_COMPRESSION value a rowstore REBUILD accepts.
        UnscriptableIndexException.ThrowIfNotRowstore(schema, table, ix);
        return $"ALTER INDEX {Sql.Q(ix.Name)} ON {Sql.Q(schema, table)} "
             + $"REBUILD WITH (DATA_COMPRESSION = {Compression.Normalize(ix.DataCompression)});";
    }
}
