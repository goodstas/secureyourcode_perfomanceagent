# SecureYourCode

A local performance-review agent for .NET code: a custom Roslyn analyzer, Graphify code graphs and GitHub Copilot SDK reviewer sessions, with host-assigned evidence levels. Specification: [docs/IMPLEMENTATION_PLAN_HACKATHON_v1.5.md](docs/IMPLEMENTATION_PLAN_HACKATHON_v1.5.md). Decisions and verified versions: [docs/architecture.md](docs/architecture.md).

## Developer setup

Each developer sets up their own machine and signs in with their own GitHub account. Nothing account- or machine-specific belongs in this repository.

### Prerequisites

| Tool | Version |
|---|---|
| .NET SDK | any 10.0.x (`global.json` accepts 10.0.100 and later) |
| Python | 3.10 or later |
| Node.js + npm | current LTS (only used to install the Copilot CLI for sign-in) |
| git | any recent version |
| GitHub Copilot | any plan that includes Copilot CLI, on your own account. Free works but has a small monthly allowance, which H1 and repeated `/analyze` runs can use up |

### Run setup

From the repository root:

```bash
python tools/setup.py --login
```

This creates your per-user state folder (`~/.secureyourcode`), installs the pinned Graphify and Copilot CLI into it, builds and tests the solution, opens the browser sign-in for Copilot, and verifies the sign-in through the SDK. Re-running it is safe; after the first time, use `python tools/setup.py` without `--login`.

To also confirm model access with one short prompt (uses a tiny amount of your Copilot quota):

```bash
python tools/setup.py --check-model
```

### Run the host

```bash
dotnet run --project src/SecureYourCode.Agent
```

It listens on `http://127.0.0.1:9876` only. On first start it creates your access token (`~/.secureyourcode/access-token`) and materializes the demo repository (`~/.secureyourcode/demo-repo`) from `test-assets/demo-shop`. To refresh the demo repo after `test-assets/demo-shop` changes, stop the host, delete `~/.secureyourcode/demo-repo`, and start it again.

### Where things live

| What | Where | Shared? |
|---|---|---|
| Source, docs, hook templates, demo project | this repository | yes, via git |
| Graphify venv, Copilot CLI, Copilot sign-in state, graphs, reports, demo repo, access token | `~/.secureyourcode/` (override with the `SECUREYOURCODE_STATE_ROOT` environment variable) | no, per developer |
| Copilot credentials | your OS credential store | no |

Never commit tokens, `~/.secureyourcode` contents, or absolute paths from your machine.
