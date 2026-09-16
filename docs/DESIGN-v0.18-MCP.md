# DESIGN v0.18 — dotquay as an MCP server

Status: proposed, not implemented. Decisions in §1 were taken with kani on 2026-09-16; everything
after that is the plan those decisions imply and is open to revision until Phase 0 (§9) has run.

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
  Dotquay.Mcp    StdioTransport : IMcpTransport            (framing; the only thing touching stdout)
                 McpServer                                 (initialize, ping, dispatch, cancellation)
                 ├── ToolRegistry  : IToolRegistry         (tools/list, tools/call)
                 │     └── one IMcpTool per operation      (schema + annotations + handler)
                 ├── ResourceRegistry : IResourceRegistry  (resources/list, resources/read, templates)
                 ├── PromptRegistry : IPromptRegistry      (prompts/list, prompts/get)
                 ├── ProjectScope  : IProjectScope         (which directories a call may name)
                 ├── WriteGuard    : IWriteGuard           (the "would this have prompted?" policy chain)
                 └── ProjectGate                           (one operation in flight per project)
                                    │
  Dotquay.Core   DependencyWorkflow / OverrideWorkflow / SourceWorkflow / JsonMapper / *Json payloads
```

Three layers, each replaceable behind an interface and each tested on its own:

**Protocol** (`Dotquay.Mcp.Protocol`): JSON-RPC 2.0 message types, the MCP handshake, request
dispatch, `notifications/cancelled`, `notifications/progress`, `notifications/message`. Knows nothing
about dotquay. Serialized through its own `McpJsonContext` — **not** added to `DotquayJsonContext`,
which is the plugin's wire contract and should not grow types the plugin will never see.

**Catalogue** (`Dotquay.Mcp.Tools`, `.Resources`, `.Prompts`): one class per tool, each declaring its
name, description, input schema, annotations, and a handler that takes typed arguments and returns a
`ToolResult`. Knows dotquay; knows nothing about stdin.

**Host** (`Dotquay.Cli.Commands.McpCommand`): parses `dotquay mcp [--project <dir>]...
[--log <file>]`, builds the real `DependencyWorkflow` through `WorkflowFactory` exactly as every other
command does, wires a `StdioTransport`, and runs the server until stdin closes.

### 3.1 Why a hand-rolled protocol layer is the default, and what would change that

The official `ModelContextProtocol` package is the obvious choice and is the wrong default *for this
project* until proven otherwise. Its tool discovery (`WithToolsFromAssembly`) is reflection-based,
its schema inference goes through `Microsoft.Extensions.AI`, and this codebase has already lost
Spectre.Console.Cli and NuGet.Protocol to exactly the failure mode that implies: green under JIT,
dead in the published NativeAOT binary (docs/SPRINT-01.md; SPEC.md §12 OQ-1/OQ-6). The SDK has an
AOT publish test in its own CI now (csharp-sdk#131 / #1246), which is encouraging and not evidence
about *our* payload types.

The surface a stdio tool server actually needs is small — `initialize`, `notifications/initialized`,
`ping`, `tools/list`, `tools/call`, `resources/list`, `resources/templates/list`, `resources/read`,
`prompts/list`, `prompts/get`, `notifications/cancelled`, `notifications/progress`,
`notifications/message`. Thirteen message shapes, one framing rule, zero reflection if the input
schemas are written as literal JSON rather than inferred from C# parameter types. That is a few
hundred lines we own completely, serialize through a source-generated context we already know how to
write, and can test with an in-memory transport pair the way the rest of the suite tests everything
else.

So: **Phase 0 is a measurement, not a debate** (project rule: probe, don't recall). A throwaway
console app publishes under real NativeAOT twice — once on the official SDK with an explicit
`JsonSerializerContext` and a hand-written schema, once on the hand-rolled layer — and each is driven
through `initialize` → `tools/list` → `tools/call` returning a real `JsonEnvelope<StatusJson>` with a
non-null `Explanation`. The SDK is adopted only if it passes that with **zero** trim/AOT warnings
attributable to it. Otherwise the hand-rolled layer ships, `IMcpTransport`/`McpServer` stay as the
seam, and the SDK can be revisited when its AOT story is measured rather than promised.

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
3. **`Console.Out` itself.** `StdioTransport` writes to `Console.OpenStandardOutput()` wrapped in a
   `StreamWriter(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))`, never to `Console.Out`,
   and reads `Console.OpenStandardInput()` the same way. This is the BOM lesson from the v0.7
   credential store applied pre-emptively: on Windows, `Console.Out`'s encoding follows the codepage
   and a BOM glued onto the first frame is exactly the kind of bug 551 passing tests cannot see.

A guard test (§8) starts the server with a fake `Console.Out` swapped in via `Console.SetOut` and
asserts that nothing reached it — so any future `Console.WriteLine` added to a Core path the server
calls fails a test instead of a user's session.

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
  answer with no configuration.
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
them via `logging/setLevel`.

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

1. **Protocol conformance** — `InMemoryTransportPair` (two linked `IMcpTransport`s, no pipes) drives
   `McpServer` through golden JSON-RPC exchanges checked byte-for-byte: handshake and capability
   negotiation, unknown method → `-32601`, malformed frame → `-32700`, `tools/list` shape,
   `notifications/cancelled` mid-call, progress token round-trip, a request arriving before
   `initialized`.
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

## 9. Phasing

| Phase | Ships | Exit criterion |
|---|---|---|
| **0 — spike** | Throwaway console app, two variants (§3.1), *not merged* | Written verdict in this doc's §3.1: SDK or hand-rolled, with the AOT publish log attached to the MR. Half a day to a day. |
| **1 — read-only server** | `Dotquay.Mcp` protocol + registry, the eight read tools, resources, `dotquay mcp`, `UpdateNotice` quiet list, `JsonErrorClassifier` move | Inspector shows all eight tools; Claude Code can call `dotquay_status` and `dotquay_doctor` on a real project from the AOT binary. Test layers 1, 3, 5 green. Releasable on its own. |
| **2 — guarded writes** | The eleven write tools, `IWriteGuard` with three policies, `ProjectGate`, progress, cancellation | Test layer 2 green; a real private-feed refusal/approval loop and a real `confirm_discard` loop walked on the AOT binary. |
| **3 — prompts + roots** | Three prompts, client `roots` integration, `--log` | Test layer 4 green; each prompt walked once in Claude Code. |
| **4 — docs** | SPEC.md §20 "MCP server (v0.18)", TUTORIAL section, README status line, version bump to 0.18 | Docs merged in the same MR as Phase 3 so the shipped binary and its description agree. |

Each phase is its own MR and its own release; CI's `changes: src/**` gate already cuts one per green
push. Phase 1 alone is useful — an agent that can *read* a project accurately already stops guessing
at command lines.

## 10. Code changes by project

**`src/Dotquay.Mcp/`** (new; `Dotquay.Mcp` namespace; `IsAotCompatible=true`, `PublishAot`
inherited by the host): `Protocol/` (JSON-RPC types, `McpJsonContext`, `McpServer`,
`IMcpTransport`, `StdioTransport`), `Tools/` (`IMcpTool`, `ToolRegistry`, one file per tool,
`ToolResult`), `Guards/` (`IWriteGuard`, `IWritePolicy`, `SourceApprovalPolicy`,
`GeneratorPrivacyPolicy`, `ForceConfirmationPolicy`), `Scope/` (`IProjectScope`, `ProjectScope`,
`ProjectGate`), `Resources/`, `Prompts/` (+ embedded `.md`), `McpProgressReporter.cs`,
`McpServerBuilder.cs` (the one place that assembles all of it, mirroring `WorkflowFactory`'s role).

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
- **OQ-3 Tool name prefix.** `dotquay_status` vs `dotquay.status`. Underscore is what most clients
  render cleanly and what the spec's examples use; revisit only if a client chokes on it.
- **OQ-4 Read tools under the gate.** Serializing reads behind `ProjectGate` (§4.4) is the safe
  default; if an agent's polling `dotquay_status` during a long `dotquay_update` turns out to matter,
  reads could take a shared lock instead. Not worth the complexity until observed.
