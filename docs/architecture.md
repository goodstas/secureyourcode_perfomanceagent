# SecureYourCode — Architecture & Decisions

Living record required by `docs/IMPLEMENTATION_PLAN_HACKATHON_v1.5.md` (§2, §6, §7 item 13). Every pinned version, probe result, decision and fallback goes here.

This is a shared team repository (several developers, each on their own machine and GitHub account). **Record nothing machine- or account-specific here**: no absolute paths from anyone's machine, no user names, no plan or quota figures. Use the placeholders below.

## Placeholders and per-developer locations

| Name | Meaning |
|---|---|
| `AppWorkspace` | This repository's root, wherever a developer cloned it |
| `StateRoot` | `<user home>/.secureyourcode`, resolved at runtime from the user profile (never a literal `~`). Override with the `SECUREYOURCODE_STATE_ROOT` environment variable |
| `RepoPath` | `<StateRoot>/demo-repo` (materialized per developer in H0) |
| Graphify interpreter | `<StateRoot>/graphify-venv/Scripts/python.exe` (Windows) or `<StateRoot>/graphify-venv/bin/python` (macOS/Linux) |
| Standalone Copilot CLI | `<StateRoot>/copilot-cli/node_modules/.bin/copilot[.cmd]`, used only for `copilot login` |
| Copilot client state | `<StateRoot>/copilot` (`CopilotClientOptions.BaseDirectory`, which sets `COPILOT_HOME`) |

Per-developer setup is automated by `tools/setup.py`; see `README.md`.

## Pinned versions

| Component | Version | Notes |
|---|---|---|
| .NET SDK | any 10.0.x | `global.json`: `10.0.100`, `rollForward: latestFeature` |
| `GitHub.Copilot.SDK` | 1.0.15 | Host. Its MSBuild targets download the pinned Copilot runtime (`CopilotCliVersion` 1.0.89) from the `github/copilot-cli` release, verify it against `SHA256SUMS.txt`, and copy it to `bin/<cfg>/net10.0/runtimes/<rid>/native/`. Which binary the SDK actually launches is verified in H1 |
| Copilot CLI (standalone) | 1.0.89 | Installed by `tools/setup.py` with `npm install --prefix <StateRoot>/copilot-cli`; never global. Must match the SDK's `CopilotCliVersion` |
| `Microsoft.CodeAnalysis.CSharp` | 5.0.0 | Analyzer (`PrivateAssets=all`) and host. 5.0 is the compiler in the first .NET 10 SDK (10.0.100); an analyzer must not reference a newer Roslyn than the compiler that loads it, so this works on every developer's .NET 10 SDK |
| `Microsoft.CodeAnalysis.Analyzers` | 3.11.0 | Analyzer; `EnforceExtendedAnalyzerRules=true` |
| `Microsoft.CodeAnalysis.CSharp.Analyzer.Testing` | 1.1.4 | Tests (framework-agnostic `DefaultVerifier`) |
| `Microsoft.CodeAnalysis.CSharp.Workspaces` | 5.0.0 | Tests; same Roslyn version as the analyzer |
| xUnit | 2.9.3 (runner 3.1.4, Test SDK 17.14.1) | From the .NET 10 `xunit` template |
| Python | 3.10 or later | Base interpreter for the Graphify venv (`graphifyy` requires ≥ 3.10) |
| `graphifyy` | 0.9.71 with `[mcp]` extra | Installed by `tools/setup.py` into `<StateRoot>/graphify-venv`. Provides the `graphify` CLI, `graphify-mcp`, and the `graphify.serve` module |
| Husky.Net (`husky`) | 0.9.1 | Installed as a local tool inside each developer's demo repo in H3 (`dotnet new tool-manifest` + `dotnet tool install husky --version 0.9.1`) |

## Team decisions (adaptations of the plan for a shared repository)

- **StateRoot is per developer.** Default `<user home>/.secureyourcode`, override `SECUREYOURCODE_STATE_ROOT`. The host resolves it the same way at runtime; `appsettings.json` never contains an absolute path.
- **OS.** Plan §2 assumes one demo machine's OS. Team members may use different OSes, so the host detects the OS at runtime and installs the matching hook template (`task-runner.windows.json` or `task-runner.unix.json`); both templates are maintained. The OS of the machine used for the recorded live demo run is noted in the H7 record.
- **Copilot accounts.** Each developer signs in with their own GitHub account (`python tools/setup.py --login`); credentials stay in the OS credential store and the sign-in is scoped to that developer's `<StateRoot>/copilot`. Model default is `"auto"`, the only model guaranteed on every plan.
- **Analyzer reference in the demo repo.** The `<Analyzer Include=...>` path (plan §4.3) is absolute and machine-specific, so it is written only into each developer's materialized demo repo under `StateRoot`, never into `test-assets/demo-shop`.
- **Environment checks** live in `tools/setup.py` and `tools/copilot-smoke/` (a small console app, not part of `SecureYourCode.slnx` and not app code).
- Solution file uses the .NET 10 default `SecureYourCode.slnx`.
- The plan file was moved to `docs/IMPLEMENTATION_PLAN_HACKATHON_v1.5.md`, where `AGENTS.md` expects it. `docs/IMPLEMENTATION_PLAN_FULL.md` is not in the repository; the hackathon plan is self-sufficient.

