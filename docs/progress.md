# Progress

Shared across the team. States: not-started, in-progress, passed, fallback, blocked, incomplete.

| Milestone | State | Notes |
|---|---|---|
| Environment setup | passed | `tools/setup.py` installs and verifies everything per developer; checks recorded in `docs/architecture.md` |
| H0 Setup | passed | Host skeleton on `127.0.0.1:9876`, config, StateRoot, `EnsureAccessToken`, OS detection, `test-assets/demo-shop`, `test-assets/ground-truth.json`, demo repo materialization. All done-criteria verified, including a clean-checkout run on an empty StateRoot, seam signature and behaviour checks, and 8 startup failure modes (see `docs/architecture.md` › H0). Tag `h0` |
| H1 Compatibility probe | passed | All eight §4.5 checks recorded in `docs/architecture.md` › H1, no fallback needed. Final H4 configuration: Empty client mode, explicit isolation options, host-written environment context, three tool-name lists, strict handler. Tag `h1` |
| H2 Analyzer | passed | PERF001, PERF003, PERF004 with 20 passing tests; Release build referenced from each developer's demo repo by the host (committed there); the demo build emits exactly P1, P2, P3, P5 and nothing for N1–N3 (see `docs/architecture.md` › H2). Tag `h2` |
| H3 Static analysis + graph | passed | Fingerprint, `GraphifyUpdater` (before/after check, predicate, atomic publish), Husky hook + `/git-post-commit` + worker, SARIF runner/parser with canonical paths, baseline candidates with host-computed symbols. 63/63 tests; a demo commit publishes a new graph; a commit with the host stopped completes cleanly (see `docs/architecture.md` › H3). Tag `h3` |
| H4 Orchestration | passed | `/analyze` with gate and `RunAsync` (30-min run / 10-min session timeouts, cancellation reasons, failure report), three specialists + critic on the H1 final configuration, JSON validation with one repair, consolidation with immutable baseline identity, critic decision semantics, token usage, run-status rules. 104/104 tests; three live runs `complete` (latest: exactly P1–P5, Graphify used by every reviewer). See `docs/architecture.md` › H4. Report files are H6; benchmarks are H5. Tag `h4` |
| H5 Verification | passed | Host-owned templates for all three kinds, required P1 benchmark run automatically, seam check, fresh process per n, kind-specific acceptance, fingerprint-bound E2 with revert. 124/124 tests; a live run reached **E2 for P1** (and P2 via an accepted proposal). See `docs/architecture.md` › H5. Tag `h5` |
| H6 Reports | passed | `report.json` + self-contained `report.html` (no script, CSP, every dynamic value HTML-encoded), transactional publication with `latest.txt`, HTTP 500 on publication failure without changing the run status. 131/131 tests; live run published both files and the HTML was inspected. See `docs/architecture.md` › H6. Tag `h6` |
| H7 Evaluation + demo | passed | `tools/evaluate` (`.ps1` + `.sh` over one tested .NET console; §5 semantics with one-to-one maximum matching; `metrics.json` in the report folder). One live demo run following `demo/DEMO.md`: `complete`, exactly P1–P5, **precision 1.0, recall 1.0, target met**. `demo/fallback/` recorded from that run and labelled. Cross-pillar duplicate: decided, no change (measured 1.0 precision; see `docs/architecture.md` › H7). 141/141 tests. Tag `h7` |

| H8 Air-gapped mode | passed | Team request after H7, not a plan milestone: ApiKey backend, internal dependency sources, npm CLI binary as runtime, SDK 1.0.13/1.0.15, Graphify 0.9.62/0.9.71. One live air-gapped run `complete`, precision 1.0, recall 1.0. See `docs/architecture.md` › H8. Tag `h8` (first H8 commit) |
| H8 audit against plan v1.5 | passed | Nine gaps closed (A1–A9 in `docs/architecture.md` › H8 audit). 176/176 tests (20 analyzer, 156 host) |

Definition-of-Done items (plan §7): **all 14 met**, see `docs/implementation-summary.md`.

## Next concrete action

1. Every developer: `python tools/setup.py --login` (see `README.md`).
2. Branching: all implementation work goes on `implementation/hackathon`, one `H<n>: ...` commit per milestone, each tagged `h<n>` (`git checkout h1` to inspect a milestone, `git revert <commit>` to undo one). Run `git pull --rebase` before pushing; use a short-lived personal branch off `implementation/hackathon` for larger parallel work and merge it back promptly. `main` stays untouched until the demo is ready.
3. Developers who signed in before H1: sign in again with `python tools/setup.py --login` (isolated mode needs the plaintext-config sign-in; see `README.md`).
4. Air-gapped PCs: set `AnalyzerTestHarness=Standalone` (and `CopilotSdkVersion`, `CopilotCliBinaryPath` as needed); see `docs/AIRGAPPED_MANUAL_SETUP.md`.
5. All milestones are done. Rehearse the demo with `demo/DEMO.md` on the machine that will present (each run uses about 16 premium request cost units of that developer's Copilot plan). `main` is updated only when the team decides the demo is ready.
