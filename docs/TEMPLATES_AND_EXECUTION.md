# M9 — Application Templates & Secure Execution

Status: **M9.1, M9.2 and M9.3 implemented** on 2026-10-16 (starter template, registry, deterministic
generation; offline sandbox runner with isolation tests; durable verification jobs with immutable
evidence in the worker, API and Product Workspace) after the user approved Docker Desktop + WSL 2,
the template stack and the DDD layer layout, M9.2 and M9.3 ("evet"). Per-job **Git** workspaces are
deferred to M10, where the Developer Agent needs repository tools. This document describes inspected code and executed local verification, not production
readiness.

## Slices

| Slice | Scope | Status |
| --- | --- | --- |
| M9.1 | Versioned starter template, minimal registry, deterministic generator, container verification of generated output | Implemented |
| M9.2 | Runner image with an offline package cache; sandbox runner with network off, read-only root, non-root user, CPU/memory/PID/time/disk limits, no credentials; malicious-code tests | Implemented |
| M9.3 | Durable verification jobs (per-job sandbox workspace), immutable hashed evidence, worker/API/Product Workspace integration, cross-project isolation tests | Implemented (Git workspaces deferred to M10) |

## M9.1 — starter template

- **Template** `dotnet-angular-postgres-ddd` 1.0.0 in `templates/starter-v1/` (37 text files). It is a
  buildable solution under the placeholder name `FactoryStarter` (`factory-starter` for the web app):
  - `FactoryStarter.Domain`: no package or project references; `Entity`, `AggregateRoot` (domain
    events), `IDomainEvent`, `DomainException`.
  - `FactoryStarter.Application`: references Domain only (plus DI abstractions); `IUnitOfWork` port.
  - `FactoryStarter.Infrastructure`: EF Core + Npgsql `AppDbContext` implementing `IUnitOfWork`.
  - `FactoryStarter.Api`: Minimal API composition root with `/health`.
  - `tests/FactoryStarter.Tests`: architecture tests enforcing the dependency direction, domain and
    API tests (no database needed).
  - `web/`: Angular 22.2.3 (standalone, zoneless, Vitest) with an exact-version `package-lock.json`.
  - .NET packages use the Factory's own pinned versions; `TreatWarningsAsErrors` and deterministic builds are on.
- **Registry** `templates/registry.json`: id, version, directory, placeholders and a SHA-256 content
  hash. `FileSystemTemplateCatalog` refuses an unknown template, an invalid registry, links and any
  file change (hash mismatch). Build/package output directories are not template content.
- **Content hash**: SHA-256 over `path NUL content NUL` for every file in ordinal path order; paths are
  relative, `/`-separated and restricted to `[A-Za-z0-9._-]` segments; content is UTF-8 text without
  NUL, with LF line endings (`templates/.gitattributes` keeps LF on checkout; CRLF is normalized).
  Limits: 500 files, 4 MiB.
- **Generation** (`StarterGenerator`, pure domain code in Factory.Core): verifies the template hash,
  replaces the placeholders in paths and contents with an `ApplicationName` (PascalCase, 2–6 words,
  ASCII; kebab form derived) and returns the files with a manifest hash. The same template, version and
  name always give the same files and hash. Names containing the placeholder are refused.
  `GeneratedApplicationWriter` writes only into a new or empty directory and refuses paths that would
  leave it.

## Executed verification — 2026-10-16

Docker Desktop 29.8.2 on WSL 2; images pinned by digest:
`mcr.microsoft.com/dotnet/sdk@sha256:0d99ebf1…5ffa` (SDK 10.0.402) and
`node@sha256:a5f4366e…aee5` (Node 24.22.0).

1. The template itself in containers: `dotnet test` 6 passed; `npm ci`, `ng test` 1 passed,
   `ng build` 190 kB initial. The lock file was created with `npm install --package-lock-only
   --ignore-scripts`; `npm audit`: 0 vulnerabilities.
2. Negative architecture check: adding EF Core to the domain project and using a `DbContext` type made
   `Domain_DependsOnlyOnTheBaseClassLibrary` fail (exit 1). This shows the guard works; it is not a
   complete architecture analysis.
3. Factory tests: 27 template unit tests (determinism, placeholder replacement, names, tampering,
   unsafe paths, registry, writer) and one Docker test that generates `SampleShop` and runs
   `dotnet test`, `npm ci`, `ng test` and `ng build` in the pinned containers (2 min 11 s). The C#
   content hash equals the independently computed registry hash.
4. Full suite with `-IncludeKeycloak -IncludeDocker`: build **0 warnings, 0 errors**; **913 passed,
   0 failed, 0 skipped**, including four live Keycloak cases and the Docker case. TRX (ignored):
   `artifacts/m6-tests/Özgen_DESKTOP-TQO89E0_2026-10-16_12_03_35.trx`, SHA-256
   `a0a29e660ad40e878640e8edba80988772f3ea8cb9a9c9767279c88638ef7209`.

## M9.2 — sandbox runner

