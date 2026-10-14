# Milestone 5B.1 — Authentication and project authorization

Implemented on 2026-10-13. Milestone 5A is committed as
`b3e95f479c2c38358a3c46b3776c2004e5618880`. Milestone 5B.1 is committed and pushed as
`4dbd0dec8721cea0d95047430c1ba2e927abe60b`; GitHub master was verified with
`git ls-remote origin refs/heads/master` on 2026-10-13. The subsequent hardening changes below are committed locally as
`ff24cd5a13a99aa02138ac4fbd29f40baf1091ac`. Milestone 5B.2 now adds three review endpoints; see
[Human Decisions](HUMAN_DECISIONS.md). No enrollment, membership-management endpoint or automatic execution exists.

## Authentication boundary

Factory.Api uses Microsoft.AspNetCore.Authentication.JwtBearer **10.0.12** on .NET 10.
Issuer, audience, signature, signed-token requirement, RS256 algorithm, JWT header type,
expiration and lifetime are validated. Clock skew is 30 seconds; original claim names are
preserved and tokens are not saved into the authentication ticket. Key retrieval uses OIDC
discovery/JWKS through the framework. Discovery URLs are operator configuration, never token input.

The API supports the explicitly configured Keycloak token profile: payload `typ=Bearer`,
exactly one nonblank `iss`, `sub`, `azp`, and `factory_kind`. `typ=ID` is rejected even with a
valid signature and API audience. `azp` must be the configured Human or Service client and
`factory_kind` must agree. That claim is an administrator-controlled hardcoded client mapper,
included only in access tokens. Caller role claims, reviewer IDs, emails and usernames confer
no project permissions. A different provider requires an explicit token-profile adapter and
tests; it is not enough to change Authority blindly. Domain/agent contracts remain provider-neutral.

Local issuer: `http://127.0.0.1:58080/realms/factory-dev`; audience: `factory-api`.
The only HTTP exception requires Development, an explicit flag and that exact issuer.
Other configurations require HTTPS. Local HTTP-authenticated project operations additionally
reject non-loopback transport addresses. No forwarded-header trust was added.

Keycloak clients:

| Client | Flow / constraints |
| --- | --- |
| factory-api | API audience, bearer-only resource |
| factory-dev-cli | Public human client, authorization code + required PKCE S256, exact callback http://127.0.0.1:58081/callback |
| factory-agent | Confidential service client, client credentials, no browser/password flow |

Both initiating clients explicitly map audience `factory-api` into access tokens, not ID tokens.
Implicit flows, direct password grants and standard token exchange are disabled. The human client
has no service account; the service client has no standard authorization-code flow. Realm
self-registration is disabled. The API does not collect passwords or hold an IdP administrator secret.

## Authorization and database ownership

Factory.Core defines TrustedIdentity, PrincipalKind, ProjectRole and IProjectAccessStore.
The API constructs identity only after validated claims. Infrastructure queries Factory-owned
records. Resource authorization uses the actual deserialized request ProjectId before any agent call.
All of project, principal and membership must be active; `(issuer, subject)` and classification must
match exactly. No permission cache is used. Revocation applies to subsequent checks; already-running
generation is not retroactively cancelled. 5B.2 decision authorization instead holds database row locks through commit; see Human Decisions.

Roles are independent grants: Reader, Generator, Reviewer. Generator authorizes generation.
Reviewer eligibility requires a Human identity and an explicit project Reviewer grant. A Service
identity is denied Reviewer by both the authorization handler/store and database constraints, even
if token roles claim otherwise. Generator never implies Reviewer, and Reviewer never implies Generator.

Additive migration: `20261013084222_ProjectAuthorization` in the existing ReviewDbContext chain.

| Table in access schema | Key / constraints |
| --- | --- |
| Projects | Id varchar(200), IsActive, positive bigint Revision concurrency token |
| Principals | UUID Id, Issuer varchar(512), Subject varchar(255), Human/Service Kind, IsActive, Revision; unique issuer/subject; alternate Id/Kind key |
| ProjectMemberships | PK ProjectId/PrincipalId/Role, PrincipalKind, IsActive, Revision; project FK and composite principal/kind FK; Reviewer requires Human |

