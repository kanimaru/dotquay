# DESIGN v0.18 — dotquay as an MCP server

Status: **shipped 2026-09-17**, all four phases in §9, each verified against a real NativeAOT binary.
The contract now lives in SPEC.md §20, which wins where the two disagree. The decisions in §1 were
taken with kani on 2026-09-16, and Phase 0 (§9) ran the same day. The rest of this document is kept
as the design record.

## The problem

Every agent that works on a Godot project today reaches dotquay the way a shell script does: it
guesses a command line, runs it, and scrapes whatever came back. That works right up to the point
where it doesn't — the agent runs `update --force` because the word "force" sounded like what the
user wanted, or it edits `dotquay.lock` by hand because the lockfile looked like JSON-adjacent text,
or it never discovers `status --explain` exists and tells the user an addon is "corrupted". The
`--json` contract (SPEC.md §17) already solves the *parsing* half of this for the Godot plugin; what
an agent lacks is the *discovery* half — a typed description of what dotquay can do, what each
operation needs, and which ones are safe to call without a human in the loop.

The Model Context Protocol is the discovery half. A `dotquay mcp` process describes its tools with
JSON schemas and safety annotations, returns the same payloads the plugin already consumes, and can
refuse — in a structured, explained way — anything that would have prompted a human at the console.
The framing that decides the shape: **MCP is a third consumer of the §17 contract, next to the shell
and the editor plugin, not a new feature surface.** Anything the MCP layer seems to need that Core
does not offer is a gap in Core, filled there, exactly as the plugin rule in SPEC.md §18 already says.

## 1. Decisions taken

| Question | Decision | Why |
|---|---|---|
| Where it lives | New `src/Dotquay.Mcp` class library + a thin `dotquay mcp` command in `Dotquay.Cli` | One AOT binary, so `self update` (v0.17) covers it and client configuration is literally `dotquay mcp`. The library depends on `Dotquay.Core` only, so the tool catalogue is testable against the existing fakes without System.CommandLine in the picture. |
| Transport | stdio only | Every client that matters (Claude Code, Claude Desktop, Cursor, Rider/VS Copilot) spawns a process. No listener, no port, no auth surface, no Kestrel in the AOT graph. |
| Write scope | Reads + guarded writes | The agent may install, add, update, remove, and capture patches. It may **not** log in, touch feeds, create local links, or replace the binary. Anything that would have asked a question at the console is refused unless the caller passes the approval it already obtained from the user (§5). |
| Primitives | Tools, resources, prompts | Resources expose `dotquay.toml` / `dotquay.lock` / `dotquay.local.toml` as `dotquay://` URIs; prompts ship three canned workflows (§7). A `dotquay mcp install --client` helper is deferred (§11). |

## 2. Non-goals

- **No new resolution, sync, or status logic in `Dotquay.Mcp`.** The library maps tool calls onto
  `DependencyWorkflow`, `OverrideWorkflow`, and `SourceWorkflow` and maps their results through the
  existing `JsonMapper`. If a tool needs a decision Core cannot make, that decision is added to Core
  (as `status --explain` was for the plugin), not to the MCP layer.
- **No credential handling of any kind.** `login`, `logout`, the credential helper, and the
  `[nuget] trusted_hosts` prompt are all out. An agent that needs a private feed tells the user
  which command to run.
- **No local links.** `link` creates a directory junction pointing at an arbitrary path chosen by the
  caller, and `addons/<folder>` deletion must never follow a link (SPEC.md §9, v0.16). Letting an
  agent choose that path is the one place where a plausible-sounding mistake destroys work git cannot
  recover. Read-side awareness of links stays (status reports `linked`; the local manifest is a
  resource).
- **No `self` commands, no `source add/remove`, no `credential-helper`.**
- **No HTTP transport, no sampling, no elicitation in v1.** Elicitation is the natural future home
  for the feed-adoption question (§5); the design leaves the seam but does not use it.
- **No changes to the Godot plugin.** Nothing here alters a `--json` payload shape or an enum wire
  value; the plugin is unaffected and stays on `--json`.

## 3. Architecture

```
                 stdin/stdout (newline-delimited JSON-RPC 2.0, UTF-8, no BOM)
                                    │
  Dotquay.Cli  ──  McpCommand  ─────┼──────────────────────────────────────────────
                                    ▼
  Dotquay.Mcp    Generic Host (Host.CreateApplicationBuilder, logging cleared — §3.2)
                 .AddMcpServer(...).WithListToolsHandler(...).WithCallToolHandler(...)
                   .WithListResourcesHandler(...) .WithReadResourceHandler(...)
                   .WithListPromptsHandler(...) .WithGetPromptHandler(...)
                   .WithStdioServerTransport()          [ModelContextProtocol 2.2.0 — §3.1]
                 ├── ToolRegistry  : IToolRegistry         (backs the ListTools/CallTool handlers)
                 │     └── one IMcpTool per operation      (schema + annotations + handler)
                 ├── ResourceRegistry : IResourceRegistry  (backs the resource handlers)
                 ├── PromptRegistry : IPromptRegistry      (backs the prompt handlers)
                 ├── ProjectScope  : IProjectScope         (which directories a call may name)
                 ├── WriteGuard    : IWriteGuard           (the "would this have prompted?" policy chain)
                 └── ProjectGate                           (one operation in flight per project)
                                    │
  Dotquay.Core   DependencyWorkflow / OverrideWorkflow / SourceWorkflow / JsonMapper / *Json payloads
```

