# Manual setup on an air-gapped Windows PC

This guide sets up SecureYourCode by hand on a work PC that can reach only internal company services: an Artifactory with NuGet and PyPI repositories, your own model endpoints, and nothing else. It does not use `tools/setup.py` or a bundle. Everything comes from what is already installed or available on the internal network.

How the pieces fit: the host is a .NET program. To talk to a model it starts one `copilot.exe` as a child process and speaks to it over stdin and stdout. That `copilot.exe` is the agent engine: it runs the reviewer prompts, reads files, queries Graphify and sends the conversation to a model. In **ApiKey mode** the host tells it to send everything to your endpoint with your key, so no GitHub account is involved. The `copilot.exe` inside your existing Copilot CLI installation serves as that engine. Versions 1.0.76, 1.0.83 and 1.0.89 were verified with the SDK.

## 0. What you need on the PC

| Item | Where it comes from | How to check |
|---|---|---|
| .NET 10 SDK (any 10.0.x) | your internal installer source | `dotnet --list-sdks` shows a `10.0.` line |
| Visual Studio 2022 or later, or just the .NET SDK and a terminal | | |
| git on the PATH | | `git --version` |
| Python 3.10 or later, with pip pointed at Artifactory's PyPI | | `python --version`, `pip config list` |
| GitHub Copilot CLI installed with npm (1.0.76 or later) | Artifactory's npm repository, already done on your PCs | see step 2 |
| The repository, branch `implementation/hackathon` | copy it in as a folder or zip; it does not need to be a git clone | `SecureYourCode.slnx` is in the root |
| Your model endpoint: base URL, model name, API key | whoever configured the Copilot CLI for your models | |

NuGet packages the build needs, which Artifactory must be able to serve (remote repositories proxying nuget.org usually have them all):

| Package | Version |
|---|---|
| `GitHub.Copilot.SDK` | 1.0.15, or 1.0.13 (see step 1a) |
| `Microsoft.CodeAnalysis.CSharp` | 5.0.0 (keep at 5.0.0: an analyzer must not reference a newer Roslyn than the compiler in your .NET SDK) |
| `Microsoft.CodeAnalysis.Analyzers` | 5.3.0 |
| `Microsoft.NET.Test.Sdk` | 17.14.1 |
| `xunit` | 2.9.3 |
| `xunit.runner.visualstudio` | 3.1.4 |
| `coverlet.collector` | 6.0.4 |
| `Husky` (a .NET tool, installed by the host at startup; optional with `"InstallGitHook": false`) | 0.9.1 |

## 1. NuGet: point everything at Artifactory

In Visual Studio: **Tools → Options → NuGet Package Manager → Package Sources**. Add your Artifactory NuGet feed, for example `https://<artifactory>/artifactory/api/nuget/v3/<repo>`, and **untick nuget.org**. From a terminal the same thing is:

```
dotnet nuget add source https://<artifactory>/artifactory/api/nuget/v3/<repo> --name artifactory
dotnet nuget disable source nuget.org
```

Why nuget.org must be disabled: at every start the host installs the Husky hook tool into the demo repository with `dotnet tool install`, which contacts every enabled source. An unreachable nuget.org makes that fail. If you prefer to keep nuget.org enabled for other projects, set `SecureYourCode:NuGetSource` in appsettings.json (step 5) to the Artifactory feed URL instead; the host then uses only that source for the tool install.

## 1a. If your feed carries GitHub.Copilot.SDK 1.0.13 instead of 1.0.15

The projects reference the SDK through one build property, `CopilotSdkVersion`, defined in `Directory.Build.props` with 1.0.15 as default. Set a **user environment variable** to switch every build to the version your feed has:

| Variable | Value |
|---|---|
| `CopilotSdkVersion` | `1.0.13` |

MSBuild reads it as a property, so Visual Studio and `dotnet build` both pick it up (restart them after setting it). SDK 1.0.13 was verified with the host: it builds, all tests pass, and ApiKey mode runs over the CLI binary. It pins Copilot CLI 1.0.83 for its own download, but with step 2 it uses the CLI you point it at (1.0.76, 1.0.83 and 1.0.89 verified).

## 2. The Copilot runtime: use your installed CLI

Find the native binary inside your npm installation. For a global install:

```
npm root -g
```

prints the global `node_modules` folder, typically `%APPDATA%\npm\node_modules`. The binary is:

```
<that folder>\@github\copilot-win32-x64\copilot.exe
```

Check its version by opening the `package.json` next to it and reading `"version"`. Do not use `copilot --version`: it prints the newest version it knows about, not its own.

Set a **user environment variable** (Start → "Edit environment variables for your account"):

| Variable | Value |
|---|---|
| `CopilotCliBinaryPath` | the full path to that `copilot.exe` |

MSBuild reads environment variables as build properties, so with this set, the SDK's build step copies your binary next to the host instead of downloading one from GitHub. **Restart Visual Studio and any open terminals** so they see the variable.

