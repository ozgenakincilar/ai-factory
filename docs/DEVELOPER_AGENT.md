# M10 — Developer Agent

Status: **M10 implemented** on 2026-10-16: M10.1 (authoritative workspace, protected rules, sandbox
session, deterministic Git diff), M10.2 (agent loop with cost cap, limits and traceability) and M10.3
(durable development jobs on approved review artifacts, immutable evidence, API and Product Workspace;
the StockFlow Product CRUD acceptance run was verified). Earlier plan text: M10.3 (development jobs, evidence, API, diff view, StockFlow Product
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

## M10.3 — development jobs, evidence and acceptance

- **Jobs** (`orchestration."DevelopmentJobs"`, migration `20261016170000_Development`): a Generator submits
  case, artifact version and SHA-256, template and `ApplicationName` with an `Idempotency-Key`. Only the
  case's CURRENT version in state Approved with exactly that hash is accepted (otherwise 412; unknown or
  other project's case 404); at most 2 active jobs per project. The claim re-checks the approval
  (`approval_changed` if superseded) and a total cap before any model call: recorded spend plus the cap of
  every running job and of every lost job must leave room for this job's cap (`total_cap_reached`).
  **Paid work is never re-executed**: an expired lease ends the job as `worker_lost`, and its full cap
  stays counted as spent. Transitions and immutability are enforced by a trigger.
- **Evidence** (`orchestration."DevelopmentEvidence"`, format 1): job, project, case, artifact version and
  hash, template and its hash, model, status, failure code, turns, actions, cost, every call's usage and
  reservation, the changes with requirement ids, base/final commits, the diff and its hash, the
  verification report and the agent log, as exact bytes with their SHA-256. `complete_development`
  accepts it only from the lease owner, only for this job, only with cost within the job's cap, and a
  Verified status only with a diff and a passed verification. Evidence never changes.
- **Worker** (`DevelopmentProcessor`, `DevelopmentWorkerService`; `scripts/Run-FactoryWorker.ps1
  -Verification -Development`): claims, re-hashes the approved artifact bytes, generates the starter
  application, runs the Developer Agent under the job cap while renewing the lease, produces the Git
  diff, and stores the evidence. A verified run without changes is recorded as `notFinished`
  (`no_changes`). `-Development` sets gpt-5.4-mini with its verified prices, the USD 0.40 job cap and a
  total cap of USD 2 minus the spend in the live-run ledger, so both paths share one budget.
  `-StrongModel gpt-5.4-mini` maps the routing policy's Strong tier (Architecture, Planning) to that model.
- **API**: `POST/GET /api/projects/{id}/developments`, `GET …/developments/{jobId}` and `…/evidence`
  (exact bytes, `ETag: "sha256-…"`); same audiences and error mapping as runs.
- **Product Workspace**: an approved, content-verified review offers a **Develop** panel to Generators
  (exact version and hash are sent; a superseded approval is explained); the project page lists
  development jobs; the development page shows status, model, turns, cost and tokens, each change with
  its requirement ids, the colored diff (text only), offline verification, agent log and hashes.

## Executed verification M10.3 — 2026-10-16

1. PostgreSQL tests (6): only the current approved version and hash can be submitted (pending, wrong hash,
   wrong version, unknown and foreign cases refused), idempotency, conflict and capacity; claims re-check
   the approval after a regeneration (`approval_changed`) and the total cap (`total_cap_reached`); the
   worker receives the exact approved bytes; a lost worker ends the job and keeps its cap counted;
   evidence must describe the job, stay within its cap and be consistent, and is immutable even for the
   owner login; role separation; the processor with a scripted model records verified evidence and
   `no_changes`; HTTP 202/412/400/403, listing, detail and evidence ETag.
2. Agent fixes found by the paid acceptance runs, each with a regression test: (a) only the previous
   turn's results were shown, so the model re-read files until the action limit, now the current content
   of every file read or written is shown every turn with a compact history; (b) the model treated the
   missing feature files as a blocker and finished without changes, now the prompt states the feature is
   new and must be written, the context says `featureCode: none yet`, and a reminder follows after two
   turns without writes; (c) "read before modifying" contradicted "do not re-read known files", so a
   known file now counts as read and re-reads are answered with a reminder. The action limit is 60.
3. The analysis-architecture run first failed twice at Architecture with gpt-4o-mini
   (`invalid_technology_decision_or_basis`: a technology decision without requirement traces was not
   marked as a design recommendation, although the prompt requires it). gpt-5.4-mini produced a valid,
   traced proposal for the same analysis, so the Strong tier (Architecture's minimum in the M8.5a policy)
   was mapped to it with `-StrongModel`; the validator was not relaxed.
4. Web unit tests: **23 passed** (diff classification, cost format, develop panel submission and 412
   message, development page with changes, diff rendered as text, verification and hashes, review page
   offering Develop).
5. **Acceptance run** (`scripts/Test-FactoryDevelopment.mjs`, real Edge, Keycloak, API, web, worker with
   `-Verification -Development -StrongModel gpt-5.4-mini`): StockFlow Product CRUD request with USER-1..4
   (SKU rules, name rules, non-negative price, create/list/view/update/delete) → analysis and architecture
   published (4 requirements, content verified) → approved on the review page → Develop → **Verified** in
   9 turns and 51 actions, 9 calls, 39,610 input / 7,739 output tokens, **USD 0.0645**. Nine files traced
   to USER-1..4: `Product` aggregate with all rules (Domain), repository port and `ProductService`
   (Application), EF Core configuration with a unique SKU index and repository (Infrastructure), CRUD
   endpoints under `/api/products` (Api), domain tests; offline verification passed all six steps
   (10 tests). Diff SHA-256 `1fed6c4560b9de1100111ce93840a26d7373626e712947bf719b5c65acaaa290`.
   **Review findings**: domain errors are not mapped to HTTP (invalid input and missing products answer
   500), no EF migration for the products table (the offline sandbox has no `dotnet-ef`), only domain
   tests (no service or API tests, length limits untested), and no Angular UI in this run. The result
   meets the M10 acceptance criteria as a reviewable, traced, verified diff; it is not production-ready.
6. Earlier paid acceptance attempts, kept as evidence: `notFinished` after re-reading (USD 0.0196),
   `no_changes` (USD 0.0359), `action_limit_reached` with 12 files written (USD 0.0769). Total paid
   Developer spend in M10: **USD 0.206** (database evidence USD 0.197 plus the M10.2 live run USD 0.009) of
   the USD 2 cap; analysis/architecture calls for these runs about USD 0.03.
7. Full suite with `-IncludeKeycloak -IncludeDocker`: build **0 warnings, 0 errors**; **986 passed,
   0 failed, 1 skipped** (the paid live test). TRX (ignored): `artifacts/m6-tests/Özgen_DESKTOP-TQO89E0_2026-10-16_22_42_17.trx`, SHA-256 `2f14fde29091299c56fe5e4f3005c1955d1caac0ba1ab98cba297f87c38f3021`.

## Limits

- Developer spend is capped by the job cap and the database total cap, not by the M8 budget tables;
  Developer calls are not in `orchestration."ModelCalls"` (they are in the evidence).
- The template has no database migrations tooling inside the offline sandbox, so generated persistence
  code ships without migrations; tests inside the sandbox cannot use PostgreSQL.
- One verified acceptance run after three fixes; agent quality varies between runs and models.
- No GitHub branch or pull request yet (M11); human review of the diff happens in the Product Workspace.
- Offline sandbox: only the template's packages exist, so features needing new packages fail.
- Protection is path-based; code inside editable files can still be wrong or weaken behaviour, which the
  architecture tests, the final verification and human review must catch.
