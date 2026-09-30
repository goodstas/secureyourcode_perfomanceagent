# SecureYourCode demo script

Five steps, about five minutes: start the host, commit to the demo repo to show the hook-driven graph refresh, run `/analyze`, open `report.html`, score it. The paths below use the default state folder `~/.secureyourcode`; if you set `SECUREYOURCODE_STATE_ROOT`, use that folder instead.

**Before the demo** (once per machine): `python tools/setup.py --login` (see `README.md`). It builds the analyzer in Release, which the host needs at startup, and signs you in to Copilot. One `/analyze` run on the demo repo takes about 1.5 to 2 minutes and uses roughly 15 to 20 Copilot model calls from your own allowance (the recorded run: 16 calls, 16 premium request cost units).

## 1. Start the host

From the repository root:

```bash
dotnet run --project src/SecureYourCode.Agent
```

Wait for `Now listening on: http://127.0.0.1:9876`. At startup the host checks the demo repo (`~/.secureyourcode/demo-repo`), its analyzer reference and its post-commit hook, then refreshes the code graph in the background; the log shows `Background graph refresh: Current`. Keep this terminal visible: the next steps show up in its log.

## 2. Show the hook refresh

In a second terminal, commit a change to the demo repo. A notes file changes the repository fingerprint without touching the demo cases or their line numbers.

PowerShell:

```powershell
cd ~/.secureyourcode/demo-repo
Add-Content demo-log.txt "demo $(Get-Date -Format o)"
git add demo-log.txt
git commit -m "Demo: trigger a graph refresh"
```

bash / zsh:

```bash
cd ~/.secureyourcode/demo-repo
echo "demo $(date -u +%Y-%m-%dT%H:%M:%SZ)" >> demo-log.txt
git add demo-log.txt
git commit -m "Demo: trigger a graph refresh"
```

The commit returns at once (Husky prints `Executing task 'graph-refresh'` and `Successfully executed`). The Husky.Net post-commit hook calls the host (`POST /git-post-commit`, token-protected), and a few seconds later the host log shows `Published graph <fingerprint>-<graphify version>` and `Background graph refresh: Current`. If the host is stopped, the same commit still completes normally; the graph is then refreshed when `/analyze` runs.

## 3. Run the analysis

PowerShell (second terminal, any folder):

```powershell
$token = (Get-Content ~/.secureyourcode/access-token -Raw).Trim()
$report = Invoke-RestMethod -Method Post -Uri http://127.0.0.1:9876/analyze -Headers @{ 'X-SecureYourCode-Token' = $token } -TimeoutSec 1900
$report.run
$report.findings | Format-Table ruleId, category, file, startLine, evidence, confidence, criticDecision
```

bash / zsh:

```bash
curl -sS -X POST --max-time 1900 -H "X-SecureYourCode-Token: $(cat ~/.secureyourcode/access-token)" -o /dev/null -w "HTTP %{http_code}\n" http://127.0.0.1:9876/analyze
```

While it runs (about 100 s), the host runs the static-analysis build, the three specialist sessions (Memory, CPU, Concurrency) one after another, the critic, and the benchmarks. Its log shows `Static analysis: 4 PERF* diagnostics`, one `… verified by <template> (E2)` line per benchmark that passed, and `Published reports to …`; the reviewer sessions themselves are not logged, so this is a good moment to explain the pipeline. The response is the report itself (HTTP 200). Other answers: 401 wrong or missing token, 409 an analysis is already running, 500 `report_publication_failed` (the run finished but its report files could not be written; the body says why).

## 4. Open the report

PowerShell:

```powershell
Invoke-Item "$HOME/.secureyourcode/reports/$(Get-Content ~/.secureyourcode/reports/latest.txt)/report.html"
```

macOS: `open ~/.secureyourcode/reports/$(cat ~/.secureyourcode/reports/latest.txt)/report.html` (Linux: `xdg-open`).

What to point out:

- **Run** and **Provenance**: run status, graph status, the analyzed commit, dirty flag and fingerprint. A banner appears only when the run is not `complete` or the graph is not `current`.
- **Evidence badges**: `E1` means a Roslyn diagnostic (`PERF001`, `PERF003`, `PERF004`); `E0` is an AI-only finding; `E2` means a host-owned benchmark measured the problem. `OrderSummaryService.BuildSummariesAsync` (P1) reaches E2 automatically: repository calls grow 10 → 100 → 1000 with the input size, each size measured in a fresh process.
- **The AI-only finding**: the Memory reviewer finds the event subscription that is never removed in `OrderEvents.cs` (P4), which no analyzer rule covers.
- **Low-confidence candidates** and the collapsed **Rejected by critic** list: the critic's decisions stay visible with their rationale.
- **Token usage** per reviewer, with cost in *premium request cost units* (never money).

## 5. Score it against the ground truth

From the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File tools/evaluate.ps1
```

```bash
sh tools/evaluate.sh
```

It matches the final findings (critic decision `keep` or `downgrade`) one-to-one against `test-assets/ground-truth.json` (five seeded problems, four clean look-alikes), prints true/false positives, false negatives, precision and recall, and writes `metrics.json` into the report folder. The hackathon target is precision ≥ 0.8 and recall ≥ 0.8 on a `complete` run.

Stop the host with Ctrl+C when done.

## If the live call fails during judging: the recorded fallback

Open [`fallback/report.html`](fallback/report.html). It is a **recorded** report from an earlier live run, with a red *RECORDED* banner at the top; say so when you show it. [`fallback/README.md`](fallback/README.md) names the run it came from, and [`fallback/run-log.txt`](fallback/run-log.txt) is that run's host log and scoring. The fallback is never evidence that a live check passed.

### Re-recording the fallback

After a good live run (`complete`, target met): copy `report.json`, `report.html` and `metrics.json` from that report folder into `demo/fallback/` unchanged, except for inserting the RECORDED banner right after `<body>` in `report.html` (copy it from the current file). Save the host console output of the run as `run-log.txt`, replacing your home folder with `~` and the repository folder with `<repo>`, and check that no personal paths, names or tokens remain. Update `fallback/README.md`.