Two layers, each replaceable behind an interface and each tested on its own — Phase 0 (§3.1) removed
the third: there is no hand-rolled protocol layer, because the SDK's own handshake, framing,
cancellation, and progress plumbing measured clean.

**Catalogue** (`Dotquay.Mcp.Tools`, `.Resources`, `.Prompts`): one class per tool, each declaring its
name, description, input schema (a JSON Schema literal, never inferred — §3.1), annotations, and a
handler that takes an `ModelContextProtocol.Server.RequestContext<TParams>` and returns the matching
SDK result type. Knows dotquay; knows nothing about stdin — the registries are plain classes the
`McpServerBuilder` (§10) wires into the SDK's `With*Handler` delegates, not SDK types themselves.

**Host** (`Dotquay.Cli.Commands.McpCommand`): parses `dotquay mcp [--project <dir>]...
[--log <file>]`, builds the real `DependencyWorkflow` through `WorkflowFactory` exactly as every other
command does, and calls `McpServerBuilder.Run` (§10), which assembles the Generic Host above and
blocks until stdin closes.

### 3.1 Verdict: adopt the official SDK, on its low-level handler API

**Measured 2026-09-16 (Phase 0 spike, not merged).** Two throwaway console apps — one on
`ModelContextProtocol` 2.2.0, one hand-rolling newline-delimited JSON-RPC directly — each published
under real NativeAOT (`win-x64`, the vcvars64 recipe) and each driven through a real child process:
`initialize` → `notifications/initialized` → `tools/list` → `tools/call` returning a
`JsonEnvelope<StatusPayload>`-shaped payload with a non-null `Explanation`, raw stdout bytes inspected
for BOM and stray output. **Both published with zero trim/AOT warnings and produced byte-identical,
correct, three-line JSON-RPC exchanges with no BOM.** The SDK's own build log had not one IL2026/IL3050
line — `TrimmerSingleWarn=false` so nothing was hidden. Full logs: `sdk-build.log`, `hand-build.log`
(not committed; the spike itself stays out of the repo per phase 0's own rule).

The reason the SDK measured clean is *how* it was used, and that "how" is now load-bearing for
everything downstream: **the low-level handler API, not attribute/DI tool discovery.**
`WithToolsFromAssembly` and `[McpServerTool]` go through `Microsoft.Extensions.AI`'s reflection-based
schema inference — the exact failure mode that already cost this project Spectre.Console.Cli and
NuGet.Protocol (docs/SPRINT-01.md; SPEC.md §12 OQ-1/OQ-6) — and the spike does not exercise it and
does not vouch for it. Instead: `AddMcpServer(options => options.ServerInfo = ...)` then
`.WithListToolsHandler(...)`/`.WithCallToolHandler(...)`/`.WithListResourcesHandler(...)`/etc. register
plain delegates (`McpRequestHandler<TParams, TResult>` — `ValueTask<TResult> (RequestContext<TParams>,
CancellationToken)`), each building its `Tool`/`ListToolsResult`/`CallToolResult` by hand. Tool input
schemas are written as JSON literals via `JsonDocument.Parse(...).RootElement` (no reflection-based
schema exporter involved). Every dotquay payload serializes through `DotquayMcpJsonContext`, our own
source-generated `JsonSerializerContext` covering exactly the `JsonEnvelope<T>` closures the MCP layer
emits — mirroring `DotquayJsonContext`'s own discipline, kept separate from it (§3, "Catalogue"). This
is what `Dotquay.Mcp` actually calls: `WithTools`/`WithToolsFromAssembly` and the `[McpServerTool]`
attribute are not referenced anywhere in the codebase, so there is nothing for a future contributor to
accidentally widen into.

Adopting the SDK over the hand-rolled layer buys real things the measurement doesn't show: `roots`
capability negotiation, `notifications/cancelled` plumbing, `logging/setLevel`, and the resources/
prompts primitives (§7) all come from the SDK's `McpServer` rather than something we would have had to
write and test ourselves — and every one of those is now *only* as reflection-safe as our own handler
code, which is the discipline §3.2 and §8 enforce. The one hand-rolled-layer advantage that doesn't
survive contact with the measurement — "a few hundred lines we own completely" — was true, but owning
protocol-framing code nobody outside this repo has stress-tested is a cost, not a feature, once the
alternative is proven to work.

**What would reopen this decision:** a future SDK release that regresses the AOT story (re-run this
exact spike before upgrading past 2.2.0 — pin the version, don't float it); or `WithListToolsHandler`
et al. being deprecated in favour of an API that isn't low-level. Nothing else in this doc needs a
hand-rolled fallback plan — there is no `IMcpTransport`/`McpServer` seam to keep warm, because the SDK
*is* that seam now.

### 3.2 stdout is the wire — everything else must be silent

The single sharpest edge of a stdio MCP server: one stray byte on stdout corrupts the channel, and
the client's error will say "invalid JSON" with no hint about where the byte came from. Three places
in dotquay write to stdout today and each needs an explicit answer:

1. **`UpdateNotice` in `Program.cs`** already writes to stderr, never stdout (its own header says
   why), so it cannot corrupt the wire — but it still fires a release-channel request on every start
   and prints a line when the server exits, into a log the user never reads. `mcp` joins its
   `IsEligible` exclusions next to `--json` (DESIGN-v0.17), for quiet rather than for safety.
2. **`ConsoleProgressReporter`** draws spinners. `McpCommand` never constructs it — it passes an
   `McpProgressReporter` (§6) that forwards to `notifications/progress` instead.
3. **`Host.CreateApplicationBuilder`'s own default logging.** Measured in the Phase 0 spike (§3.1):
   the Generic Host registers a console logger provider by default, and the SDK's own `McpServer` and
   `StdioServerTransport` log at `Information`/`Debug`/`Trace` on every message — with logging left at
   its default, that lands on stdout and corrupts the wire on the very first request. `McpCommand`
   calls `builder.Logging.ClearProviders()` before anything else, confirmed empty-stdout in the spike
   (`sdk-out3.bin`: 1791 bytes, three JSON-RPC lines, zero bytes of anything else). If a future change
   wants console logging for `--log`'s stderr fallback (§6), it must go through
   `AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace)`, never bare `AddConsole()` — the
   spike confirmed the threshold option is what routes it to stderr instead of stdout, and skipping it
   silently reintroduces the exact corruption `ClearProviders()` was added to prevent.
4. **The SDK's own stdio transport writes the actual frames**, via `WithStdioServerTransport()`
   (`Console.OpenStandardInput()`/`OpenStandardOutput()` under the hood — not `Console.Out`/`Console.In`,
   which the Generic Host's logging can repoint). The spike's raw-byte check (§3.1) found no BOM and no
   stray bytes on either variant, so this is not a place dotquay's own code needs to intervene — it is
   listed here because it is the thing item 3's `ClearProviders()` call is protecting.

A guard test (§8) starts the server with a fake `Console.Out` swapped in via `Console.SetOut` and
asserts that nothing reached it — so any future `Console.WriteLine`, or a reverted `ClearProviders()`
call, fails a test instead of a user's session.

## 4. Tool catalogue

Names are `dotquay_<verb>`, snake_case, because clients display them verbatim next to tools from
other servers and `status` alone is ambiguous. Every tool takes an optional `project` (absolute path,
§4.3); every tool returns both a human `text` content block and `structuredContent` holding the exact
`JsonEnvelope<T>` the equivalent `--json` command would have printed. **The MCP layer does not invent
a second schema** — an agent that has read SPEC.md §17 already knows the shape, and the plugin's
`dotquay_wire.gd` is a living test of it.

Annotations follow the MCP `ToolAnnotations` fields: `readOnlyHint`, `destructiveHint`,
`idempotentHint`, `openWorldHint`. They are hints to the client's confirmation UI, not enforcement —
enforcement is §5.

### 4.1 Read tools

| Tool | Arguments | Core call | Annotations |
|---|---|---|---|
| `dotquay_status` | `explain?: bool` | `StatusAsync` | read-only; `openWorld` when `explain` (may fetch) |
| `dotquay_list` | — | `ListInstalled` + `ListInstalledDotnet` | read-only, idempotent |
| `dotquay_graph` | — | `InspectorFactory` → `InspectResolvedAsync` | read-only, open-world |
| `dotquay_outdated` | — | `OutdatedAsync` | read-only, open-world |
| `dotquay_doctor` | — | `DoctorAsync` | read-only, open-world |
| `dotquay_add_preview` | same as `dotquay_add` minus approvals | `PreviewAddAsync` | read-only, open-world |
| `dotquay_source_list` | — | `SourceWorkflow.List` | read-only, idempotent |
| `dotquay_override_list` | — | `OverrideWorkflow.List` | read-only, idempotent |

`dotquay_add_preview` is the load-bearing one: it is how an agent finds out *before* writing anything
that an addon wants a NuGet feed the project does not trust, or ships a source generator into a
packable project. Its `PreviewSourceJson` entries are what the agent shows the user and then passes
back as `approved_source_urls` (§5). This mirrors the plugin's own preview → approve → add flow from
v0.9 exactly, which is the point: one policy, three consumers.

### 4.2 Guarded write tools

| Tool | Arguments | Core call | Annotations |
|---|---|---|---|
| `dotquay_init` | `package?: bool`, `name?`, `nuget_source?` | `InitService.InitProject` / `InitPackage` | not read-only, not destructive, idempotent |
| `dotquay_add` | `git_url`, `version?`, `branch?`, `rev?`, `path?`, `install_as?`, `nuget_source?`, `approved_source_urls?: string[]`, `keep_generator_private?: bool` | `ResolveShorthandAsync` → `AddAsync` | writes; not destructive |
| `dotquay_add_dotnet` | `package_id`, `version?`, `nuget_source?`, `keep_generator_private?` | `AddDotnetAsync` | writes; not destructive |
| `dotquay_install` | `force?: bool`, `confirm_discard?: string[]` | `InstallAsync` | writes; destructive only with `force` |
| `dotquay_update` | `name?`, `force?`, `confirm_discard?: string[]`, `approved_source_urls?` | `UpdateAsync` | writes; destructive only with `force` |
| `dotquay_remove` | `name` | `RemoveAsync` | destructive |
| `dotquay_patch_capture` | `name` | `PatchAsync` | writes; not destructive |
| `dotquay_patch_resolve` | `name` | `ResolvePatchAsync` | writes; not destructive |
| `dotquay_patch_abandon` | `name` | `AbandonPatchAsync` | destructive (drops the patch) |
| `dotquay_override_set` | `name`, `git?`, `version?`, `branch?`, `rev?`, `path?` | `OverrideWorkflow.Set` | writes; idempotent |
| `dotquay_override_clear` | `name` | `OverrideWorkflow.Clear` | writes; idempotent |

`dotquay_remove` is destructive and stays in v1 because it is *explained* destruction: it deletes
`addons/<folder>` through `InstallPaths` and the sync engine like every other caller, never follows a
link, and the agent can (and the prompt in §7 tells it to) run `dotquay_status` first. `install_as`
is accepted on `dotquay_add` because the folder-casing trap (SPEC.md §9, `CorrectFolderCase`) is one
of the things an agent is *good* at noticing from `doctor` output and fixing on a re-add.

### 4.3 `project` — which directory a call may touch

An MCP server has no working directory worth trusting: the client spawned it, from wherever the
client happened to be. Every tool therefore takes `project` explicitly, the same reason `--project`
exists for the plugin (GlobalOptions.cs). What a call may *name* is decided by `IProjectScope`:

- `dotquay mcp --project <dir>` (repeatable) pins the allowed roots. A `project` argument must be
  one of them or a subdirectory; anything else is refused with `kind: "scope"` (§5.3) naming the
  allowed roots, so the agent can tell the user how to widen it.
- With no `--project`, the scope is the process's current directory — which is whatever the client
  chose, and is the right default for `claude mcp add` run from inside a Godot project.
- If the client advertises the `roots` capability, `McpServer` requests them after `initialize` and
  adds them to the scope. A client that changes its roots sends `notifications/roots/list_changed`
  and the scope is re-read. This is the mechanism designed for exactly this, and it means Claude
  Code's "which folder am I working in" and dotquay's "which folder may I write to" are the same
  answer with no configuration. *Removed 2026-11-07: the MCP spec deprecates `roots` as of
  2026-07-28 (SEP-2577), so the scope is `--project` or the working directory only (SPEC.md §20.1).*
- `project` defaults to the *single* root when there is exactly one, and is required otherwise. An
  agent working across two Godot projects has to say which one — silently picking the first is how
  a lockfile ends up in the wrong repo.

### 4.4 `ProjectGate` — one operation per project at a time

Two `tools/call` requests can be in flight at once; MCP does not serialize them. Two `install`s
racing on one lockfile is a corruption, and a `status` read halfway through an addon swap is
meaningless. `ProjectGate` holds one `SemaphoreSlim(1,1)` per normalized project path; every tool,
read or write, acquires it for the duration of its Core call. Different projects proceed in parallel.
The gate is keyed on the path *after* `Path.GetFullPath` and case-normalization on Windows, so
`F:\Game` and `f:/game/` are the same gate.

## 5. The write guard — refusing what would have prompted

The console CLI asks three questions a human answers interactively. Under `--json` each of them
already has a non-interactive answer, chosen for the plugin. The MCP layer reuses those answers and
adds one policy the plugin never needed, because the plugin has a human clicking the button and an
agent does not.

### 5.1 Feed adoption → `approved_source_urls`

`ISourceApproval` is already a seam with a `PreApprovedSourceApproval` implementation (v0.9). The
tool passes `approved_source_urls` straight into it; with none, `DenyAllSourceApproval` applies and
the resulting `WorkflowException` becomes a tool error whose structured payload carries the feed
that was refused. The agent's job is then to show that to the user and call again with the URL in
the list. Nothing is added to Core.

### 5.2 Generator privacy → `keep_generator_private`

`IGeneratorAssetDecision` likewise. Default `ShareGeneratorDecision` (NuGet's own behaviour; the
`--json` default), `KeepGeneratorPrivateDecision` when the flag is true. Nothing is added to Core.

### 5.3 `--force` → `confirm_discard`

This is the new policy. `update --force` and `install --force` discard hand-edits that were never
captured as a patch; at the console the red `modified` line and the `--explain` verdict are what
stop a person. An agent gets neither unless it asked. So:

- `force: true` without `confirm_discard` is refused when *any* addon is `modified` with real content
  changes. The error's structured payload is the `status --explain` result, so the agent has the
  exact list of files that would be lost and can show it.
- `force: true` with `confirm_discard: ["name", ...]` proceeds only if the list covers **every**
  addon whose explain verdict is not `SafeToForce`. A partial list is refused with the names that are
  missing — the agent must have named each one, which means it must have shown each one.
- Addons whose verdict is `SafeToForce` (uid-only rewrites, line endings, editor artifacts) need no
  confirmation, exactly as the console tells a human `--force is safe`.

`IWriteGuard` is a chain of `IWritePolicy` instances evaluated before the Core call; the three above
are its initial members and each is a class with its own tests. Adding a fourth (say, "refuse
`remove` of an addon something else depends on" once the graph exposes reverse edges) is a new class,
not an edit to a switch statement.

### 5.4 Error shape

A refused or failed tool returns `isError: true` with a text block and a `structuredContent` that
reuses `JsonError` (`kind`, `message`) plus an optional `detail` object. `kind` extends
`JsonErrorKind` with two MCP-only values, kept in `Dotquay.Mcp` so the plugin's contract is
untouched:

| kind | Meaning |
|---|---|
| `scope` | `project` is outside the allowed roots. `detail.allowed_roots` lists them. |
| `needs_confirmation` | A write policy refused. `detail.policy` names it; `detail` carries what the agent must show the user (`unapproved_sources`, `unconfirmed_addons` with their explanations). |

Everything else maps through the existing `JsonOutput.Fail` classification — moved from
`Dotquay.Cli` into `Dotquay.Core.Json` as `JsonErrorClassifier` so both hosts share it, which is a
small refactor with an obvious test (the switch is already a table).

## 6. Progress, cancellation, logging

**Progress.** `McpProgressReporter : IProgressReporter` forwards each `ProgressUpdate` as a
`notifications/progress` carrying the request's `progressToken` when the client supplied one, and
drops updates silently when it did not. `DependencyWorkflow.WithProgress` already exists for this;
the reporter is per-request, not per-server, because two concurrent calls on different projects each
have their own token.

**Cancellation.** Each in-flight request owns a `CancellationTokenSource`; `notifications/cancelled`
cancels it, and the token is the one every Core `*Async` method already accepts. A cancelled call
returns nothing (per spec) and releases its `ProjectGate` slot in `finally`. Because the sync engine's
addon swap is a single `MoveDirectory` (SPEC.md §9), cancellation cannot leave `addons/` half-written
— the same property that made the v0.9 preview safe.

**Logging.** `--log <file>` appends a plain-text trace (every request id, method, duration, and the
first line of any exception) for debugging a client session after the fact; stderr gets the same
lines when no file is given, because clients generally surface stderr in their own logs and it is not
the wire. `notifications/message` carries `warning`-level and above to the client when it asked for
them via `logging/setLevel`. *Removed 2026-11-07: the MCP spec deprecates Logging as of 2026-07-28
(SEP-2577) in favour of stderr; only the `--log`/stderr trace remains (SPEC.md §20.4).*

## 7. Resources and prompts

**Resources** expose the three files dotquay owns, read-only, as `text/plain` with a
`application/toml`-flavoured `mimeType` where clients honour it:

| URI template | File | Notes |
|---|---|---|
| `dotquay://{project}/manifest` | `dotquay.toml` | `{project}` is the percent-encoded absolute path; must pass `IProjectScope`. |
| `dotquay://{project}/lockfile` | `dotquay.lock` | |
| `dotquay://{project}/local` | `dotquay.local.toml` | Listed only when present; it is per-machine and often absent. |
| `dotquay://{project}/patches/{name}` | `<patches_dir>/<name>.patch` | One per captured patch; `patches_dir` resolved the way Core resolves it, never assumed. |

`resources/list` enumerates concrete URIs for every root in scope; `resources/templates/list`
advertises the four templates. Writing through `resources` is not offered — the manifest is edited by
`dotquay_add`/`dotquay_override_set`, which validate, and never by text replacement. That is the same
rule the plugin follows and the reason it holds up.

**Prompts** are three server-provided templates that chain the tools in the order a careful person
would:

- `dotquay_diagnose_modified` (`name`) — call `dotquay_status` with `explain`, read the resource for
  the patch if one exists, classify the changes, and recommend exactly one of: capture as patch,
  `--force` with `confirm_discard`, or leave alone. Encodes the "diff against a fresh clone, split
  artifact vs. content" diagnostic pattern from the project notes so an agent doesn't rediscover it.
- `dotquay_upgrade_safely` — `dotquay_outdated` → `dotquay_status --explain` → for each outdated
  addon, `dotquay_update` one at a time, stopping at the first conflict marker and pointing at
  `dotquay_patch_resolve`. One at a time on purpose: a conflicted update aborts before writing the
  lockfile (SPEC.md §10), and a batch that half-applied is harder to reason about than five single
  ones.
- `dotquay_add_addon` (`git_url`) — `dotquay_add_preview` → show feeds/generators → obtain the
  user's yes → `dotquay_add` with approvals → `dotquay_doctor`. This is the plugin's add dialog as
  prose.

Prompt text lives in `Dotquay.Mcp/Prompts/*.md` as embedded resources, so it can be read and edited
as prose and a test can assert every tool name a prompt mentions exists in the registry.

## 8. Testing

Tests live in `tests/Dotquay.Tests/Mcp/` rather than a fourth project, because the fakes they need
(`InMemoryFileSystem`, `FakeGitClient`, `FakeNuGetClient`, …) live in `tests/Dotquay.Tests/Support/`
and extracting them into a shared `Dotquay.Tests.Support` project is a refactor with its own blast
radius. If `Dotquay.Mcp` ever grows a second host, that extraction is the moment to do it.

Layers, each independent:

1. **Handler conformance** — `WithStreamServerTransport(Stream, Stream)` (the SDK's own low-level
   entry point, in place of a hand-rolled in-memory transport pair — §3.1) wires two linked
   `MemoryStream`/`Pipe` pairs to a real `Dotquay.Mcp` server in-process and drives it through golden
   JSON-RPC exchanges checked against expected results: `initialize` capability negotiation,
   `tools/list` shape matches the registry exactly, an unknown tool name in `tools/call`, a
   `notifications/cancelled` mid-call. Handshake framing, malformed-JSON handling, and message
   dispatch are the SDK's own tested surface (§3.1) and are not re-tested here — this layer verifies
   *our* handlers behave correctly once the SDK calls them, not that the SDK's protocol plumbing
   works.
2. **Catalogue against fakes** — every tool exercised the way the equivalent command's tests already
   are, plus the MCP-specific paths: feed adoption refused without approvals and accepted with them;
   `force` refused without `confirm_discard`, refused with a partial list, accepted with a full one,
   not required when every verdict is `SafeToForce`; `project` outside scope; `project` omitted with
   two roots; two calls on one project serialized, two projects in parallel.
3. **Schema validity** — every tool's `inputSchema` parses as JSON Schema draft 2020-12 and every
   property the handler reads is declared in it (reflection over the handler's argument record, in
   the *test* project only, where reflection is fine).
4. **Prompt integrity** — every `dotquay_*` name mentioned in a prompt's markdown exists in the
   registry, so a renamed tool cannot orphan a prompt.
5. **stdout purity** — start the server with `Console.SetOut` redirected to a capturing writer, run
   a tool that would spin a progress reporter under the console host, assert the capture is empty.
6. **Real-binary verification** — not a unit test and not optional (project rule): publish for real
   under NativeAOT, then (a) drive the binary with a scripted stdin session and inspect the raw
   stdout bytes for a BOM and for anything outside frames; (b) run it under
   `npx @modelcontextprotocol/inspector` and exercise every tool once; (c) register it in Claude Code
   with `claude mcp add dotquay -- dotquay mcp --project <a real Godot project>` and walk the three
   prompts end to end, including one refusal → approval → retry loop for a private feed. Each new
   payload type in `McpJsonContext` is a new closed generic and needs (a) at minimum.

**(a) found a real bug on the first Phase 1 run** — `ProcessGitClient.RunGitAsync` redirected git's
stdout/stderr but not its stdin, so a spawned `git` child inherited whatever handle dotquay's own stdin
happened to be. Harmless under the CLI (a real console); under `dotquay mcp`, dotquay's own stdin is the
anonymous pipe the stdio transport holds a pending read on, and a console child spawned that way with no
console of its own deadlocked before it could even print `--version` — hanging every `doctor`/`status`/
`graph` call that shells out to git, forever, with no response ever sent back to the MCP client. Fixed by
redirecting git's stdin too and closing it immediately (clean EOF instead of an open, unread handle). Only
reproduces over the real `mcp` stdio transport on a real OS process tree — neither the fakes-based suite
nor the in-process handler-conformance harness (test layer 1) can see it, since the test host itself still
has a real console. Regression test: `tests/Dotquay.Tests/Git/ProcessGitClientRealProcessTests.cs`, which
spawns the real apphost exe's `mcp` subcommand over piped stdio and drives it with raw JSON-RPC.

## 9. Phasing

| Phase | Ships | Exit criterion |
|---|---|---|
| **0 — spike** | Throwaway console app, two variants (§3.1), *not merged* | **Done, 2026-09-16.** Verdict: adopt `ModelContextProtocol` 2.2.0 on its low-level handler API — both variants published AOT-clean, SDK chosen for the primitives it gives for free (§3.1). |
| **1 — read-only server** | `Dotquay.Mcp` registries + `McpServerBuilder`, the eight read tools, resources, `dotquay mcp`, `UpdateNotice` quiet list, `JsonErrorClassifier` move | **Done, 2026-09-17.** Inspector (`--cli`) lists all eight tools against the published AOT binary; a raw JSON-RPC driver completed `initialize` -> `tools/list` -> `tools/call` (`dotquay_status`, `dotquay_doctor`) -> `resources/list` with byte-clean stdout (no BOM, no CRLF, every line valid JSON). Test layers 1, 3, 5 green (1057/1057, including a new real-process regression test for a git-subprocess-stdin deadlock the AOT run surfaced -- see below). Releasable on its own. |
| **2 — guarded writes** | The eleven write tools, `IWriteGuard` with three policies, `ProjectGate`, progress, cancellation | **Done, 2026-09-17.** Test layer 2 green (1075/1075) -- including `tests/Dotquay.Tests/Mcp/WriteToolsRealProcessTests.cs`, a permanent real-process regression test that spawns the real apphost's `mcp` subcommand over piped stdio against a real local git repo (a `file://` remote, no network) and walks both flows for real: `dotquay_add` refused `needs_confirmation`/`source_approval` for an addon-declared private feed, then succeeds once the same feed is named back via `approved_source_urls`; `dotquay_update --force` refused `needs_confirmation`/`force_confirmation` once a hand-edited installed addon makes it dirty, then succeeds once the addon is named back via `confirm_discard`. Re-ran the same test against a real `dotnet publish src/Dotquay.Cli -c Release -r win-x64 -p:PublishAot=true` binary via a `DOTQUAY_TEST_CLI_EXE` override (0 build/publish errors beyond the pre-existing Tomlyn trim warnings; genuinely native, no runtime DLLs alongside the exe) -- both loops walked cleanly on the AOT binary, satisfying this phase's exit criterion directly. |
| **3 — prompts + roots** | Three prompts, client `roots` integration, `--log` | **Done, 2026-09-17.** Test layer 4 green (1090/1090). Verified against the published AOT binary via the official MCP Inspector CLI (`npx @modelcontextprotocol/inspector --cli`, the same tool this exit criterion names) rather than a hand-rolled JSON-RPC driver: `tools/list` (19 tools), `prompts/list` (all three), `prompts/get` for each with placeholder substitution and no leftover `{{...}}`, a missing-required-argument refusal returning a clean JSON-RPC error instead of a blank prompt, a `tools/call` round trip, and `--log <file>` confirmed to open and write to the named file on process start (checked via a direct piped spawn, since the Inspector CLI's own `--log-level` option prefix-collides with dotquay's `--log` and swallows it when routed through that tool — a test-harness quirk, not a dotquay bug). Roots negotiation (client-supplied project root widening scope on the very first tool call, plus `notifications/roots/list_changed` resync) is covered at the protocol layer by `RootsIntegrationTests.cs` over the in-process harness, matching Phase 1/2's own tiering: real-stdio verification here was for the specific things the fakes and in-process harness cannot see (does the AOT binary reply over real OS pipes at all, does its stdout hold byte-clean UTF-8), not for negotiation logic the SDK already owns. Also fixed in passing: a pre-existing mojibake em-dash (source files saved with a UTF-8-decoded-as-Latin-1-then-re-encoded string literal) in 15 Phase 1/2 tool description strings, confirmed corrected at the raw UTF-8 byte level in the AOT binary's own JSON-RPC output. |
| **4 — docs** | SPEC.md §20 "MCP server (v0.18)", TUTORIAL section, README status line, version bump to 0.18 | **Done, 2026-09-17.** SPEC.md §20 written (contract, tool catalogue, write guard, progress/cancellation/logging, resources, prompts, and a §20.6 connection tutorial) and cross-linked from README's Quick start. README's Status section rewritten to state v0.18/1090 tests instead of the stale v0.1-v0.4/261 claim, and its Commands table gained an `mcp` row; Project layout gained a `Dotquay.Mcp` entry. Version bumped to 0.18.0 in both `Dotquay.Cli.csproj` (the `DotquayVersion.Current`/`serverInfo.version` source) and `plugin.cfg` (lockstep precedent, `75212d3`). Re-verified against the published AOT binary via the Inspector CLI that `serverInfo.version` now reports `0.18.0`. |

Each phase is its own MR and its own release; CI's `changes: src/**` gate already cuts one per green
push. Phase 1 alone is useful — an agent that can *read* a project accurately already stops guessing
at command lines.

## 10. Code changes by project

**`src/Dotquay.Mcp/`** (new; `Dotquay.Mcp` namespace; `PackageReference` on `ModelContextProtocol`
2.2.0 — pinned, not floated, per §3.1 — and `Microsoft.Extensions.Hosting`; `PublishAot` inherited
from the host): `Json/DotquayMcpJsonContext.cs` (our own source-generated context for every
`JsonEnvelope<T>` closure the MCP layer emits — separate from `DotquayJsonContext`, per §3), `Tools/`
(`IMcpTool`, `ToolRegistry`, one file per tool, each producing a `Tool`/`CallToolResult` by hand —
no `[McpServerTool]`, no `WithToolsFromAssembly`, per §3.1), `Guards/` (`IWriteGuard`, `IWritePolicy`,
`SourceApprovalPolicy`, `GeneratorPrivacyPolicy`, `ForceConfirmationPolicy`), `Scope/`
(`IProjectScope`, `ProjectScope`, `ProjectGate`), `Resources/`, `Prompts/` (+ embedded `.md`),
`McpProgressReporter.cs`, `McpServerBuilder.cs` (the one place that calls `AddMcpServer` and every
`With*Handler`, mirroring `WorkflowFactory`'s role — and the place a future SDK version bump gets
re-verified against the Phase 0 spike before the pin moves).

**`src/Dotquay.Cli/`**: `Commands/McpCommand.cs` (new, ~60 lines); `Program.cs` registers it;
`UpdateNotice.IsEligible` excludes `mcp`; `InspectorFactory` and `WorkflowFactory` become
`internal`-visible to `Dotquay.Mcp` **or** — cleaner — `McpCommand` builds the workflow/inspector
itself and hands them to `McpServerBuilder`, so `Dotquay.Mcp` never constructs real infrastructure
(the same boundary that keeps Core free of it); `JsonOutput.Fail`'s classification switch moves to
`Dotquay.Core.Json.JsonErrorClassifier` and `JsonOutput` calls it. `Dotquay.Cli.csproj` gains a
`ProjectReference` to `Dotquay.Mcp`. **Nothing else** — no other command changes.

**`src/Dotquay.Core/`**: `Json/JsonErrorClassifier.cs` (moved logic, no behaviour change). Possibly
nothing else; if Phase 2 finds it needs a `StatusResult` field that isn't there, it is added to
Core with a `--json` field and a plugin check, per the SPEC.md §18 rule.

**`tests/Dotquay.Tests/Mcp/`**: as §8. **`Dotquay.sln`**, **`.gitlab-ci.yml`**: the new project
is under `src/**`, so the release gate already covers it; `build:*` publish `src/Dotquay.Cli`, which
now pulls `Dotquay.Mcp` in transitively. No pipeline change.

## 11. Deferred, with the reason

- **`dotquay mcp install --client <claude-code|claude-desktop|cursor>`** — writes the server entry
  into the client's config. Useful for the reason `self notify` was (a setting whose only interface
  is a text editor goes unused), but each client's config format and location is a moving target,
  and the value of v1 is not gated on it. Revisit after real usage shows which clients people use.
- **Elicitation** for feed adoption and `--force` — the protocol's own mechanism for "ask the human
  mid-call". Strictly better UX than refuse-and-retry once clients support it broadly; the
  `IWritePolicy` seam is where it plugs in, replacing a refusal with a question.
- **HTTP transport** — only if a client that cannot spawn processes turns up.
- **`dotquay_link`** — see §2. Would need a scope rule for the *target* path as well as the project,
  and a real argument for why an agent should choose it.

## 12. Open questions

- **OQ-1 Protocol version.** Declare the newest spec revision the spike targets and accept the
  client's if older; record the exact string once Phase 0 pins it.
- **OQ-2 TTY guard.** Should `dotquay mcp` refuse to start when stdin is a terminal (a human who typed
  it by accident sees a hung prompt)? Proposed: yes, print one line explaining it is meant to be
  spawned by an MCP client, exit 2. Cheap, and it is the first thing a confused user will do.
  **Resolved 2026-09-17:** implemented as proposed — `TtyGuard.ShouldRefuse` checks
  `Console.IsInputRedirected` in `McpCommand`, refusing on stderr with exit 2 before anything else
  runs. The decision is a pure function of one bool (`TtyGuardTests.cs`); a real interactive TTY is
  not something an automated test can reproduce, so only that boolean is unit-tested.
- **OQ-3 Tool name prefix.** `dotquay_status` vs `dotquay.status`. Underscore is what most clients
  render cleanly and what the spec's examples use; revisit only if a client chokes on it.
- **OQ-4 Read tools under the gate.** Serializing reads behind `ProjectGate` (§4.4) is the safe
  default; if an agent's polling `dotquay_status` during a long `dotquay_update` turns out to matter,
  reads could take a shared lock instead. Not worth the complexity until observed.
