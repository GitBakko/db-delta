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
| `--no-transaction` | no | Emit a script that opens no transaction of its own and declares it with `-- dbdelta:transaction=none` on its first line, so `apply` does not add one either. A failure halfway leaves the target halfway: re-compare and re-generate, never re-run the script. |

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
| `--no-transaction` | no | Never open a client-side transaction. Not needed for a script that already declares `-- dbdelta:transaction=none`. |
| `--command-timeout` | no | Per-batch timeout in seconds, default `60`. `0` means no limit — for batches that legitimately run long, such as a table rebuild copying rows. |

```bash
dbdelta apply --target "..." --script deploy.sql --dry-run
```

### Who owns the transaction

**Not always the client.** A script that opens its own transaction is left to
run it, because wrapping it would nest one inside another: `@@TRANCOUNT` reaches
2 and the script's `COMMIT` only decrements it, so the outer transaction stays
open and the deploy neither commits nor rolls back. The scripts `dbdelta script`
writes are exactly that kind — unless `--no-transaction` is passed, see below —
and carry a `-- dbdelta:transaction=script` marker saying so.

A script can also declare the opposite, with `-- dbdelta:transaction=none` **on
its first line**. `apply` honours it without `--no-transaction` on the command
line. `ScriptGenerator` writes that marker when a caller passes
`ComparisonOptions.NoTransactions`, which is what `dbdelta script
--no-transaction` sets; the GUI has no control for it, so from there the line is
one you put at the top of a script yourself. Before the marker existed such a
script came out with no envelope at all, `apply` could not tell it from a
hand-written one, and wrapped it in a client transaction — undoing the option
that had generated it unless the operator passed the flag as well.

The two verbs spell the flag the same way on purpose, and it does two different
things: `script --no-transaction` writes the declaration, `apply
--no-transaction` suppresses the client transaction. A script that carries the
declaration needs no flag on `apply` at all.

The marker has to be the FIRST line, not merely present somewhere: this answer
only ever REMOVES the client transaction, and no flag puts it back, so a marker
arriving inside a copied comment or a string literal must not count.

**A declaration outranks the guess.** For a script with no marker — written by
hand or by another tool — `apply` falls back to looking for a line-anchored
`BEGIN TRANSACTION`, and that fallback over-detects: a `CREATE PROCEDURE` body
carrying one reads as self-managed. When a marker is present it decides, so a
script that declares `none` is never reported as `script`. Silence is not a
declaration, though: a script that merely has no `BEGIN TRANSACTION` still gets
a client transaction, because that is the case where a failure at batch 3 of 5
would otherwise leave the database half-migrated.

So there are three outcomes, and the JSON output names the one that happened in
its `transaction` field:

| `transaction` | When |
|---------------|------|
| `script` | The script manages its own — DbDelta stays out of the way. |
| `client` | The script does not say, and no line-anchored `BEGIN TRANSACTION` was found, so `apply` wraps every batch in one transaction and rolls back on the first failure. |
| `none` | The script declares `-- dbdelta:transaction=none`, or `--no-transaction` was passed **on a script that does not manage its own**. Each batch commits as it runs, and a failure halfway leaves the target halfway. `--no-transaction` against a `-- dbdelta:transaction=script` script still reports `script`: the flag only removes a client transaction, and there was never going to be one. |


### What `rolledBack` does and does not promise

`apply`'s JSON also carries a `rolledBack` field, and it answers a narrower
question than its name suggests: **did `apply` itself issue a rollback and see
it acknowledged.** Read it as a one-way guarantee.

| `rolledBack` | What you may conclude |
|--------------|-----------------------|
| `true` | The target is unchanged. `apply` rolled back and the server confirmed it. |
| `false` | **Nothing.** Either the run succeeded, or the failure left the outcome indeterminate from the client's side. It is *not* a statement that the target kept the work. |

The case that surprises people is a script that manages its own transaction —
which is every script `dbdelta script` writes, unless you asked for
`--no-transaction`. SQL Server rolls such a transaction back on its own for a
failure at severity 14 or above, so by the time `apply` looks there is no open
transaction left for it to roll back, and it reports `false` with the whole
deploy already undone.