- **Runner image** (`runner/Dockerfile`, built by `scripts/Build-FactoryRunner.ps1` from the registered
  template): the digest-pinned .NET SDK image plus Node/npm copied from the digest-pinned Node image, a
  non-root `runner` user (uid 10001), and the template's NuGet (`/opt/factory/nuget`) and npm
  (`/opt/factory/npm`) caches warmed at build time and read-only at run time. The script records the
  local image id in ignored `artifacts/runner/image.json`; the sandbox accepts only immutable
  references (`sha256:…` image id or `name@sha256:…`).
- **Contracts** (`Factory.Core.Execution`): `SandboxLimits` (validated; default 2 CPUs, 4096 MiB memory
  including the tmpfs mounts, 1024 PIDs, 1024 MiB workspace, 512 MiB temp, 10 min per step, 20 min per
  job, 64 KiB output per step), Factory-chosen `SandboxStep`s, `StepResult`/`SandboxReport` (runner
  image, workspace hash, limits, per-step outcome, exit code, duration, bounded output), `ISandbox`, and
  `StarterVerification` (restore, build, test, `npm ci --offline --ignore-scripts`, web test, web build).
- **`DockerSandbox`** (`Factory.Infrastructure.Execution`): one container per job created with
  `--network none --read-only --ipc none --user 10001:10001 --cap-drop ALL --security-opt
  no-new-privileges --init`, CPU, memory (swap equal to memory) and PID limits, and size-limited tmpfs
  mounts for `/work` and `/tmp`; no bind mounts, volumes, host environment or Docker socket. The
  workspace is streamed in as a tar of validated text files. Each step runs through `docker exec`, so
  its exit code comes from the Docker daemon, not from output the code could forge. The first step
  that does not pass ends the job (later steps are reported as skipped); a step or job timeout ends it
  as timed out. Output keeps the first and last half of the limit. The container is removed in every
  case, including cancellation.
- Design finding: a tmpfs-backed Docker *volume* is mounted as a new empty tmpfs for each container,
  so it cannot carry a workspace across per-step containers; one container per job with `docker exec`
  was chosen instead.

## Executed verification M9.2 — 2026-10-16

1. Manual experiment with the runner image: all six offline steps passed for the template
   (restore 1 s, build 4 s, test 6 passed, npm ci 8 s, web test, web build) with the network disabled
   and a read-only root; workspace use 287 MiB. Restore first failed with NU1900 (NuGet's
   vulnerability audit needs the network, and warnings are errors), hence `-p:NuGetAudit=false`.
2. Factory tests: 9 unit tests (isolation flags, immutable image references, limits, steps) and 8
   Docker tests against the runner image: a generated `SampleShop` passes all six steps offline; an
   isolation probe shows uid 10001, `NoNewPrivs 1`, no effective capabilities, Node v24 present but no
   HTTP or DNS, no writes outside `/work` (including `/opt/factory` caches), no Docker socket and no
   host environment (a canary variable set in the test process is absent); a 200 MiB write stops at the
   64 MiB workspace limit; memory exhaustion is killed with exit 137 and later steps are skipped; a
   fork bomb is contained by a 64-PID limit and the job ends; an endless step times out after 5 s; a
   50 MB output flood is cut to the 16 KiB limit with first and last lines kept; a concurrent job cannot
   find another job's files. After every case no sandbox container remains.
3. Full suite with `-IncludeKeycloak -IncludeDocker`: build **0 warnings, 0 errors**; **930 passed,
   0 failed, 0 skipped**. TRX (ignored): `artifacts/m6-tests/Özgen_DESKTOP-TQO89E0_2026-10-16_12_35_00.trx`,
   SHA-256 `c85d087d4cac14732b68b9c425b376a2adf753fa9bf64594896393b5f14b4a21`. Runner image
   `factory-runner:1.0.0-2e41c7460734` = `sha256:b8b763cc01628e2f23e5ef1e44f7f37ef3669be9199f7581334d179d0a549e00`
   (local id; a rebuild yields a new id).

## M9.3 — verification jobs and evidence

- **Jobs** (`orchestration."VerificationJobs"`, migration `20261016150000_Verification`): a Generator
  of a project submits template id, version and `ApplicationName` with an `Idempotency-Key` (replay
  returns the same job; a changed body is a conflict); at most 5 active jobs per project. Jobs have their
  own queue and leases (90 s, renewed every 30 s); they are not workflow runs, so no workflow function
  changed. A lost lease is retried once, then the job fails with `attempts_exhausted`. A trigger allows
  only Queued→Running→(Queued | Passed | Failed | TimedOut | SandboxError); identity, template and name
  never change and finished jobs are immutable.
- **Evidence** (`orchestration."VerificationEvidence"`): the worker stores one JSON envelope (format 1:
  job, project, template id/version/SHA-256, application name, generated-workspace SHA-256 and the full
  `SandboxReport`) as exact bytes with their SHA-256, checked by a constraint. `complete_verification`
  accepts it only from the lease owner and only when the envelope describes that job and its outcome is
  consistent; the job's status follows the report in the same transaction. Evidence rows can never be
  updated or deleted, even by the owner login.