IDs retain exact, case-sensitive project semantics. Existing review data is not auto-enrolled,
rewritten or linked by a new mandatory FK. The three review tables, immutable guards, deferred
current-artifact FK and PendingReview-only CHECK remain intact.

`factory_authorization_reader` is a dedicated NOINHERIT login with schema USAGE and SELECT only
on authorization tables. It cannot write memberships, change schema or read review artifacts.
The grants script fails on unexpected elevated role flags, memberships, ownership or mutation
privileges. Migration-owner credentials are used only by explicit operator commands/tests.
There is no runtime migration, enrollment or role escalation route. Direct DB operators remain trusted.

For future operator changes, use an explicit transaction and an expected Revision in the WHERE
clause, increment Revision and require exactly one affected row. EF concurrency is tested; raw SQL
does not automatically increment Revision. Do not grant the API administrative database access.

## Endpoint compatibility and failure behavior

All three existing POST routes require bearer authentication and project Generator authorization:
`/api/agents/analyst`, `/api/agents/architect`, `/api/workflows/analysis-architecture`.
Successful request/response DTOs, agent interfaces and prompts are unchanged. GET /health remains
anonymous and reports application health, not database/identity-provider readiness.

Missing/invalid token: 401 with Bearer challenge. No project grant/inactive/unknown resource: 403.
Authorization-store failure: static 503 response and no agent invocation. Missing or insecure
authentication configuration or absent connection configuration fails startup. Unavailable signing
metadata without usable cached keys fails closed. A valid token can still be validated against
cached public keys while Keycloak is offline; this is offline JWT validation, not introspection.

Authentication/project denials precede workflow execution and therefore have no WorkflowId.
Safe diagnostics use the HTTP request trace ID and static failure text. Framework authentication
exception logging is suppressed to avoid untrusted token material; custom logs do not print claims,
headers or exceptions. Authorization errors do not print DB exception details. The subsequent
hardening pass replaces standalone agent raw exception logging with static failure
codes, HTTP status, server request ID and exception type. No exception objects/messages, stack traces,
inner exceptions, Data collections or provider bodies are logged by these handlers.
Tokens/claims are never added to model requests, prompts or tools. Live integration tests inspect the
fake provider's captured requests for token leakage. Deliberately putting secrets in task text is not
prevented by authentication; callers remain responsible for submitted task contents.

## Local provisioning and reproducible verification

Verified environment: Windows, SDK 10.0.401 / ASP.NET Core 10.0.12, Temurin Java 25.0.4.1,
Node 24.19.0 and installed Microsoft Edge. No Docker or WSL installation was used.
Keycloak **26.8.0** ZIP runs with the existing Java runtime and embedded development H2 storage.
Its distribution, realm import, credentials, browser profile and data are under ignored artifacts/keycloak.
PostgreSQL **18.6** remains under ignored artifacts/postgres; persistent data is in artifacts/review-postgres.

Download source: the official Keycloak download page links these release artifacts:

- https://github.com/keycloak/keycloak/releases/download/26.8.0/keycloak-26.8.0.zip
- https://github.com/keycloak/keycloak/releases/download/26.8.0/keycloak-26.8.0.zip.sha1

The archive matched the published SHA-1: `363e215b4ca65ff41edd2ca8d4f1c293436604ee`.
Local SHA-256: `7ed1de3fda2598369262613bf682aab7e233d80a38c405e91588f7a7454370a1`.
No detached publisher signature was verified. SHA-1 is weaker integrity evidence than a modern
signed digest; local SHA-256 is a fingerprint, not independent publisher authentication. Extract only
after checking the publisher checksum into artifacts/keycloak/distribution/keycloak-26.8.0.
The separate unsigned PostgreSQL executable/unverified publisher checksum limitation remains in
[Review Foundation](REVIEW_FOUNDATION.md).

With verified distributions already present and free ports, run as the same local operator:

```powershell
./scripts/Start-ReviewPostgres.ps1
./scripts/Initialize-FactoryAuthorization.ps1
./scripts/Start-FactoryKeycloak.ps1
# Wait for the loopback discovery endpoint to become ready (startup is asynchronous).
./scripts/Provision-FactoryDevelopment.ps1
node scripts/Test-KeycloakBrowser.mjs
./scripts/Test-FactorySecurity.ps1 -IncludeKeycloak
./scripts/Stop-FactoryKeycloak.ps1
./scripts/Stop-ReviewPostgres.ps1
```

