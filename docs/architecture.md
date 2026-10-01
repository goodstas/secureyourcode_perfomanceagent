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
| Offline bundle | A folder made by `tools/airgap/bundle.py` on a connected machine and carried into the air-gapped environment (H8). Never inside the repository |

Per-developer setup is automated by `tools/setup.py`; see `README.md`.

## Pinned versions

| Component | Version | Notes |
|---|---|---|
| .NET SDK | any 10.0.x | `global.json`: `10.0.100`, `rollForward: latestFeature` |
| `GitHub.Copilot.SDK` | 1.0.15 | Host. Its MSBuild targets download the pinned Copilot runtime (`CopilotCliVersion` 1.0.89) from the `github/copilot-cli` release, verify it against `SHA256SUMS.txt`, and copy it to `bin/<cfg>/net10.0/runtimes/<rid>/native/`. Which binary the SDK actually launches is verified in H1 |
| Copilot CLI (standalone) | 1.0.89 | Installed by `tools/setup.py` with `npm install --prefix <StateRoot>/copilot-cli`; never global. Must match the SDK's `CopilotCliVersion`. Online (Copilot) mode only: ApiKey mode has no sign-in, so air-gapped setups skip it (H8) |
| `Microsoft.CodeAnalysis.CSharp` | 5.0.0 | Analyzer (`PrivateAssets=all`) and host. 5.0 is the compiler in the first .NET 10 SDK (10.0.100); an analyzer must not reference a newer Roslyn than the compiler that loads it, so this works on every developer's .NET 10 SDK |
| `Microsoft.CodeAnalysis.Analyzers` | 3.11.0 | Analyzer; `EnforceExtendedAnalyzerRules=true` |
| `Microsoft.CodeAnalysis.CSharp.Analyzer.Testing` | 1.1.4 | Tests (framework-agnostic `DefaultVerifier`) |
| `Microsoft.CodeAnalysis.CSharp.Workspaces` | 5.0.0 | Tests; same Roslyn version as the analyzer |
| xUnit | 2.9.3 (runner 3.1.4, Test SDK 17.14.1) | From the .NET 10 `xunit` template |
| Python | 3.10 or later | Base interpreter for the Graphify venv (`graphifyy` requires ≥ 3.10) |
| `graphifyy` | 0.9.71 with `[mcp]` extra | Installed by `tools/setup.py` into `<StateRoot>/graphify-venv`. Provides the `graphify` CLI, `graphify-mcp`, and the `graphify.serve` module. **0.9.62 verified equivalent in H8** (same four tools, arguments and output layout); `--graphify-version 0.9.62` installs it |
| Husky.Net (`husky`) | 0.9.1 | Installed as a local tool inside each developer's demo repo in H3 (`dotnet new tool-manifest` + `dotnet tool install husky --version 0.9.1`) |

## Team decisions (adaptations of the plan for a shared repository)

- **StateRoot is per developer.** Default `<user home>/.secureyourcode`, override `SECUREYOURCODE_STATE_ROOT`. The host resolves it the same way at runtime; `appsettings.json` never contains an absolute path.
- **OS.** Plan §2 assumes one demo machine's OS. Team members may use different OSes, so the host detects the OS at runtime and installs the matching hook template (`task-runner.windows.json` or `task-runner.unix.json`); both templates are maintained. The OS of the machine used for the recorded live demo run is noted in the H7 record.
- **Copilot accounts.** Each developer signs in with their own GitHub account (`python tools/setup.py --login`); the sign-in is scoped to that developer's `<StateRoot>/copilot` (since H1 stored there as the CLI's plaintext config, because isolated mode ignores the OS keychain; see H1 › Client isolation mechanism). Model default is `"auto"`, the only model guaranteed on every plan.
- **Two model backends (H8).** `SecureYourCode:Llm:Mode` is `Copilot` (the H1 configuration, GitHub sign-in per developer) or `ApiKey` (a self-hosted OpenAI-compatible endpoint with an API key, for air-gapped environments). The modes never fall back to each other; a misconfigured mode stops the host at startup. The key comes from an environment variable (`SECUREYOURCODE_LLM_API_KEY` by default) or a file, never from `appsettings.json`, and never appears in logs or reports.
- **Analyzer reference in the demo repo.** The `<Analyzer Include=...>` path (plan §4.3) is absolute and machine-specific, so it is written only into each developer's materialized demo repo under `StateRoot`, never into `test-assets/demo-shop`.
- **Environment checks** live in `tools/setup.py` and `tools/copilot-smoke/` (a small console app, not part of `SecureYourCode.slnx` and not app code). `tools/evaluate/` (H7) is in the solution, because the host tests reference it.
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

## H3 — Static analysis + graph (2026-09-29)

**No fallback needed:** the hook works on the detected OS (Windows); the Unix template is maintained but not exercised on this machine.

### Decisions

- **Fingerprint** (`Graph/RepoFingerprint.cs`): `git ls-files -z -c -o --exclude-standard`, minus `.husky/.local-token`, sorted ordinally. Each file contributes `"<path>\0<sha256(content)>\n"` to one SHA-256 (lowercase hex). A tracked file deleted from the working tree contributes `<deleted>`, so deletions change the fingerprint too. `RepoState` reads the commit SHA and dirty flag for report provenance.
- **`GraphifyUpdater`** (`Graph/GraphifyUpdater.cs`) implements plan §4.2 as written, behind a DI-singleton `SemaphoreSlim(1,1)`, with a 5-minute timeout.
  - `graphifyVersion` is read from the venv (`importlib.metadata.version('graphifyy')`), not hard-coded.
  - Published folders are `<StateRoot>/graphs/<fingerprint>-<version>/`; `current.txt` holds that folder name and is updated by temp file + rename.
  - Failure reasons: `source_changed_during_graph_build`, `invalid_graph_output`, `extraction_failed: …`. The temp folder is always deleted, and nothing is published.
  - `TryGetPublishedAsync(fingerprint)` and `ReadCurrent()` give H4 the `current`/`stale`/`none` graph status. The extractor sits behind `IGraphExtractor` (`GraphifyCliExtractor` runs `graphify extract <RepoPath> --code-only --no-cluster --out <temp>`), so the before/after logic is unit-tested with a fake.
