# Ruflo — Claude Code Configuration

## Rules

- Do what has been asked; nothing more, nothing less
- NEVER create files unless absolutely necessary — prefer editing existing files
- NEVER create documentation files unless explicitly requested
- NEVER save working files or tests to root — use `/src`, `/tests`, `/docs`, `/config`, `/scripts`
- ALWAYS read a file before editing it
- NEVER commit secrets, credentials, or .env files
- Keep files under 500 lines
- Validate input at system boundaries

## UI / UX Invariable Rules (DbDelta Avalonia app)

These are **non-negotiable** styling rules. Apply on every UI change.

1. **No naked buttons.** Every button MUST have a visible background fill OR a
   visible border. "Ghost" buttons that only reveal on hover are banned. Pick
   the fill colour by semantic meaning of the action:
   - `primary` (cobalt) — confirmation / commit / "go forward" actions (OK,
     Connetti, Allinea destinazione)
   - `secondary` (violet) — informational / discovery actions (Scansiona)
   - `success` (emerald) — read-only success path (Genera script, Connetti
     test-only)
   - `danger` (crimson) — destructive / irreversible (Esegui, Drop)
   - `neutral` (raised-grey filled) — secondary / utility actions (Salva,
     Carica, Annulla, Modifica, navigation icons)
   Refer to the design-system brushes in `Styles/Tokens.axaml` (Primary,
   Secondary, Emerald, Danger, BgRaised ramps).

2. **Uniform monoline height.** All single-line interactive controls in the
   same surface share the SAME height. Default for app shell + dialogs:
   **32 px** (Min + Max). Includes:
   - `Button`
   - `TextBox`
   - `AutoCompleteBox`
   - `ComboBox`
   - `CheckBox` (visual height; checkbox itself is 16 but row height is 32)
   The shared height makes rows of mixed controls (Cerca + Raggruppa +
   Tema, panel forms, footer action bars) read as a single elegant strip.
   When introducing a new control, set `Height="32"` or use a shared style.

3. **DRY — Don't Repeat Yourself, ALWAYS.** This is the single binding rule
   for every future change. Any UI pattern (XAML markup, code-behind logic,
   view-model boilerplate) that would be duplicated more than **once**
   MUST be extracted before the second copy ships:
   - Repeated XAML: extract to a `UserControl` under `Views/Controls/` (or
     a `Style` in `AppStyles.axaml` when it is purely visual).
   - Repeated behaviour: extract to a method, base class, or `behavior`.
   - Repeated view-model logic: extract to a shared service or partial
     base view-model.
   - When tempted to copy-paste a snippet "just this once" — STOP, create
     the abstraction first, then use it from both call sites.
   Violation example we've already paid for: the in-button "busy" markup
   (spinner + label) was copy-pasted into 4 buttons; one was missed in a
   refactor and shipped in an inconsistent state. The lesson: a reusable
   `LoadingContent` UserControl is mandatory — see
   `Views/Controls/LoadingContent.axaml`. Apply the same principle to
   every future repeated pattern. No spaghetti programming.

## Agent Comms — Reality-Based Coordination

