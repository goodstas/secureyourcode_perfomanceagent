# Working with the implementation plan

This repository is built from one specification: `docs/IMPLEMENTATION_PLAN_HACKATHON_v1.5.md` (the hackathon plan). This file only governs *how* to work through it across sessions.

## First: is this an implementation request?

Reading these files does not authorize changing anything. Proceed only when the operator explicitly asks for implementation (for example: "Implement SecureYourCode using AGENTS.md and docs/IMPLEMENTATION_PLAN_HACKATHON_v1.5.md. This is an implementation request."). If the request is ambiguous, treat it as a review or discussion request and say so.

## Every session, start here

1. If `docs/architecture.md` exists, read it first. It records pinned versions, the compatibility-probe results, and every decision and fallback taken so far. Do not re-decide those without a concrete new reason; if you change one, record why. If it doesn't exist yet, this is the first session — create it.
2. Read the full `docs/IMPLEMENTATION_PLAN_HACKATHON_v1.5.md`. It is short enough to read every time.
3. Work out the next milestone from the repository's actual state, the git log (`H<n>:` commits), and `docs/architecture.md` — not from memory of an earlier conversation.

## Keep moving

- Work through the milestones in plan section 6 without asking for approval of routine decisions or milestone transitions.
- Respect each milestone's time budget. If a milestone runs over, apply its stated fallback, record it, and move on. Do not polish beyond the definition of done (plan section 7).
- Stop and ask only for the blocking conditions in plan section 0. If one part is blocked, finish all independent work first.
- At the end of each milestone: build green, tests passing, commit as `H<n>: <summary>`, update `docs/architecture.md`.

## Done means

Every item in plan section 7 is either met, or recorded as not met with the reason. Follow the honesty rules in plan section 0: never report a check as passed when it wasn't, never invent measurements, and never present the demo fallback as a live result.

## Final summary

When all milestones are finished (or the remaining ones are blocked), end with a summary that lists:

- the milestones completed, and any that are blocked or unfinished, with the reason
- every test and check you actually executed, with its result
- the Definition-of-Done checklist (plan section 7), marking each item met, not met, or blocked
- every fallback taken
- known limitations
- the exact commands to start the host and run the demo
