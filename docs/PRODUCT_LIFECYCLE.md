# Milestone 6 — Product Lifecycle & Review Integration

Status: approved architecture, implemented **uncommitted local candidate** on master
`ce9f8b078c8355dbf086c96fd3d0b7412b6495e4`. No commit, push or M7 implementation is authorized.
This document describes inspected code and executed local verification, not production readiness.

## Identity, publication and compatibility

Project identity remains the existing authorized project. Admission creates stable server UUIDs
for WorkItem and Run in the same transaction as the scoped command key. RunId is exactly
WorkflowResult.WorkflowId and the stored ReviewEnvelope workflow identity. One WorkItem has at
most one ReviewCase; a failed first generation has none until successful publication. Each later
attempt gets a new RunId, preserving the WorkItem and case lineage. ReviewCase owns pure
publication/decision validation; independent database guards remain authoritative at commit.

Generation runs outside database transactions. Only validated complete Analyst → Architect results
are serialized through the existing immutable artifact format. Publication atomically records the
case/version, exact bytes and SHA-256, publication event, lineage and successful command outcome.
Existing IAgent/IModelProvider contracts, provenance and old ephemeral workflow HTTP behavior remain.
Existing review endpoints, ETags, reason policy, decision events and receipts remain compatible.
Approval never invokes an agent. Generator membership does not grant artifact or decision-reason access.

## Command lifecycle and recovery

Running may become Published, GenerationFailed, Cancelled, PublicationFailed or Abandoned.
Terminal commands are immutable. Completion clears ActiveRunId, retains LatestRunId and historical
outcome, and increments WorkItem revision. Ownership uses a private random 32-byte token; only its
SHA-256 digest is stored. Neither token nor digest is exposed in lifecycle responses.

Idempotency scope is project, principal, command kind, command version and UUID key. Fingerprint v1
uses ASCII magic UAF-M6-COMMAND, big-endian int32 fingerprint/command versions, length-prefixed strict
UTF-8 fields, -1 for null and ordered array counts. Inputs include kind, workflow version, project,
existing WorkItem/If-Match when applicable, task/context and confirmed sources. Create excludes new
server identities. Null, empty, order and Unicode bytes remain distinct; no normalization is applied.
SQL independently computes the same fingerprint. Reuse with changed input is 409; identical concurrent
admission returns the same IDs without another LLM execution. Replay rechecks authorization.

Active ownership and case revision fence finalization. Explicit new attempts can abandon an expired
command atomically; there is no background expiry worker or automatic regeneration. Model failures,
cancellation and definite publication rollback have separate terminal outcomes. Unknown commit outcomes
are not rewritten as failures: query the run or replay the original key. A committed response lost in
transport returns its historical outcome on replay without claiming its old ETag is current.

Bounds: 180-second generation deadline, four database-wide Running commands and four process-local
executions, two provider calls, no automatic application retries. The lifecycle OpenAI client disables
SDK retries and has a 90-second network timeout. Cancellation cannot forcibly terminate a noncooperative
provider; its process slot stays occupied until completion. There is no exact cost guarantee. Deadline
validation occurs under locks at publication admission, not at the eventual COMMIT timestamp.

## HTTP contracts

All routes require a validated JWT, active Factory identity/project membership and path-scoped project.

| Route | Permission and result |
| --- | --- |
| POST `/api/projects/{projectId}/work-items` | Generator; Idempotency-Key; admit and synchronously generate first publication. |
| POST `/api/projects/{projectId}/work-items/{workItemId}/analysis-architecture` | Generator; Idempotency-Key and exact strong If-Match; new attempt on existing lineage. |
| GET `/api/projects/{projectId}/work-items/{workItemId}` | Generator or Human Reviewer; safe lifecycle metadata and current WorkItem ETag. |
| GET `/api/projects/{projectId}/workflow-runs/{runId}` | Generator or Human Reviewer; safe historical command outcome. |

POST JSON fields are task, context, confirmedRequirements (id/description) and workflowVersion
(`analysis-architecture-v1`); no client project/actor/run identity. Strict field parsing rejects
unknown/duplicate fields; body limit 64 KiB. Responses expose IDs, status, static failure code and
publication reference, never prompts, artifact bytes, execution credentials or human reasons.

Strong WorkItem ETag: `"wi-v1:{workItem:N}:{workRevision}:{case:N-or-none}:{caseRevision-or-0}"`.
One SQL snapshot supplies all represented fields. Command changes increment WorkItem revision;
human decisions change the embedded case revision. Weak/list/wildcard/noncanonical tags are rejected.
POST outcomes, including replay, carry no ETag; fetch WorkItem for its current validator.
Responses use no-store; replay is explicitly marked. Running replay is 202, first publication 201,
later publication 200. Invalid requests are 400, denied membership 403, absent/cross-project IDs 404,
conflicts 409, stale preconditions 412, missing If-Match 428 and capacity 429. Integrity failures use
safe 500; storage/unknown outcomes use safe 503. No raw exception or SQL payload enters diagnostics.

