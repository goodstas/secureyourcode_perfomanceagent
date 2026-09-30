# RECORDED fallback: not a live run

Show these files **only if the live `/analyze` call fails during judging**, and say that they are recorded. They are never evidence that a live check passed (plan §4.8, and honesty rule 6 in plan §0).

| File | What it is |
|---|---|
| `report.html` | The published report of the recorded run, unchanged except for the red *RECORDED* banner after `<body>` and the "RECORDED —" prefix in its title. Open it directly in a browser; it needs no network. |
| `report.json` | The published `report.json` of that run, byte for byte. |
| `metrics.json` | `tools/evaluate` output for that run, byte for byte. |
| `run-log.txt` | The host console and the commands of `demo/DEMO.md` steps 1 to 5 during that run (home folder shown as `~`, repository folder as `<repo>`). |

## The recorded run

- Run `20260930-050822-ce1f`, recorded on 2026-09-30 by following `demo/DEMO.md` on Windows 11 with the pinned versions in `docs/architecture.md`.
- Analyzed demo-repo commit `92544c1` (the step-2 demo commit), dirty: no; graph status `current`; run status **`complete`** in 102 s; 16 model calls, 16 premium request cost units.
- Final findings: exactly the five seeded problems. P1 `OrderSummaryService.BuildSummariesAsync` (`PERF001`, **E2**: 10 → 100 → 1000 repository calls), P2 `NotificationService.NotifyAllAsync` (`PERF003`, **E2**: 10 → 100 → 1000 tasks started), P3 `ReportCache` (`PERF004`, E1), P4 the `OrderEvents` subscription that is never removed (AI-only, E0), P5 `InvoiceService` (`PERF001`, E1, downgraded by the critic to low confidence). Nothing in the four clean look-alikes; nothing rejected.
- Evaluation: TP 5, FP 0, FN 0, precision 1.0, recall 1.0; the target (≥ 0.8 each on a `complete` run) is met.

The commit SHA and fingerprint are from the recording machine's demo repository; they differ on every developer's machine because the demo repo is materialized per developer.