Initialization applies migrations explicitly and provisions the reader. The development provisioning
script grants the two imported synthetic subjects access to `factory-local`: Human gets Generator
and Reviewer; Service gets Generator only. It does not reactivate or overwrite existing grants.
Do not use this sample provisioning for real people or production.

`Run-FactoryApi.ps1` runs the API on 127.0.0.1:5168 with the reader connection and local issuer.
Configuration uses `WebApplication.CreateBuilder` defaults, highest priority first: command-line
arguments, environment variables, Development User Secrets, `appsettings.{Environment}.json`,
then `appsettings.json` (with host configuration as fallback). The duplicate late User Secrets
registration was removed. Environment settings such as `Authentication__Authority` and
`ConnectionStrings__Authorization` now override conflicting secrets; command-line settings override
both. Startup validation applies to the winning values and fails closed rather than falling back
to a lower-priority valid value. Development secrets still supply settings without an override.
Never put literal credentials in command lines or documentation.

Credentials are generated, never committed or printed. Protect-FactoryDevelopmentSecrets restricts
the two private directories to the current operator, SYSTEM and Administrators using DACL-only
updates. Same-user processes and administrators remain trusted. Startup import JSON contains the
synthetic human password and service secret; it is private evidence, not a distributable realm export.
Bootstrap admin credentials remain local, are not passed to the API, and must not be supplied to agents.
Stop-Keycloak verifies the dedicated process/listener before terminating it; it preserves H2 data but
is Windows process termination, not a production graceful-shutdown procedure.

Browser verification uses a real headless Edge login form with the synthetic human account, validates
state, and exchanges the authorization code with PKCE. The live .NET test independently verifies the
ID-token signature, issuer, audience, lifetime and nonce, and confirms the API rejects that ID token.
Private token evidence has a five-minute access-token lifetime: rerun the browser script immediately
before the live suite. This is an automated browser test, not an observed manual login by the developer.

## Executed Milestone 5B.1 evidence (historical)

- Full build: **0 warnings, 0 errors**. Full suite: **205 passed, 0 failed, 0 skipped**.
  Includes 194 deterministic cases, nine real PostgreSQL cases and two live Keycloak cases.
- Original 170 cases retained; existing HTTP regressions now send signed test access tokens.
- Negative tests cover forged/malformed/unsigned/wrong-key/issuer/audience/expired/future/no-expiry
  tokens, ID-token misuse even with API audience, missing/duplicate identity claims, unknown client,
  classification mismatch, project boundaries, inactive records, forged reviewer fields and token roles,
  service Reviewer attempts, metadata failure, DB failure, external transport and zero model calls on denial.
- PostgreSQL tests verify read-only runtime restrictions, service Reviewer CHECK and kind FK,
  exact membership matching, activation gates and optimistic concurrency. The original immutability,
  transaction rollback and concurrent artifact tests still pass.
- Both live identities passed middleware validation and database authorization through TestServer,
  receiving HTTP 200 from a deterministic two-stage workflow, then 403 for another project without
  additional model calls. TestServer supplies a simulated loopback socket address; token validation,
  discovery/JWKS requests and database membership queries are real.
- A separate real Kestrel smoke run returned health 200 and, on all three POST routes, missing-token
  401, cross-project 403, and authorized empty-task 400. No live OpenAI call was made.
- Migration applied to the existing 5A database and reapplication reported current. EF reports no
  pending model changes. Both migrations also applied to fresh database
  `factory_auth_fresh_49594272e354`; it has three access tables, both immutable triggers and the
  initially deferred current-artifact FK. The test database is preserved, not deleted.
- Keycloak was stopped/restarted; the preserved realm and both identity flows passed again.
  Listeners were verified on 127.0.0.1 only. The dedicated verification API was stopped without
  touching the pre-existing API process on port 5050.

Without the private database variables nine PostgreSQL cases skip; without Keycloak/browser
variables two live cases skip. A default no-environment test run is not full integration verification.
The live API test host uses Development and therefore the existing Development User Secrets file;
its model provider is replaced before requests. Deterministic tests use Testing and synthetic keys.
The first deterministic-only run was 190 passed / five skipped before later tests were added.
All final configured integration cases passed; no skipped result is counted as verified.

