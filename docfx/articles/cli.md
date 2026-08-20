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

Executes a pre-generated script against the target, split on `GO`.

| Option | Required | Description |
|--------|----------|-------------|
| `--target` | yes | Target connection string. |
| `--script` | yes | Path to the T-SQL script to apply. |
| `--dry-run` | no | Parse and count batches without executing. |
| `--no-transaction` | no | Never open a client-side transaction. |
| `--command-timeout` | no | Per-batch timeout in seconds, default `60`. `0` means no limit — for batches that legitimately run long, such as a table rebuild copying rows. |

```bash
dbdelta apply --target "..." --script deploy.sql --dry-run
```

### Who owns the transaction

**Not always the client.** A script that opens its own transaction is left to
run it, because wrapping it would nest one inside another: `@@TRANCOUNT` reaches
2 and the script's `COMMIT` only decrements it, so the outer transaction stays
open and the deploy neither commits nor rolls back. The scripts `dbdelta script`
writes are exactly that kind and carry a `-- dbdelta:transaction=script` marker
saying so.

So there are three outcomes, and the JSON output names the one that happened in
its `transaction` field:

| `transaction` | When |
|---------------|------|
| `script` | The script manages its own — DbDelta stays out of the way. |
| `client` | The script does not, so `apply` wraps every batch in one transaction and rolls back on the first failure. |
| `none` | `--no-transaction` was passed. Each batch commits as it runs, and a failure halfway leaves the target halfway. |

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