- **Worker** (`VerificationProcessor`, `VerificationWorkerService`, opt-in with `Verification:Enabled`,
  `Templates:Root`, `Verification:RunnerImage`; `scripts/Run-FactoryWorker.ps1 -Verification`): claims a
  job, loads and hash-checks the template, generates the application, runs `StarterVerification` in the
  `DockerSandbox` while renewing the lease, and stores the evidence. A lost lease cancels the sandbox. A
  template that cannot be loaded fails the job without evidence (`template_unavailable`).
- **Sandbox lifetime**: containers are created with `--rm` and `sleep <job timeout + 60 s>`, so a sandbox
  orphaned by a crashed worker (whose cleanup never ran) ends and removes itself.
- **API**: `POST /api/projects/{id}/verifications`, `GET …/verifications`, `GET …/verifications/{jobId}`
  (job, parsed evidence and its SHA-256) and `GET …/verifications/{jobId}/evidence` (the exact bytes,
  `ETag: "sha256-…"`). Same audiences as runs; the API re-checks the stored hash before answering.
- **Product Workspace**: the project page has a verification panel (name check, submit, list); the
  verification page shows each step's outcome, exit code, duration and bounded output, the evidence,
  template and workspace hashes, runner image and limits. Job status is polled every 3 s while active
  (no push notification for jobs).

## Executed verification M9.3 — 2026-10-16

1. Migration: applied by the initializer to `factory_m86_verify_20261016` and
   `factory_m7_verify_20261015`, and to a new database `factory_m93_verify_20261016` (all 18 migrations
   and the runtime grants with their safety checks).
2. PostgreSQL tests (6): idempotent, bounded and project-scoped submission; completion only by the lease
   owner and only with an envelope for that job; hash-checked, immutable evidence (UPDATE/DELETE refused
   for the owner login); failed reports; lost lease retried once then `attempts_exhausted`; runtime and
   worker roles reach only their own functions and no tables; processor with the real template catalog;
   HTTP submission, replay, strict bodies, listing, foreign project 403, evidence bytes and ETag.
3. Docker tests: one more sandbox case shows an orphaned sandbox (never removed by its creator) ends
   and disappears by itself after job timeout plus grace.
4. Web unit tests: **19 passed** (name rule, durations, panel submission, verification page).
5. Real-browser end-to-end (`scripts/Test-FactoryVerification.mjs`) with Keycloak, the API, the web
   dev server and a worker with `-Verification`: `BrowserShop` submitted from the project page, all six
   offline steps passed in the sandbox, the evidence page showed them and the evidence hash; the stored
   SHA-256 matched the bytes in PostgreSQL; job finished on its first attempt in 31 s; no worker errors,
   no token-like strings in logs, no sandbox left. No model calls.
6. Live crash check: while `CrashShop` ran in the sandbox the worker process was killed; a new worker
   reclaimed the job after the lease expired and it passed on attempt 2 (111 s in total, browser
   end-to-end passed). The crashed worker's sandbox (started 10:34:49 UTC, `sleep 1260`) kept running as
   expected and was gone at 10:55:52 UTC, removed by Docker when its bounded lifetime ended.
   The first full-suite attempt failed one case because the migration-count assertion still expected 17
   migrations; it was updated to 18 (as at every earlier migration).
7. Full suite with `-IncludeKeycloak -IncludeDocker`: build **0 warnings, 0 errors**; **937 passed,
   0 failed, 0 skipped**. TRX (ignored): `artifacts/m6-tests/Özgen_DESKTOP-TQO89E0_2026-10-16_13_59_11.trx`, SHA-256 `80ffe515fe63ea13448c2c8a43aa6016d8f6477e361aa43f77cf6c3b866c559c`.

## Limits

- The M9.1 container test builds only Factory-authored output with network restore; untrusted code
  must go through the M9.2 sandbox.
- The sandbox isolates; it does not make results truthful. Tests are part of the untrusted code and can
  pass falsely; M11 separates verified results from agent claims.
- Offline means only the template's packages are available; adding a package fails until an
  allow-listed restore path exists. NuGet vulnerability auditing is off inside the sandbox.
- Isolation relies on Docker Desktop's Linux VM and default seccomp/AppArmor profiles; no custom seccomp
  profile, user-namespace remapping or gVisor-style runtime. Not reviewed by a third party; local
  Docker Desktop is not a production runner (production runners come with M13A).
- Background processes started by one step live until the job ends; a job is one trust unit.
- The workspace is a per-job tmpfs, not a Git repository; Git workspaces, diffs and commits come with
  M10. Only the starter template has a verification plan.
- A crashed worker's sandbox runs until its bounded lifetime ends (up to 21 minutes by default).
- Job status reaches the browser by polling; verification jobs send no SignalR hints.
- Evidence proves what the sandbox observed, not that the generated tests are meaningful (M11).
- No PostgreSQL integration test in the template yet (the health endpoint needs no database).
- One template and one stack; the registry is a file in the repository, not a database or service.
- Names with consecutive capitals (for example `APIGateway`) or non-ASCII letters are refused.
