# Progress

Shared across the team. States: not-started, in-progress, passed, fallback, blocked, incomplete.

| Milestone | State | Notes |
|---|---|---|
| Environment setup | passed | `tools/setup.py` installs and verifies everything per developer; checks recorded in `docs/architecture.md` |
| H0 Setup | passed | Host skeleton on `127.0.0.1:9876`, config, StateRoot, `EnsureAccessToken`, OS detection, `test-assets/demo-shop`, `test-assets/ground-truth.json`, demo repo materialization. All done-criteria verified, including a clean-checkout run on an empty StateRoot, seam signature and behaviour checks, and 8 startup failure modes (see `docs/architecture.md` › H0). Tag `h0` |
| H1 Compatibility probe | passed | All eight §4.5 checks recorded in `docs/architecture.md` › H1, no fallback needed. Final H4 configuration: Empty client mode, explicit isolation options, host-written environment context, three tool-name lists, strict handler. Tag `h1` |
| H2 Analyzer | passed | PERF001, PERF003, PERF004 with 20 passing tests; Release build referenced from each developer's demo repo by the host (committed there); the demo build emits exactly P1, P2, P3, P5 and nothing for N1–N3 (see `docs/architecture.md` › H2). Tag `h2` |
| H3 Static analysis + graph | not-started | |
| H4 Orchestration | not-started | Depends on H1 |
| H5 Verification | not-started | |
| H6 Reports | not-started | |
| H7 Evaluation + demo | not-started | |

Definition-of-Done items (plan §7): none assessed yet.

## Next concrete action

1. Every developer: `python tools/setup.py --login` (see `README.md`).
2. Branching: all implementation work goes on `implementation/hackathon`, one `H<n>: ...` commit per milestone, each tagged `h<n>` (`git checkout h1` to inspect a milestone, `git revert <commit>` to undo one). Run `git pull --rebase` before pushing; use a short-lived personal branch off `implementation/hackathon` for larger parallel work and merge it back promptly. `main` stays untouched until the demo is ready.
3. Developers who signed in before H1: sign in again with `python tools/setup.py --login` (isolated mode needs the plaintext-config sign-in; see `README.md`).
4. Next milestone: H3 (static analysis + graph). Use `/p:ErrorLog=<path>%2Cversion=2` for SARIF 2.1.0 (see H2 finding). H4 (orchestration) builds on the H1 final configuration.