- **Refresh queue:** `GraphRefreshQueue` wraps the plan's DI-singleton `Channel<bool>` (capacity 1, `DropWrite`). `GraphRefreshWorker` (a `BackgroundService`) runs `RefreshAsync` for each item. The host also queues one refresh at startup.
- **`POST /git-post-commit`:** a token check (`401` otherwise), then a queued refresh and `202 Accepted`, immediately.
- **`EnsureHookInstalled()`** (`Graph/GitHookInstaller.cs`) runs at every start, after the analyzer reference. It checks and repairs:
  - `.config/dotnet-tools.json` with `husky` 0.9.1;
  - `core.hooksPath = .husky` plus `.husky/_/husky.sh` (via `dotnet tool restore` + `dotnet husky install`);
  - `.husky/post-commit` (from `hook-templates/post-commit`);
  - `.husky/task-runner.json` (the OS template);
  - `.husky/.local-token` (the host token, owner-only).

  Then `git add .husky .config .gitignore`, and it commits only if something is staged. A file merely missing from the working tree is restored to its committed content, so there is nothing to commit; wrong *committed* content is repaired and committed ("Add SecureYourCode graph-refresh hook").
  - **.NET 10 difference:** `dotnet new tool-manifest` now writes `dotnet-tools.json` at the repository root, so the installer passes `--output .config` to keep the plan's `.config/dotnet-tools.json`.
  - **Line endings:** the post-commit script is always written with LF line endings (a CRLF checkout would break `sh`), and chmod 755 on Unix.
- **Deviation from the plan's Windows task template:** `hook-templates/task-runner.windows.json` appends `; exit 0` after the `try { … } catch { }`. Verified: with the plan's literal command, Windows PowerShell 5.1 exits with code 1 after the caught connection failure, and Husky prints "task failed … post-commit hook exited with code 1 (error)" on every commit while the host is stopped. That violates "never … errors a commit". The Unix template already ends in `|| true`.
- **Static analysis** (`StaticAnalysis/StaticAnalysisRunner.cs`) runs `dotnet build <DemoShop.csproj> -t:Rebuild /p:ErrorLog=<runs/<runId>/analysis.sarif>%2Cversion=2 /p:UseSharedCompilation=false -nodeReuse:false -nologo` with a 5-minute timeout.
  - The last two build switches stop compiler-server or MSBuild node processes from outliving the run or keeping the analyzer DLL locked.
  - The fingerprint is checked before the build and after the SARIF. Precedence follows plan §4.7: a failed build or missing SARIF → `Failed`; otherwise a changed fingerprint → `SourceChanged` (no candidates; `analysis.sarif.validity.json` records `validForEvidence: false, reason: source_changed_during_static_analysis`); otherwise the SARIF is parsed.
  - An unresolvable `PERF*` location → `Failed` with `sarif_data_error: <uri>`.
