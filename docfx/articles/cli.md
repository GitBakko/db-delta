# CLI reference

DbDelta's CLI exposes four verbs. Every verb takes SQL Server **connection
strings** for `--source` / `--target`. The diff direction is always
*source -> target*: the source is the desired state, the target is what gets
modified.

## `compare`

Computes the diff and prints it.

| Option | Required | Description |
|--------|----------|-------------|
| `--source` | yes | Source SQL Server connection string. |
| `--target` | yes | Target SQL Server connection string. |
| `--format` | no | Output format: `text` (default) or `json`. |

```bash
dbdelta compare --source "..." --target "..." --format json
```

## `script`

Generates a dependency-ordered T-SQL deployment script.

| Option | Required | Description |
|--------|----------|-------------|
| `--source` | yes | Source connection string. |
| `--target` | yes | Target connection string. |
| `--out` | no | Output file path, or `-` for stdout. |
| `--include-permissions` | no | Emit `GRANT`/`REVOKE` statements (off by default — Redgate-parity). |

```bash
dbdelta script --source "..." --target "..." --out deploy.sql
```

## `apply`

Executes a pre-generated script against the target, GO-split inside a single
transaction.

| Option | Required | Description |
|--------|----------|-------------|
| `--target` | yes | Target connection string. |
| `--script` | yes | Path to the T-SQL script to apply. |
| `--dry-run` | no | Parse and count batches without executing. |

```bash
dbdelta apply --target "..." --script deploy.sql --dry-run
```

## `report`

Produces a self-contained diff report.

| Option | Required | Description |
|--------|----------|-------------|
| `--source` | yes | Source connection string. |
| `--target` | yes | Target connection string. |
| `--html` | no | Output path for the self-contained HTML report. |
| `--json` | no | Output path for the JSON report. |

```bash
dbdelta report --source "..." --target "..." --html diff.html
```