Measured on 2026-09-02 against a real catalog, same script and the same
`Msg 208`, two throwaway targets:

| Run | `transaction` | `rolledBack` | `batchesExecuted` | Objects left in the target |
|-----|---------------|--------------|-------------------|----------------------------|
| with the envelope | `script` | `false` | 1400 | **0** — everything undone |
| `--no-transaction` | `none` | `false` | 1398 | **1599** — everything kept |

Same field, same value, opposite outcomes. So `apply` also emits `targetState`,
which names the outcome instead of leaving you to derive it:

| `targetState` | Meaning |
|---------------|---------|
| `applied` | The run succeeded. |
| `unchanged` | `apply` rolled back and the server acknowledged it. |
| `partial` | There was no transaction (`transaction` is `none`), so the batches before the failure are committed and stay committed. |
| `unknown` | The script or the client owned a transaction, and `apply` could not confirm what became of it. Usually the server had already rolled it back — but "usually" is not a guarantee, and this field will not pretend otherwise. |

On `unknown`, the target's own catalog is the only unambiguous answer: re-run
`dbdelta compare`.

### Why no DROP is guarded

Every `DROP` DbDelta writes is bare. There is no `IF EXISTS` and no existence
probe, on any object kind, and that is deliberate: **a `DROP` that fails is
telling you something true** — the target is no longer the database the
comparison read, so the rest of the script is a delta computed against a
database that has moved.

It follows that a generated script is **not** meant to be run twice. After a
failure, compare again and generate again; the second script is the one that
matches what the target is now. Re-running the first one is how a half-applied
target happens.

Until 2026-09-01 the four module kinds — view, function, procedure, trigger —
carried `IF EXISTS` while the other nine did not, so a second execution cleared
the module drops and then died on the first table with Msg 3701. That was the
worst of both policies: neither fail-fast nor re-runnable.

### What the failure gate catches, and what it does not

Every batch is followed by `IF @@ERROR <> 0 SET NOEXEC ON`, and the closing
verdict rolls the transaction back when that gate tripped. Measured on
`mssql/server:2022-latest` 16.0.4265.3 rather than assumed:

- `@@ERROR` **survives the `GO`**, so the gate does read the previous batch's
  last statement — it is not a no-op.
- It is blind as soon as the failed statement is followed, in the same batch, by
  one that succeeds. `@@ERROR` reports the *last* statement, and even a `PRINT`
  clears it.
- It is blind to `EXEC` in **every** position, last included: a failed
  `sp_rename` leaves `@@ERROR` at 0 while returning a non-zero return code.

What decides whether that matters is the error's **severity**, not
`SET XACT_ABORT ON`:

| Severity | Example | The batch |
|----------|---------|-----------|
| 11 | Msg 3701 on a missing table, view, procedure, function, index, sequence, synonym or trigger; Msg 15225 and 15335 from `sp_rename` | continues, and commits |
| 14 and up | Msg 2714, 3726, 3727/3728, 4902, 8106, 218, 15151, 207 — and Msg 3701 itself when it means *you do not have permission*, which is raised at severity 14 | aborts, and rolls back |

That split was measured for **run-time** errors. A batch that fails to COMPILE
is a third case and behaves like severity 11 here: Msg 4145 at severity 15 never
runs a statement, so it leaves the transaction open. It cannot be a swallowed
error either — nothing in that batch executed.

So the only error a generated script can swallow is "the object this `DROP` was
about to remove is already gone" — the re-run case the policy above already
declares unsupported. Every error that would leave the target in a shape you did
not ask for aborts the batch and takes the transaction with it.

One asymmetry is worth knowing, because the same file behaves differently in the
two places it can run. `dbdelta apply` and the desktop app are **stricter** than
the script: `Microsoft.Data.SqlClient` raises an exception for a severity-11
error too, so the run stops at that batch, the script's `COMMIT` is never sent,
and the deploy is reported as failed with the target unchanged. The same file run
by hand in SSMS or `sqlcmd` commits and prints `The database update succeeded`.

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