## Migration and least privilege

Additive `20261014060000_ProductLifecycle` embeds ProductLifecycle.sql. Historical migrations and
EF snapshot are unchanged. Under an exclusive case-table lock, existing rows receive the constant
Legacy classification and the insertion default changes to Managed. No artifact/audit/revision
history is rewritten. Legacy classification cannot be changed and new Legacy inserts are forbidden.
Legacy CreateAsync retains its signature but fails after migration; old publication is limited to
existing Legacy cases. Managed writes require the guarded lifecycle publication path.

New lifecycle.WorkItems, lifecycle.GenerationCommands and review.ReviewCaseLineage have composite
project/identity constraints, append-only/terminal guards and deferred integrity triggers. Managed
artifacts require matching successful commands, lineage and publication events at COMMIT. Existing
human decision guards remain active. Project/principal/membership share locks order revocation
against admission/publication; case locks order publication against human decisions.

Explicit lifecycle-runtime-grants.sql assigns functions to dedicated NOLOGIN/NOINHERIT
factory_lifecycle_guard with no elevated flags or role memberships. Functions have fixed
pg_catalog,pg_temp search_path and qualified references; PUBLIC execution is revoked. The separate
factory_lifecycle_runtime has EXECUTE on five entrypoints, no protected-table DML/read privileges,
no DDL or owner-role membership. The authorization reader stays SELECT-only. The owner receives
narrow required reads/inserts/column updates, not table/schema ownership. API-supplied JWT identity
is a trusted boundary: PostgreSQL does not independently validate bearer tokens.

Operator setup uses a dedicated Factory database, never startup provisioning:

```powershell
./scripts/Initialize-FactoryLifecycle.ps1 -Database factory_m6_final
./scripts/Initialize-FactoryLifecycle.ps1 -Database factory_m6_legacy -BaselineOnly
node scripts/Test-KeycloakBrowser.mjs
./scripts/Test-FactoryLifecycle.ps1 -Database factory_m6_final -IncludeKeycloak -SdkDll 'C:/Program Files/dotnet/sdk/10.0.103/dotnet.dll'
```

Existing PostgreSQL/Keycloak installations and private settings are prerequisites. The scripts refuse
the existing factory_review_5a database. BaselineOnly creates a separate historical 5B.2 regression
database. Configure ConnectionStrings:Lifecycle with lifecycle runtime credentials, Reviews with
decision runtime credentials and Authorization with the read-only identity, all against the intended
migrated application database. Run-FactoryApi.ps1 does not configure the new lifecycle connection;
supply it explicitly through the environment. No secrets belong in committed configuration.

## Historical pre-correction verification — 2026-10-14

Final build using .NET SDK **10.0.103**: **0 warnings, 0 errors**.
Final full suite: **669 passed, 0 failed, 0 skipped**, including real PostgreSQL and four live
Keycloak cases. Fresh headless Edge PKCE/state login succeeded. All 624 existing cases plus 45 M6
cases ran. No live LLM requests were made; model results use deterministic validated fixtures.

TRX (ignored): `artifacts/m6-tests/Özgen_DESKTOP-TQO89E0_2026-10-14_12_34_46.trx`.
SHA-256: `7ec7a734fe7b60e631607cfdb279823b3d63b5967ce0f5d2ce05b6538566b0e2`.

Fresh migration of factory_m6_final passed. Upgrade tests start at 5B.2, seed historical artifact,
decision and receipt records, apply M6, compare exact bytes/audit/revisions and exercise legacy
publication and decisions. EF has-pending-model-changes reported none. Raw lifecycle SQL objects
are outside the tracked EF model; drift validation alone cannot verify their guards or grants.
The original verification database was untouched; isolated test/upgrade databases are retained.
Dedicated Keycloak and PostgreSQL verification services were stopped after verification; data preserved.

At that historical checkpoint: 32 UTF-8 text files, no historical migration modifications, empty index.
Git diff --check and local documentation link/anchor checks passed. Candidate binary/generated-file
and credential-pattern scanning found only an explicit fake API-key fixture in LifecycleHttpTests;
no real credential was identified. This final scan was not a new full-history or exhaustive secret audit.

