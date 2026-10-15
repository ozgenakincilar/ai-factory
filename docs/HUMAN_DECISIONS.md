# Milestone 5B.2 — Secure human decision transactions

Approved implementation scope on 2026-10-14: existing stored cases only, three authenticated

review endpoints, DecisionEvents and CommandReceipts, no generation submission or execution.

## Current committed status

Milestone 5B.2 is committed and pushed as `e1bced7230d89352ddf6e69aeae4bee71fd106bf`.
Local HEAD was verified against this implementation baseline; pushed status was confirmed by
the user's independent GitHub inspection. The original implementation baseline was ff24cd5.
The verification sections and candidate manifests below retain their historical pre-commit
context, including statements that no staging/commit/push occurred during those checks.

Historical full verification: **624 passed, 0 failed, 0 skipped**, with PostgreSQL and live
Keycloak; build **0 warnings/errors**. The later documentation-only run reported **332 passed,
0 failed, 36 skipped** without configured PostgreSQL/Keycloak, and build **0 warnings/errors**.
It is partial evidence and does not supersede the full suite. No tests were independently rerun
for this status reconciliation. Future commits, pushes and milestone implementation require
explicit user authorization. M6 subsequently received that approval and is committed as 1b93fc4; see [Product Lifecycle](PRODUCT_LIFECYCLE.md). The verification below remains historical 5B.2 evidence.

## Security requirements reviewed before SQL implementation

Code inspection confirms that 5A owns immutable bytes/hash and the shared case Revision, while

5B.1 provides authenticated issuer/subject and independent Reviewer membership. The existing

PendingReview-only CHECK must not simply be removed without a replacement integrity boundary.

1. **Authorization helper:** fixed `search_path = pg_catalog, pg_temp`; every application table

   and function reference schema-qualified; no dynamic SQL. Dedicated NOLOGIN, NOINHERIT,

   nonsuperuser owner receives SELECT and only the column UPDATE privilege needed for row locks

   on access tables. No memberships or schema ownership. PUBLIC EXECUTE revoked; decision runtime

   explicitly granted helper execution. Lock project, principal, then Reviewer membership FOR SHARE

   until commit; recheck active Human identity. Test both revocation race orders at database level.

2. **Transition integrity:** a BEFORE guard checks identity, legal state/version changes and exactly

   one revision increment. Deferred constraint triggers require each terminal transition to match

   an immutable event and receipt, and each inserted event to match the case transition. Raw SQL

   missing/mismatched records must fail at COMMIT, not merely through EF. Artifact immutability

   remains unchanged. One decision per case/version; the decision event is also the audit record.

3. **Idempotency:** unique (project, principal, command version, key). Canonical command fingerprint

   includes case, expected revision, version/hash, action and exact reason. Receipt references the

   immutable event and a versioned historical result; no independent mutable response copy.

   Identical replay is historical, returns no ETag; conflicting reuse is 409. Reauthorize replay.

   Deterministic concurrent duplicates and discarded/lost-response recovery are required.

4. **ETag/concurrency:** one consistent SQL snapshot for metadata; exact strong case If-Match,

   no wildcard/weak/list support. Decisions and publication contend on the same Revision.

   Verify bytes and row/envelope identity before deciding; test both publication-race orders,

   stale revisions, version/hash mismatches, and unchanged historical bytes/provenance.

5. **Least privilege:** authorization reader remains SELECT-only. Decision runtime may read review

   records, insert event/receipt, update case State/Revision and execute the helper. No artifact,

   publication, membership, DDL, UPDATE/DELETE/TRUNCATE audit permissions. Guards also protect direct

   SQL. Dedicated helper owner is provisioned and checked before runtime grants are enabled.

These requirements were documented before SQL implementation. Executed results are recorded below. PostgreSQL

constraint triggers support deferred COMMIT checks; FOR SHARE blocks UPDATE/DELETE, and locking

SELECT requires UPDATE privilege. This is why the runtime uses a narrow SECURITY DEFINER helper

instead of receiving access-table UPDATE rights. Function ownership/grants are operator provisioning;

