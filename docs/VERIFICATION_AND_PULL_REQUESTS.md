# M11 — Verification Agent & GitHub Pull Request

Status: **M11.1, M11.2 and M11.3 implemented** on 2026-10-17 (Factory-established verification checks, a
bounded correction loop, and pull requests of verified, evidence-matching commits). The live GitHub run
is pending: the supplied token does not yet include the target repository; decisions: [Decisions](DECISIONS.md#m11-github-target-credential-and-merge-policy-2026-10-17-user-decision).

## M11.1 — verified results versus agent claims

When the Developer Agent finishes, the Factory runs the offline verification in a fresh sandbox and
evaluates the change itself (`ChangeChecks`, Factory.Core). The agent's finish summary is stored as its
**claim** only. Blocking checks:

- **sandbox**: every offline step passed (exit codes from the Docker daemon).
- **required_tests**: `dotnet test` writes a TRX report; a Factory step (`test-report`) extracts its
  counters and per-test outcomes right after the tests. No failed test, and the three protected
  architecture tests must appear by name as Passed. A run that exits 0 without running them (for
  example by skipping tests) is not verified.
- **layers**: a text scan of the authoritative sources, without executing anything: the domain
  references no `Microsoft.`, `Npgsql`, `System.Data` or other layer; the application no EF Core, Npgsql,
  ASP.NET Core, Infrastructure or Api; infrastructure not ASP.NET Core or the Api. Comment lines are ignored.
- **protected_files**: solution, projects, build settings, package manifests, Git files and the
  architecture tests are identical to the generated base (a second line of defence behind the workspace rules).

Advisory checks: **requirement_coverage** (every approved requirement referenced by a change) and
**new_tests** (the change adds or updates tests). The checks, their details and the number of
corrections are stored in the development evidence (additive fields of format 1) and shown on the
development page as "Factory checks", separate from the agent's log.

## M11.2 — correction loop

If the verdict is not verified and turns remain, the blocking findings and the failing step's output go
back to the agent as an observation ("NOT VERIFIED (correction n of 2)") and the loop continues; after two
corrections, or without turns left, the job ends as `VerificationFailed` with `check_<name>`. Every
correction runs under the same job cost cap; nothing is retried outside these bounds.

## Executed verification — 2026-10-17

1. Unit tests: TRX parsing; layer violations found in sources but not in comments or non-C# files; a
   green sandbox without the architecture tests (or with them failed, or without a report) is not
   verified; layer violations block while coverage and missing tests are warnings; a failed verification
   is returned to the agent, which corrects and is verified (1 correction); repeated failures end after 2
   corrections with `check_sandbox`; the claim is recorded, not trusted.
2. Docker: the scripted developer run is verified on real builds with 7 offline steps, the report
   showing 8 of 8 tests including the architecture tests; the verification-job and isolation cases pass
   with the added `test-report` step.
3. Web unit tests: 23 passed (Factory checks table and correction count on the development page).
4. Full suite with `-IncludeKeycloak -IncludeDocker`: build **0 warnings, 0 errors**; **992 passed,
   0 failed, 1 skipped** (the paid live test). TRX (ignored):
   `artifacts/m6-tests/Özgen_DESKTOP-TQO89E0_2026-10-17_00_53_28.trx`, SHA-256 `ee4328bd3cc498e137b086fa92697a5e64064ae0fe3f4a089ee0bf11330e97a9`. No model calls.

## M11.3 — pull requests

- **Outcome table** (`orchestration."DevelopmentPullRequests"`, migration `20261017090000_PullRequests`): one
  immutable row per verified job, `Opened` (number, URL, pushed head commit) or `Failed` (code). The
  database accepts an opened row only for a Verified job, a `factory/...` branch, a URL under
  `https://github.com/<repository>/pull/` and a head commit equal to the evidence's final commit.
  `next_pull_request` gives the worker the oldest verified job without an outcome with its exact evidence
  bytes; `read_development` now includes the outcome.
- **Publisher** (`GitHubPullRequestPublisher`): regenerates the base files from the hash-verified
  template, re-creates the base commit and applies the evidence diff with git on the host in a temporary
  repository isolated from the user's Git configuration (no system or global config, no credential
  helpers or hooks, `core.autocrlf=false`, fixed identity and dates). Both commit ids must equal the
  evidence's before anything is pushed. It pushes the base commit to `main` only when `main` does not
  exist, the change to `factory/<application>-<job>` without force, and opens the pull request through the
  REST API (an existing one for the branch is returned). It never calls a merge API. The token is passed
  to git as an HTTP header through environment configuration (not on the command line) and to the API as
  a bearer token; it never enters the sandbox.
