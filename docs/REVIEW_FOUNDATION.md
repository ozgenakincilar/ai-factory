# Milestone 5A — Persistent review foundation

Implemented scope: pure approval state rules, PostgreSQL persistence for review cases and
immutable whole-package versions, SHA-256 verification, append-only publication audit,
explicit transactions, optimistic concurrency, migrations, and real database tests.

**No approval endpoint, authenticated reviewer, OIDC, membership authorization, decision
writer, or command idempotency is implemented.** Those belong to 5B. Nothing is registered
in Program.cs; existing Milestone 1–4 routes, DI, DTOs, agents and validators are unchanged.
Running the API does not require PostgreSQL and does not automatically persist workflows.
Only internal callers/tests explicitly invoke IReviewStore. No stored approval can be made
through this foundation: the database currently accepts only PendingReview.

## Responsibilities and schema

Core owns ReviewState/ReviewDecision, pure state rules, ReviewEnvelope, defensive-copy
ReviewArtifact, snapshots, safe conflict/integrity exceptions and IReviewStore.
Infrastructure owns ReviewDbContext, PostgresReviewStore, serialization and migrations.
EF/Npgsql types do not enter Core or Orchestration. Design-time creation reads only
FACTORY_REVIEW_CONNECTION; it does not boot the API or load OpenAI credentials.

| Table in schema review | Purpose / constraints |
| --- | --- |
| ReviewCases | UUID key, ProjectId (200 characters), CurrentVersion, Revision concurrency token, State restricted to PendingReview in 5A |
| ArtifactVersions | Composite key (CaseId, Version), source WorkflowId, Content bytea, ContentHash (64 lowercase SHA-256 hex), UTC CreatedAt; FK to case, DB hash check |
| PublicationEvents | UUID key, case/version/hash FK to artifact, UTC timestamp; at most one publication event per version via unique constraint; store transaction inserts the event |