Observed setup issues: Keycloak initially warned JAVA_HOME was unset; the launcher now derives it
from the selected Java executable. An ACL helper using Set-Acl failed on rerun with SeSecurityPrivilege;
it was corrected to DACL-only DirectoryInfo access control and successfully rerun. Fresh EF history
discovery logged a missing-history-table query before successfully applying both migrations.
Historical HTTP 500 and 502 causes remain unresolved; none of these checks establishes a root cause.

Final workspace checks found 31 candidates (11 modified, 20 new), all text source/docs/scripts.
Exact local passwords/secrets/tokens and credential-pattern checks found no matches in candidates;
local Keycloak/API logs also contained none of the checked private values. Git ignores private
credentials, realm import, browser evidence, binaries, data and build output. This is a scoped check,
not a comprehensive security audit. The protected directories correctly rejected sandbox-context
reads; the final comparison succeeded under their operator context without printing secrets.
Git diff whitespace check passed and the index is empty. No commit or push was made during that verification; 5B.1 was subsequently committed and pushed.
Keycloak, PostgreSQL, browser debugging/callback and the dedicated API verification listeners are
stopped; data remains preserved. The pre-existing API on 5050 remains outside this verification.

## Committed Milestone 5B.1 manifest

Modified (11):

```text
AGENTS.md
docs/CURRENT_STATE.md
docs/DECISIONS.md
docs/REVIEW_FOUNDATION.md
src/Factory.Api/Factory.Api.csproj
src/Factory.Api/Program.cs
src/Factory.Infrastructure/Reviews/Migrations/ReviewDbContextModelSnapshot.cs
src/Factory.Infrastructure/Reviews/ReviewDbContext.cs
tests/Factory.Tests/Integration/AnalystEndpointTests.cs
tests/Factory.Tests/Integration/ArchitectEndpointTests.cs
tests/Factory.Tests/Integration/WorkflowEndpointTests.cs
```

New (20), including intentional EF migration sources:

```text
docs/AUTHENTICATION.md
scripts/Initialize-FactoryAuthorization.ps1
scripts/Protect-FactoryDevelopmentSecrets.ps1
scripts/Provision-FactoryDevelopment.ps1
scripts/Run-FactoryApi.ps1
scripts/Start-FactoryKeycloak.ps1
scripts/Stop-FactoryKeycloak.ps1
scripts/Test-FactorySecurity.ps1
scripts/Test-KeycloakBrowser.mjs
scripts/authorization-runtime-grants.sql
src/Factory.Api/Security/FactoryAuthentication.cs
src/Factory.Api/Security/ProjectAuthorization.cs
src/Factory.Core/Authorization/ProjectAccess.cs
src/Factory.Infrastructure/Authorization/ProjectAccessStore.cs
src/Factory.Infrastructure/Reviews/Migrations/20261013084222_ProjectAuthorization.Designer.cs
src/Factory.Infrastructure/Reviews/Migrations/20261013084222_ProjectAuthorization.cs
tests/Factory.Tests/Authorization/LiveKeycloakTests.cs
tests/Factory.Tests/Authorization/PostgresAuthorizationTests.cs
tests/Factory.Tests/Integration/AuthenticationTests.cs
tests/Factory.Tests/Integration/TestSecurity.cs
```

## Remaining boundaries

No production TLS/proxy deployment, MFA policy, token revocation/introspection, signing-key rotation
drill, provider compromise resistance or remote/organizational identity federation was verified.
Short-lived bearer tokens can be replayed until expiration; Factory membership disablement is checked
on every new operation. Identity administrators can change client policy or misclassify accounts and
remain trusted. Application/DB administrators can bypass policy and remain outside tamper resistance.
API configuration is required at startup, but DB availability is checked per operation, not by /health.
Milestone 5B.2 review decisions, audit and idempotency are documented separately in [Human Decisions](HUMAN_DECISIONS.md); its uncommitted implementation awaits user review.

## Official references consulted