New cases: domain 8, service 8, PostgreSQL 10, HTTP 3, integrity 4, migration 1, concurrency 10,
live Keycloak 1. Negative SQL probes cover missing/mismatched publication/command/lineage at COMMIT,
terminal mutation, closed Legacy creation and role escalation. Runtime denial covers artifacts,
memberships, audit mutation and DDL. Revocation tests observe backend lock waits in both orders;
concurrent create, conflicting keys, approval/publication, rollback, expired abandonment and late
finalization are tested. Real Service JWT generation succeeds but review reads/decisions fail.
HTTP tests cover strict bodies, project isolation, stable IDs, ETags and replay without repeated models.

An interim integrity fixture failed because pending deferred events prevented trigger alteration;
the owner-only expiry fixture now flushes constraints before restoring its trigger inside the same
transaction. Final verification supersedes interim runs. Test-only deadline setup uses owner privileges;
restricted runtime denial is tested separately. No failed run is counted as passing evidence.

## Targeted correction verification - 2026-10-14

The audit finding was reproduced in `factory_m6_final` with a rollback-only privileged probe:
a later artifact/event/Published command without case advancement passed SET CONSTRAINTS ALL
IMMEDIATE. The transaction was rolled back; no preserved schema or data was changed.

The corrected uncommitted migration checks the newly Published command transition independently
of ReviewCases updates, binding case/project/lineage, version/hash, RunId, expected/resulting
revision and PendingReview state. Published ResultRevision must explicitly be non-null. Historical
published commands are not incorrectly compared against later case states. Existing decision and
artifact guards, authorization locks and grants remain in place.

Fresh installation of all five migrations and explicit runtime grants passed in the newly approved
`factory_M6_correction` database. EF has-pending-model-changes reported none; this does not validate
raw SQL objects. Fresh focused tests: **74 passed, 0 failed, 0 skipped**, build **0 warnings/errors**.
TRX: `artifacts/m6-tests/Özgen_DESKTOP-TQO89E0_2026-10-14_21_58_07.trx`.
The correction adds 31 discovered cases: 12 publication regressions, 7 transaction failures,
4 domain and 4 SQL Generate fingerprint vectors, 1 conflicting-key race, 1 HTTP revision/replay
case and 2 additional service classification cases. Existing integrity and upgrade tests were
also extended. The extended upgrade test passed in the final full run below.

COMMIT and immediate-constraint tests reject missing first/later case advancement, including
publication after Approved, Rejected and ChangesRequested, plus revision/hash/version mismatches.
Successful publication and subsequent human decisions preserve historical run outcomes.
Transaction tests exercise real deadlock, lock timeout, statement timeout and deferred COMMIT
failure. SQLSTATE 40001 is injected by a server error, not a real serialization anomaly.
Connection-loss tests terminate only fixture-owned backends before/after commit; the after-commit
observer simulates a lost acknowledgment, not packet-level failure at every commit boundary.
HTTP lost-response coverage discards a response and replays; it does not disconnect a real network.

PostgresLifecycleStore records TransactionAborted only after confirmed rollback or explicit
transaction-aborting COMMIT rejection. In particular 55P03/57014 require acknowledged rollback;
transport errors and cancelled awaits remain OutcomeUnknown. LifecycleService records
PublicationFailed only for established safe failures; it does not regenerate or blindly retry.
Initial fault tests exposed three failures (4 passed, 3 failed), corrected before the final focused
run: disposed transaction access, deferred COMMIT classification and reuse of a deliberately killed
test pooled connection. A default-output build also failed (30 warnings, 6 errors) because an
unrelated API held DLLs; the separate Milestone6 output build succeeded without stopping that API.

A separate run without configured integrations reported **354 passed, 0 failed, 70 skipped**
(TRX `artifacts/m6-tests/Özgen_DESKTOP-TQO89E0_2026-10-14_21_58_49.trx`). Skipped parameterized
integration cases make its discovery total differ from a fully configured run. It is not full
verification. At that checkpoint, upgrade databases and a separate historical baseline regression database
required additional approval. The subsequently approved full run is recorded below. The historical
669-test result above is not current correction evidence and used a separate M5B.2 schema for older
cases. No live LLM request was made; fresh Keycloak verification is recorded below.

### Final correction verification after database approval

Fresh full build: **0 warnings, 0 errors** (.NET SDK 10.0.103).
Full suite: **700 passed, 0 failed, 0 skipped**, including four live Keycloak cases and a fresh
headless Edge PKCE/state login. All 624 historical cases plus 76 M6 cases executed.
TRX (ignored): `artifacts/m6-tests/Özgen_DESKTOP-TQO89E0_2026-10-14_22_03_19.trx`.
SHA-256: `ce08c5b2e636b743aed2f231ab4f63b3f3efb0db373e18576a1cebdd17e763c7`.