A deferred FK binds the case's current version to an actual artifact at transaction commit.
New artifacts, their publication event, and the updated case revision commit atomically.
Immutable-table triggers reject UPDATE, DELETE and TRUNCATE; EF also rejects tracked
modification/deletion. Runtime grants deny these operations and DDL. Manual migration SQL
contains the triggers and deferred FK (not represented by EF's model snapshot); preserve
and review those guards in future migrations. No cascade deletion is configured.

Publication events are system events, **not human approval audit records**. Reviewer identity,
membership, durable workflow-run coordination, decision events and idempotency receipts are
deferred rather than represented by unauthenticated placeholder identities.

## Artifact boundary

Only successful workflows with both stages completed, no failure metadata, nonempty workflow
ID and valid typed analysis/architecture can be stored. Existing validators recheck provenance,
traceability and proposed status against the detached input/evidence. Partial/failed outputs
are rejected. This verifies consistency, not cryptographic proof of origin: internal callers
are trusted in 5A. Project filtering is isolation by key, **not authorization**.

Format 1 serializes the envelope once to UTF-8 JSON with the existing strict options. It binds
case/version, workflow, input/context/evidence, analysis, proposed architecture, format and
validator version, and UTC creation time. Timestamps are normalized to PostgreSQL microseconds.
All nested caller collections are detached before the first asynchronous database operation.
Callers must keep collections stable during that initial synchronous serialization.

SHA-256 is computed over the exact stored bytes, never jsonb or a later reserialization.
Byte arrays are defensively copied. Reads verify hash, typed contents and row/envelope identity,
including project, workflow, version and timestamp. A new version changes the envelope/hash;
prior versions are retained unchanged. Format/validator changes require a new format and
preservation of the old reader; never rewrite stored bytes to a new serializer format.
Hashes establish integrity relative to the stored hash, not authenticity against a DB owner.

## State and transactions

Pure rules: PendingReview + Approve => Approved; Reject => Rejected; RequestChanges =>
ChangesRequested. Reject/RequestChanges require a nonblank reason. Every decision is terminal
for that version. Publishing a revision returns PendingReview. These rules do not authenticate
a reviewer, persist a decision, alter provenance, or execute code. Persistence of terminal states
is deliberately forbidden by a 5A database CHECK until 5B supplies authenticated decisions.

CreateAsync publishes v1. PublishAsync requires the exact expected Revision and matching project.
Every operation owns its context and transaction. The case concurrency update precedes artifact
and event insertion inside that transaction; conflicts abort, and later insertion failure rolls
back the earlier update. No automatic retries or application-level idempotency are provided.
An ambiguous commit outcome must be reconciled before retrying; this is a 5B command-receipt task.

## Local setup and migrations

Environment verified before installation on 2026-10-13: Docker/psql/pg_ctl were not found,
no PostgreSQL service/default-port listener was found, WSL reported not installed, .NET SDK
10.0.401 was available. No Docker or WSL installation was performed.

For actual testing, the official EDB Windows PostgreSQL **18.6** binary archive was downloaded
to ignored artifacts/postgres and extracted under distribution/pgsql. It is not a repository
dependency or Windows service. A SCRAM-authenticated cluster runs on 127.0.0.1:55432 only.
Scripts use Windows short paths to avoid an observed native initialization encoding failure
under the non-ASCII profile path. Machines without suitable short paths may need an ASCII-only
binary/workspace location. Docker/WSL operation has not been tested.

Pinned dependencies: Microsoft.EntityFrameworkCore, Relational and Design **10.0.12**;
Npgsql.EntityFrameworkCore.PostgreSQL and Npgsql **10.0.3**. The downloaded official provider
nuspec requires EF/Relational >=10.0.4 and <11.0.0, and Npgsql >=10.0.3. Successful restore/build
verified resolution. The workspace-local dotnet-ef tool is **10.0.12**.

Once a trusted PostgreSQL Windows binary distribution is available at the default path:

```powershell
dotnet tool install dotnet-ef --version 10.0.12 --tool-path artifacts/tools
./scripts/Start-ReviewPostgres.ps1
./scripts/Test-ReviewFoundation.ps1
./scripts/Stop-ReviewPostgres.ps1
```

Start creates only its dedicated cluster/database/roles and preserves existing data on restart.
It refuses an occupied port. No helper deletes data. Stop targets only the dedicated cluster.
The launcher uses a hidden pg_ctl process and waits for that parent, not all descendant server
processes. Database passwords are generated locally and stored under ignored artifacts;
protect that directory with filesystem permissions. Never print or commit connection.json,
owner-password, logs, binaries or database files. Scripts do not change machine ACLs or install
a service. Do not use these local development credentials/settings as production provisioning.

For a separately provisioned PostgreSQL instance, supply FACTORY_REVIEW_CONNECTION privately
for the migration owner, then run:

```powershell
./artifacts/tools/dotnet-ef database update --project src/Factory.Infrastructure --startup-project src/Factory.Infrastructure --context ReviewDbContext
```

Provision a separate factory_review_writer LOGIN and apply scripts/review-runtime-grants.sql
as the owner. Set FACTORY_REVIEW_TEST_CONNECTION privately to the writer connection for tests.
Without it, five PostgreSQL tests explicitly skip; that is **not** full verification. Tests append
synthetic records to an isolated test database and do not clean/delete historical data.

For future schema changes: generate a new migration, review SQL (including guards and grants),
test against a fresh and upgraded real database, and apply explicitly with the owner role.
Never run production migrations at API startup, use EnsureCreated alongside migrations, or grant
the future runtime service schema ownership. The generated Down migration is destructive and
requires explicit approval; it was not executed. Production TLS, backups and deployment/migration
automation are not verified by the local test.

## Executed verification and limitations

Migration 20261013020634_InitialReviewFoundation applied successfully to PostgreSQL 18.6.
Reapplication reported already up to date; has-pending-model-changes reported none.
The first fresh-database history lookup logged a missing-history-table query before EF created
and applied migration history; final update completed successfully.

Full build: **0 warnings/errors**. Full suite: **170 passed, 0 failed, 0 skipped**, including
155 existing cases, 10 new unit cases, and 5 real PostgreSQL cases using the restricted writer.
Tests verify serialization/provenance, detached input/bytes, hashes/version binding, terminal
state rules, fresh contexts/connections, project filters, concurrent publishers, transaction
rollback after claiming a revision, DB hash/FK constraints, denial of persisted approval,
and restricted-role mutation/DDL denial.

Seven stored artifact versions and database-recomputed SHA-256 hashes matched exactly before
and after a real cluster stop/start, using separate psql processes. An owner-issued UPDATE was
also rejected by the immutable-table trigger. A database owner can disable/drop guards; protection
against such an administrator is explicitly deferred. No API restart persistence claim is made:
the API is intentionally not connected to this store yet.

The final full suite was rerun through scripts/Test-ReviewFoundation.ps1 after restart and after
strengthening the concurrency test to synchronize both publishers' reads of the same revision;
170 tests passed with no skips. The isolated database was stopped afterward; its files remain
under ignored artifacts for review/restart. No commit or push was made. Stop for 5A review.

Observed setup issues: NuGet initially reported an existing-file error (serial restore succeeded);
initdb argument handling then Windows path encoding required fixes; the original launcher waited
for server descendants and was corrected. An initial manual SQL probe lost identifier quotes in
PowerShell; stdin SQL corrected it. These were setup/verification issues, not unexplained passing
tests. Historical Milestone 3 HTTP 502 and Milestone 4 HTTP 500 remain unresolved and unrelated
to any verified root-cause diagnosis here. No live model request was required for 5A.

## Official references consulted

## Closure review (2026-10-13)

Reviewed all 19 candidates (4 modified, 15 new). No application code changed during closure.
The setup helper restarted the preserved cluster, migration update reported already current,
and full build/test completed with **0 warnings/errors; 170 passed, 0 failed, 0 skipped**.
All five real PostgreSQL cases ran with the restricted writer. The cluster was stopped afterward.
The concurrency test synchronizes both reads; rollback injects failure after the revision update.
Restricted-role tests exercise mutation/DDL denial; they do not independently exercise owner
triggers (the earlier owner probe is separate evidence). No approval route or API registration exists.

PostgreSQL's Windows page links EDB binary archives; EDB lists version 18.6, matching the local
postgres.exe version output. The downloaded archive's local SHA-256 fingerprint is
`e2246ba91d22345bc3d017586c09ede52d9df180b1eeb480f050445f1cad84e2`.
Extracted postgres.exe, pg_ctl.exe, initdb.exe and psql.exe matched their archive entries by SHA-256.
postgres.exe has no Authenticode signature. No independent publisher checksum was verified:
this fingerprint and archive comparison establish local consistency, not authenticated provenance.

Candidate text inspection and credential-pattern checks found no credentials, private keys,
binaries, database files or build output. Generated local database passwords were checked against
all candidates without printing them. Git ignores the archive, cluster, credentials and build output;
the EF-generated migration sources are intentionally included. These checks are not a security audit.

Runtime grants assume a dedicated role without inherited or pre-existing elevated privileges;
the grants script does not revoke arbitrary earlier grants. Direct SQL writers remain trusted:
event existence and monotonically increasing revisions are enforced by store transactions, not
every possible raw SQL command. The unique event constraint alone does not enforce event existence.
Production provisioning, authenticated authorization, idempotency, migration downgrade and
administrator tamper resistance remain outside verified 5A scope. Local tests depend on Windows
short paths, the ignored PostgreSQL distribution, dotnet-ef 10.0.12 and the private connection file.
Historical HTTP 500/502 causes remain unresolved. No staging, commit, push or 5B work occurred.

### Reference links

- [Npgsql EF 10 release notes](https://www.npgsql.org/efcore/release-notes/10.0.html)
- [Provider package metadata](https://www.nuget.org/packages/Npgsql.EntityFrameworkCore.PostgreSQL/10.0.3)
- [EF migrations](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/applying)
- [EF design-time context](https://learn.microsoft.com/en-us/ef/core/cli/dbcontext-creation)
- [PostgreSQL Windows distributions](https://www.postgresql.org/download/windows/)
- [EDB binary downloads](https://www.enterprisedb.com/download-postgresql-binaries)
