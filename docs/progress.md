# Progress

Shared across the team. States: not-started, in-progress, passed, fallback, blocked, incomplete.

| Milestone | State | Notes |
|---|---|---|
| Environment setup | passed | `tools/setup.py` installs and verifies everything per developer; checks recorded in `docs/architecture.md` |
| H0 Setup | passed | Host skeleton on `127.0.0.1:9876`, config, StateRoot, `EnsureAccessToken`, OS detection, `test-assets/demo-shop`, `test-assets/ground-truth.json`, demo repo materialization. All done-criteria verified, including a clean-checkout run on an empty StateRoot, seam signature and behaviour checks, and 8 startup failure modes (see `docs/architecture.md` › H0). Committed on branch `feature/h0-setup` |
| H1 Compatibility probe | not-started | Needs a developer who has run `tools/setup.py --login` |
| H2 Analyzer | not-started | Project and test project exist with pinned Roslyn packages |
| H3 Static analysis + graph | not-started | |
| H4 Orchestration | not-started | Depends on H1 |
| H5 Verification | not-started | |
| H6 Reports | not-started | |
| H7 Evaluation + demo | not-started | |

Definition-of-Done items (plan §7): none assessed yet.

## Next concrete action

1. Every developer: `python tools/setup.py --login` (see `README.md`).
2. Branching: one `feature/h<n>-...` branch per milestone, reviewed and merged into `main`.
3. Next milestone: H1 (compatibility probe), which needs a signed-in developer. H2 (analyzer) does not depend on H1.