## H0 — Setup (2026-09-29)

### Decisions

- **Host infrastructure** lives in `src/SecureYourCode.Agent/Infrastructure/` (options, paths, access token, OS detection, process runner, demo-repo materializer). Plan §3 lists no folder for these startup concerns; this is the simplest addition.
- **Configuration**: `appsettings.json` has `Urls: http://127.0.0.1:9876` and a `SecureYourCode` section (`StateRoot`, `AppWorkspace`, `Model`, empty or `auto` by default). `launchSettings.json` uses the same URL. The content root is set to the binaries' folder, so `appsettings.json` (and the binding) loads from any working directory. Found in verification: without this, starting the DLL from another directory silently bound to `localhost:5000`.
- **StateRoot resolution**: `SecureYourCode:StateRoot`, then `SECUREYOURCODE_STATE_ROOT`, then `<user home>/.secureyourcode`. It must be absolute, and StateRoot and AppWorkspace must not contain each other; otherwise the host refuses to start. **AppWorkspace**: `SecureYourCode:AppWorkspace`, or found by walking up from the binaries to `SecureYourCode.slnx`.
- **Startup order** (`Program.cs`): resolve paths → `EnsureAccessToken` (`LocalAccessToken.Ensure`) → log OS and paths → materialize the demo repo → listen. Startup failures (malformed token, bad paths, git failure) are thrown as `InvalidOperationException` with a clear message, so the host never starts in a bad state.
- **Access token**: 32 bytes from `RandomNumberGenerator`, lowercase hex (64 chars), written to `<StateRoot>/access-token` via temp file + rename. Owner-only: on Windows a protected DACL with a single full-control entry for the current user; on Unix mode `0600` at creation. Surrounding whitespace is trimmed on load. `Matches` uses `CryptographicOperations.FixedTimeEquals` and requires exactly one header value. The token is never logged.
- **OS detection**: `HostPlatform` detects the OS at runtime, logs it at startup, and selects `task-runner.windows.json` or `task-runner.unix.json` for H3.
- **Demo repo materialization** (`DemoRepoMaterializer`, runs at every start): if `RepoPath` exists with a `.git` folder it is left untouched; if it exists without one, the host refuses to start. Otherwise `test-assets/demo-shop` is copied (skipping `bin`, `obj`, `.vs`) into `<StateRoot>/demo-repo.tmp-<guid>`, the §5 `.gitignore` is written, and `git init --initial-branch=main`, `git add --all` and `git commit` run there, and the folder is then renamed to `RepoPath`. The commit uses a fixed app identity (`SecureYourCode <secureyourcode@localhost>`) and `commit.gpgsign=false`, because it is an app-owned, never-pushed repo that must not depend on each developer's git configuration. To pick up later changes to `test-assets/demo-shop`, delete `RepoPath` and restart.
- **Demo project** (`test-assets/demo-shop/`, one `net10.0` web API project, `DemoShop.csproj` at its root so repo-relative paths are `Services/...`). The §5 seams exist exactly as named. Additions beyond the named seams: `ICustomerRepository.GetByIdsAsync` (the batched query N1 needs; `CountingCustomerRepository` counts it as one call), `IProductRepository`/`InMemoryProductRepository` (for P5 and N3), and `BatchedNotificationService.NotifyAllInBatchesAsync` (N2, a different type from the `NotificationService.NotifyAllAsync` seam). The event cases keep the hub and handler in one file (`Services/OrderEvents.cs` for P4, `Services/InventoryEvents.cs` for N4). Demo code carries no comments that label the seeded issues. It has no EF Core dependency: P1 and P5 use `*Repository` types; the EF Core part of `PERF001` is covered by analyzer unit tests in H2.
- **Ground truth** (`test-assets/ground-truth.json`, written before any agent code; never to be changed after observing results). Line ranges: P1, P2, P5, N1, N2 cover the method span; P3 covers the static field declaration through `GetOrAdd` (field plus growth without removal is the mechanism); P4 and N4 cover the hub and handler types; N3 covers the `ProductCache` class. P4's `ruleId` is recorded as `LLM-MEM` (specialist-only).

