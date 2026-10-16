# M10 — Developer Agent

Status: **M10.1 implemented** on 2026-10-16 (authoritative workspace, protected rules, long-lived
sandbox session, deterministic Git diff). M10.2 (agent loop, budgets, traceability; first paid run
with gpt-5.4-mini at its end) and M10.3 (development jobs, evidence, API, diff view, StockFlow Product
CRUD acceptance) are planned. Design and decisions: [Roadmap](ROADMAP.md#m10-design-2026-10-16-decided),
[Decisions](DECISIONS.md#m10-developer-agent-model-and-budget-2026-10-16-user-decision). This document
describes inspected code and executed local verification, not production readiness.

## M10.1 — workspace, sandbox session and Git

- **Authoritative workspace** (`Factory.Core.Development.DevelopmentWorkspace`): starts from a generated
  application (M9.1) and is the only source of truth for the code. Reads, listings and literal searches
  are answered from it; writes and deletes go through it. Untrusted code that changes files in the
  sandbox while building or testing can never alter what is diffed or verified.
- **Rules**: paths must be safe and relative and may not touch `.git`, build output or packages; every
  write or delete names 1–20 requirement ids; text only (no NUL), at most 256 KiB per file, 600 files and
  8 MiB in total; case-insensitive duplicates are refused. **Protected files** cannot be changed: the
  solution, every `.csproj`, `global.json`, `Directory.Build.*`, `Directory.Packages.props`, NuGet
  configuration, the architecture tests, `.git*` files and the web package/Angular/TypeScript
  configuration. The agent implements features inside the layers; it cannot loosen the rules that check
  them. `Changes()` lists net added/modified/deleted files against the base with their requirement ids.
- **Sandbox session** (`DockerWorkspaceSession`): one container per job with the M9.2 isolation, kept
  for the whole job so builds and tests can follow edits. Files are written from the workspace (path as
  a positional argument, never interpolated into a script). A timed-out step faults the session.
  `DockerSandbox` shares its process and archive helpers.
- **Git snapshot** (`GitSnapshot`): in a fresh sandbox where no workspace code runs, the base files and
  the final files become two commits (hooks, external diff drivers and text conversion disabled; fixed
  author, committer and dates), and the unified diff with its SHA-256 is returned. The commit ids depend
  on content only.

## Executed verification M10.1 — 2026-10-16

1. 27 workspace unit tests: reads, listings, searches; edits tracked with requirement ids and reverted
   edits removed from the changes; 15 protected paths and 9 unsafe paths refused; missing or malformed
   requirement ids, NUL content, oversize files, case-insensitive duplicates and missing files refused.
2. 3 Docker tests against the runner image: a session builds and tests a generated `SampleShop` with
   an agent-style `Product` aggregate and its test (restore, build, test passed), untrusted code
   overwriting `Entity.cs` inside the sandbox does not reach the diff, deletes reach the sandbox; two
   snapshots of the same content give identical commit ids and diff hash, an unchanged workspace gives
   an empty diff; a timed-out step faults the session.
3. The Docker test classes now run in one non-parallel xUnit collection: the first full run had two
   failures because isolation cases counted another class's live sessions as leftovers.
4. Full suite with `-IncludeKeycloak -IncludeDocker`: build **0 warnings, 0 errors**; **967 passed,
   0 failed, 0 skipped**. TRX (ignored): `artifacts/m6-tests/Özgen_DESKTOP-TQO89E0_2026-10-16_15_53_20.trx`,
   SHA-256 `de75f2e22d0b689329b94040260300b5cf0b0e7b9412a69db7796dc20cbe578c`. No model calls.

## Limits

- No agent loop or model yet (M10.2); no jobs, evidence or UI yet (M10.3).
- Offline sandbox: only the template's packages exist, so features needing new packages fail.
- Protection is path-based; code inside editable files can still be wrong or weaken behaviour, which the
  architecture tests, the final verification and human review must catch.