**Tool-availability asymmetry:** `SendMessage` works **lead↔subagent** and lead↔lead, but **NOT subagent↔subagent**. Subagents spawned via the `Agent` tool are stateless one-shot workers — they have no inbox, cannot wait for events, and `SendMessage`/`TaskUpdate` are typically not in their tool allowlists. The `hive-mind_*` MCP tools provide coordination **metadata** (registry, consensus state) but do NOT grant subagents communication channels. Patterns that assume peer messaging will silently fail — agents either abort cleanly or run open-loop with stale assumptions. (See ruvnet/ruflo#2028 for the diagnosis.)

### Canonical pattern: memory-as-bus, lead-orchestrated phases

```
Lead (the orchestrator)
  │
  ├─ spawns agent → agent reads inputs from memory keys → writes outputs to memory keys → completes
  │
  ├─ verifies outputs in memory
  │
  └─ spawns next agent with explicit input-key list in its brief
```

All inter-agent state lives in a shared memory namespace (`memory_store` / `memory_search`). Lead-to-subagent `SendMessage` is fine when needed; subagent-to-subagent `SendMessage` is not.

### Spawning rules

- **Parallelize ONLY when work is genuinely independent** (no upstream dependency between siblings).
- **Spawn dependent agents only after the lead confirms upstream outputs are in memory.** Do NOT tell a downstream agent to "WAIT for SendMessage from X" — it has no mechanism to wait; it will abort.
- **Every subagent brief MUST include a degraded-mode paragraph** at the top: *"If your expected coordination tools (SendMessage, TaskUpdate, hive-mind_*) are missing, do NOT abort. Read these specific source files directly, write outputs to these specific memory keys, and complete your phase."*
- **Name agents** — `name: "role"` makes them addressable by the lead even though they cannot address each other.
- **After spawning**: STOP, tell user what's running, wait for completion notifications. No polling.

### Spawning example (memory-as-bus)

```javascript
// Phase 1 — independent parallel work
Agent({
  prompt: "Read docs at <paths>. Write inventory JSON to memory key phase1/researcher/inventory in namespace <ns>. Degraded mode: if memory tools missing, return inventory in your final message.",
  subagent_type: "researcher", name: "researcher", run_in_background: true
})
Agent({
  prompt: "Walk the source tree. Write capability matrix to memory key phase1/coder/capability-matrix. Degraded mode: ...",
  subagent_type: "coder", name: "source-reader", run_in_background: true
})

// AFTER both Phase 1 agents complete (lead verifies via memory_search), THEN spawn Phase 2.
// Each Phase 2 agent's brief explicitly lists the Phase 1 memory keys it should read.
```

### Patterns

| Pattern | Flow | Use When |
|---------|------|----------|
| **Sequential pipeline** | Lead → A → (verify in memory) → B → (verify) → C | Phase dependencies (audit, complex refactor) |
| **Fan-out** | Lead → A, B, C (parallel) → Lead aggregates from memory | Independent parallel work (research, multi-lens critique) |
| **Lead-as-bus** | Subagents → Lead → reroute by spawning next | Workaround when supervisor↔workers coordination needed |

### Anti-patterns (will silently fail)

- "WAIT for SendMessage from X" in a subagent prompt — no mechanism to wait
- "SendMessage findings to architect" in a subagent prompt — architect can't receive
- Spawning N dependent agents in one batch expecting them to chain via messages — they won't
- Relying on `hive-mind_consensus` to gather subagent votes — subagents aren't registered hive workers

### Lead-only SendMessage (still works)

`SendMessage` is still useful for **lead → subagent** redirects and priority changes:

```javascript
// Lead → subagent: redirect or update priority mid-flight
SendMessage({ to: "developer", summary: "Prioritize auth", message: "Auth is blocking tester, do that first." })
// Lead → subagent: graceful shutdown
SendMessage({ to: "developer", message: { type: "shutdown_request" } })
```

## Swarm & Routing

### Config
- **Topology**: hierarchical-mesh (anti-drift)
- **Max Agents**: 15
- **Memory**: hybrid
- **HNSW**: Enabled
- **Neural**: Enabled

```bash
npx @claude-flow/cli@latest swarm init --topology hierarchical --max-agents 8 --strategy specialized
```

### Agent Routing

| Task | Agents | Topology |
|------|--------|----------|
| Bug Fix | researcher, coder, tester | hierarchical |
| Feature | architect, coder, tester, reviewer | hierarchical |
| Refactor | architect, coder, reviewer | hierarchical |
| Performance | perf-engineer, coder | hierarchical |
| Security | security-architect, auditor | hierarchical |

### When to Swarm
- **YES**: 3+ files, new features, cross-module refactoring, API changes, security, performance
- **NO**: single file edits, 1-2 line fixes, docs updates, config changes, questions

### 3-Tier Model Routing

| Tier | Handler | Use Cases |
|------|---------|-----------|
| 1 | Agent Booster (WASM) | Simple transforms — skip LLM, use Edit directly |
| 2 | Haiku | Simple tasks, low complexity |
| 3 | Sonnet/Opus | Architecture, security, complex reasoning |

## Memory & Learning

### Before Any Task
```bash
npx @claude-flow/cli@latest memory search --query "[task keywords]" --namespace patterns
npx @claude-flow/cli@latest hooks route --task "[task description]"
```

### After Success
```bash
npx @claude-flow/cli@latest memory store --namespace patterns --key "[name]" --value "[what worked]"
npx @claude-flow/cli@latest hooks post-task --task-id "[id]" --success true --store-results true
```

### MCP Tools (use `ToolSearch("keyword")` to discover)

| Category | Key Tools |
|----------|-----------|
| **Memory** | `memory_store`, `memory_search`, `memory_search_unified` |
| **Bridge** | `memory_import_claude`, `memory_bridge_status` |
| **Swarm** | `swarm_init`, `swarm_status`, `swarm_health` |
| **Agents** | `agent_spawn`, `agent_list`, `agent_status` |
| **Hooks** | `hooks_route`, `hooks_post-task`, `hooks_worker-dispatch` |
| **Security** | `aidefence_scan`, `aidefence_is_safe`, `aidefence_has_pii` |
| **Hive-Mind** | `hive-mind_init`, `hive-mind_consensus`, `hive-mind_spawn` |

### Background Workers

| Worker | When |
|--------|------|
| `audit` | After security changes |
| `optimize` | After performance work |
| `testgaps` | After adding features |
| `map` | Every 5+ file changes |
| `document` | After API changes |

```bash
npx @claude-flow/cli@latest hooks worker dispatch --trigger audit
```

## Agents

**Core**: `coder`, `reviewer`, `tester`, `planner`, `researcher`
**Architecture**: `system-architect`, `backend-dev`, `mobile-dev`
**Security**: `security-architect`, `security-auditor`
**Performance**: `performance-engineer`, `perf-analyzer`
**Coordination**: `hierarchical-coordinator`, `mesh-coordinator`, `adaptive-coordinator`
**GitHub**: `pr-manager`, `code-review-swarm`, `issue-tracker`, `release-manager`

Any string works as a custom agent type.

## Build & Test

This is a **.NET 10** solution (`DbDelta.sln`), NOT a Node project.

- ALWAYS run tests after code changes
- ALWAYS verify build succeeds before committing
- **CI gates hard on `dotnet format --verify-no-changes`** (windows-build job).
  Run `dotnet format` on every file you touch before committing, or CI goes red.
- DB-backed tests (LiveDb / Persistence integration / Cli acceptance / compat)
  need Docker (Testcontainers). The nightly compat matrix self-skips unless
  `DBDELTA_COMPAT=1`.

```bash
dotnet build DbDelta.sln -c Debug
dotnet test DbDelta.sln                       # or a specific tests/<project>
dotnet format DbDelta.sln --verify-no-changes # CI gate — must exit 0
```

Backlog / task list for new sessions: `docs/BACKLOG.md`.

## CLI Quick Reference

```bash
npx @claude-flow/cli@latest init --wizard           # Setup
npx @claude-flow/cli@latest swarm init --v3-mode     # Start swarm
npx @claude-flow/cli@latest memory search --query "" # Vector search
npx @claude-flow/cli@latest hooks route --task ""    # Route to agent
npx @claude-flow/cli@latest doctor --fix             # Diagnostics
npx @claude-flow/cli@latest security scan            # Security scan
npx @claude-flow/cli@latest performance benchmark    # Benchmarks
```

26 commands, 140+ subcommands. Use `--help` on any command for details.

## Setup

```bash
claude mcp add claude-flow -- npx -y @claude-flow/cli@latest
npx @claude-flow/cli@latest daemon start
npx @claude-flow/cli@latest doctor --fix
```

**Agent tool** handles execution (agents, files, code, git). **MCP tools** handle coordination (swarm, memory, hooks). **CLI** is the same via Bash.