The approved new `factory_m6_correction_legacy` database received the committed M5B.2 migrations,
explicit grants and synthetic identities. Historical tests run there; lifecycle tests run against
`factory_M6_correction`. Migration tests created approved `factory_m6_upgrade_<GUID>` and
`factory_5b2_<fresh|upgrade>_<GUID>` databases. The expanded M6 upgrade test passed, preserving exact
historical bytes/audit/revisions and exercising all three decisions, artifact reads, revision
publication and historical receipt replay using restricted credentials after upgrade. Legacy
CreateAsync is intentionally denied after M6, rather than preserved as a compatible creation path.
This is representative M6 compatibility coverage, not all 624 old tests against the M6 schema.
Fresh corrected M6 installation passed earlier in this correction; EF drift was rerun successfully
after the full suite. Raw SQL integrity remains separately tested rather than covered by EF drift.

Dedicated Keycloak and PostgreSQL verification services were stopped; synthetic and preserved
database data remain intact. Final candidate manifest: 34 text files, empty index, no changed
committed migrations. Diff whitespace and heuristic credential/artifact checks passed; the only
credential-pattern match was an explicitly fake HTTP fixture key. No staging, commit or push.
The targeted corrections are ready for review; a commit still requires explicit approval.

## Limits and review gate

This is local integration evidence, not exhaustive concurrency proof, production performance/HA,
penetration testing or a live provider reliability test. Lost-response tests discard results and
reconnect; they do not reproduce all network failures or machine crashes. Administrators and API
identity mapping remain trusted. Deferred checks inspect artifact history; scaling is unbenchmarked.
Expired Running commands consume admission capacity until explicit recovery; revoked projects may
need trusted operator review. There is no durable result recovery or step resume after process loss.
M7 scheduling and M8 cost enforcement remain future milestones. Historical binary provenance and
unexplained live 500/502 limitations remain in the earlier evidence documents.

Credential scanning is heuristic and cannot prove absence of arbitrary/encoded secrets. No staging,
commit or push is authorized. Stop for review of this candidate before proceeding.

## Exact candidate manifest

34 text files (12 modified, 22 new); unstaged. Previously committed migrations are unchanged.

```text
AGENTS.md
docs/CURRENT_STATE.md
docs/DECISIONS.md
docs/HUMAN_DECISIONS.md
docs/PRODUCT_LIFECYCLE.md
docs/ROADMAP.md
scripts/Initialize-FactoryLifecycle.ps1
scripts/lifecycle-runtime-grants.sql
scripts/Test-FactoryLifecycle.ps1
src/Factory.Api/LifecycleEndpoints.cs
src/Factory.Api/Program.cs
src/Factory.Core/Lifecycle/GenerationFingerprint.cs
src/Factory.Core/Lifecycle/LifecycleContracts.cs
src/Factory.Core/Reviews/ReviewArtifact.cs
src/Factory.Core/Reviews/ReviewCase.cs
src/Factory.Infrastructure/Factory.Infrastructure.csproj
src/Factory.Infrastructure/Lifecycle/PostgresLifecycleStore.cs
src/Factory.Infrastructure/Reviews/Migrations/20261014060000_ProductLifecycle.cs
src/Factory.Infrastructure/Reviews/Migrations/ProductLifecycle.sql
src/Factory.Infrastructure/Reviews/PostgresReviewDecisionStore.cs
src/Factory.Infrastructure/Reviews/PostgresReviewStore.cs
src/Factory.Orchestration/Lifecycle/LifecycleService.cs
src/Factory.Orchestration/Workflows/AnalysisArchitectureWorkflow.cs
tests/Factory.Tests/Lifecycle/LifecycleConcurrencyTests.cs
tests/Factory.Tests/Lifecycle/LifecycleDomainTests.cs
tests/Factory.Tests/Lifecycle/LifecycleHttpTests.cs
tests/Factory.Tests/Lifecycle/LifecycleIntegrityTests.cs
tests/Factory.Tests/Lifecycle/LifecycleKeycloakTests.cs
tests/Factory.Tests/Lifecycle/LifecycleMigrationTests.cs
tests/Factory.Tests/Lifecycle/LifecyclePostgresTests.cs
tests/Factory.Tests/Lifecycle/LifecyclePublicationRegressionTests.cs
tests/Factory.Tests/Lifecycle/LifecycleServiceTests.cs
tests/Factory.Tests/Lifecycle/LifecycleTransactionFailureTests.cs
tests/Factory.Tests/Reviews/DecisionMigrationTests.cs
```
