# v0.24 - Verbose / trace diagnostics

Status: shipped

## Problem

When a dotquay command fails or is slow, the user sees one sentence. Finding out *which* git
command failed, against *which* URL, with *which* credential, after *how long* meant attaching a
debugger or adding `Console.Error.WriteLine` by hand. Field reports (BotV2, the ssh/known_hosts
work in v0.15, the GitLab 401-vs-404 ambiguity in v0.7) each needed that loop.

## What it does

| Switch | Level | Adds |
|---|---|---|
| `--verbose`, `-v` | verbose | every git/dotnet/cmd command line with exit code and duration, every HTTP request (method, URL, status, duration, whether a credential was attached), every pipeline step, cache hits/misses, resolver decisions (which tag a constraint matched), destructive file operations (delete/move tree, link), full exception chains, and a header naming the build (version, JIT vs NativeAOT, OS) and the resolved `--project` |
| `--trace` | trace | all of the above, plus each subprocess's captured stdout/stderr (capped at 4000 chars per stream) and every individual file write |
| `DOTQUAY_VERBOSE=1` / `=trace` | either | the same, for callers that can't add a flag: git invoking `credential-helper`, the Godot plugin, an MCP client's configured command, a CI script |

Both options are recursive globals (like `--project`), so they work on every command and in either
position: `dotquay -v update` and `dotquay update --verbose`. Flags and the variable combine to the
more verbose of the two.

```
[   0.031s] dotquay  | dotquay 0.18.0 (NativeAOT) on Microsoft Windows 10.0.26100 X64
[   0.032s] dotquay  | project: F:\projects\godot\BotV2
[   0.120s] step     | [1/3] resolving vbeami
[   0.121s] git      | $ git ls-remote --tags --heads https://gitlab.kemomi.de/kani/vbeami.git
[   0.702s] git      | exit 0 after 581 ms
[   0.703s] resolve  | vbeami: '~0.4' against 12 tag(s) -> v0.4.2 (3f1c...)
[   0.704s] cache    | hit https://gitlab.kemomi.de/kani/vbeami.git@3f1c09ab12 -> C:\Users\kani\.dotquay\cache\...
[   0.950s] http     | GET https://gitlab.kemomi.de/api/v4/projects/42/packages/nuget/index.json [auth: Basic] -> 200 OK after 88 ms
[   1.402s] dotquay  | exit 0 after 1371 ms
```

## Invariants

1. **stderr only.** stdout belongs to `--json`'s single document and to `dotquay mcp`'s JSON-RPC
   stream. Core never chooses a stream (`IDiagnosticSink`, same split as `IProgressReporter`); the
   CLI sink writes `Console.Error`. Guarded by `VerboseRealProcessTests` against the real apphost.
2. **Safe to paste.** `SecretRedactor` masks URL passwords (and any user-info on http(s)), secret
   query parameters, `Authorization`/`PRIVATE-TOKEN`-shaped text, `--token <value>`, and known
   token shapes (`glpat-...`, `ghp_...`). HTTP headers are never logged at all - only whether a
   credential was attached and its scheme. ssh users (`git@host`) are kept: they are what an ssh
   failure needs to show.
3. **Free when off.** Messages go through an interpolated-string handler, so a disabled line costs
   one comparison and evaluates no holes. `DiagnosticHttpHandler` and `ProcessTrace` are
   pass-throughs.
4. **Plays with the spinner.** Each line pauses `ConsoleProgressReporter` so lines scroll above it.
5. **A host that redirects stderr must drain it.** Found verifying against the AOT binary: under
   `DOTQUAY_VERBOSE=trace`, `WriteToolsRealProcessTests` hung because its harness redirected the
   MCP server's stderr and never read it. The pipe buffer filled and the server blocked mid-write.
   That was already latent (the `dotquay mcp` request trace goes to stderr without `--log`); both
   real-process harnesses now drain stderr. Real MCP clients and the Godot plugin
   (`OS.execute_with_pipe`) read it.

## Design choices

- **Ambient sink (`AsyncLocal`) instead of constructor injection.** The interesting call sites
  (`ProcessGitClient`, `ProcessPatchTool`, `ProcessDotnetCliRunner`, the three HTTP clients,
  `SystemFileSystem`) are constructed in a dozen places across Core, CLI and `Dotquay.Mcp`, several
  inside `DependencyWorkflow`. Injection would touch most constructors for an off-by-default feature,
  and the next `new ProcessGitClient()` that forgot the argument would go silently dark.
  `AsyncLocal` (not a plain static) keeps parallel tests isolated and flows through `await`,
  `Task.Run` and `HttpClient` handler chains. Installed once in `Program.cs`, before the update
  check starts, so no command has to opt in.
- **One shape per kind of site.** `ProcessTrace` for every subprocess, `DiagnosticHttpHandler` (via
  `DiagnosticHttp.CreateClient()`) for every HTTP client, `DiagnosticProgressReporter` reusing the
  existing progress messages as the step skeleton rather than a second set of stage announcements.
- **Two levels, not five.** Verbose answers "what did it do"; trace answers "what did the tools
  say". Anything finer is grep.

## Not done

- The Godot plugin has no UI toggle; `DOTQUAY_VERBOSE` set in the editor's environment works today
  because the plugin reads stdout and stderr separately (`OS.execute_with_pipe`).
- `dotquay mcp --verbose` logs to stderr alongside the existing `--log` request trace; the two are
  not merged into one file.