## 3. Build

Build the analyzer in **Release** first; the host refuses to start without that DLL, because the demo repository references it. Then build the solution and the check tool:

```
dotnet build src\SecureYourCode.PerformanceAnalyzer -c Release
dotnet build SecureYourCode.slnx
dotnet build tools\copilot-smoke
```

In Visual Studio the same: set the configuration to Release, build the `SecureYourCode.PerformanceAnalyzer` project, switch back to Debug, build the solution, then build `tools\copilot-smoke\copilot-smoke.csproj` (it is not part of the solution; open it separately or use the command above).

Verify that the build used your binary:

```
dir src\SecureYourCode.Agent\bin\Debug\net10.0\runtimes\win-x64\native\
```

should list `copilot.exe` and a file named `.copilot-explicit-cli`. If instead the build tried to download, the error names `github.com/github/copilot-cli`: the environment variable is not visible to that process (step 2, restart).

## 4. Graphify

Install `graphifyy` with its MCP extra from Artifactory's PyPI into a virtual environment at the location the host looks in by default:

```
python -m venv %USERPROFILE%\.secureyourcode\graphify-venv
%USERPROFILE%\.secureyourcode\graphify-venv\Scripts\pip install "graphifyy[mcp]==0.9.62"
%USERPROFILE%\.secureyourcode\graphify-venv\Scripts\python -c "import graphify.serve; print('ok')"
```

0.9.62 and 0.9.71 are both verified; use whichever Artifactory has. With the venv at that path, no configuration is needed. If you would rather use an existing interpreter that already has `graphifyy[mcp]` (for example the one your VS Code Graphify server uses), set both `SecureYourCode:Graphify:Python` and `SecureYourCode:Graphify:Cli` in step 5 to its `python.exe` and `graphify.exe`.

## 5. Configure ApiKey mode

Edit `src\SecureYourCode.Agent\appsettings.json`. A complete example is in `tools\airgap\appsettings.ApiKey.example.json`; the `SecureYourCode` section should look like this:

```json
"SecureYourCode": {
  "StateRoot": "",
  "AppWorkspace": "",
  "Model": "auto",
  "NuGetSource": "",
  "Llm": {
    "Mode": "ApiKey",
    "Provider": {
      "Type": "openai",
      "BaseUrl": "http://<your model endpoint>/v1",
      "WireApi": "chat-completions",
      "WireModel": "<the model name your endpoint expects>",
      "ModelId": "",
      "ApiKeyEnvironmentVariable": "SECUREYOURCODE_LLM_API_KEY",
      "ApiKeyFile": ""
    }
  },
  "Graphify": {
    "Python": "",
    "Cli": ""
  }
}
```

The values come from how your Copilot CLI is configured for your models; the names map one to one:

| Copilot CLI setting | appsettings.json |
|---|---|
| `COPILOT_PROVIDER_TYPE` | `Llm:Provider:Type` (`openai` for any OpenAI-compatible server) |
| `COPILOT_PROVIDER_BASE_URL` | `Llm:Provider:BaseUrl` |
| `COPILOT_PROVIDER_WIRE_API` | `Llm:Provider:WireApi` (`chat-completions` or `responses`) |
| `COPILOT_PROVIDER_WIRE_MODEL` or `COPILOT_MODEL` | `Llm:Provider:WireModel` |
| `COPILOT_PROVIDER_MODEL_ID` | `Llm:Provider:ModelId` (optional; a well-known model name the engine uses to pick prompting and limits) |
| `COPILOT_PROVIDER_BEARER_TOKEN` or `COPILOT_PROVIDER_API_KEY` | the key, in the environment variable below |

The key itself never goes into appsettings.json. Set a second **user environment variable**:

| Variable | Value |
|---|---|
| `SECUREYOURCODE_LLM_API_KEY` | your API key |

(Or put the key alone in a file and set `ApiKeyFile` to its absolute path.) The CLI's own `COPILOT_PROVIDER_*` variables are not read by the host, and if they are set on the machine they do not interfere.

Restart Visual Studio and terminals again after adding the variable.

## 6. Check the model connection

```
dotnet run --project tools\copilot-smoke --no-build -- chat
```

Expected output ends with:

```
llm backend: ApiKey (openai endpoint http://.../v1, model <name>) (key from environment variable SECUREYOURCODE_LLM_API_KEY)
runtime: 1.0.76 (protocol 3)
github sign-in (not required in ApiKey mode): absent
usage: model=<name> input=... output=...
reply: OK
```

`runtime:` shows the real version of the binary the host runs. A wrong key or URL ends with `model call failed: Authentication failed with provider at ... (HTTP 401)` or a connection error naming the URL.

## 7. Start the host

```
dotnet run --project src\SecureYourCode.Agent
```