- **SARIF parsing** (`StaticAnalysis/SarifParser.cs`): 2.1.0 only (anything else is a data error); keeps `PERF*` results only; `endLine ?? startLine`. `NormalizeSarifPath` follows the four plan steps: decode `file://`/percent-encoding; resolve a relative path against its `uriBaseId` in `originalUriBaseIds`, else the project folder, accepting `\` separators; `GetFullPath` and require the result inside RepoPath; return it repo-relative with `/`.
- **Enclosing symbol** (`StaticAnalysis/EnclosingSymbolResolver.cs`): the innermost method, constructor, destructor, operator, property, indexer, event, accessor or local function whose line span contains the line, as `Type.Nested.Member`. Naming rules:
  - An accessor reports its property's name, a constructor its type name, a local function its own name.
  - With no member, the innermost type; code outside any type (top-level statements) resolves to `Program`.
- **Candidate model** (`Orchestration/Candidate.cs`): `candidateId` = first 12 hex of SHA-256(`ruleId|file|enclosingSymbol|startLine`); host-owned `category` from the rule ID; baseline candidates are `origin: roslyn`, `confidence: candidate`, `evidence: E1`. They also carry the analyzer's message as a host-owned `DiagnosticMessage`.
- **New test project `src/SecureYourCode.Agent.Tests`** (not in the plan's §3 layout). The start prompt requires deterministic tests for source binding and related logic that run without credentials; this project holds them.

### Verification

| Check | Result |
|---|---|
| All tests | **63/63 pass** (20 analyzer, 43 host); the host tests leave no temp folders behind |
| Host unit tests | SARIF: `PERF*`-only filtering, `endLine` fallback, percent-encoding, `uriBaseId` and project-dir resolution, backslashes, outside-repo and `..` paths as data errors, SARIF 1.0 rejected. Enclosing symbol: constructor, accessor, method, local function, nested type, field → type, top-level → `Program`. Candidate ID and category mapping. Graph predicate (6 shapes). Fingerprint: stable, ignores `bin/` and the token, changes on edit, new untracked file and deletion. `GraphifyUpdater` (fake extractor): publish + `current.txt` + reuse without re-extraction; source change mid-extraction, invalid JSON, empty output, empty `nodes` and extractor crash all leave nothing published and no temp folder; four concurrent refreshes → one extraction, never overlapping |
| Host integration tests (real tools, no credentials) | Real build: exactly 4 baseline candidates, `OrderSummaryService.BuildSummariesAsync` (P1, line 17), `InvoiceService.BuildInvoiceLinesAsync` (P5, 19), `NotificationService.NotifyAllAsync` (P2, 13), `ReportCache.GetOrAdd` (P3, 28), with the right categories, all `roslyn`/`candidate`/`E1`, and the fingerprint unchanged by the build. Wrong `analysisFingerprint` → `SourceChanged`, no candidates. Broken source → `Failed` (`build_failed`), not zero findings. Real Graphify extraction → a valid published graph, RepoPath byte-identical. Hook: install → one commit, clean tree, repo-local `core.hooksPath`; idempotent; deleted files restored; wrong committed content repaired and committed |
| Real demo repo, first start | Hook installed and committed; startup refresh published a graph; `current.txt` updated |
| `POST /git-post-commit` | no token → 401, wrong token → 401, valid token → 202 |
| **A commit produces a new published graph** | Commit with the host running: hook ran in about 0.2 s, and a new graph was published for the new fingerprint. Committing the removal switched `current.txt` back to the existing graph with no re-extraction. No temp folders left; demo content unchanged |
| **A stopped host doesn't break commits** | Commit with the host stopped: hook task "successfully executed" in about 2.2 s, commit exit 0, no error output (after the `exit 0` template fix) |
| Restart | Hook check is idempotent (no commit); startup refresh reused the existing graph |

## H4 — Orchestration (2026-09-29)

### Decisions

- **`POST /analyze`** is exactly as in plan §4.8: token check (`401`), a DI-singleton gate `SemaphoreSlim(1,1)` with `Wait(0)` (`409` while a run is active), then `Orchestrator.RunAsync(requestAborted)` returns `200` with the report object. `RunAsync` owns everything else:
  - a linked token with a 30-minute `CancelAfter`, and a 10-minute linked timeout per reviewer session;
  - steps 1–7 in order, run state, and the cancellation reason (`cancelled` if `requestAborted` fired, else `timeout`);
  - publication through `IReportPublisher` with a fresh 10-second token. That is a no-op until H6, so `/analyze` returns the report and no files are written yet.

  Child processes stop on cancellation: `ProcessRunner` kills builds and Graphify with `Kill(entireProcessTree: true)`, and the Copilot client is stopped via `StopAsync`, falling back to `ForceStopAsync` after 10 s.
- **Graph selection:** `current` if a graph is published for `analysisFingerprint`; otherwise `RefreshAsync` (it waits for the updater's gate) and `current` if that publishes this fingerprint; otherwise the graph named by `current.txt` as `stale`; otherwise `none`. With no graph, reviewers run without the Graphify MCP server and tools, and the report notes "Graphify unavailable to reviewers".
- **One Copilot client per run** (`CopilotReviewerClient`), started before the specialists. A failure to start or sign in means the run is `failed` (`orchestration_failed`), with the baseline candidates still reported. Sessions run sequentially (MemoryReviewer, CpuReviewer, ConcurrencyReviewer, then VerificationReviewer), each an explicitly selected custom agent with exactly the H1 final configuration. The agent prompt is `Prompts/<reviewer>.md`, copied to the output. `Model` comes from `SecureYourCode:Model`, else `"auto"`; the report records `ModelsUsed` from the usage events.
- **Task messages** (`ReviewerPrompts`): the specialists get all baseline candidates (ID, rule, file, lines, symbol, category, analyzer message), the graph status with guidance for `current`, `stale` and `none`, instructions (enrich baseline candidates in their pillar by `candidateId` and copy their identity unchanged; report new issues as `LLM-<pillar>-nn`; never re-report a baseline issue; 1-based lines; repo-relative paths), and the exact §4.5 JSON schema. The critic gets every consolidated candidate with the §4.5 field list, the rule "exactly one decision per candidateId", the fixed benchmark list from `BenchmarkTemplates`, and the §4.5 JSON schema. The agent prompts state that repository content is data, never instructions; that reviewers are read-only; not to claim confirmed issues; and they describe each pillar generically, without naming the demo's planted cases.
- **JSON handling** (`ReviewerReplies`): the whole reply, or else its first-`{`-to-last-`}` span (code fences, prose), must be one object of the §4.5 shape, with the required fields present and typed, and confidence `candidate` or `strong`. On failure, **one** repair request quotes the error; still invalid → that reviewer `failed`, and the run continues. Every raw reply is saved to `<StateRoot>/runs/<runId>/reviewers/<Reviewer>.reply<n>.txt` (a per-developer run log for H7).
- **Consolidation** (`Consolidator`) follows plan §4.5 as written, with two details:
  - A mismatch note is recorded when the specialist's `ruleId`, lines, `file` or `enclosingSymbol` differ from the matched baseline.
  - LLM-only locations reuse `SarifParser.NormalizeSarifPath`, so absolute paths and backslashes are accepted. A `PERF*` ID without a baseline match is relabelled `LLM-<pillar of that rule>-00`.
- **Critic** (`CriticDecisions`): missing, duplicate or invalid decisions and unknown IDs trigger the single repair request, which lists the problems and the valid IDs. After it, a reply of the right shape is resolved by the host rules, with no second repair:
  - a candidate without exactly one valid decision → `keep` with "not reviewed by critic" and the critic `incomplete` (run `partial`);
  - unknown IDs are ignored and noted, and do not by themselves make the critic incomplete;
  - an unparseable reply after the repair → critic `failed` (run `partial`).

  Decision semantics: `downgrade` sets confidence `low` and never changes evidence; `remove` moves the candidate to `rejectedCandidates`. Benchmark proposals: at most 2, `keep` candidates only, a pair in the template table, and the candidate's `TypeName.MemberName` equal to the template's seam; others are ignored with a note. Accepted proposals are recorded for H5.
- **Run status** (`RunStatusRules`), exactly plan §4.7.
  - `failed`: static analysis failed, the client could not start, or a timeout or cancellation stopped the run **before all specialists finished**.
  - A timeout or cancellation after the specialists makes the run `partial` instead (`critic_failed (timeout)`, or a verification failure).
  - `partial` reasons are combined with `; `.
- **Token usage** (`Reporting/TokenUsage.cs`): per session and in total — model calls, models, and input tokens, output tokens and `Cost` labelled "premium request cost units". A field no call reported shows "not reported" (never 0), and a partially reported sum is labelled "(partial: k of N calls reported)".
- **Denied tool requests** are recorded per reviewer as a count plus up to 10 distinct descriptions (`read <path>`, `mcp <server>/<tool> (arguments: …)`, `shell …`, `write …`, `url …`) in the reviewer's report notes.
- **Graphify `project_path`, found in live runs:** in the first two live runs every reviewer's Graphify calls were denied, because the models pass `project_path` (the tools' schema offers it), and the strict handler denies it (H1 decision). Allowing it would not help: the server would then look for `<project_path>/graphify-out/graph.json` inside the repository, which never exists by design. **Fix:** the task messages now say the graph is preloaded and to call the Graphify tools without `project_path`. The handler still denies it as a safety net.
- **Benchmark proposals:** the critic's list shows the exact JSON strings to copy, after a live critic invented scenario names (correctly ignored with notes).

### Verification

| Check | Result |
|---|---|
| All tests | **104/104 pass** (20 analyzer, 84 host). H4 adds 41 host tests |
| H4 unit tests | Reply parsing: plain, fenced and prose-wrapped JSON; 6 invalid shapes with repair messages; critic decisions kept raw. Consolidation: enrichment by ID and by rule + file + range, identity immutable with a mismatch note, longer text and higher confidence win, unenriched marked; LLM-only validation (absolute paths, recomputed symbol, host ID and category, E0) and 6 discard reasons; `PERF*` relabelling; overlapping merge. Critic: keep/downgrade/remove (evidence unchanged); missing, duplicate and invalid decisions → keep "not reviewed by critic" + incomplete; unknown IDs ignored and noted; benchmark filtering (P5 rejected as not P1's seam; downgrade rejected; not in table; more than 2). Token usage "not reported"/"partial"; status precedence; path containment |
| Orchestrator flow tests (fakes, real timers) | Happy path `complete` (critic semantics, usage for 4 sessions, provenance, accepted proposal, saved replies); specialist invalid after repair → `partial` (`specialist_failed: CpuReviewer`), baseline still reported; successful repair → `complete`; critic incomplete after repair → `partial` (`critic_incomplete`); critic invalid → `partial` (`critic_failed`); source change after the graph stage → `partial` (`source_changed`) with no static analysis; static analysis failed → `failed`, no reviewer sessions; SARIF not evidence → `partial`; source changed by the end → `partial`; client fails to start → `failed` (`orchestration_failed`) with the baseline reported; run timeout during specialists → `failed` (`timeout`) and still published; client cancellation → `failed` (`cancelled`); run timeout during the critic → `partial` (`critic_failed (timeout)`); session timeout fails only that specialist; no graph → reviewers without Graphify plus a note; gate |
| `/analyze` auth and gate (live host) | no token → 401; a second request during a run → **409** "An analysis is already running." |
| **Live run 1** (`auto` → gpt-6-luna) | **`complete`** in 100 s, graph `current`, all 4 sessions `completed` without repair, 18 model calls. Findings: P1, P5, P2, P3 (Roslyn, E1, enriched, kept) + **P4** found by MemoryReviewer (`LLM-MEM-01`, E0; the host recomputed the symbol from the model's line) + one extra `LLM-CPU-01` on the same subscription (would be a false positive against the ground truth). Graphify calls 0 (denied: see above) |
| **Live run 2** (gpt-6-luna, gpt-5.6-luna) | `complete` in 79 s, exactly P1–P5, 14 model calls; denial details showed every denied request was a Graphify `query_graph` call |
| **Live run 3** (after the `project_path` fix; gpt-5.6-luna) | **`complete`** in 92 s, exactly P1–P5, 21 model calls. **Graphify used by every reviewer** (5, 8, 2 and 5 successful calls), **0 denials**, and `graphPath` filled on three findings. The critic's two invented benchmark scenarios were ignored with notes |
| **Live run 4** (verification of both prompt fixes, committed H4 build; gpt-5.6-luna) | **`complete`** in 78 s, exactly P1–P5, 17 model calls. **Graphify used by every reviewer again** (2, 7, 1 and 6 calls), 0 denials. **Both benchmark proposals used the exact template strings and were accepted:** `RepositoryCallAmplification/OrderCustomerLookup` for P1 and `CollectionGrowth/ReportCache` for P3; none were ignored. Demo repo unchanged after all four live runs (6 commits, clean tree) |

Model output varies between runs (one run had an extra CPU-framed duplicate of P4). H7 records precision and recall from one `complete` run, as the plan says.

## H5 — Verification (2026-09-29)

### Decisions

- **Templates** live in `src/SecureYourCode.Agent/Verification/Templates/<Kind>/` (`<Kind>.csproj` + `Program.cs`). They are ordinary reviewed console apps, excluded from the host's compilation (`Compile/None/Content Remove`). Each parses `--n <n>` and prints exactly one JSON line via `System.Text.Json`:
  - `RepositoryCallAmplification` builds n orders with distinct customers, calls `OrderSummaryService.BuildSummariesAsync`, and prints `CountingCustomerRepository.CallCount` (metric `repository calls`).
  - `CollectionGrowth` calls `ReportCache.GetOrAdd` with n distinct keys, forces a full GC, and prints `ReportCache.Count` (metric `entries retained`). Demo code gets no reset method.
  - `TaskFanOut` calls `NotificationService.NotifyAllAsync` with n recipients and prints `CountingSender.StartedCount` **read as soon as the call returns, before awaiting** (metric `tasks started`). It therefore counts sends in flight at once: a sequential batched version would show about 20, not n.
- **Runner** (`DotnetBenchmarkRunner`):
  1. Copies the template to `<StateRoot>/verification/<runId>/<candidateId>/` and replaces `__DEMO_PROJECT__` with the XML-escaped absolute path of the demo project.
  2. Runs `dotnet build -c Release -nologo /p:UseSharedCompilation=false -nodeReuse:false`.
  3. Runs `dotnet bin/Release/net10.0/<Kind>.dll --n <n>` for n = 10, 100 and 1000, **each a fresh process**.

  Building the reference writes only the demo repo's gitignored `bin/obj`.
- **Verifier** (`BenchmarkVerifier`, the only code that assigns E2).
  - **Plan:** the required template for every candidate whose rule and `TypeName.MemberName` match the template's rule and seam and whose critic decision is not `remove` (independent of proposals); plus accepted proposals, already limited to kept candidates at the seam. The same (candidate, template) pair never runs twice.
  - **Status per candidate:** `rejected by the critic`, `not_run` (no template applies), `skipped` (`source_changed_before_verification`), `failed` (infrastructure), `not_verified` (acceptance rule not met, or result discarded because the source changed around the benchmark), `verified` (E2, with the previous level kept for a revert), `invalidated` (reverted).
  - **Time limit:** one 3-minute budget covers the template build and all three processes, linked to the run token.
  - **Validation:** exit code 0, exactly one output line, valid JSON, the requested `n`, the template's metric, and a non-negative value.
  - **Acceptance** (`BenchmarkTemplates.Evaluate`): every value(n) ≥ 0.9·n, and for `RepositoryCallAmplification` also value(1000) ≥ 50·value(10). A failure reason looks like "benchmark ran; acceptance rule not met: value(10)=1 < 0.9·10".
- **Infrastructure failure → run `partial`:** a template build failure, a non-zero exit, invalid output, a missing template, or the 3-minute timeout sets `verification_failed: …`. A rule not being met is a result, not a failure.
- **Source binding** (plan §4.6):
  1. The fingerprint is checked before verification; a change skips it all.
  2. It is checked before and after each benchmark set; a change discards that result.
  3. At step 6, if the final fingerprint differs, every E2 from the run is reverted to its previous level with "verification invalidated: source changed during run".
  - **Beyond the plan:** if the run is stopped by a timeout or cancellation after E2 was awarded, the final check never runs, so those E2s are reverted too ("verification not confirmed").
- The report notes that E2 means verified by a host-owned template bound to a demo seam, and applies only to the demo project in the hackathon.

**No fallback needed.**

### Verification

| Check | Result |
|---|---|
| All tests | **124/124 pass** (20 analyzer, 104 host). H5 adds 20 host tests |
| Verifier unit tests (fake runner) | Which benchmarks run: the required template runs for the P1 seam without a proposal (E2; previous level kept; observations recorded; demo-only note); a downgraded seam candidate still runs (E2 with confidence `low`); a removed one never runs; P5 is never verified by the P1 template, even when proposed; optional templates run only when accepted, and a proposal equal to the required run executes once. Acceptance: value(n) < 0.9·n → `not_verified` with the reason; the 50× clause alone (20/100/950) fails. Infrastructure failures (never E2): wrong `n`, wrong metric, negative value, two lines, not JSON, non-zero exit, build failure, 3-minute set timeout. Source binding: changed before verification → all skipped; changed around a set → result discarded |
| Orchestrator tests | E2 kept in a complete run; a final fingerprint change → `partial`, with E2 reverted to E1 and `invalidated`; verifier infrastructure failure → `partial` (`verification_failed: …`); run timeout during verification → `partial`, with the unconfirmed E2 reverted |
| Real benchmark integration tests | All three templates built against a throwaway demo repo and run in fresh processes: **E2 for P1, P3 and P2, with values exactly 10/100/1000**, the demo repo clean and the fingerprint unchanged. P1's seam rewritten to batch its lookups (`GetByIdsAsync`) → measured **1/1/1** → `not_verified`: "value(10)=1 < 0.9·10" |
| **Live `/analyze`** (gpt-5.6-luna) | **`complete`** in 107 s. **P1 reached E2** via `RepositoryCallAmplification/OrderCustomerLookup` (10→10, 100→100, 1000→1000; both acceptance clauses met; ran once although the critic also proposed it). P2 reached E2 via the accepted `TaskFanOut/NotificationRecipients` proposal (10/100/1000 tasks started at once). P5 not run (not the P1 seam); P3 not run (optional, not proposed in this run); P4 E0. Graphify used by all reviewers, 0 denials; demo repo unchanged. Report strings keep UTF-8 `≥` and `·` |

## H6 — Reports (2026-09-29)

### Decisions

- **`report.json`** is the canonical report object serialized with the web defaults (camelCase, indented): the exact shape `/analyze` returns (`ReportJson`). Missing fix directions stay `null` in JSON; the HTML shows "no fix direction provided".
- **`report.html`** (`HtmlReportRenderer`) is rendered server-side from the same object.
  - **Security:** no JavaScript, no external resources (inline CSS only; collapsibles are `<details>`), and a `Content-Security-Policy` meta of `default-src 'none'; style-src 'unsafe-inline'`, so even an encoding bug could not run script or load anything. Every dynamic value, in text and in attributes, goes through `WebUtility.HtmlEncode`; the report JSON is not embedded in the page.
  - **Sections:** header; banners when the run is not `complete` (amber `partial`, red `failed`, with the reason) or the graph is not `current` (`stale`/`none`, with its meaning); Run; Provenance (commit, dirty flag, fingerprint, graph fingerprint); Token usage (per session and total, cost column labelled "premium request cost units"); Reviewers (status, error, Graphify calls, denied requests, notes); findings grouped into *Memory & Allocation*, *CPU & Amplification* and *Concurrency* (critic `keep`) and *Low-confidence candidates* (`downgrade`); a collapsed *Rejected by critic* list; Notes.
  - **Each finding** is a collapsed `<details>` with an evidence badge (E0 grey, E1 blue, E2 green, with a title such as "E2: verified by host benchmark template …"), an origin label (Roslyn / AI), a confidence label, and rule, location and symbol in the summary. The body holds every §4.7 field: candidate ID, category, origin (with reviewers), analyzer message, mechanism, trigger, execution path, evidence, critic decision and rationale, verification result with benchmark observations, and fix direction. There is no severity and no overall score.
- **Transactional publication** (`FileReportPublisher`, replacing the H4 placeholder):
  1. Both files are written to `<StateRoot>/reports/<runId>_<shortSha>[-dirty].tmp/` (`unknown` when there is no commit).
  2. The folder is renamed to its final name in one step.
  3. Only then is `latest.txt` replaced (temp file + rename).

  Any failure deletes the temp folder, leaves `latest.txt` unchanged, and raises `ReportPublicationException`. `RunAsync` computes the run status *before* publishing, logs a failure, and rethrows it carrying the report. `/analyze` then answers **HTTP 500** with `{ error, runId, runStatus, detail }`. The run status never depends on publication. Every run is published, including `partial` and `failed` ones, with whatever completed.

**No fallback needed.**

### Verification

| Check | Result |
|---|---|
| All tests | **131/131 pass** (20 analyzer, 111 host). H6 adds 7 host tests |
| Renderer | Every section, banner, badge (e0/e1/e2), Roslyn/AI label and confidence label present; "no fix direction provided"; "premium request cost units"; *Rejected by critic* collapsed; balanced `<details>`; no "severity". No banner for a `complete`/`current` run; red banner for `failed` with graph `none` |
| **HTML encoding** | A fixture puts `<script>alert('x')</script>` (a script copied from a source comment) and an attribute breakout `"><img src=x onerror=alert(1)>` into every dynamic field: run reason, models, commit, notes, reviewer notes and error, candidate ID (an `id` attribute), rule, file, symbol, confidence, analyzer message, mechanism, trigger, graph path, fix direction, critic rationale, verification status/template/reason, reviewers, token-usage session and model. The page contains **no `<script` and no `<img`** at all; the payload appears encoded (`&lt;script&gt;alert(&#39;x&#39;)&lt;/script&gt;`); the attribute stays intact (`id="candidate-&quot;&gt;&lt;img …&gt;"`); the CSP meta is present |
| Publication | Success: `<runId>_<sha7>-dirty/` with both files; `report.json` equals the serialized object; `latest.txt` updated; no `.tmp` left. Failure (a file already occupies the final folder name): `ReportPublicationException` carrying the report, temp folder removed, `latest.txt` unchanged |
| Orchestrator | A publisher throwing `IOException("disk full")` → `ReportPublicationException` whose report keeps the computed status `complete` |
| **Live `/analyze`** (gpt-6-luna) | `complete` in 101 s, HTTP 200. Published `reports/<runId>_1656394/` with `report.json` and `report.html`; `latest.txt` points to it; no `.tmp` folder; the HTTP body and `report.json` describe the same run. P1 **E2** (required benchmark) and P3 **E2** (accepted `CollectionGrowth/ReportCache` proposal, a first in live runs); P2 and P5 E1; P4 E0 |
| **HTML viewed** | The page contains no `<script`, `src=`, `href=`, `@import` or `url(` (self-contained). The browser pane does not open `file://` URLs, so the file was served unchanged from a throwaway `127.0.0.1` static server and inspected: header, Run, Provenance, Token usage, Reviewers, the three category groups with E2 (green), E1 (blue) and E0 (grey) badges and Roslyn/AI labels, an expanded E2 finding (execution path from Graphify, critic rationale, verification with observations 10/100/1000, fix direction), *Low-confidence candidates* (empty), a collapsed *Rejected by critic*, and Notes. All rendered correctly |