the migration revokes PUBLIC execution immediately and does not grant runtime access itself.

References checked before SQL:

- https://www.postgresql.org/docs/18/sql-createfunction.html

- https://www.postgresql.org/docs/18/sql-createtrigger.html

- https://www.postgresql.org/docs/18/sql-select.html

- https://www.postgresql.org/docs/18/explicit-locking.html

- https://learn.microsoft.com/en-us/ef/core/saving/concurrency

- https://www.rfc-editor.org/rfc/rfc9110.html

## Boundaries

API and DB administrators remain trusted. PostgreSQL does not independently validate the JWT;

the API supplies validated identity. Reasons and artifact contents are confidential data, never

diagnostic fields. No automatic retries of writes; retry the same command/key after an uncertain

response. Only successful commands consume receipts. No retention/deletion policy is added.

Preserve format-1 historical response serialization when adding later formats.

## Implementation and endpoint contracts

`IReviewDecisionStore` is separate from the existing publication store. Core gains only decision

commands/results/errors; agent/provider contracts are unchanged. `PostgresReviewDecisionStore`

authorizes inside a transaction, checks a durable receipt before stale-state checks, verifies exact

artifact bytes, claims the shared Revision, inserts event and receipt, then commits. A concurrency

loser opens a new read/reconciliation transaction; it never blindly retries the write.

Migration `20261013211346_SecureHumanApprovalTransactions` adds the two tables, composite foreign

keys/unique constraints and SQL guards. The old PendingReview-only constraint is replaced atomically.

Event preparation reauthorizes and supplies database timestamp, canonical command fingerprint, and

project/principal/membership revisions. Events store authenticated issuer/subject/principal snapshots.

Receipts use a composite FK to the exact event/scope/fingerprint; format 1 reconstructs its historical

response from immutable fields. Both tables reject UPDATE, DELETE and TRUNCATE. Down migration throws

rather than silently deleting human decision history.

All three routes require a validated Human access token and explicit active project Reviewer membership:

| Method and route | Contract |

| --- | --- |

| GET `/api/projects/{projectId}/reviews/{caseId}` | Case ID/project, current version, SHA-256, revision, state and current decision summary from one SQL statement. Strong ETag `"review-{caseId:D}-r{revision}"`. |

| GET `/api/projects/{projectId}/reviews/{caseId}/artifacts/{version}` | Exact stored JSON bytes, with immutable ETag `"sha256-{hash}"`; historical versions remain readable by current Reviewers. |

| POST `/api/projects/{projectId}/reviews/{caseId}` | Exact strong `If-Match` case ETag plus one nonempty GUID-D `Idempotency-Key`. JSON described below. Returns 200 and decision snapshot only after commit. |

POST JSON has exactly four properties: `decision` (`approve`, `reject`, `requestChanges`),

`artifactVersion` (positive integer), `artifactSha256` (64 lowercase hex), and `reason` (string or

null). Reject/Request Changes require a nonblank reason; strings are limited to 4,000 UTF-16 code

units. The body limit is 16 KiB. Duplicate/unknown fields and client identity fields are rejected.

Only PendingReview can transition to Approved/Rejected/ChangesRequested. One decision per artifact

version. Publishing a new version through the existing internal store resets PendingReview and

increments Revision; this milestone adds no publication/submission endpoint.

Missing If-Match is 428; malformed commands/weak/list/wildcard validators are 400; unauthenticated

requests are 401; unauthorized projects/principals are 403. An authorized project addressing a

missing or another project's case receives 404. Stale Revision is 412; version/hash mismatch,

already-decided state, or conflicting key reuse is 409. Unavailable storage is 503; detected integrity

failure is a safe 500. No database messages, SQL, reasons or artifact bytes enter diagnostics.

Successful new decisions carry their resulting case ETag. Identical replay returns the original

historical body, `Idempotency-Replayed: true`, and **no ETag**, even after publication. Fetch the case

again for its current validator. All review responses use `Cache-Control: no-store`. Replays are

reauthorized and can be denied following revocation; the receipt is not an authorization bypass.

## Reason policy v1 and invariant correction

