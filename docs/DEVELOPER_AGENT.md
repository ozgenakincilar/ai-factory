# M10 — Developer Agent

Status: **M10.1 and M10.2 implemented** on 2026-10-16 (authoritative workspace, protected rules,
long-lived sandbox session, deterministic Git diff; agent loop with cost cap, limits and traceability,
verified by scripted models and one paid gpt-5.4-mini run). M10.3 (development jobs, evidence, API, diff view, StockFlow Product
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

## M10.2 — agent loop

- **`DeveloperAgent`** (Factory.Orchestration): each turn is one Structured Outputs call
  (`developer_turn`, strict schema) returning up to 8 actions: `list`, `read`, `search`, `write`
  (whole file), `delete`, `build` (offline restore + build), `test` (dotnet test; web tests too once
  web files changed), `finish`. The model never gets a shell; the agent executes actions against the
  authoritative workspace and the sandbox session and returns bounded observations (tool output keeps its
  end) in the next prompt. Writes and deletes must name functional requirement ids of the approved
  analysis; unsafe, protected or untraced actions are refused and reported back, not executed.
- **Bounds** (`DevelopmentLimits`): 12 turns, 8 actions per turn, 40 actions, 12,000 output tokens per
  call, bounded prompt and observations, and the cost cap. Any bound ends the job (`turn_limit_reached`,
  `action_limit_reached`, `cost_cap_reached`); malformed, refused or failed model output and sandbox
  failures end it too. Nothing is retried silently.
- **Cost guard** (`CostGuard`, Core): before every call the worst case (conservative input estimate plus
  the full output bound) is reserved; a call is made only if it fits under the job cap with what is spent
  or reserved; reported usage settles the reservation, unknown usage keeps the worst case. Default cap
  USD 0.40 (user decision).
- **Success** is decided only by `finish` followed by the full offline verification of the authoritative
  files in a fresh sandbox (`Verified`); otherwise `VerificationFailed`, `NotFinished`,
  `BudgetExhausted`, `ModelFailed` or `SandboxFailed`.
- **Live run harness**: `scripts/Test-DeveloperAgentLive.ps1` reads the OpenAI key from the worker's User
  Secrets into the test process only, refuses to start when an ignored ledger
  (`artifacts/developer-live/ledger.json`) leaves less than USD 0.40 under the USD 2 total, and records
  each run's evidence (status, calls, usage, changes, verification, log, commits, diff).
  `scripts/model-prices.sql` now has the verified gpt-5.4-mini price.

## Executed verification M10.2 — 2026-10-16

1. 9 unit tests with a scripted model and fake sandbox: a full edit/build/test/finish run verified with
   the final authoritative files; six refused actions (two protected files, unknown and missing
   requirement ids, two unsafe paths) reported to the model and nothing written; cost guard arithmetic
   (reservation, settlement, unknown usage) and the loop stopping at the cap; turn and action limits;
   malformed output (not JSON, no actions, unknown action kind, extra property); refusal; sandbox failure.
2. Docker test with a scripted model on real builds: verified, all six offline steps passed, 8 tests
   (template 6 + agent 2). It found a real defect first: the agent's `test` action ran `dotnet test`
   whose implicit restore attempted NuGet's online audit (NU1900) offline; the action now passes
   `-p:NuGetAudit=false`.
3. **Paid live run** (2026-10-16T19:05Z, gpt-5.4-mini served as `gpt-5.4-mini-2026-03-17`): task "Product
   with a required, trimmed name of at most 100 characters and a non-negative price" on a generated
   `StockFlow`. Result **Verified** in 3 turns and 12 actions; 3 calls, 4,067 input / 1,356 output
   tokens, **estimated USD 0.009154** (worst-case reservation about USD 0.056 per call). Changes:
   `src/StockFlow.Domain/Product.cs` added (aggregate with the rules, `DomainException` codes) and
   `tests/StockFlow.Tests/DomainTests.cs` modified (4 tests), both traced to FR-1 and FR-2; all six
   offline verification steps passed; diff SHA-256 `6891c4f53e6a3963dbf2c1dbbf5a65416fc9b08c67bfaedc8377717ffa870887`.
   Review notes: no test for exactly 100 characters, error codes use PascalCase unlike the template's
   snake_case, and the file sits in the Domain root instead of a feature folder. One small task is
   evidence of the path, not a quality measure.
4. Full suite with `-IncludeKeycloak -IncludeDocker`: build **0 warnings, 0 errors**; **977 passed,
   0 failed, 1 skipped** (the paid live test, which runs only through its script). TRX (ignored):
   `artifacts/m6-tests/Özgen_DESKTOP-TQO89E0_2026-10-16_22_06_59.trx`, SHA-256 `e7033f1e1c4d4e31b2f66c9f0b4202d426aa7b88a8b1a0d036b7a5efe595f287`.

## Limits

- No durable jobs, stored evidence, API or UI yet (M10.3); the loop runs in-process with an in-process
  cost guard. The live ledger is a local file, not the M8 budget tables.
- One live run on a small domain task; larger features (persistence, endpoints, UI) are untested.
- Offline sandbox: only the template's packages exist, so features needing new packages fail.
- Protection is path-based; code inside editable files can still be wrong or weaken behaviour, which the
  architecture tests, the final verification and human review must catch.