or F5 on `SecureYourCode.Agent` in Visual Studio. The first start takes a minute: it creates `%USERPROFILE%\.secureyourcode`, writes the access token, copies the demo project into `%USERPROFILE%\.secureyourcode\demo-repo` and commits it, installs the Husky hook there (this is the NuGet tool install from step 1), and extracts the first code graph. The log should show, among others:

```
LLM backend: ApiKey (openai endpoint ..., model ...); key from environment variable SECUREYOURCODE_LLM_API_KEY
Graphify: ...\graphify-venv\Scripts\python.exe / ...\graphify-venv\Scripts\graphify.exe
Graph-refresh hook installed ...
Published graph <fingerprint>-0.9.62
Now listening on: http://127.0.0.1:9876
```

## 8. Run an analysis

In PowerShell, with the host running:

```powershell
$token = Get-Content "$env:USERPROFILE\.secureyourcode\access-token" -Raw
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:9876/analyze -Headers @{ "X-SecureYourCode-Token" = $token.Trim() } -TimeoutSec 1800
```

The reports land in `%USERPROFILE%\.secureyourcode\reports\<run>\report.html` and `report.json`; `reports\latest.txt` names the newest folder. Score the run against the demo's ground truth with:

```powershell
powershell -ExecutionPolicy Bypass -File tools\evaluate.ps1
```

The full demo walkthrough is `demo\DEMO.md`. The report's provenance section shows the backend, and its cost column reads "not applicable (ApiKey mode)".

**Please record the outcome of the first run** (run status, whether the reviewers' Graphify calls and file reads worked, precision and recall from `metrics.json`) in `docs\architecture.md` under H8. Tool calling with your models is the one part that could not be verified outside your network.

## Troubleshooting

| Message | Cause and fix |
|---|---|
| build: `Copilot CLI binary not found at '...'` or a download attempt from `github.com/github/copilot-cli` | `CopilotCliBinaryPath` is unset, wrong, or not visible to this process. Check the path, restart Visual Studio or the terminal |
| host start: `dotnet tool install husky --version 0.9.1 failed` / downloading Husky failed | the feed has no package `Husky` 0.9.1 (it has no dependencies of its own), or the tool install used an unreachable source. Request `Husky` 0.9.1 for the feed. Until then set `"InstallGitHook": false` in appsettings.json: the host starts without the commit hook and refreshes the graph at startup and at every `/analyze` instead |
| `Unable to load the service index for source https://api.nuget.org/v3/index.json` | nuget.org is still an enabled source. Disable it (step 1) or set `SecureYourCode:NuGetSource` to the Artifactory feed |
| host start: `SecureYourCode:Llm:Mode must be 'Copilot' or 'ApiKey'`, `...BaseUrl must be an absolute http(s) URL`, `...WireModel ... is required`, `No API key found in ApiKey mode` | appsettings.json or the key variable is incomplete; the message names the setting |
| `Copilot is not signed in for the isolated client` | `Llm:Mode` is still `Copilot`. Set it to `ApiKey` |
| `Authentication failed with provider at <url> (HTTP 401)` | wrong key, or the endpoint expects the key in a different header. Try `"UseBearerToken": false` in the Provider section to send it as an API-key header instead of `Authorization: Bearer` |
| `SDK protocol version mismatch` | the CLI binary is too old or too new for the SDK. 1.0.76, 1.0.83 and 1.0.89 are verified with SDK 1.0.13 and 1.0.15 |
| restore: `Unable to find package GitHub.Copilot.SDK with version (= 1.0.15)` | the feed has only 1.0.13: step 1a |
| `The SecureYourCode analyzer is not built: ... is missing` | step 3, Release build of `SecureYourCode.PerformanceAnalyzer` |
| `graphify extract failed` or `Could not read the installed graphifyy version` | step 4: the venv is missing, or `Graphify:Python`/`Graphify:Cli` point to the wrong files (set both or neither) |
| `/analyze` → run status `failed`, reason `orchestration_failed` | the engine could not complete a reviewer session; the report's reviewer section and the host log carry the endpoint's error. Baseline analyzer findings are still reported |
| hook test or host start hangs for minutes at `dotnet tool install` | a NuGet source is unreachable but not refusing connections (firewall drop). Remove it from the sources |

## What is where

| What | Where |
|---|---|
| Per-user state: demo repo, graphs, reports, Graphify venv, access token | `%USERPROFILE%\.secureyourcode` (override with the `SECUREYOURCODE_STATE_ROOT` variable) |
| The engine's isolated home (no sign-in in ApiKey mode; your CLI's own settings are not touched) | `%USERPROFILE%\.secureyourcode\copilot` |
| The API key | `SECUREYOURCODE_LLM_API_KEY` or the `ApiKeyFile` you named, never the repository |
| Build-time runtime choice | the `CopilotCliBinaryPath` variable |
| Build-time SDK version choice | the `CopilotSdkVersion` variable (default 1.0.15) |