The accepted whitespace set is explicitly pinned in C# and PostgreSQL:
`U+0009-U+000D`, `U+0020`, `U+0085`, `U+00A0`, `U+1680`, `U+2000-U+200A`,
`U+2028`, `U+2029`, `U+202F`, `U+205F`, `U+3000` (25 code points).
Reject and Request Changes require a non-null reason containing at least one code point
outside this set. Approve retains an optional reason, including null, empty or whitespace-only.
All supplied reasons must be well-formed Unicode without NUL and at most 4,000 UTF-16 code units.
Reasons are stored verbatim: no trimming, normalization or fingerprint changes. U+200B and U+FEFF
are outside the whitespace set and therefore count as explanation characters, preserving the
previous .NET behavior. This is a structural invariant, not a guarantee of meaningful prose.
The independent 16 KiB HTTP body limit still applies, including JSON escaping overhead.

`DecisionReasonPolicy` is shared by command and state-machine validation. PostgreSQL's
`decision_reason_v1` CHECK uses `btrim` with the explicit character list, not a POSIX whitespace
class. Its length expression counts code points plus supplementary code points under `C` collation
to enforce the same UTF-16 limit. PostgreSQL UTF-8 text rejects NUL and malformed Unicode.
See [PostgreSQL string functions](https://www.postgresql.org/docs/18/functions-string.html) and
[.NET whitespace behavior](https://learn.microsoft.com/en-us/dotnet/api/system.string.isnullorwhitespace?view=net-10.0).

Additive migration `20261014010250_AlignDecisionReasonPolicy` atomically replaces the old CHECK.
Previously applied migrations and their SQL helpers remain byte-for-byte unchanged in this pass.
The migration validates existing rows: any legacy invalid reason blocks the upgrade and rolls back
the constraint replacement. It does not rewrite or delete immutable audit records. Stop for operator
review if that occurs; remediation requires a separately approved history-preserving design.
Downgrade is deliberately blocked pending explicit review. No tables, grants, locks, transaction
boundaries, revisions, receipt formats, authorization or idempotency behavior are changed.

`ReviewEndpoints.Run` classifies PostgreSQL SQLSTATE class 23 integrity failures as safe HTTP 500
`review_integrity_failure`, including failures wrapped in EF `DbUpdateException`. Storage failures
remain safe HTTP 503. Logs retain correlation and exception type, never exception payloads, SQL,
reasons, tokens or artifact contents.

## Operator setup

Use a dedicated factory database and the existing local PostgreSQL installation. No new packages,

Windows services, Docker or WSL components were installed for 5B.2.

```powershell

./scripts/Start-ReviewPostgres.ps1

./scripts/Initialize-FactoryDecisions.ps1

# With local Keycloak already configured/running:

node scripts/Test-KeycloakBrowser.mjs

./scripts/Test-FactoryDecisions.ps1 -IncludeKeycloak

```

The initializer applies migrations as owner, then runs `decision-runtime-grants.sql`. It provisions

`factory_decision_runtime` separately from the authorization reader and transfers SECURITY DEFINER

functions to `factory_review_guard` (NOLOGIN, NOINHERIT, no elevated flags or role memberships).

The guard receives only required reads and access-table Revision-column UPDATE for FOR SHARE.

The decision runtime receives review reads, case State/Revision UPDATE, event/receipt INSERT, and

explicit helper/fingerprint EXECUTE. No access-table writes, artifact writes, audit changes or DDL.

PUBLIC function execution is revoked in the migration itself. Provisioning additionally removes

PUBLIC CREATE/TEMP on this dedicated database and CREATE on public schema, and checks unexpected

role/table/column privileges. Do not use this grant script against a shared application database.

Configure `ConnectionStrings:Reviews` with the decision runtime; retain `ConnectionStrings:Authorization`

with the SELECT-only reader. `Run-FactoryApi.ps1` sets both through environment variables, which

remain above Development User Secrets. Startup does not apply migrations or grant privileges.

Private passwords, tokens, cluster data, logs and verification artifacts stay in ignored local paths.

Deploy migration and grants together before exposing review routes; the migration alone is not

complete runtime provisioning. Custom SQL triggers/privileges are not covered by EF model drift alone.

## Original implementation verification - 2026-10-14 (historical)

- `node scripts/Test-KeycloakBrowser.mjs`: fresh real headless Edge login, state check and PKCE exchange

  succeeded. .NET independently validated token signature/audience/lifetime and ID-token nonce.

- `./scripts/Test-FactoryDecisions.ps1 -IncludeKeycloak`: full `dotnet build` succeeded with **0 warnings,

  0 errors**; `dotnet test --no-build --no-restore` passed **291 tests, 0 failed, 0 skipped**.

  Existing 220 cases remain; 71 cases were added. Final local TRX:

  `artifacts/decision-tests/Özgen_DESKTOP-TQO89E0_2026-10-14_00_37_41_net10.0.trx` (ignored).

  TRX SHA-256: `05578c7ceab62b4a6138dfad0df121c00df89866888b54f06c5123ac522575e6`.

- New coverage: 40 PostgreSQL decision/security cases, 28 HTTP/error cases (26 use real PostgreSQL),

  two fresh/upgrade migration cases, and one live Keycloak review case. The total suite includes

  three live Keycloak cases plus the nine existing PostgreSQL cases. HTTP hosts use TestServer;

  authentication middleware and decision database calls are real, with synthetic signed tokens

  except in the explicit live Keycloak cases. No live OpenAI calls or automatic agent execution.

- Fresh database migration and upgrade from `20261013084222_ProjectAuthorization` both passed.

  Upgrade preserved stored bytes, SHA-256 and case metadata. Both databases exercised restricted

  runtime decisions and a direct unauthorized transition rejected at COMMIT. Isolated verification

  databases are retained, not dropped. The existing local database was also upgraded successfully.

- `dotnet-ef migrations has-pending-model-changes --project src/Factory.Infrastructure

  --startup-project src/Factory.Infrastructure --context ReviewDbContext`: **no model changes**.

  The same assertion passed inside both migration tests.

- Real database tests observed exact backend-PID lock waits for project/principal/membership revocation in both orders;

  covered deterministic identical/conflicting concurrent commands, separate-key winners, both

  approval/publication orders, consistent snapshots during uncommitted publication, discarded-result

  recovery with fresh connections, replay after publication/revocation, cross-project and principal

  scope, and stale version/hash/revision failures.

- Raw SQL probes bypassed EF: missing/mismatched event/receipt/state/revision failed deferred checks

  at COMMIT; identity/hash/project/receipt forgery failed database enforcement. Restricted roles

  were denied artifact/membership mutation, audit changes, schema/table/temporary DDL and owner-role

  escalation. Owner-issued audit deletion was rejected by immutable triggers.

- Reviewed the full tracked diff and new files. `git diff --check` passed. All 26 candidate files

  are UTF-8 text; no binary/runtime/build artifacts are included. Known local credential/token values

  were compared in memory without printing them; no matches. Index empty; no commit/push/rewrite.

- Dedicated Keycloak and PostgreSQL verification services were stopped; all data was preserved.

Initial failures are not omitted: the first database run had 24 failures/5 passes due primarily to

an ambiguous PL/pgSQL PrincipalId reference; table aliases fixed it. A subsequent basic transaction

run caught EF supplying an empty fingerprint to the receipt; explicitly supplying the canonical

DB-computed fingerprint fixed it. Race tests stalled in the first lock-monitor implementation;

bounded independent Npgsql monitoring replaced it, after which all race cases passed. One HTTP

fixture compile error was corrected before execution. The unreleased migration's helper was

corrected on the existing development database with CREATE OR REPLACE; final fresh/upgrade tests

executed the corrected migration from scratch. No released migration or Git history was rewritten.

## Invariant-correction verification - 2026-10-14 (historical pre-commit run)

- `./scripts/Test-FactoryDecisions.ps1 -IncludeKeycloak`: build **0 warnings, 0 errors**;
  **624 passed, 0 failed, 0 skipped**, including all existing cases, real PostgreSQL transactions
  and three live Keycloak cases. Fresh browser PKCE login succeeded before the run.
  TRX: `artifacts/decision-tests/Özgen_DESKTOP-TQO89E0_2026-10-14_04_09_08_net10.0.trx` (ignored).
  SHA-256: `90599f6c87a10471638944ef5ce30b54c3cff1f29d4fc093e47bcee27178d9cd`.
- This pass adds **333 test cases** over the previously verified 291: 111 reason matrices through
  domain/command validation; three malformed UTF-16 domain cases; 111 restricted-runtime direct SQL
  cases; 99 authenticated HTTP reason cases; seven direct/wrapped integrity or storage-error cases;
  two additional migration scenarios. Reasons cover every pinned whitespace character, mixed
  spaces/tabs/CR/LF/Unicode whitespace, null/empty/NUL, valid unchanged text, U+200B/U+FEFF and
  BMP/supplementary UTF-16 length boundaries.
- Four migration scenarios passed: fresh database, upgrade from 5B.1, upgrade from original 5B.2
  preserving a valid historical decision, and rejection of an original-5B.2 database containing a
  tab-only Reject. The last scenario verifies SQLSTATE 23514, unchanged migration history, restored
  old constraint, unchanged invalid audit text and unchanged artifact bytes after rollback.
  Dedicated databases are retained. The existing local development database upgraded successfully.
- Invalid restricted-runtime INSERTs fail a CHECK/type/encoding boundary before COMMIT; attempting
  COMMIT on the aborted transaction rolls back the claimed revision and leaves zero events/receipts.
  Valid cases commit with exact unchanged reasons. Existing deferred COMMIT, revocation, duplicate
  command, publication race, rollback, authorization and least-privilege tests all passed again.
- Fresh `dotnet-ef migrations has-pending-model-changes --project src/Factory.Infrastructure
  --startup-project src/Factory.Infrastructure --context ReviewDbContext`: **no model changes**.
  Fresh and successful upgrade tests independently assert the same result.
- Final diff review and `git diff --check` passed. All 31 candidate files decode as UTF-8;
  no credentials, binary files or runtime/build artifacts were found. The 15-file correction delta
  is listed below. All seven previously existing migration/helper files match their pre-correction
  SHA-256 fingerprints. Transaction store, SQL guards and grant scripts are unchanged by this pass.
- Fresh pattern scans covered the candidate and all 122 unique blobs in the five local
  branch/tag/remote commit histories: no findings or committed build artifacts. In-memory comparison
  against ten known private values from six local settings/token files found no candidate matches.
  Ignore checks covered PostgreSQL/Keycloak settings, test evidence, bin and obj. This does not prove
  absence of arbitrary secrets, encoded secrets or sensitive prose. The earlier all-object audit
  separately found generated binary objects under local Codex tree refs; they are outside commit
  history and this candidate. No ref cleanup or mirror push is authorized or performed.
- Dedicated Keycloak and PostgreSQL verification services were stopped after this run; data and
  isolated migration databases were preserved. No staging, commit, push or history rewrite occurred.
- Initial correction verification is retained: 331 targeted cases passed; two malformed-Unicode
  fixtures failed because test-attribute serialization replaced invalid surrogate characters.
  Fixtures now construct those strings from integers inside each test. Two subsequent compile
  errors in the new migration fixture (raw-string delimiter and local variable shadowing) were
  corrected before the successful full run. No failed run is counted as successful evidence.

## Limits and review gate

This is tested local transaction security, not a production deployment audit or exhaustive schedule

proof. Lost-response coverage discards a committed result and recreates connections; it does not

simulate a machine crash, network partition or storage loss. PostgreSQL and API administrators remain

trusted, including the API's supplied validated identity. Database credentials must never reach

untrusted clients. Revocation ordered after an already-authorized decision waits for that transaction;

it is not retroactive. No JWT introspection, instant IdP revocation, production TLS/proxy/HA deployment,

external log-sink redaction, performance benchmark or independent penetration test was performed.

The earlier PostgreSQL binary publisher-checksum limitation remains in REVIEW_FOUNDATION.md.

The credential scan is scoped to candidate files and known local secrets plus pattern/content

review; it is not proof of absence of all sensitive information or a fresh full-history audit.

Historical response format 1 must remain stable in future serializers. Receipt retention, online

membership administration, workflow coordination and automatic execution are out of scope.

The pre-commit review gate was subsequently satisfied by explicit user approval and staged-content
verification before commit e1bced7. Future commits, pushes and milestone implementation still
require explicit user authorization; this evidence does not authorize M6 implementation.

## Reviewed candidate manifest (historical pre-commit snapshot)

31 text files: 13 modified and 18 new; none were staged at that verification checkpoint. This includes the original 26-file
5B.2 candidate and five additional files from the invariant correction (one modified, four new).

```text
AGENTS.md
docs/AUTHENTICATION.md
docs/CURRENT_STATE.md
docs/DECISIONS.md
docs/HUMAN_DECISIONS.md
docs/REVIEW_FOUNDATION.md
scripts/Initialize-FactoryDecisions.ps1
scripts/Run-FactoryApi.ps1
scripts/Test-FactoryDecisions.ps1
scripts/decision-runtime-grants.sql
src/Factory.Api/Program.cs
src/Factory.Api/ReviewEndpoints.cs
src/Factory.Core/Reviews/DecisionReasonPolicy.cs
src/Factory.Core/Reviews/ReviewDecisions.cs
src/Factory.Core/Reviews/ReviewState.cs
src/Factory.Infrastructure/Reviews/DecisionModel.cs
src/Factory.Infrastructure/Reviews/Migrations/20261013211346_SecureHumanApprovalTransactions.Designer.cs
src/Factory.Infrastructure/Reviews/Migrations/20261013211346_SecureHumanApprovalTransactions.cs
src/Factory.Infrastructure/Reviews/Migrations/20261014010250_AlignDecisionReasonPolicy.Designer.cs
src/Factory.Infrastructure/Reviews/Migrations/20261014010250_AlignDecisionReasonPolicy.cs
src/Factory.Infrastructure/Reviews/Migrations/ReviewDbContextModelSnapshot.cs
src/Factory.Infrastructure/Reviews/Migrations/SecureHumanApprovalSql.cs
src/Factory.Infrastructure/Reviews/PostgresReviewDecisionStore.cs
src/Factory.Infrastructure/Reviews/ReviewDbContext.cs
tests/Factory.Tests/Authorization/LiveKeycloakTests.cs
tests/Factory.Tests/Integration/ReviewEndpointTests.cs
tests/Factory.Tests/Integration/TestSecurity.cs
tests/Factory.Tests/Reviews/DecisionMigrationTests.cs
tests/Factory.Tests/Reviews/DecisionPostgresTests.cs
tests/Factory.Tests/Reviews/DecisionReasonTests.cs
tests/Factory.Tests/Reviews/PostgresReviewTests.cs
```

## Files changed by the invariant-correction pass (historical)

Compared with the independently audited 26-file local candidate, that pass changed 15 files
(including four newly created files). The full 31-file commit candidate is listed above.

```text
AGENTS.md
docs/CURRENT_STATE.md
docs/DECISIONS.md
docs/HUMAN_DECISIONS.md
src/Factory.Api/ReviewEndpoints.cs
src/Factory.Core/Reviews/DecisionReasonPolicy.cs
src/Factory.Core/Reviews/ReviewDecisions.cs
src/Factory.Core/Reviews/ReviewState.cs
src/Factory.Infrastructure/Reviews/DecisionModel.cs
src/Factory.Infrastructure/Reviews/Migrations/20261014010250_AlignDecisionReasonPolicy.Designer.cs
src/Factory.Infrastructure/Reviews/Migrations/20261014010250_AlignDecisionReasonPolicy.cs
src/Factory.Infrastructure/Reviews/Migrations/ReviewDbContextModelSnapshot.cs
tests/Factory.Tests/Integration/ReviewEndpointTests.cs
tests/Factory.Tests/Reviews/DecisionMigrationTests.cs
tests/Factory.Tests/Reviews/DecisionReasonTests.cs
```
