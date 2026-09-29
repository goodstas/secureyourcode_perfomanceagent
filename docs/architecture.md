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

## Open items for H1 (not yet verified — do not assume)

- Copilot CLI isolation flags seen in `copilot --help`: `--no-custom-instructions`, `--disable-builtin-mcps`, `--allow-tool`/`--deny-tool`, `--deny-url`. The SDK-level equivalents must be found in the SDK API (probe step 8).
- Copilot usage per plan: GitHub docs (checked 2026-09-29) say all plans include Copilot CLI and Free allows auto model selection only. Free has a small allowance that H1 plus repeated `/analyze` runs (4 sessions each, plus repair prompts) can exhaust, so the developer running H1 and the live demo should use a paid plan.