- [ASP.NET Core 10 JWT bearer validation](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-jwt-bearer-authentication?view=aspnetcore-10.0)
- [ASP.NET Core resource authorization](https://learn.microsoft.com/en-us/aspnet/core/security/authorization/resource-based?view=aspnetcore-10.0)
- [Pinned JwtBearer package](https://www.nuget.org/packages/Microsoft.AspNetCore.Authentication.JwtBearer/10.0.12)
- [EF migrations](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/applying)
- [EF concurrency](https://learn.microsoft.com/en-us/ef/core/saving/concurrency)
- [OIDC Core](https://openid.net/specs/openid-connect-core-1_0.html)
- [Keycloak Windows/Java setup](https://www.keycloak.org/getting-started/getting-started-zip)
- [Keycloak administration and audience mappers](https://www.keycloak.org/docs/latest/server_admin/index.html#_audience)

## Focused security hardening (2026-10-13, committed)

The API now emits `X-Request-Id` from the server request trace identifier for correlation, including
failures before workflow execution. Workflow IDs and success payloads are preserved. Standalone SDK
errors remain 502 with provider status and an allowlisted provider request ID; unexpected agent
errors remain 500; observed cancellation remains 499. Authentication/project authorization semantics
and existing 401/403/503 paths are unchanged.

A request exception boundary also handles binding, dependency activation and response-writing
failures outside agent handlers. Invalid HTTP requests keep their framework status with a static
problem title; unexpected failures use a static 500 response. If the response already started,
the connection is aborted without attempting a second body or rethrowing the raw exception.
Binding exception logs are suppressed and bad-request exceptions are handled by the safe boundary,
including in Development. This deliberately removes detailed Development error bodies.

Configuration tests substitute synthetic values at the default secret-provider position and use a
unique environment prefix; they cover all authentication settings and the authorization connection,
command-line precedence, and invalid overrides. A separate real-entry-point test prevents late
secret registration. Request-log tests include nested exceptions, exception Data, SDK failures,
malformed JSON in Development and failures during dependency activation. They inspect response
bodies, correlation, logged text and logged exception objects. Startup diagnostics are outside
these request-log assertions; early runs encountered sandbox Data Protection startup errors.

This is scoped application request-path hardening, not a global logging/telemetry redaction guarantee.
Host startup, web server/proxy access logs, external sinks, caller-submitted secrets in task text,
and production TLS/IdP deployment remain outside this verification. No 5B.2 work is included.

Official references: [ASP.NET Core configuration precedence](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/configuration/?view=aspnetcore-10.0),
[bad-request handling](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.routing.routehandleroptions.throwonbadrequest?view=aspnetcore-10.0).

Executed hardening verification:

- Targeted regressions: **15 passed, 0 failed, 0 skipped**. Initial test-harness failures were
  resolved: configuration callbacks ran after startup validation, machine-specific startup logs
  polluted request assertions, and the Architect fixture needed the existing string-enum serializer.
- `node scripts/Test-KeycloakBrowser.mjs`: fresh real headless Edge PKCE login succeeded.
- `./scripts/Test-FactorySecurity.ps1 -IncludeKeycloak`: `dotnet build` succeeded with **0 warnings,
  0 errors**; `dotnet test --no-restore` completed with **220 passed, 0 failed, 0 skipped**, including
  nine real PostgreSQL and two live Keycloak cases. All 205 baseline cases are retained.
- Reviewed the tracked diff and all three new source/test files; `git diff --check` passed.
  Six modified and three new files were subsequently committed as ff24cd5. That hardening
  verification preceded 5B.2 and did not include a push.
- Dedicated Keycloak/PostgreSQL services stopped after verification; data preserved. No live
  OpenAI generation was performed. Remote master readback matches `4dbd0de`.

## Milestone 5B.2 configuration and authorization extension

Configure `ConnectionStrings:Reviews` with the separately provisioned decision runtime login.
The authorization connection remains the SELECT-only reader; never reuse owner credentials in API
configuration. Default precedence remains command line > environment > Development User Secrets >
environment JSON > base JSON. `Run-FactoryApi.ps1` supplies both connections through environment
configuration without printing them. Apply the migration and `decision-runtime-grants.sql` before
starting the API. Generation still requires Generator; review reads and decisions require Human
plus explicit active Reviewer, including on historical replay. No role-claim or client-provided
identity can supply membership. See [Human Decisions](HUMAN_DECISIONS.md) for race semantics and tests.