- **Body** (`PullRequestText`): source (approved case, version and hash; template; model, turns, calls,
  cost, corrections; commits; diff and evidence hashes), a requirement traceability table, the Factory
  checks, the offline verification steps, and the statements that human review is required and the
  Factory never merges. Model-derived text is escaped so it cannot break the tables or add markup.
- **Worker** (`PullRequestProcessor`, `PullRequestWorkerService`; `scripts/Run-FactoryWorker.ps1
  -Verification -Development -GitHubRepository ozgenakincilar/stockflow` with `GitHub:Token` in the worker
  User Secrets): transient failures (network, API) are retried three times, permanent ones (commit
  mismatch, diff not applying, branch conflict, missing template) are recorded at once.
- **Product Workspace**: the development page links the pull request (only URLs of the configured
  repository's pull requests are rendered, `rel="noopener noreferrer"`) or shows the failure code.
- **Test script**: with `-IncludeKeycloak`, the live Keycloak cases now run first; browser access tokens
  live 300 s and the suite takes about 6 minutes (one run failed with 401 for that reason).

## Executed verification M11.3 — 2026-10-17

1. Unit tests: branch, title and body from the evidence; model-derived text escaped; repository names;
   the processor publishes from the regenerated base and records the outcome, defers transient failures
   and records them after three attempts, records permanent failures at once.
2. Docker test with real evidence commits from `GitSnapshot`, a local bare repository and a fake GitHub
   API: the host re-creates exactly the evidence commits (container and host git agree), `main` receives
   the base commit and the branch the change; the pull request is created once with base `main`, the
   branch as head and `maintainer_can_modify=false`; publishing again returns it; no merge or PUT request
   is ever made; a tampered diff is refused (`final_commit_mismatch`) and nothing is pushed.
3. PostgreSQL test: outcomes recorded once, only for the evidenced head, this repository's pull URL and a
   `factory/` branch; the outcome appears in the job view; rows are immutable for the owner login; the
   runtime role cannot reach the worker functions.
4. Full suite with `-IncludeKeycloak -IncludeDocker`: build **0 warnings, 0 errors**; Keycloak pass **4
   passed**, remaining pass **1000 passed, 0 failed, 1 skipped** (the paid live test). TRX (ignored):
   `artifacts/m6-tests/Özgen_DESKTOP-TQO89E0_2026-10-17_01_41_18.trx` (SHA-256
   `8000e410dc7cc8c79a99db45cd6b0301c6ad0c044da8a4cb5bf2a9f2af6010af`) and
   `artifacts/m6-tests/Özgen_DESKTOP-TQO89E0_2026-10-17_01_41_20.trx` (SHA-256
   `d663ee59f64306ecfa9a2ac9cce2cb2c1a554c6245f21dc2a2763406983f778a`).
5. **Live GitHub run: not yet possible.** The token the user supplied (pasted in chat on 2026-10-17 and
   stored in the worker User Secrets at the user's request) is valid but does not include the `stockflow`
   repository: GitHub answers 404 and reports `metadata=read` and `pull_requests=read` as missing. The
   user was asked to add the repository and Contents/Pull requests write permissions to the token.

## Limits

- The test report is written inside the sandbox, where the untrusted code also runs; a deliberately
  malicious workspace could still forge it. The checks raise the bar and are recorded; the sandbox
  limits the harm; human review remains required.
- The layer scan is textual (namespaces and `using` lines), not a semantic analysis; the reflection-based
  architecture tests remain the primary guard.
- Without branch protection (GitHub Free, private repository), "never merge" and "main only once" are
  Factory behaviour, not server-side rules; the token itself could push to `main`.
- No dependency vulnerability scan beyond the protected, already audited template manifests (the agent
  cannot change package references).
