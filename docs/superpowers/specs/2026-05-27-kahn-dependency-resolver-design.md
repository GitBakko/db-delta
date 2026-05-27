# #24 — Kahn dependency resolver (v0.15.0, spec M7)

**Status:** released 2026-05-27 · tag v0.15.0
**Driver:** correctness — the script generator currently orders objects
alphabetically within each kind, which breaks any schema whose objects
reference each other out of alphabetical order, and emits whole kinds in a
fixed sequence that is wrong for genuine cross-kind dependencies.

## Problem

`ScriptGenerator` today orders by a fixed kind-level pipeline
(sequences → tables → indexes → views → functions → procedures → triggers →
synonyms → FKs → permissions) and, inside each kind,
`.OrderBy(Identity.SchemaName).ThenBy(...)` — i.e. alphabetical, **not
dependency-aware**. Two classes of latent bug follow:

1. **Intra-kind** — a view that selects from another view, a schemabound
   function that calls another function, etc. SQL Server validates referenced
   objects at `CREATE` time for views and functions, so if the referenced
   object sorts *after* the referencing one the generated script fails.
2. **Cross-kind** — the fixed pipeline emits functions *after* tables, but a
   computed column / `CHECK` / `DEFAULT` can reference a scalar UDF or a
   sequence. A computed column referencing a UDF therefore produces a script
   that creates the table before the function it needs → failure. The fixed
   kind order cannot express this because the dependency runs *against* the
   pipeline direction.

## Key correctness facts (scope shapers)

- **Module reference cycles cannot exist in a valid source DB.** SQL Server
  validates referenced objects at `CREATE` of views and functions, so a true
  `A→B→A` reference cycle among create-validated objects is uncreatable. The
  resolver therefore does **not** need a general cycle-breaking subsystem for
  modules.
