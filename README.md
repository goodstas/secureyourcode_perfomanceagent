# SecureYourCode

A local performance-review agent for .NET code: a custom Roslyn analyzer, Graphify code graphs and GitHub Copilot SDK reviewer sessions, with host-assigned evidence levels. Specification: [docs/IMPLEMENTATION_PLAN_HACKATHON_v1.5.md](docs/IMPLEMENTATION_PLAN_HACKATHON_v1.5.md). Decisions and verified versions: [docs/architecture.md](docs/architecture.md).

## Developer setup

Each developer sets up their own machine and signs in with their own GitHub account. Nothing account- or machine-specific belongs in this repository.

### Prerequisites

| Tool | Version |
|---|---|
| .NET SDK | any 10.0.x (`global.json` accepts 10.0.100 and later) |
| Python | 3.10 or later |
| Node.js + npm | current LTS (online mode only: used to install the Copilot CLI for sign-in) |
| git | any recent version |
| GitHub Copilot | online mode only: any plan that includes Copilot CLI, on your own account. Free works but has a small monthly allowance, which H1 and repeated `/analyze` runs can use up |

The host has two model backends, chosen in `src/SecureYourCode.Agent/appsettings.json` under `SecureYourCode:Llm:Mode`:

| Mode | Model calls go to | Sign-in | Typical use |
|---|---|---|---|
| `Copilot` (default) | GitHub Copilot, through your own GitHub account | `copilot login` (per developer) | Connected machines |
| `ApiKey` | Your own OpenAI-compatible endpoint, with an API key | none | Air-gapped environments; see [Air-gapped setup](#air-gapped-setup) |

The two modes never fall back to each other: a misconfigured mode stops the host at startup with a message naming the setting.

### Run setup (online, Copilot mode)

From the repository root:

```bash
python tools/setup.py --login
```

This creates your per-user state folder (`~/.secureyourcode`), installs the pinned Graphify and Copilot CLI into it, builds and tests the solution, opens the browser sign-in for Copilot, and verifies the sign-in through the SDK. Re-running it is safe; after the first time, use `python tools/setup.py` without `--login`.

During sign-in the CLI asks **"System keychain unavailable. Store token in plaintext config file? (y/N)"**. Answer **`y`**. The app uses the Copilot SDK's isolated client mode, which deliberately ignores the system keychain and reads credentials only from `~/.secureyourcode/copilot` (your user profile, outside the repository). Treat that file like a password. If you prefer a narrower credential, sign in with a fine-grained personal access token that has only the "Copilot Requests" permission (`copilot login --with-token`, same environment variables as `setup.py` uses).

To also confirm model access with one short prompt (uses a tiny amount of your Copilot quota):

```bash
python tools/setup.py --check-model
```

### Air-gapped setup

For a step-by-step manual setup on a work PC that uses an already installed Copilot CLI and internal Artifactory feeds, without `setup.py` or a bundle, see [docs/AIRGAPPED_MANUAL_SETUP.md](docs/AIRGAPPED_MANUAL_SETUP.md). The rest of this section describes the scripted routes.

An air-gapped machine has no route to nuget.org, PyPI, npm or GitHub, and no GitHub Copilot sign-in. The host then runs in **ApiKey mode** against your organization's model endpoint, and every dependency comes from a bundle or from internal mirrors. Nothing else changes: the same Copilot runtime, the same reviewer agents, the same read-only permission handler and the same Graphify tools.

**1. On a connected machine**, make the bundle (packages, the pinned Copilot runtime archives, Graphify wheels):

```bash
python tools/airgap/bundle.py <bundle-folder> --platforms win32-x64,linux-x64
```

`--platforms` names the operating systems of the air-gapped machines. Add `--python-version 3.12 --python-platform win_amd64` when the target Python differs from the machine making the bundle, or `--skip-python` when an internal Python package index exists. The bundle holds a `MANIFEST.json` with the SHA-256 of every file. Carry the folder across.

**2. On the air-gapped machine**, with the .NET 10 SDK, Python 3.10+ and git installed from your own sources (Node.js is not needed):

```bash
python tools/setup.py --mode airgapped --bundle <bundle-folder>
```

This installs Graphify from the bundle's wheels, restores NuGet packages from the bundle, serves the bundle's Copilot runtime archive to the SDK's build over loopback (the SDK still verifies its SHA-256), builds, and runs the tests. Instead of a bundle you can point at internal mirrors: `--nuget-source <folder or feed URL>`, `--pip-index-url <url>`, and `--copilot-cli-base-url <url>` (a mirror of `github/copilot-cli` releases, serving `v1.0.89/github-copilot-1.0.89-<platform>.tgz` and `SHA256SUMS.txt`). An existing Graphify installation can be used with `--graphify-python <interpreter> --graphify-cli <graphify executable>`; `--graphify-version 0.9.62` installs that verified version instead of the default 0.9.71.

**Using Artifactory instead of a bundle.** When the air-gapped network reaches an internal Artifactory, nothing has to be carried across: every source becomes an internal URL. Artifactory's NuGet and PyPI repositories (local, or remote ones that proxy nuget.org and PyPI) serve the packages, and its npm repository serves the Copilot runtime, the same `@github/copilot` package you already install the Copilot CLI from:

```
python tools/setup.py --mode airgapped ^
  --nuget-source https://<artifactory>/artifactory/api/nuget/v3/<nuget-repo> ^
  --pip-index-url https://<artifactory>/artifactory/api/pypi/<pypi-repo>/simple ^
  --copilot-npm-registry https://<artifactory>/artifactory/api/npm/<npm-repo>/
```

Setup installs `@github/copilot@1.0.89` under `~/.secureyourcode/copilot-cli` from that registry (Node.js and npm are then prerequisites, as for the CLI itself) and hands the native binary inside the platform package, `node_modules/@github/copilot-<platform>/copilot[.exe]`, to the SDK as `CopilotCliBinaryPath`. The SDK then skips its release-archive download and drives that binary over stdio; everything else, including ApiKey mode, is unchanged. The SDK version itself is a build property, `CopilotSdkVersion` in `Directory.Build.props` (default 1.0.15; `--sdk-version 1.0.13` in setup, or the environment variable, selects the other verified version when a feed carries only that one). SDK 1.0.15 is built for CLI 1.0.89 and 1.0.13 for 1.0.83; if your registry does not carry the pinned one, `--copilot-cli-version 1.0.83` installs that one, which was verified to work with the SDK (the SDK checks protocol compatibility itself when it starts the binary, and the backend check prints the version it actually ran). If the CLI is already installed on the machine, `--copilot-cli-binary <path to that binary>` uses it directly and npm is not needed at all; a global npm install puts it at `%APPDATA%\npm\node_modules\@github\copilot-win32-x64\copilot.exe` on Windows. Versions 1.0.89, 1.0.83 and 1.0.76 were verified with the SDK. Do not judge a binary by `copilot --version`: it prints the newest known version, not its own (a 1.0.83 binary prints 1.0.89); setup reads the version from the `package.json` next to the binary. **Set the environment variable `CopilotCliBinaryPath` to that path permanently** afterwards (setup prints it): MSBuild reads it as a property, so every later `dotnet build` or `dotnet run` uses the same runtime instead of trying to download the archive.

A Generic repository works too, if you have one: either a **remote** one pointing at `https://github.com` or a **local** one holding the bundle's `copilot-cli/v1.0.89/` folder, with `--copilot-cli-base-url https://<artifactory>/artifactory/<generic-repo>/github/copilot-cli/releases/download` (remote) or `.../<generic-repo>` (local). The SDK verifies the archive against `SHA256SUMS.txt`, so both files must pass through unchanged.

**Analyzer tests in air-gapped mode.** The analyzer tests use `Microsoft.CodeAnalysis.CSharp.Analyzer.Testing` by default (plan §4.3). Its packages are often missing from internal feeds, and it fetches reference assemblies from nuget.org at test time, so `--mode airgapped` builds with `AnalyzerTestHarness=Standalone` instead: a self-contained harness with the same tests that needs only `Microsoft.CodeAnalysis.CSharp`. Set the environment variable `AnalyzerTestHarness=Standalone` permanently on such a machine (setup prints the reminder), or pass `--analyzer-test-harness Testing` when your feed does carry the framework.

Put the NuGet URL into `SecureYourCode:NuGetSource` so the host can install the Husky.Net hook tool at startup. The feeds must allow anonymous read from the machine: `dotnet restore`, the host's tool install, `pip` and `npm` do not prompt for credentials (a `NuGet.Config` with `packageSourceCredentials`, a `pip.conf`, and the `.npmrc` you already use for the CLI are the usual answers if they do not). The bundle remains the fallback for a machine that cannot reach Artifactory.

**3. Configure ApiKey mode.** Copy `tools/airgap/appsettings.ApiKey.example.json` over `src/SecureYourCode.Agent/appsettings.json` (or set the same keys as `SecureYourCode__Llm__...` environment variables) and fill in:

| Setting | Meaning |
|---|---|
| `Llm:Mode` | `ApiKey` |
| `Llm:Provider:Type` | `openai` for any OpenAI-compatible server (default); also `azure`, `anthropic`, `ollama` |
| `Llm:Provider:BaseUrl` | The endpoint, for example `http://models.internal:8000/v1` |
| `Llm:Provider:WireApi` | `chat-completions` (default) or `responses` |
| `Llm:Provider:WireModel` | The model name your endpoint expects (your internal name) |
| `Llm:Provider:ModelId` | Optional: a well-known model id the runtime uses to pick prompting and token limits (for example the public model your internal one is based on). Empty: same as `WireModel` |
| `Llm:Provider:ApiKeyEnvironmentVariable` | Where the key comes from; default `SECUREYOURCODE_LLM_API_KEY` |
| `Llm:Provider:ApiKeyFile` | Alternative: an absolute path to a file containing only the key, used when the variable is unset |
| `Graphify:Python`, `Graphify:Cli` | Only when Graphify is not in the venv that setup creates: the interpreter and CLI of your installation (both or neither) |

**The key never goes into `appsettings.json`** or the repository; put it in the environment variable or a protected file. The report shows the mode, endpoint and model name, never the key, and its cost column reads "not applicable (ApiKey mode)" because Copilot's premium-request accounting does not apply.

Then check the round trip with one short prompt against your endpoint:

```bash
python tools/setup.py --mode airgapped --bundle <bundle-folder> --check-model
```

or directly `dotnet run --project tools/copilot-smoke -- chat`. A wrong key or URL is reported as `model call failed: Authentication failed with provider at <url> (HTTP 401)` or similar.

**Verified so far** (see `docs/architecture.md`, H8): the whole setup, build and tests from a bundle with no network at all, and ApiKey mode round trips through the Copilot runtime to an OpenAI-compatible test endpoint with no GitHub sign-in. Not yet verified: tool calling (the reviewers' `view`/`grep`/`glob` and Graphify calls) against your real models, which depends on the endpoint supporting OpenAI function calling. Run one `/analyze` there and record the outcome in `docs/architecture.md`.

If your team also keeps a long-running Graphify MCP server for VS Code, leave it running; the host does not use it. It builds its own graph per commit under `~/.secureyourcode/graphs/` and starts its own short-lived Graphify server per reviewer session from the same installation. If you run `graphify extract .` inside a repository the host analyses, add `graphify-out/` to that repository's `.gitignore`, or the untracked folder changes the fingerprint and the graph is reported stale.

### Run the host

```bash
dotnet run --project src/SecureYourCode.Agent
```

It listens on `http://127.0.0.1:9876` only. On first start it creates your access token (`~/.secureyourcode/access-token`) and materializes the demo repository (`~/.secureyourcode/demo-repo`) from `test-assets/demo-shop`. At every start it makes sure the demo project references the Release build of the analyzer (`dotnet build src/SecureYourCode.PerformanceAnalyzer -c Release`, done by `tools/setup.py`) and commits that change in the demo repo; the host refuses to start if the analyzer has not been built. On Windows, if rebuilding the analyzer fails because the file is in use, run `dotnet build-server shutdown` first (the compiler server keeps loaded analyzers open).

The host also installs a Husky.Net post-commit hook into the demo repo (never into this repository). Each commit there tells the running host to refresh the code graph (`POST /git-post-commit`, token-protected); if the host is stopped, commits still complete normally. Graphs are published under `~/.secureyourcode/graphs/`. To refresh the demo repo after `test-assets/demo-shop` changes, stop the host, delete `~/.secureyourcode/demo-repo`, and start it again.

### Analyze, evaluate, demo

With the host running, `POST http://127.0.0.1:9876/analyze` with the header `X-SecureYourCode-Token: <contents of ~/.secureyourcode/access-token>` runs the full analysis (about 2 minutes) and publishes `report.json` and `report.html` under `~/.secureyourcode/reports/` (`latest.txt` names the newest folder). Score a report against `test-assets/ground-truth.json` with:

```bash
sh tools/evaluate.sh
```

or on Windows `powershell -ExecutionPolicy Bypass -File tools/evaluate.ps1`. Both write `metrics.json` into the report folder. The step-by-step demo, with PowerShell and bash commands, is [demo/DEMO.md](demo/DEMO.md); `demo/fallback/` is a recorded run, to be shown only if the live call fails.

### Where things live

| What | Where | Shared? |
|---|---|---|
| Source, docs, hook templates, demo project | this repository | yes, via git |
| Graphify venv, Copilot CLI, Copilot sign-in state, graphs, reports, demo repo, access token | `~/.secureyourcode/` (override with the `SECUREYOURCODE_STATE_ROOT` environment variable) | no, per developer |
| Copilot credentials (Copilot mode) | `~/.secureyourcode/copilot` (the plaintext config written at sign-in; treat it like a password) | no |
| Endpoint API key (ApiKey mode) | the `SECUREYOURCODE_LLM_API_KEY` environment variable or the file named in `Llm:Provider:ApiKeyFile` | no |
| Offline bundle | wherever you carried it; not in this repository | no |

Never commit tokens, `~/.secureyourcode` contents, or absolute paths from your machine.
