# Milestone 7 — Durable Workflow Orchestration

Status: M7.1 committed and pushed as `bcd50fa`. M7.2 (human-decision waiting and cancellation)
implemented and verified on 2026-10-15 under the user's full approval; see
[M7.2](#milestone-72--waiting-for-human-decisions-and-cancellation). Parallel steps are deferred
to M8 (see [Architectural Decisions](DECISIONS.md#milestone-72-scope-2026-10-15)). M7.3 (real-time
push) is not implemented. This document describes inspected code and executed local verification,
not production readiness. Sections up to "Limits" describe M7.1.

## Model (Workflow Orchestration bounded context)

- `WorkflowRun` aggregate (`Factory.Core/Orchestration`) owns the step plan
  (Analysis → Architecture → Publication) and transitions: Queued → Running → Succeeded/Failed.
  Steps: Pending → Running → Succeeded/Failed, Pending → Cancelled, Running → Pending (lost lease).
  One step of a run runs at a time. PostgreSQL enforces the same transitions independently.
- `StepAttempt`: one lease of one step by one worker. Outcomes Running, Succeeded, Failed, Abandoned.
- Ports: `IWorkflowRunStore` (API: submit, read) and `IStepQueue` (worker: claim, renew, complete,
  fail, publish). `PostgresOrchestrationStore` implements both; each process uses its own role.
- `DurableWorkflowProcessor` (`Factory.Orchestration/Durable`) executes one claimed step outside
  database transactions, renews the lease every 20 s and records the checkpoint or failure.
- `Factory.Worker`: separate .NET generic-host process running bounded claim loops
  (`Worker:Concurrency`, default 2, max 8). The API never executes durable steps.

## Execution semantics

- `POST /api/projects/{projectId}/runs` (Generator, `Idempotency-Key`, same strict body as M6
  create) returns **202** immediately with `runId`, `status` and `replayed`; `Location` points to
  `GET /api/projects/{projectId}/runs/{runId}` (Generator or Human Reviewer). Responses are
  `no-store` and contain safe metadata only: no task, context, model output or lease material.
- Idempotency scope is project, requester and key; reuse with different input is 409. At most 20
  Queued/Running runs per project (429). Admission and publication reauthorize Generator membership.
- Leases are 60 s, fixed by the database. A lost lease (crash, stop) makes the step claimable
  again. Completed steps keep their validated checkpoint and are never re-executed.
- Model steps allow 2 attempts, publication 3, then the run fails `step_attempts_exhausted`.
  Definite model failures (invalid output, refusal, timeout, provider error) fail the run with a
  static code and are **not** retried automatically. A lost lease can repeat the unfinished step's
  provider call: nothing is exactly-once.
- Expired leases are recovered by the next claim; there is no separate sweeper process.
- Publication creates, in one transaction under the M6 deferred guards, the M6 work item, a
  published `GenerationCommand` (RunId = durable run id = artifact `workflowId`), the managed case,
  lineage, artifact and publication event, and completes the run. The database rejects an artifact
  whose analysis/architecture differ from the stored checkpoints. Approval never schedules work.
- M7.1 durable runs always create a new work item. Regenerating an existing work item remains on
  the M6 synchronous route; M6 routes, functions and guards are unchanged.

## Database and least privilege

Additive migration `20261015200000_DurableWorkflows` (embedded `DurableWorkflows.sql`) adds schema
`orchestration` with `WorkflowRuns`, `WorkflowSteps`, `StepAttempts`, no-delete triggers,
transition guards and a deferred whole-run integrity check. `scripts/orchestration-runtime-grants.sql`
(run by `Initialize-FactoryLifecycle.ps1` after the lifecycle grants):

- All orchestration functions are owned by `factory_lifecycle_guard` (required by the M6 managed
  case guard); PUBLIC execution revoked; fixed `search_path`.
- `factory_lifecycle_runtime` (API) may execute only `submit` and `read_run`.
- New `factory_orchestration_worker` LOGIN (NOINHERIT, no elevated flags or memberships) may execute
  only `claim`, `renew`, `complete`, `fail` and `publish`; no table, DDL or temporary privileges.
- The script fails closed if a role is unsafe or has unexpected table privileges.

Worker configuration: `ConnectionStrings:Orchestration` (worker role), `OpenAI:ApiKey`,
`OpenAI:Model`. The worker uses an OpenAI client with SDK retries disabled and a 90 s network timeout.
Local operators can use `./scripts/Run-FactoryWorker.ps1 -Database <initialized database>`; it sets
`DOTNET_ENVIRONMENT=Development`, without which the generic host does not load User Secrets.

## Executed verification — 2026-10-15

SDK 10.0.103. Uncommitted working tree on top of `aa14450`.

1. Initial `Initialize-FactoryLifecycle.ps1` failed: a PL/pgSQL `IF ... CASE ... THEN` parse error
   in the new migration. Diagnosed by replaying the SQL inside `BEGIN ... ROLLBACK`, fixed, then the
   initializer completed on the same database (`factory_m7_verify_20261015`); `-BaselineOnly` on
   `factory_m7_verify_20261015_legacy`. Six migrations applied; all 15 orchestration functions
   owned by the guard role.
2. Unit tests before PostgreSQL: 21 passed (aggregate and processor).
3. Orchestration + lifecycle filter without Keycloak: 109 passed, 1 not executed (the Keycloak case).
4. Real headless Edge PKCE/state login, then the full suite with `-IncludeKeycloak`:
   build **0 warnings, 0 errors**; **734 passed, 0 failed, 0 skipped** (700 existing + 34 new),
   including four live Keycloak cases.
   TRX (ignored): `artifacts/m6-tests/Özgen_DESKTOP-TQO89E0_2026-10-15_22_35_42.trx`.
   SHA-256: `77ce98be13fd91b714308b24e00eeb58f0b32040b8f0a7ccba3ad2c0bbd86310`.
5. Keycloak and PostgreSQL stopped; data preserved.

New PostgreSQL cases cover idempotent/conflicting/bounded submission; full publication readable by
M6 and approvable by a human reviewer; crash after Analysis resuming at Architecture with one
Analyst call; stale workers rejected after reclaim; concurrent claims leasing each run once;
invalid model output failing without publication; attempt exhaustion; revoked requester blocked at
publication with no case or command created; database rejection of an artifact that differs from
checkpoints; role separation; project-scoped reads. HTTP cases cover 202/replay/status and
401/403/400/404. The M6 upgrade test now expects six migrations and applies the new grants.

## Live worker-process verification — 2026-10-15

User-approved, budget-limited run against `factory_m7_verify_20261015` with the real OpenAI
provider (`gpt-4o-mini`, SDK retries disabled). The built `Factory.Worker.exe` was started as a
separate process with `DOTNET_ENVIRONMENT=Development` (required for User Secrets), the restricted
`factory_orchestration_worker` login and `Worker:Concurrency=1`. Runs were submitted through
`orchestration.submit` as `factory_lifecycle_runtime` for a synthetic project and principal, using
the nonsensitive task-list request from the historical evidence. The API was not involved.

| Run | Result |
| --- | --- |
| `bb3a61b0-5769-41e5-b0bb-cede7553abfb` | Succeeded; all three steps on attempt 1; managed case published. |
| `615d3a55-6e82-4edd-a4c0-d99d6812327a` | Succeeded; all three steps on attempt 1 (intended crash test missed: a monitoring-script bug). |
| `05303437-db1c-48fe-95be-7ae0eafe393a` | Worker A force-killed while Architecture was Running (22:45:31). Worker B, started 1 s later, reclaimed after lease expiry; final attempts: Analysis 1 Succeeded, Architecture 1 Abandoned + 2 Succeeded, Publication 1 Succeeded (22:46:39). |

Model calls: about seven (2 + 2 + Analysis 1 + Architecture 2, the first of which was interrupted
by the kill and may or may not have been billed). Token usage and cost are not recorded (M8); the
cost was estimated as small but not measured. Real model output passed schema/provenance
validation in every completed step. Worker logs contained only static step outcomes, no errors and
no credential pattern. `scripts/Run-FactoryWorker.ps1` packages the same configuration for operators
but was not itself executed; the test launched the built executable directly.

## Limits

- One live crash/restart sample is evidence of the recovery path, not a reliability measurement.
  Graceful shutdown during a step, multi-worker contention across processes and network failures
  between worker and database were not exercised as processes.
- Not exhaustive concurrency proof, performance, HA or penetration testing. Lease timing relies
  on database clock; long GC pauses beyond the lease can cause duplicate provider calls.
- No waiting state, parallel steps, cancellation, real-time push, global LLM budget or model
  routing; these belong to M7.2, M7.3 and M8.

## Milestone 7.2 — Waiting for human decisions and cancellation

Additive migration `20261015230000_DurableWorkflowDecisions` (embedded `DurableWorkflowDecisions.sql`);
the released M7.1 migration is unchanged. Existing rows stay valid and no history is rewritten.

- **Waiting.** A worker holding a Running Analysis or Architecture step may call
  `request_decision` with a static reason code and 2–8 static option codes that always include
  `stop`. The attempt ends as `Suspended`, the step and run become `Waiting`, and a Pending
  `StepDecision` is recorded. The step holds no lease and consumes no worker capacity; there is no
  timeout and no automatic default. Publication can never wait. In M7.2 no production step
  requests a decision yet; M8 customer-model routing is the intended producer.
- **Deciding.** `POST /api/projects/{projectId}/runs/{runId}/decisions/{decisionId}` with exactly
  `{"choice":"<offered code>"}`. Only an active Human project Reviewer may decide, locked against
  revocation; this is an interim authority until M8 tenant administrator/project owner roles.
  A repeated identical decision is a replay; a different choice on a settled decision is 409.
  Any non-`stop` choice returns the step to Pending; the next claim delivers the step's decided
  choices in `ClaimedStep.Decisions`. `stop` cancels the run.
- **Cancellation.** `POST /api/projects/{projectId}/runs/{runId}/cancel` (Generator). Queued,
  Running or Waiting runs become Cancelled with the canceller recorded; pending decisions are
  cancelled; a Running attempt becomes `Cancelled`, so its worker loses the lease at its next
  renew, complete or fail (an in-flight provider call is not interrupted before that). Repeating
  a cancel returns the cancelled view; finished runs cannot be cancelled (409).
- **Attempt bound.** Only `Abandoned` (lost-lease) attempts count toward the 2/3 bound; suspended
  attempts do not. Run and step views report `abandoned` computed from attempts.
- **Capacity.** Waiting runs count toward the 20 active runs per project.
- **Least privilege.** `cancel` and `decide` are executable only by `factory_lifecycle_runtime`;
  `request_decision` only by `factory_orchestration_worker`; neither role can read or change
  `StepDecisions` directly. Run views expose decision id, step, reason, options, status and choice,
  never the decider's identity.

### Executed verification — 2026-10-15

SDK 10.0.103, working tree on top of `bcd50fa`; no live LLM calls.

1. The new migration was replayed inside `BEGIN … SET CONSTRAINTS ALL IMMEDIATE … ROLLBACK` on the
   M7.1 database that holds the live-run data, then applied for real by the initializer (upgrade
   path: seven migrations; the live crash-test run still reads `Succeeded` with `abandoned`=1).
2. Fresh initialization of `factory_m72_verify_20261015` and `-BaselineOnly`
   `factory_m72_verify_20261015_legacy`.
3. Unit tests: 25 passed (aggregate, decision codes, processor).
4. Real headless Edge PKCE/state login, then the full suite with `-IncludeKeycloak`: build
   **0 warnings, 0 errors**; **745 passed, 0 failed, 0 skipped** (734 existing + 11 new), including
   four live Keycloak cases. TRX (ignored): `artifacts/m6-tests/Özgen_DESKTOP-TQO89E0_2026-10-15_22_57_50.trx`,
   SHA-256 `047b2dffde2c3df496859ca1b05ee3983096b78a61c5ce5264f0aaa093b1ede3`.
5. Keycloak and PostgreSQL stopped; data preserved.

New cases cover a waiting step surviving lease recovery and other workers, resuming only after a
human decision and completing publication; replayed and conflicting decisions; `stop` cancelling
the run; cancellation revoking a running lease; finished runs not cancellable; Service, non-member,
revoked-Reviewer and cross-project deciders refused; non-offered choices and unknown decisions;
database rejection of invalid reasons/options and of waiting publication; role separation; and
HTTP strict bodies for decide/cancel.

### M7.2 limits

- No production step requests decisions yet; the waiting path is exercised by tests through the
  worker port. Notification delivery (popup, real-time push) is M7.3/M12.
- Cancellation does not abort an in-flight provider call immediately; the worker stops at its next
  lease check (heartbeat 20 s) and the call may still be billed.
- Deciders are interim Human project Reviewers until M8 introduces tenant roles.