### Verification

| Check | Result |
|---|---|
| `dotnet build SecureYourCode.slnx` | pass: 0 warnings, 0 errors |
| `dotnet test SecureYourCode.slnx` | pass: 1/1, only the template placeholder; no H0 unit tests (the plan requires none for H0) |
| Host starts, started as the DLL from another working directory (no `launchSettings.json`) | pass: `Now listening on: http://127.0.0.1:9876`; the only listening socket is `127.0.0.1:9876`; `GET /` returns 404 (no endpoints yet) |
| Host starts via `dotnet run --project src/SecureYourCode.Agent` | pass: `http://127.0.0.1:9876`, environment Development |
| First start | created `access-token` (64 lowercase hex chars; `icacls` shows a single full-control entry for the current user) and materialized the demo repo |
| Restart | the token file is unchanged (same hash); demo repo reported present and not recreated |
| Refuses to start | pass for: malformed token, empty token, StateRoot inside AppWorkspace, relative StateRoot (each an `InvalidOperationException` with a clear message; the bad token file was not modified; nothing was created inside AppWorkspace) |
| Demo repo | one commit `Initial demo shop` by the app identity; the tracked files are the demo sources plus `.gitignore` with the exact §5 content; no analyzer reference |
| Demo project builds normally | pass in the materialized repo: 0 warnings, 0 errors; `git status --porcelain` is empty after the build (bin/obj ignored, so the fingerprint is unaffected) |
| OS | detected at runtime and logged; H0 verified on Windows x64 |

## H1 — Compatibility probe (2026-09-29)