## H7 — Evaluation + demo (2026-09-30)

### Decisions

- **`tools/evaluate`.** The plan names "`evaluate.ps1` or `evaluate.sh`". The team uses more than one OS, so both exist, as thin wrappers around one small .NET console project, `tools/evaluate/` (`Evaluator.cs` + `Program.cs`). The matching rules therefore live in one tested place instead of two script dialects.
  - Unlike the environment-check tools, it is part of `SecureYourCode.slnx`, because the host test project references it for its unit tests. The host itself does not reference it.
  - **Input:** no argument → the latest report (`<StateRoot>/reports/<latest.txt>`, with StateRoot resolved like the host: `SECUREYOURCODE_STATE_ROOT`, else `<user home>/.secureyourcode`); or a report folder or `report.json` path.
  - **Ground truth:** `<AppWorkspace>/test-assets/ground-truth.json`, found like the host finds AppWorkspace.
  - **Exit codes:** 0 evaluated (the numbers are printed and written whether or not the target is met), 1 error, 2 usage.
- **Semantics** (plan §5):
  - Only `findings` whose critic decision is `keep` or `downgrade` are read. `rejectedCandidates` are never read, and benchmark proposals are not findings.
  - A match is the same canonical file, an overlapping inclusive line range and the same category. Rule IDs are not part of the §5 match rule (P4's ground truth says `LLM-MEM`; the host names specialist findings `LLM-MEM-nn`).
  - Matching is one-to-one: a maximum bipartite matching (augmenting paths), so the order of findings can never cost a true positive, and one finding never satisfies two cases.
  - TP = matched positive cases; FN = unmatched positive cases; FP = every unmatched final finding, with a note naming the negative case it falls in, if any. Negative cases never produce FNs.
  - Precision and recall are rounded to 4 decimals, or `"undefined"` when the denominator is 0. The target is met only by a `complete` run with both ≥ 0.8 (inclusive); undefined never meets it. A `partial`/`failed` run is recorded with `countsTowardTarget: false`.
- **`metrics.json`** is written into the report folder (temp file + rename) with `runId`, `runStatus`, `countsTowardTarget`, `truePositives`, `falsePositives`, `falseNegatives`, `precision`, `recall`, `targetMet`, `matches` (case → candidate ID), `falsePositiveFindings`, `falseNegativeCases` and `notes`.
- **`demo/DEMO.md`** gives PowerShell and bash variants of each step. Step 2 commits a notes file (`demo-log.txt`) to the demo repo: this changes the fingerprint, so a new graph is extracted, without touching the demo cases or their line numbers.
- **`demo/fallback/`** comes from the H7 live run.
  - `report.json` and `metrics.json` are byte-for-byte copies.
  - `report.html` is unchanged except for a red *RECORDED FALLBACK* banner after `<body>` and a "RECORDED —" title prefix.
  - `run-log.txt` holds the host console and the step commands and outputs, with the home folder as `~` and the repository as `<repo>`.
  - `README.md` labels it all as recorded and names the run.
  - All files were scanned for personal paths, names and tokens: none.
- **`.gitattributes`**: `*.sh` and `hook-templates/post-commit` stay LF on every checkout, since Windows developers with `core.autocrlf=true` also run `evaluate.sh` from Git Bash. `demo/fallback/report.json` and `metrics.json` are `-text`: the host writes CRLF on Windows, and without this, git would normalize them, so they would no longer be byte-for-byte copies (verified: the committed blobs hash-equal the published files).
- **Cross-pillar duplicate (open item from H4): decided — no change.** The measurements:
  - The H7 run scored precision 1.0 and recall 1.0; the H6 run, scored with the same tool, also 1.0 / 1.0.
  - No live run after H4 run 1 reproduced the duplicate (H4 runs 2–4, H5, H6, H7).
  - Its worst observed case, H4 run 1, would score 5/6 ≈ 0.83, still above the target.

  Changing the prompts now would tune them against known answers without a measured need, and would invalidate the recorded runs. It stays a known limitation, with the generic prompt rule below as a v2 candidate.
- The recorded live demo run was made on **Windows 11 x64** (see the Team decisions on OS).

**No fallback needed.**

### Verification

| Check | Result |
|---|---|
| All tests | **141/141 pass** (20 analyzer, 121 host). H7 adds 10 evaluator tests |
| Evaluator unit tests | The ground-truth file still has exactly its H0 content. Reports built with the host's own `Report` types and `ReportJson` serialization: exactly P1–P5 (with P5 downgraded) → 5/0/0, 1.0/1.0, target met, no FN from the four negatives; rejected candidates on P2 and on N1 ignored → 4/0/1, recall 0.8 meets the target (inclusive); a CPU-category copy of P4 plus a second Memory finding on P4 → 2 FPs ("matches no positive case"), precision 0.7143; a finding in N1 → FP with "is in negative case N1"; maximum matching (a greedy order would lose case B) and one finding never matching two cases; same file / touching lines / category rules; zero findings → precision `"undefined"` in `metrics.json`, recall 0, target not met; `partial` and `failed` runs recorded but not counted |
| `tools/evaluate` CLI | `evaluate.ps1` (no argument → latest report) and `evaluate.sh` (Git Bash, with a `report.json` path) on the H6 and H7 reports. Errors: missing report → 1; two arguments → usage, 2; relative `SECUREYOURCODE_STATE_ROOT` → 1; a StateRoot without reports → 1 with "run /analyze first" |
| **Live demo run** (`demo/DEMO.md` followed verbatim in PowerShell; `auto` → gpt-5.6-luna) | Host started; the step-2 commit returned at once and the hook led to `Published graph <fingerprint>-0.9.71`. `/analyze` → **`complete`** in 102 s, graph `current`, 16 model calls, 16 premium request cost units. **Exactly P1–P5**: P1 **E2** (required benchmark, 10/100/1000 repository calls), P2 **E2** (accepted `TaskFanOut` proposal), P3 E1, P4 E0 (MemoryReviewer), P5 E1 downgraded to low confidence (the critic noted the in-memory repository). Nothing rejected. Graphify used by every reviewer (3, 1, 2 and 5 calls), 0 denials. Without the token → 401. Demo repo clean afterwards |
| **Metrics** (`metrics.json` of that run) | **TP 5, FP 0, FN 0, precision 1.0, recall 1.0: target met** on a `complete` run. The H6 run scores the same |
| Bash variants (Git Bash on Windows) | Step-2 commit → hook → new graph published; `curl` with a wrong token → 401; `curl` with the step-3 token expansion against the cheap `/git-post-commit` → 202 (not `/analyze`, to avoid a second paid run); the step-4 path resolves. macOS `open` / Linux `xdg-open` not run (no such machine) |
| Fallback | Rendered from a throwaway `127.0.0.1` static server: banner and title show RECORDED; findings, E2 badges and sections intact. No personal data |
| `tools/setup.py` | Re-run passes with the new project in the solution |

## H8 — Air-gapped mode: ApiKey backend and offline dependencies (2026-09-30)

Requested after H7: the team also works on air-gapped machines with no GitHub Copilot sign-in and no route to nuget.org, PyPI, npm or GitHub, but with their own OpenAI-compatible model endpoints (API key) and an internal Python package index. The host must run in either environment, chosen by configuration, with every dependency available from the team's own sources. Not a plan milestone; recorded here like one.

### Decisions

- **The Copilot runtime stays; only the credential path changes.** `GitHub.Copilot.SDK` 1.0.15 has a whole-session BYOK provider (`SessionConfig.Provider`, a `ProviderConfig` with `Type`, `BaseUrl`, `WireApi`, `WireModel`, `ModelId`, `ApiKey`/`BearerToken`, `Headers`, `Azure`, token limits). Its docs say this singular provider replaces Copilot API authentication (unlike the additive `Providers`/`Models` registry). So the four reviewer sessions, the strict permission handler, the tool-name lists, the isolation options and the Graphify MCP wiring from H1/H4 are unchanged in ApiKey mode; each session additionally carries the provider, and `Model` is the provider's `ModelId`.
- **Configuration** (`Infrastructure/SecureYourCodeOptions.cs`, resolved once at startup by `Infrastructure/LlmSettings.cs`):
  - `SecureYourCode:Llm:Mode`: `Copilot` (default) or `ApiKey`. Any other value fails startup naming the setting.
  - `SecureYourCode:Llm:Provider` (ApiKey mode): `Type` (`openai` default, `azure`, `anthropic`, `ollama`), `BaseUrl` (absolute http(s), required), `WireApi` (`chat-completions` default or `responses`), `WireModel` (required: the name the endpoint expects), `ModelId` (optional well-known id for the runtime's prompting/limits lookup; default `WireModel`), `ApiKeyEnvironmentVariable` (default `SECUREYOURCODE_LLM_API_KEY`), `ApiKeyFile` (absolute path, used when the variable is unset), `UseBearerToken` (default true for `openai`: `Authorization: Bearer`), `Headers`, `AzureApiVersion`, `MaxPromptTokens`, `MaxOutputTokens`. Missing key → startup error naming both sources; `ollama` needs none.
  - `SecureYourCode:Graphify:Python` / `:Cli`: an existing Graphify installation instead of the venv under StateRoot (both or neither, absolute).
  - `SecureYourCode:NuGetSource` (or `SECUREYOURCODE_NUGET_SOURCE`): a folder of `.nupkg` files or a feed URL for the Husky.Net tool install in the demo repo. Found by the emulation below: `GitHookInstaller` runs `dotnet tool install husky` at every host start, which reaches nuget.org. With a source configured, the host writes `<StateRoot>/nuget.config` (`<clear/>` + that source) and passes `--configfile` to `dotnet tool install/update/restore`.
- **`CopilotReviewerClient`**: in ApiKey mode `StartAsync` does not gate on `GetAuthStatusAsync` (there is no sign-in), and `CreateSessionAsync` sets `Provider = ToProviderConfig(provider)`. In Copilot mode nothing changed.
- **Report**: `provenance.llmBackend` records the mode and, in ApiKey mode, the provider type, endpoint and wire model (never the key; `Description` is tested for that). In ApiKey mode every Cost cell reads `not applicable (ApiKey mode)`: Copilot's premium-request accounting does not apply, and the runtime reported `cost=0` in the probe, which must not be shown as a real measurement. Input/output tokens are reported as before.
- **`tools/copilot-smoke`** now references the host project and reads the same configuration (`appsettings.json` + `SecureYourCode__*` variables, `--appsettings <file>` or `SECUREYOURCODE_APPSETTINGS` to override), so it checks exactly the configured mode: sign-in and quota in Copilot mode; in ApiKey mode `chat` is one round trip through the endpoint, and a wrong key or URL is reported as `model call failed: … (HTTP 401)` with exit 1 (exit 3 = configuration error).
- **`tools/setup.py --mode online|airgapped`.** Air-gapped mode takes `--bundle <folder>` (everything from the bundle) or the mirrors `--nuget-source`, `--pip-index-url`, `--copilot-cli-base-url`, plus `--graphify-python/--graphify-cli` for an existing installation and `--graphify-version`. It skips Node/npm, the standalone Copilot CLI and the sign-in; restores with `--source`; installs Graphify with `--no-index --find-links` (or the index); and serves the bundle's `copilot-cli/` folder over loopback HTTP during the build with `COPILOT_CLI_DOWNLOAD_BASE_URL` pointed at it, because the SDK's targets only download over http(s) (MSBuild `DownloadFile`) and still verify the archive against the bundled `SHA256SUMS.txt`. The alternative `CopilotCliBinaryPath` was rejected: with an explicit binary the targets copy only the binary, not the runtime's asset tree (the cache-folder properties that drive that copy are private).
- **Internal Artifactory instead of a bundle (team confirmed they have one).** All three sources are plain URLs to setup: `--nuget-source` (Artifactory NuGet v3 feed), `--pip-index-url` (Artifactory PyPI `simple` index) and `--copilot-cli-base-url` (a Generic repository; a remote one pointing at `https://github.com` works unchanged because the SDK's download path equals GitHub's release URL layout, `<base>/v1.0.89/github-copilot-1.0.89-<platform>.tgz` + `SHA256SUMS.txt`). Documented in README with the URL shapes. The loopback mirror is then not used, which also avoids a managed Windows proxy policy that might intercept loopback traffic. Feeds must allow anonymous read, since neither `dotnet restore`, the host's Husky tool install nor pip prompt for credentials.
- **`tools/airgap/bundle.py`** (run on a connected machine) collects: every `.nupkg` the solution, `tools/copilot-smoke` and `tools/h1-probe` restore (fresh `--packages` folder, so nothing is missed because it was cached) plus Husky.Net 0.9.1 and its dependencies (a throwaway `--tool-path` install with `NUGET_PACKAGES` pointed at the same folder); the Copilot runtime archives for the chosen platforms in the release layout (`v1.0.89/github-copilot-1.0.89-<platform>.tgz` + `SHA256SUMS.txt`), each verified against the checksum file; and `graphifyy[mcp]` wheels for the target Python/platform (`--skip-python` when an internal index exists). `MANIFEST.json` lists versions and the SHA-256 of every file. Not bundled: the .NET SDK, Python, git (installed from the organization's sources); Node is not needed.
- **`tools/airgap/mock_openai_server.py`**: a test double (fixed reply, `/v1/models`, `/v1/chat/completions` streaming and non-streaming, minimal `/v1/responses`) that logs each request's path, model, streaming flag and whether the bearer key matched. It exists only so the ApiKey wiring can be verified with no model and no network. Not app code.
- **Graphify 0.9.62 versus 0.9.71**: the team's air-gapped machines carry 0.9.62 from their internal index. Both were probed (below) and behave identically for everything the host uses, so `--graphify-version 0.9.62` is supported; 0.9.71 remains the default pin. The host already reads the installed version at runtime and names published graphs with it, so the two can coexist under one StateRoot.
- **The team's long-running Graphify MCP server** (`graphify-mcp graphify-out/graph.json --transport http --port 8765`, for VS Code) is deliberately **not** used by the host: it serves one fixed graph, while the host publishes a graph per commit fingerprint and reports current/stale/none. Switching such a server between graphs would need the `project_path` argument, which the strict handler denies (H1) because it loads any file on disk. The host keeps its per-session stdio server from the same installation. Their `graphify extract .` writes `graphify-out/` into the repository; if the host ever analyses such a repository, that folder must be gitignored, or the untracked files change the fingerprint (documented in README).

### Verification

| Check | Result |
|---|---|
| Builds | Solution, `tools/copilot-smoke`, analyzer Release: 0 warnings, 0 errors |
| Unit tests (H8 adds 23) | `LlmSettingsTests`: default Copilot/auto; mode case-insensitive; unknown mode fails naming `SecureYourCode:Llm:Mode`; ApiKey mode reads the default variable, a custom variable, or the file (variable wins; empty/unreadable/relative file fails); `Description` and `KeySource` never contain the key; `BaseUrl` must be absolute http(s); `WireModel` required; `Type`/`WireApi` validated; `ollama` needs no key; `ToProviderConfig` maps bearer (default) or `ApiKey` + `Azure.ApiVersion`, headers and limits; Cost reads "not applicable" in ApiKey mode; Graphify overrides absolute and set together; `NuGetSource` from option (trimmed, absolute or URL). `OrchestratorTests.ApiKeyMode_…`: a full fake run records `llmBackend`, all Cost cells "not applicable", tokens still summed, and the serialized report does not contain the key. `ReportingTests`: `llmBackend` HTML-encoded like every other dynamic string |
| **BYOK round trip with no sign-in and no network** | `tools/copilot-smoke -- chat` in ApiKey mode inside a fresh Linux network namespace (`unshare -n`, loopback only; `curl https://api.nuget.org` → no route), fresh empty `COPILOT_HOME`, mock endpoint at `http://127.0.0.1:8089/v1` with key `test-key`. Runtime 1.0.89 started; `GetAuthStatusAsync` → not signed in (reported, not enforced); the session's one prompt reached the mock as `POST /v1/chat/completions` with `Authorization: Bearer test-key` (match), `model=mock-model`, non-streaming; usage event `model=mock-model input=10 output=1 cost=0`; reply `OK`; exit 0. Nothing but `installed-plugins/` and `session-state/` was written under `COPILOT_HOME` |
| Wrong key | Same setup with `wrong-key`: the mock logged three `mismatch` requests (the runtime retries 401 twice), then `SendAndWaitAsync` threw "Authentication failed with provider at http://127.0.0.1:8089/v1 (HTTP 401)"; the smoke tool reports it and exits 1 (the host would fail the run as `orchestration_failed`, with baseline candidates still reported, as in H4) |
| **Graphify 0.9.62 vs 0.9.71** (`tools/h1-probe/graphify_mcp_probe.py` on a graph of `test-assets/demo-shop` extracted by each version) | Both: `extract <repo> --code-only --no-cluster --out <dir>` → `<dir>/graphify-out/graph.json`, 107 nodes / 163 edges, identical top-level keys (`nodes, edges, hyperedges, input_tokens, output_tokens, extracted_sources`) and node keys; the source folder untouched. Both expose the same 10 tools; the four the host allows (`query_graph`, `get_node`, `get_neighbors`, `shortest_path`) take the same required arguments and returned the same answers. Only difference: 0.9.71 makes `label` optional on `get_node`/`get_neighbors` and adds `node_id`. `GraphStructure.IsValidGraphFile` accepts both (the 0.9.62 graph passed the H3 integration test in the offline run below) |
| **Air-gapped emulation, run 1** | Fresh copy of the working tree (no `bin/`/`obj/`, no `.git`), fresh `HOME`, fresh `NUGET_PACKAGES`, fresh StateRoot, `unshare -n`; `python tools/setup.py --mode airgapped --bundle <bundle> --check-model` with the mock endpoint on loopback. NuGet restore from the bundle folder, Copilot runtime downloaded from the loopback mirror and SHA-256-verified, analyzer Release + solution + smoke built, Graphify 0.9.71 installed from the wheels, 20 analyzer tests passed, 142/143 host tests passed. **Found:** `HookInstaller_InstallsCommitsAndRepairs` failed because `dotnet tool install husky` hit `https://api.nuget.org/v3/index.json`. Fixed with `NuGetSource` (above) and Husky in the bundle |
| **Air-gapped emulation, runs 2–3** (after the fix) | Run 2 failed only on a new unit test that assumed `SECUREYOURCODE_NUGET_SOURCE` unset (setup sets it for the test run); test corrected to expect the variable's value. **Run 3, same setup, passed end to end in 86 s**: `curl` to nuget.org → no route (000); NuGet restore of the solution and the smoke tool from the bundle folder into a fresh packages folder; "Downloading Copilot CLI 1.0.89 for linux-x64" from the loopback mirror, SHA-256-verified, full runtime asset tree in `bin/`; Graphify 0.9.71 installed with `--no-index --find-links`; **20/20 analyzer and 144/144 host tests passed**, including the Graphify extraction test and the Husky hook test (Husky 0.9.1 came from the bundle through `<StateRoot>/nuget.config`); then `copilot-smoke chat` in ApiKey mode: runtime 1.0.89, sign-in absent and not required, one `POST /v1/chat/completions` with the matching bearer key, reply `OK`, exit 0. `setup.py` printed the ApiKey and NuGetSource reminders and "Environment ready" |
| Online mode unchanged | `python tools/setup.py` default path untouched except `--no-restore` on the builds after an explicit restore. Full test suite in Copilot mode on this machine (Linux x64, .NET SDK 10.0.112): **20/20 analyzer, 144/144 host** |

**Fallbacks:** none. **Not verified here** (needs the team's air-gapped machine): tool calling through their real endpoints (the reviewers depend on OpenAI function calling for `view`/`grep`/`glob` and the Graphify tools), the runtime's behaviour when its telemetry endpoints are unreachable over a long run, and reviewer quality with the internal models (H7 metrics are for Copilot models only). The first `/analyze` there should be recorded in this file.

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

- **Cross-pillar duplicate findings: decided in H7, no change (see H7 › Decisions); kept here as a known limitation.** Found in H4 live run 1: CpuReviewer reported P4's mechanism (a scoped handler subscribed to a singleton hub and never unsubscribed) a second time as `LLM-CPU-01` at `Services/OrderEvents.cs:17`, framed as CPU cost ("each publish invokes every retained handler"), while MemoryReviewer reported it as `LLM-MEM-01` at line 9. The critic kept both. Its duplicate rule covers "the same mechanism at the same location", and these differed in line and category. Consolidation merges only LLM-only findings with the same file, enclosing symbol **and category** (plan §4.5). Against the ground truth, the CPU copy is a false positive (P4 is category Memory): run 1 would score precision 5/6 ≈ 0.83, recall 5/5. Runs 2–4 did not produce it, which is model variance, not a fix. Nothing was changed, to avoid tuning prompts against the known answers. Candidate generic fix for v2: a specialist-prompt rule "report only issues whose root cause belongs to your pillar; do not restate another pillar's issue".
- Copilot usage per plan: GitHub docs (checked 2026-09-29) say all plans include Copilot CLI and Free allows auto model selection only. Free has a small allowance that H1 plus repeated `/analyze` runs (4 sessions each, plus repair prompts) can exhaust, so the developer running H1 and the live demo should use a paid plan.