- **FK cycles are real but already handled.** Foreign keys are added after
  both tables exist, so a source DB *can* contain `Table A ↔ Table B` FK
  cycles. The existing design already emits **all** FKs last (pipeline
  section 7 + the #33 inbound-FK lifecycle in sections 0.9 / 7.9), which
  breaks every table-level FK cycle by construction. FK edges are therefore
  **excluded** from the topological graph.
- **Procedures and triggers use deferred name resolution.** Their `CREATE`
  succeeds even if referenced objects do not yet exist, so their relative
  order is never required for validity. Edges from procs/triggers are *soft*:
  honoured when acyclic for cleaner output, safely ignored inside an SCC.

Net effect: Kahn topological sort is needed for the **create-validated** kinds
(sequences, alias UDTs, tables, scalar/inline/table functions, views,
synonyms) and the **cross-kind** edges between them. `CycleDetector` is a
safety net, not a subsystem.

## Approach (chosen: global topo order, phased emission)

Build one object-level `DependencyGraph` over all create-validated objects
(FK edges excluded), produce a single deterministic topological order, and
restructure `ScriptGenerator` into four phases. This is the only approach that
makes cross-kind ordering correct *by construction* rather than via per-kind
special cases. Rejected alternatives: per-kind topo sort + ad-hoc cross-kind
exceptions (leaky, not real M7); layered prologue (still ad-hoc).

## Components

### 1. `DbDelta.Core/Dependency/` — pure, no I/O

- `EdgeKind` — enum: `ModuleReference`, `ComputedColumn`, `CheckConstraint`,
  `DefaultConstraint`, `FunctionOnTable`, `TriggerOnTable`, `ForeignKey`
  (FK kind exists for completeness but FK edges are filtered out before the
  sort).
- `DependencyEdge(ObjectIdentity Dependent, ObjectIdentity Referenced, EdgeKind Kind)`
  — record. "Dependent depends on Referenced" ⇒ Referenced must be created
  first.
- `DependencyGraph` — built from `(nodes, edges)`. Filters out `ForeignKey`
  edges. Exposes adjacency + in-degree for the sort.
- `TopologicalSort` — Kahn's algorithm with a **stable tiebreak**
  `(KindRank, SchemaName, Name)` so ready-nodes are dequeued deterministically.
  Determinism is a hard requirement (golden + property tests assert it).
  `KindRank` keeps a sensible default grouping when no edge constrains two
  nodes (e.g. sequences before tables) without forcing it when an edge says
  otherwise.
- `CycleDetector` — Tarjan SCC over the non-FK graph.
  - SCC of **only** deferred-resolution kinds (procedure, trigger) → emit its
    members in `(SchemaName, Name)` order (safe; deferred resolution).
  - SCC containing any **create-validated** kind → `throw
    DependencyCycleException(path)`. This signals a reader bug (an uncreatable
    cycle was reported), not user error; the message lists the cycle path.

### 2. Edge source + object-model change

- `Database` gains `IReadOnlyList<DependencyEdge> Dependencies` (default
  empty). Pure Core consumes it; only the provider populates it. I/O stays in
  the provider.
- `DbDelta.Providers.LiveDb` gains a `DependencyReader` that reads
  `sys.sql_expression_dependencies` for module bodies, plus derives edges for:
  - computed column / `CHECK` / `DEFAULT` → referenced function or sequence,
  - table-valued **schemabound** function → referenced table(s),
  - trigger → its base table,
  - FK → emitted as `EdgeKind.ForeignKey` (filtered out before sorting; kept
    only so the edge list is a complete record).
- **Unresolved edges** (cross-database refs, dynamic SQL, `referenced_id`
  NULL) produce **no edge**. The affected node then falls back to its stable
  alphabetical position — still valid for the common case. Documented
  limitation; not a correctness regression versus today (today is *always*
  alphabetical).

### 3. `ScriptGenerator` — four phases

1. **DROP** — reverse topological order. Absorbs the #33 section-0.9 drop of
   inbound FKs that point at identity-rebuild targets.
2. **CREATE** — a single interleaved loop over the topological order,
   dispatching by `Identity.Kind` to the **existing** per-object emitters
   (sequence / alias-UDT / table / function / view / synonym / procedure /
   trigger interleaved by real dependency).
3. **FK** — emitted last, unchanged: the pair-level FK delta plus the #33
   section-7.9 re-add of inbound FKs onto rebuilt tables, with the existing
   name-based dedup against the rebuild-orchestrated set.
4. **Permission** — last, gated on `!IgnorePermissions` (default ON), unchanged.

Per-object emitters are **not** rewritten; only the orchestration changes.
All existing `ComparisonOptions` gating is preserved.

## Testing

- **Pure unit** — `TopologicalSort`: valid linearization (every edge respected),
  determinism across repeated runs and across input permutations.
  `CycleDetector`: deferred-only SCC → alphabetical; create-validated SCC →
  throws with the cycle path.
- **Golden re-baseline** — regenerate the 28 expected scripts, inspecting
  **every** diff to confirm it is a correct reordering, not a semantic
  regression (verify-before-accept per fixture).
- **Live integration** (reuses the #21 round-trip harness) — a schema
  exercising computed-col→UDF, view→view, view→function, and
  schemabound-TVF→table: generate → apply on a Testcontainers SQL Server
  container → assert the script applies clean (a clean apply *is* proof the
  order is valid).
- **Property** — generated output is a valid topological linearization of the
  dependency graph and is deterministic.

## Risks

- **Highest:** reconciling the #33 inbound-FK rebuild logic (current sections
  0.9 / 7.9) into phases 1 + 3 without regression. Covered by the existing #33
  unit tests + scenario-12 parity fixture.
- **Most labour:** reviewing the golden re-baseline diffs.
- **Perf:** graph build + Kahn are O(V+E), negligible against the §6.1 budget
  (current `ComparisonBench.Compare` 10k = 17.8 ms vs 3000 ms).

## Out of scope (YAGNI)

- ScriptDom-based edge extraction from raw definitions — only justified by an
  offline/snapshot source, and `ISchemaSource` has a single live
  implementation today. `sys.sql_expression_dependencies` is authoritative for
  the live path.
- A general module cycle-breaker (stub-then-ALTER) — uncreatable in a valid
  source, so unreachable.