Executable discovery against GitHub.Copilot.SDK 1.0.15 (runtime 1.0.89) and graphifyy 0.9.71. Probe sources: `tools/h1-probe/` (C# console app: `dotnet run --project tools/h1-probe -- <step> [--mode cli|empty] [--agent-names dash|mcpdash|raw] [--filter-names dash|mcpdash|raw] [--client-wd neutral|repo] [--env-context default|host]`) and `tools/h1-probe/graphify_mcp_probe.py`. Sanitized evidence (home directory shown as `~`) is written per developer to `<StateRoot>/probe/evidence/` and is not committed. The API surface was established by reflecting over the installed SDK assembly and reading its XML docs, not assumed. **No plan §4.5 fallback was needed.** Model: `auto` resolved to `claude-sonnet-5` in every probe run; every model call reported `Cost = 1`.

### Recorded values

| Plan item | Value |
|---|---|
| Send-and-wait method (step 1) | `CopilotSession.SendAndWaitAsync(MessageOptions options, TimeSpan? timeout, CancellationToken ct)`, returning `AssistantMessageEvent` (`Data.Content`) |
| Working-directory mechanism (step 2) | **Session option `SessionConfig.WorkingDirectory = RepoPath`.** Proven with the client's `WorkingDirectory` pointing at an empty folder: `view` read `Services/OrderSummaryService.cs`, `grep` found `GetByIdsAsync`, `glob` listed `Services/*Notification*.cs`, all in RepoPath |
| `graphifyOutputRelativePath` (step 3) | `graphify-out/graph.json`, via `graphify extract <RepoPath> --code-only --no-cluster --out <folder under StateRoot>`. RepoPath stayed byte-identical (`git status --porcelain --ignored` unchanged, no `graphify-out/`) |
| `--no-cluster` | **Used.** The four needed queries return the same results on a `--no-cluster` graph as on a clustered one (only `community` is empty). The formats differ: `--no-cluster` writes `{nodes, edges, hyperedges, input_tokens, output_tokens, extracted_sources}`; clustered writes node-link `{directed, multigraph, graph, nodes, links, hyperedges, built_at_commit}`. The MCP server loads both |
| `graphStructurePredicate` | Root is a JSON object; `nodes` is a non-empty array whose elements are objects with a string `id`; `edges` is an array. (Derived from the real `--no-cluster` output: 107 nodes, 163 edges for the demo repo; node `source_file` values are repo-relative, e.g. `Services/OrderSummaryService.cs`) |
| Graphify MCP server | Started per session by the SDK: `McpStdioServerConfig { Command = <Graphify interpreter>, Args = ["-m", "graphify.serve", <graph.json>], Tools = graphifyServerToolNames, WorkingDirectory = <neutral folder under StateRoot> }` |
| Tools the running Graphify server exposes | `query_graph, get_node, get_neighbors, get_community, god_nodes, graph_stats, shortest_path, list_prs, get_pr_impact, triage_prs` (from a real MCP `list_tools`, and again from `session.Rpc.Mcp.ListToolsAsync("graphify")`) |
| **`graphifyServerToolNames`** (raw, `McpStdioServerConfig.Tools`) | `query_graph`, `get_node`, `get_neighbors`, `shortest_path`. Excluded: `list_prs`, `get_pr_impact`, `triage_prs` (they run the external `gh` CLI and reach the network, `graphify/prs.py`); `get_community` (needs clustering); `god_nodes`, `graph_stats` (not needed) |
| **`graphifyAgentToolNames`** (`CustomAgentConfig.Tools`, step 4) | `graphify-query_graph`, `graphify-get_node`, `graphify-get_neighbors`, `graphify-shortest_path` (`<server-key>-<tool>`). Proven: an explicitly selected agent (`SessionConfig.Agent = "MemoryReviewer"`) produced a real `ToolExecutionStartEvent` with `ToolName = graphify-shortest_path`, `McpServerName = graphify`, `McpToolName = shortest_path`, completed successfully, and its reply contained the real graph path |
| **`graphifySessionFilterToolNames`** (`SessionConfig.AvailableTools`, step 5) | `mcp:graphify-query_graph`, `mcp:graphify-get_node`, `mcp:graphify-get_neighbors`, `mcp:graphify-shortest_path`. Proven in a plain session (no agent), where the allow-list alone decides: Graphify available (model saw 7 tools) and executed. Negative control with the raw names: Graphify absent (3 tools) |
| Built-in tool names (runtime 1.0.89, `client.Rpc.Tools.ListAsync`) | `powershell, read_powershell, stop_powershell, list_powershell, glob, grep, create, edit, skill, view, web_fetch, task, read_agent, list_agents, write_agent`. Read-only: `view`, `grep`, `glob` |
| Permission request exposes the tool kind (step 5) | **Yes.** `PermissionRequest.Kind` plus typed subclasses: `PermissionRequestRead` (`Path`, `ResolvedPath`), `PermissionRequestWrite` (`FileName`), `PermissionRequestShell` (`FullCommandText`), `PermissionRequestUrl` (`Url`), `PermissionRequestMcp` (`ServerName`, `ToolName`, `Args`, `ReadOnly`), `PermissionRequestMemory`, `PermissionRequestCustomTool`, `PermissionRequestHook`, `PermissionRequestExtension*`, `PermissionRequestWorkflow`. Observed: `PermissionRequestMcp.ToolName` is server-qualified (`graphify-shortest_path`), and Graphify reports `ReadOnly = false`, so the handler must not rely on that flag |
| Usage fields (step 6) | `AssistantUsageEvent` fires once per model call; `Model`, `InputTokens`, `OutputTokens`, `Cost` are all populated (`Cost = 1` per call in these runs). `availableToolCount` appears in the event JSON but has no public property in SDK 1.0.15 |
| Plain-session fallback (step 7) | Works: no custom agent, specialist instructions at the top of the prompt, same allow-list, strict handler and Graphify; (a) Graphify executed, (b) `view`/`grep`/`glob` worked, (c) write/shell/URL blocked, RepoPath unchanged. It is usable, but not needed |

### Permission-handler choice

**Strict handler, never `PermissionHandler.ApproveAll`.** It approves only:
- `PermissionRequestRead` whose `ResolvedPath` (or `Path`) lies inside RepoPath;
- `PermissionRequestMcp` with `ServerName == "graphify"`, `ToolName` in `graphifyAgentToolNames`, and no `project_path` argument. Every Graphify tool accepts `project_path`, which loads `<project_path>/graphify-out/graph.json` from anywhere on disk (`graphify/serve.py`, `_resolve_graph_path`).

It denies everything else: write, shell, URL, memory, custom tools, hooks, extensions, workflows, and anything unrecognised. Denials use `PermissionDecision.Reject(...)`. `PermissionDecision` is experimental in SDK 1.0.15, so the host needs `<NoWarn>GHCP001</NoWarn>`.

The RepoPath restriction on reads is load-bearing. In several runs the model tried `view` on paths outside RepoPath (`~/Services/...`, a path under the Copilot folder, the runtime's session-state folder, `C:\`), and the handler denied every one. Handler-only test (write, shell and URL tools deliberately made available): all three attempts produced `shell`, `write` and `url` permission requests, all denied; no file was created and RepoPath stayed clean.

### Client isolation mechanism (step 8)

- **Client:** `CopilotClientOptions { Mode = CopilotClientMode.Empty, BaseDirectory = <StateRoot>/copilot }`. Empty mode disables optional features by default, exposes no tools unless `AvailableTools` is given, defaults `SkipCustomInstructions`/`CustomAgentsLocalOnly` to true, and sets `COPILOT_DISABLE_KEYTAR=1`, so credentials are read only from `<StateRoot>/copilot`.
  **Consequence:** a normal `copilot login` (stored in the OS keychain) is invisible to Empty mode. Developers sign in with `COPILOT_DISABLE_KEYTAR=1` and accept the CLI's "store token in plaintext config file" prompt; `tools/setup.py --login` does this, and `tools/copilot-smoke` now checks Empty mode.
- **Session options set explicitly (never relying on defaults):** `SkipCustomInstructions = true`, `EnableOnDemandInstructionDiscovery = false` (it would otherwise pull `AGENTS.md`/`.github/copilot-instructions.md` in after file views), `EnableConfigDiscovery = false`, `CustomAgentsLocalOnly = true`, `EnableSkills = false`, `EnableFileHooks = false`, `EnableHostGitOperations = false`, `EnableSessionStore = false`, `Memory = { Enabled = false }`.
- **System prompt:** `SystemMessage = { Mode = Customize, Sections = { EnvironmentContext: Replace with host-written text naming the repository root and asking for absolute paths under it; CustomInstructions: Remove } }`. Found in verification: Empty mode strips the ambient environment context, so the model does not know the working directory and guessed wrong paths for `view`. Setting the client's `WorkingDirectory` to RepoPath as well did **not** fix this; the host-written environment context did (no out-of-repo reads afterwards). Removing the `CustomInstructions` section is an extra isolation layer.
- **Discovered instruction locations** (`client.Rpc.Instructions.GetDiscoveryPathsAsync`): user level, under the redirected COPILOT_HOME, `<StateRoot>/copilot/copilot-instructions.md` and `<StateRoot>/copilot/instructions/`; repository level: `.github/copilot-instructions.md`, `AGENTS.md`, `CLAUDE.md`, `GEMINI.md`, `.github/instructions/`, `.claude/rules/`. The user's own `~/.copilot` is not a discovery location while COPILOT_HOME is redirected.
- **Canaries:** planted in a throwaway copy `<StateRoot>/probe-repo` (never the real demo repo), one distinct token per location. That is 9 in total: the 6 repository locations, the parent folder (`<StateRoot>/AGENTS.md`), and both user-level locations, which are inside `<StateRoot>/copilot` and so inside the approved roots. Pre-checks confirmed that none of these files existed; nothing was overwritten. All were removed after every run (post-checks clean).
- **Positive control** (CopilotCli mode, default settings, prompt "List the files in the repository root."): the reply contained 8 canaries (all repository and user-level ones). **The parent-folder canary was not loaded even under defaults**; the runtime does not read above the repository's git root.
- **Isolation test** (final configuration, Empty mode, run as a specialist `MemoryReviewer` and as the critic `VerificationReviewer`): **no canary token in either reply → PASS.** Run twice: before and after adding the host environment context; both passed.
- **Supplementary injection check.** `session.Rpc.Instructions.GetSourcesAsync()` still *lists* the discovered canary files under the final configuration. To tell discovery from injection: same session config, same one-line prompt, fixed model, first-call input tokens without vs with canaries.

  | Run | Final config, Empty (without → with) | Positive control, CopilotCli defaults (without → with) |
  |---|---|---|
  | 1 | 6,086 → 6,081 (−5) | 6,243 → 6,543 (+300) |
  | 2 | 6,083 → 6,083 (0) | 6,248 → 6,544 (+296) |

  Injecting the canaries costs about 300 tokens, and the isolated configuration shows no increase. **Conclusion: discovered instruction files are not injected under the final configuration.** Note: run 1 printed "INCONCLUSIVE" because the probe's pre-set rule demanded an exact 0 delta, and per-session variation is a few tokens (baselines 6,081–6,086); run 2, unchanged, met it.

### Final configuration for H4

```csharp
var client = new CopilotClient(new CopilotClientOptions { Mode = CopilotClientMode.Empty, BaseDirectory = "<StateRoot>/copilot" });
new SessionConfig
{
    WorkingDirectory = RepoPath,
    AvailableTools = ["view", "grep", "glob", "mcp:graphify-query_graph", "mcp:graphify-get_node", "mcp:graphify-get_neighbors", "mcp:graphify-shortest_path"],
    McpServers = { ["graphify"] = new McpStdioServerConfig { Command = GraphifyPython, Args = ["-m", "graphify.serve", graphJsonPath],
                   Tools = ["query_graph", "get_node", "get_neighbors", "shortest_path"], WorkingDirectory = "<neutral folder under StateRoot>" } },
    CustomAgents = [new CustomAgentConfig { Name = "<Reviewer>", Description = "...", Prompt = "...", Infer = false,
                   Tools = ["view", "grep", "glob", "graphify-query_graph", "graphify-get_node", "graphify-get_neighbors", "graphify-shortest_path"] }],
    Agent = "<Reviewer>",
    OnPermissionRequest = strictHandler,
    SkipCustomInstructions = true, EnableOnDemandInstructionDiscovery = false, EnableConfigDiscovery = false, CustomAgentsLocalOnly = true,
    EnableSkills = false, EnableFileHooks = false, EnableHostGitOperations = false, EnableSessionStore = false, Memory = new() { Enabled = false },
    SystemMessage = new() { Mode = SystemMessageMode.Customize, Sections = {
        [SystemMessageSection.EnvironmentContext] = new() { Action = SectionOverrideAction.Replace, Content = "The repository under review is <RepoPath> ..." },
        [SystemMessageSection.CustomInstructions] = new() { Action = SectionOverrideAction.Remove } } },
};
```

With no graph (graph status `none`), omit `McpServers` and the Graphify entries from both tool lists.

### Probe runs (Empty mode unless noted)

| Run | Result |
|---|---|
| `discover` (both modes) | Built-in tools and discovery paths recorded. Empty mode initially **not signed in** (keychain login invisible), resolved by the operator's sign-in with `COPILOT_DISABLE_KEYTAR=1` |
| `basics` (CopilotCli, Empty) | PASS / PASS. Empty mode: the model saw exactly 3 tools |
| `agent` (CopilotCli) first run | FAIL: the strict handler denied the Graphify call because it compared raw tool names; it revealed the server-qualified `PermissionRequestMcp.ToolName`. Handler fixed, then PASS |
| `agent` (Empty) | PASS (7 tools) |
| `secure` (CopilotCli, Empty) | PASS / PASS: (a) Graphify, (b) view/grep/glob, (c) write/shell/URL blocked, RepoPath unchanged; `GetSourcesAsync` empty |
| `secure --client-wd repo` | FAIL: `view` tried an out-of-repo path (denied by the handler); showed the client working directory does not fix path guessing |
| `secure --env-context host` (final configuration) | PASS, no out-of-repo reads |
| `plain` (final configuration) | PASS |
| `plain --filter-names raw` (negative control) | Graphify absent, as expected |
| `handler` | PASS: shell, write and URL requests all denied, no file created |
| `canary` (default context, then final configuration) | PASS / PASS: positive control 8 canaries; isolation test none |
| `inject` ×2 | See table above |

## H2 — Analyzer (2026-09-29)

`src/SecureYourCode.PerformanceAnalyzer` (netstandard2.0, Roslyn 5.0.0, `EnforceExtendedAnalyzerRules`, release tracking in `AnalyzerReleases.*.md`). All three rules are warnings in category `Performance`, and their messages say "potential". **No fallback needed.**

### Rule decisions

- **PERF001** (`QueryInLoopAnalyzer`, CPU & Amplification). Flags an invocation whose syntax lies inside the *body* of an enclosing loop (`ILoopOperation`: for, foreach, while, do). The walk up stops at lambdas and local functions (they run later). A `foreach` collection expression is evaluated once, so it is not flagged.
  Matched APIs:
  - EF Core: `ToList(Async)`, `First(OrDefault)(Async)`, `Single(OrDefault)(Async)`, `Count(Async)` when the receiver implements `IQueryable<T>`; `SaveChanges(Async)` on a `Microsoft.EntityFrameworkCore.DbContext`; `FindAsync` on a `DbContext` or `DbSet<T>`. EF types are matched by metadata name, so the demo needs no EF dependency.
  - Repositories (lower confidence): a call whose receiver type or containing type name ends in `Repository`.
  The diagnostic property `match` is `ef-query` or `repository`. LINQ-to-objects (`List<T>`, non-`IQueryable`) and building an `IQueryable` without executing it are not flagged.
- **PERF003** (`UnboundedTaskFanOutAnalyzer`, Concurrency). `Task.WhenAll(<source>.Select(...))`; a trailing `ToList`/`ToArray`/`AsEnumerable` on the Select is unwrapped. The source is traced back through size-preserving `Enumerable` operators (`Chunk`, `Where`, `Distinct`, `OrderBy*`, `ThenBy*`, `AsEnumerable`, `ToList`, `ToArray`, `Cast`, `OfType`).
  - Input-sized, so flagged: parameters, fields, properties, `await`ed or direct invocation results (query results), and locals whose initializer is one of these (traced up to 4 levels).
  - Not flagged: collection literals, fixed arrays and collection expressions; `Enumerable.Range`/`Repeat` with a constant count; and **any `foreach` iteration variable**. That includes the plan's sequential bounded batch, `foreach (var batch in x.Chunk(n)) await Task.WhenAll(batch.Select(...))`.
  - `x.Chunk(n).Select(...)` directly inside `WhenAll` **is** flagged, as the plan requires.
- **PERF004** (`StaticCollectionGrowthAnalyzer`, Memory & Allocation). It collects, across the compilation, growth operations on `static` fields whose declared type is `Dictionary<,>`, `ConcurrentDictionary<,>` or `List<>`, with the plan's growth sets; dictionary indexer assignment counts as growth, `List<T>` indexer assignment does not. At compilation end it reports those whose field has no removal operation (`Remove`, `TryRemove`, `RemoveAt`, `RemoveAll`, `RemoveRange`, `Clear`) inside the field's containing type, nested types included. The rule therefore carries the `CompilationEnd` custom tag (reported in command-line builds and SARIF, not live in the IDE). Instance fields and other types (e.g. `MemoryCache`) are ignored.

### Wiring into the demo repo

The host's startup now calls `DemoRepoMaterializer.EnsureAnalyzerReferenceAsync` after materialization. It adds `<ItemGroup><Analyzer Include="<AppWorkspace>/src/SecureYourCode.PerformanceAnalyzer/bin/Release/netstandard2.0/SecureYourCode.PerformanceAnalyzer.dll" /></ItemGroup>` to the demo repo's `DemoShop.csproj` and commits it there ("Reference the SecureYourCode analyzer", app identity).
- It is idempotent: an existing correct reference means no commit.
- It repairs a stale path (e.g. after the repository moved) and commits the repair.
- It refuses to start if the Release analyzer DLL is missing.

`tools/setup.py` now builds the analyzer in Release. On Windows the compiler server keeps a loaded analyzer DLL open, so rebuilding it can need `dotnet build-server shutdown` first (noted in `README.md`).

### Verification

| Check | Result |
|---|---|
| Analyzer tests (`dotnet test`) | **20/20 pass**. PERF001 ×8: foreach, nested loops, for/while, EF Core query/`FindAsync`/`SaveChangesAsync` with stubs (positives); batched lookup before the loop, `IQueryable` built in a loop, LINQ-to-objects, call in a foreach collection expression (negatives). PERF003 ×5: parameter (method group and lambda + `ToList`), field and query result, `Chunk(20).Select` (positives); sequential bounded batches, collection literal/expression and `Range` with a constant (negatives). PERF004 ×7: `Dictionary.Add`, indexer + `TryAdd`, `ConcurrentDictionary` `TryAdd`/`AddOrUpdate`/`GetOrAdd`, `List` `Add`/`AddRange`/`Insert` (positives); `List<T>` indexer assignment, fields also removed/cleared (including from a nested type), instance fields (negatives). Each test fails on any missing or extra diagnostic |
| Builds | Solution and analyzer (Debug and Release): 0 warnings, 0 errors, including the analyzer-authoring rules |
| Demo build with the analyzer (`dotnet build -t:Rebuild` + SARIF) | Exactly 4 warnings, all ours: PERF001 `Services/OrderSummaryService.cs:17` (P1), PERF001 `Services/InvoiceService.cs:19` (P5), PERF003 `Services/NotificationService.cs:13` (P2), PERF004 `Services/ReportCache.cs:28` (P3). None for N1–N4, P4 or any other code. Each line lies inside its ground-truth range. The compiler reporting it was Roslyn 5.3, loading the analyzer built against 5.0 |
| Host wiring on the real demo repo | First start added and committed the reference; second start: no new commit; working tree clean before and after demo builds |
| Clean-checkout run (fresh StateRoot, `setup.py`, host, demo build) | Setup built everything (20/20 tests, Release analyzer); the host materialized the demo repo and referenced the checkout's analyzer; the demo build produced the same 4 diagnostics |
| Stale analyzer path | Repaired and committed on the next start; tree clean |
| Missing analyzer DLL | Host refuses to start ("The SecureYourCode analyzer is not built: …"); no commit |

### Finding for H3: SARIF version

The plan's command `"/p:ErrorLog=<path>,version=2"` produces **SARIF 1.0.0**. MSBuild splits `/p:` values on `,`, so `version=2` becomes a separate, ignored property. Escaping the comma, `/p:ErrorLog=<path>%2Cversion=2`, produces **SARIF 2.1.0**, which H3 must use. Observed 2.1.0 output: `runs[].results[].locations[0].physicalLocation.artifactLocation.uri` is an absolute `file:///` URI with no `uriBaseId` and no `originalUriBaseIds`; `region.startLine`/`endLine` present.

## Pre-implementation environment checks (2026-09-29)

Verified on one Windows x64 developer machine, and again with `tools/setup.py` against a fresh, empty `StateRoot` (simulating a new developer). These are smoke checks; they do not replace the formal H1 probe.

| Check | Result |
|---|---|
| Solution build | pass: 0 warnings, 0 errors |
| Tests | pass: 1/1, but it is only the template placeholder; no real tests exist yet |
| Copilot runtime download by the SDK build | pass: archive downloaded and SHA-256-verified |
| SDK starts its bundled runtime | pass: `GetStatusAsync` → version 1.0.89, protocol 3 |
| Not signed in (fresh `StateRoot`) | correctly reported: `GetAuthStatusAsync` → `isAuthenticated: false`; `tools/copilot-smoke` exits with 2, and `tools/setup.py` prints the sign-in command. A sign-in in one `StateRoot` is not visible from another |
| Signed in | pass after `copilot login` with `COPILOT_HOME=<StateRoot>/copilot`: the SDK-launched runtime with `BaseDirectory = <StateRoot>/copilot` picks up the login. The token is in the OS credential store; `<StateRoot>/copilot/config.json` holds only user identity keys. The user's own `~/.copilot` was not modified |
| Quota | readable with `client.Rpc.Account.GetQuotaAsync` (values depend on each developer's plan). The quota snapshot embedded in `AssistantUsageEvent` disagreed with `GetQuotaAsync` in one run, so reports must not rely on the embedded snapshot |
| Models | `ListModelsAsync` depends on the developer's plan: right after a plan change it returned only `auto`; later, on a paid plan, it returned 17 models. `auto` is always present, and the model it resolves to varies per call (the usage event reports it), so H1/H4 must record the model actually used |
| One model call, all tools denied | pass: reply `OK`. `AssistantUsageEvent` populated `Model`, `InputTokens`, `OutputTokens`, `Cost`; 0 permission requests; no files created in the neutral working directory |
| Default session tool exposure | **34 tools** available to a default session (about 10.6k tokens of tool definitions). H1/H4 must restrict this with `AvailableTools` and agent tool lists; never rely on defaults |
| Graphify C# extraction | pass: `graphify extract <dir> --code-only --no-cluster --out <outDir>` built a graph and **wrote nothing into the source folder** |
| Graphify output layout | `<outDir>/graphify-out/graph.json` (candidate `graphifyOutputRelativePath = graphify-out/graph.json`); top-level keys `nodes, edges, hyperedges, input_tokens, output_tokens, extracted_sources`. `graphify update <path>` writes into `<path>/graphify-out/`, so it must not be used on `RepoPath` |
| `graphify.serve` MCP module | imports successfully (not yet started as an MCP server) |

SDK API notes (to be confirmed in H1):
- `CopilotClientOptions.BaseDirectory` sets `COPILOT_HOME` for the spawned runtime; `CopilotClientOptions.WorkingDirectory` and `SessionConfig.WorkingDirectory` both exist.
- `SessionConfig.AvailableTools` exists (session-level allow-list). SDK README: MCP tools are `mcp:<server-key>-<tool-name>` in `AvailableTools`, and `<server-key>-<tool-name>` in `CustomAgents[].Tools`.
- Custom permission handler: `Func<PermissionRequest, PermissionInvocation, Task<PermissionDecision>>`, with request subtypes such as `PermissionRequestShell`. **`GitHub.Copilot.Rpc.PermissionDecision` is marked experimental in SDK 1.0.15 (`GHCP001`, a build error unless suppressed)**, so the host needs `<NoWarn>GHCP001</NoWarn>` for the plan's strict handler.
- `AssistantUsageData` has no public `AvailableToolCount` property, although the serialized event contains `availableToolCount`.
- `GetAuthStatusAsync`, `ListModelsAsync`, `SendAndWaitAsync` exist.

## Open items

- Copilot usage per plan: GitHub docs (checked 2026-09-29) say all plans include Copilot CLI and Free allows auto model selection only. Free has a small allowance that H1 plus repeated `/analyze` runs (4 sessions each, plus repair prompts) can exhaust, so the developer running H1 and the live demo should use a paid plan.
