# Current state — fresh-session handoff

Implementation verification date: **2026-10-14**, Europe/Istanbul.
M6 implementation and its verification evidence levels are recorded below; older milestone evidence remains historical.

## Start here

Read [AGENTS.md](../AGENTS.md), [Accuracy Protocol](AI_ENGINEERING_ACCURACY_PROTOCOL.md),
this document, and [DECISIONS.md](DECISIONS.md) before editing. Actual source and executed
verification are authoritative. M6 is committed on `master` as
`1b93fc4e0f82e942d82be1ba1372d496961eb0cb`; on 2026-10-15 `git ls-remote` showed origin master
at that commit, and the user confirmed the commit/push was authorized. M5B.2 remains
committed/pushed as e1bced7. M7 (Durable Workflow Orchestration) was approved on 2026-10-15;
M7.1 is committed and pushed as `bcd50fa`, M7.2 as `9cee79c`, M7.3 as `73f83ab`; parallel steps
moved to M8. The M8 plan was approved on 2026-10-15; M8.1 (usage metering) is committed and pushed as
`7da963f`, with a versioned price script (`scripts/model-prices.sql`). M8.2 (tenancy) is committed
and pushed as `4c7789d`. M8.3 (budgets, pre-call worst-case reservations, budget/price refusals as
customer decisions, organization budget view, Anthropic demo cap) is committed and pushed as
`2564a85`, verified with **776 passed, 0 failed, 0 skipped** including four live Keycloak cases. By user
decision the unmetered synchronous generation routes (ephemeral agent/workflow endpoints and M6
lifecycle POSTs) are retired: 410 Gone by default, re-enable only in Development/Testing; full
suite then **781 passed, 0 failed, 0 skipped**. Durable regeneration of existing work items
(`POST /api/projects/{projectId}/work-items/{workItemId}/runs`, strong ETag, next case version,
stale instead of overwrite) replaces the retired M6 route: full suite **786 passed, 0 failed,
0 skipped**. M8.4a (customer-provided OpenAI-compatible models: AES-GCM credential protection,
organization model registry, capability verification, explicit risk acceptance, run model snapshots,
SSRF-guarded egress, no silent fallback) is committed and pushed as `c7eb11d` (828 passed). M8.4b
(Anthropic provider via the official SDK: `Generation:Provider` selection, customer Anthropic
credentials, verified Claude Haiku 5.5 price as an upper bound) is committed and pushed as `8d0f3d6`
(841 passed); no live Anthropic generation call yet (the account has no credit). M8.5a (model
routing policy: tiers, deterministic role/complexity/importance rules, per-attempt route evidence)
is committed and pushed as `fe83818` (853 passed). M8.5b/c (decomposition-v1: Planner, blocking plan
approval, parallel part steps on routed tiers, deterministic merge into the unchanged artifact
format) is committed and pushed as `2b29be2` (881 passed). A live run then failed planning
(parts without requirements); a Planner prompt rule and `minItems: 1` schema fix followed, a second
live decomposed run succeeded end to end (968 micro-USD), and the full suite passed again (881).
M12a (first Product Workspace slice: Angular 22 web app with Keycloak PKCE sign-in, projects, runs,
new-run form, blocking decision popup and live updates; additive listing endpoints and an optional
`Authentication:WebClient`) is committed and pushed as `8d30e71` (885 passed, 8 web unit tests, one
real-browser end-to-end run). M12b adds the review page (proposal display with requirement provenance,
browser-side SHA-256 check of the shown bytes, approve/request changes/reject on the existing review
endpoints; no backend change), verified with 885 passed, 15 web unit tests and one real-browser
end-to-end run including an approval; see [Product Workspace](PRODUCT_WORKSPACE.md). The M9 runtime
(Docker Desktop + WSL 2) is approved, installed and smoke-tested, and the user confirmed the template
stack and DDD layer layout. M9.1 (versioned starter template `dotnet-angular-postgres-ddd` 1.0.0,
file registry with content hash, deterministic generator) is verified with 913 passed including a
Docker case that builds and tests a generated application; see
[Templates and Execution](TEMPLATES_AND_EXECUTION.md). M9.2 (runner image with offline package caches,
`DockerSandbox` with no network, read-only root, non-root user, capability/privilege drops,
CPU/memory/PID/disk/time/output limits, exec-reported exit codes) is verified with 930 passed including
8 Docker isolation cases. M9.3 (durable verification jobs, immutable hashed evidence, worker/API/
Product Workspace integration, self-ending orphaned sandboxes) is verified with 937 passed, 19 web tests, a
real-browser end-to-end verification and a live worker-crash recovery; Git workspaces are deferred to M10.
M10 design, model (gpt-5.4-mini) and budget (USD 0.40 per job, USD 2 total) are decided; M10.1
(authoritative workspace with protected rules, sandbox session, deterministic Git diff) is verified
with 967 passed. M10.2 (Developer Agent loop with cost guard, limits and traceability) is verified with
977 passed and one paid gpt-5.4-mini run that implemented a small domain task and passed offline
verification (USD 0.009). M10.3 (development jobs on approved review artifacts, immutable evidence,
API, Develop panel and diff view) is verified with 986 passed, 23 web tests and a browser acceptance run
in which the Developer Agent implemented StockFlow Product CRUD across the DDD layers and passed offline
verification (USD 0.0645; total paid Developer spend USD 0.206 of USD 2); see [Developer Agent](DEVELOPER_AGENT.md).
M11 decisions: pull requests to the new private repository `ozgenakincilar/stockflow`, a repository-scoped
token entered by the user, the Factory never merges. M11.1 (Factory-established checks: sandbox, required
architecture tests from the TRX report, layer scan, protected files; advisory coverage and tests) and
M11.2 (correction loop, at most 2) are committed as db9e00c. M11.3 (pull requests of verified,
evidence-matching commits; never merges) is verified with 1004 passed against a local bare repository and
a fake API, and live: a new StockFlow Product CRUD development job was verified (USD 0.0542, one
correction) and opened https://github.com/ozgenakincilar/stockflow/pull/1 with commits equal to the
evidence; it awaits human review (the Factory never merges); see [Verification and Pull Requests](VERIFICATION_AND_PULL_REQUESTS.md).
The user's Azure free-trial subscription was verified on 2026-10-17 and M13A was fully approved. M13A.1
(Terraform deployment engine behind `IDeploymentEngine`, module `deploy/azure/app-service-postgres`
1.0.0 within the USD 5/month budget, release packages rebuilt from evidence commits in the sandbox,
plan-binding approval, `scripts/Deploy-FactoryStaging.ps1`) is implemented and live: after the user ran
`scripts/Initialize-AzureStaging.ps1` (France Central; West Europe refused new free-trial resources), two
user-approved plans deployed the StockFlow release of stockflow#1 to
https://stock-flow-staging-9b228545.azurewebsites.net (health 200, Key Vault reference Resolved with
module 1.1.0); see [Deployment](DEPLOYMENT.md).

## Milestone 8.1 current handoff

Per-call model usage recorded by the durable worker, priced from an operator-maintained append-only
price table (empty by default; prices are never invented), exposed per run and per project.
Executed 2026-10-15 (SDK 10.0.103, no live LLM calls): upgrade and fresh initialization; build
**0 warnings/errors**; full suite **763 passed, 0 failed, 0 skipped** including four live Keycloak
cases. The M6 synchronous route is not metered. See [Cost Controls](COST_CONTROLS.md).

## Milestone 7.3 handoff (committed as 73f83ab)

PostgreSQL commit-time `NOTIFY` relayed by the API to a JWT-authenticated SignalR hub
(`/hubs/workflows`); clients watch a project and re-read state over HTTP. Executed 2026-10-15
(SDK 10.0.103, no live LLM calls): upgrade and fresh initialization; build **0 warnings/errors**;
full suite **754 passed, 0 failed, 0 skipped** including four live Keycloak cases. The SignalR
test client was pinned to 10.0.13 after a high-severity advisory in 10.0.12; no vulnerable packages
remain. See [Durable Workflows](DURABLE_WORKFLOWS.md#milestone-73--real-time-run-change-notifications).

## Milestone 7.2 handoff (committed as 9cee79c)

Additive migration for waiting/decisions and cancellation; decide/cancel HTTP routes; interim
Human-Reviewer decider authority. Executed 2026-10-15 (SDK 10.0.103, no live LLM calls): upgrade
of the M7.1 database holding live-run data; fresh initialization; build **0 warnings/errors**;
full suite **745 passed, 0 failed, 0 skipped** including four live Keycloak cases. No production
step requests decisions yet. See [Durable Workflows](DURABLE_WORKFLOWS.md#milestone-72--waiting-for-human-decisions-and-cancellation).

## Milestone 7.1 handoff (committed as bcd50fa)

Asynchronous, durable Analyst → Architect → Publication runs executed by a separate
`Factory.Worker` process with PostgreSQL leases and checkpoints; the API returns 202 and status.
Executed 2026-10-15 (SDK 10.0.103): build **0 warnings/errors**; full suite **734 passed, 0 failed,
0 skipped** including four live Keycloak cases, on databases provisioned by the updated initializer.
An initial migration parse error was found and fixed before that run. A user-approved live check
then ran the worker as a real process with `gpt-4o-mini`: two runs succeeded, and a third survived
a forced worker kill during Architecture, resuming on a new worker without repeating Analysis
(about seven model calls; cost not measured). See [Durable Workflows](DURABLE_WORKFLOWS.md) for
contracts, evidence and limits.

## Milestone 6 current handoff

The approved synchronous lifecycle integration is committed as 1b93fc4. Explicit authenticated
Generator requests admit a durable command, run Analyst then Architect outside transactions,
and atomically publish immutable artifacts with command completion. Human Reviewer authorization
and existing decision receipts remain independent. No automatic execution or durable scheduling.

Historical pre-correction verification on 2026-10-14: build **0 warnings/errors**, **669 passed, 0 failed, 0 skipped**,
including PostgreSQL and four live Keycloak cases. Fresh and upgrade migrations and EF model-drift
validation passed. The original verification database was left untouched; separate M6 and historical
regression databases were used. See [Product Lifecycle](PRODUCT_LIFECYCLE.md) for exact evidence,
roles, HTTP contracts, manifest and limitations. M7–M16, including Terraform/Multi-Cloud/BYOC, remain future work.

The subsequent audit reproduced the managed-publication integrity gap, corrected before the M6 commit.
Post-correction full-suite run: **700 passed, 0 failed, 0 skipped**, build **0 warnings/errors**,
including four live Keycloak cases. Corrected fresh migration, upgrade preservation/compatibility
and EF drift checks passed. Older regression cases use the separately approved M5B.2 baseline
database; the expanded upgrade test supplies targeted M6-schema compatibility evidence, not a
claim that all historical tests run against M6. Dedicated verification services are stopped and
all data preserved. The 700-test run (2026-10-14 22:03) preceded the final change to
`scripts/Initialize-FactoryLifecycle.ps1` (file timestamp 22:45, shortly before commit). The only
later recorded run was 22 domain/service unit tests without PostgreSQL, Keycloak or provisioning;
its TRX timestamp (22:44:26) also precedes the initializer timestamp. The full suite was not rerun
after the final provisioning change at that time. On 2026-10-15 an approved run executed the
committed initializer (normal and `-BaselineOnly`) on new isolated databases, then the full suite:
build **0 warnings/errors**, **700 passed, 0 failed, 0 skipped**, including four live Keycloak
cases (commit aa0d346, SDK 10.0.103). A post-correction build, a 21-case identifier validation check and an empty
remote commit-status query were reported during development but have no record in this repository;
they are reported, not verified, and no remote CI pass is claimed. See
[evidence levels](PRODUCT_LIFECYCLE.md#evidence-levels-after-commit) for details.

## Milestone 5B.2 handoff (historical)

Historical full build: **0 warnings/errors**. Full suite: **624 passed, 0 failed, 0 skipped**, with real
PostgreSQL and live Keycloak enabled. Fresh/upgrade migrations and EF drift validation passed.
The reviewed 31-file implementation was committed as e1bced7 and subsequently pushed.
Verification services were stopped with data preserved.

The later documentation-only verification reported build **0 warnings/errors** and **332 passed,
0 failed, 36 skipped**, without configured PostgreSQL/Keycloak. This partial run does not replace
the historical 624-test full-suite result. Neither run was independently repeated for this status
reconciliation.

**At the 5B.2 handoff, the next planned milestone was M6.** Architectural approval
was required before implementation. At that checkpoint M6–M16 capabilities in [Roadmap](ROADMAP.md) were planned,
including Terraform/Multi-Cloud/BYOC; documentation approval does not implement them. StockFlow
is the first planned end-to-end pilot on Azure.

See [Human Decisions](HUMAN_DECISIONS.md) for the approved five security requirements, endpoint
contracts, transaction design, migration/grant setup, exact executed evidence and limitations.
Existing stored cases support Approve, Reject and Request Changes by authenticated Human project
Reviewers. Durable receipts and append-only decision events bind exact immutable versions/hashes.
PostgreSQL guards enforce transition integrity at COMMIT, while row locks order membership
revocation against decisions. No agent contract, provenance, generation submission or execution changed.
Two new migrations plus explicit operator grants are required; API startup never migrates or provisions.
The reason-policy correction adds an explicit shared whitespace/UTF-16 invariant and safe wrapped
integrity-error classification. Existing invalid historical reasons block upgrade without rewriting
audit records. See Human Decisions for the exact migration, tests and review limitation.

## Git baseline

The local root commit on `master` is:
`c8c3330fe7e8a4d15f73208a52d3fdd24250c534`

Message: `Initial baseline: Universal AI Factory milestones 1–3`.

The baseline contains 49 reviewed text files, including .gitignore and four historical
JSON evidence files. Staged file manifest, binary/artifact checks, sensitive-pattern checks,
and whitespace checks passed. Evidence was read and contains nonsensitive task-list examples.
Configuration contains no API key; launch settings use localhost, and the UserSecretsId is
configuration metadata, not a credential. Pattern checks are not a comprehensive security audit.
Build artifacts, IDE state, test output, logs, local secrets, and private keys are ignored.
Milestone 4 was subsequently committed as b9ce70554d27404d0210169466875e19d5eacadb;
the tree was clean when 5A began. Milestone 5A was committed as b3e95f479c2c38358a3c46b3776c2004e5618880. At that historical checkpoint no push or history rewrite had occurred.
Milestone 5B.1 was subsequently committed and pushed as `4dbd0dec8721cea0d95047430c1ba2e927abe60b`.
Remote master was independently verified with `git ls-remote` on 2026-10-13.
Git index writes required approved sandbox escalation; a per-command safe.directory exception
was used for this workspace, without changing global Git configuration.

## Milestone 5B.1 baseline verification and scope (historical)

See [Authentication](AUTHENTICATION.md) for implementation, setup, exact security boundaries and
executed evidence. All three generation endpoints now require a validated API access token and
Factory-owned active project Generator membership. Health remains anonymous. Successful payloads
and agent contracts are preserved. Human/Service classification requires agreement between the
Keycloak client policy and Factory principal; Service Reviewer grants are denied in API and DB.
No enrollment, membership management, approval decision or 5B.2 endpoint was added.

Keycloak 26.8.0 runs locally using Java 25, with a Development-only exact loopback HTTP issuer;
PostgreSQL has three additive access tables and a separate read-only API login. Migrations passed
against existing and fresh databases; model drift check reported none. Full build: **0 warnings/errors**.
Full suite: **205 passed, 0 failed, 0 skipped**, including nine real PostgreSQL and two live Keycloak
cases. Human browser login used real headless Edge with PKCE/state and independently validated nonce;
service client credentials were verified separately. No live OpenAI generation was performed.

At the historical hardening checkpoint, 5B.1 was committed/pushed as `4dbd0de`, and hardening
was committed locally as `ff24cd5a13a99aa02138ac4fbd29f40baf1091ac` without a push in that step.
Both are now ancestors of the committed/pushed e1bced7 baseline.
Local Keycloak SHA-1 matched the publisher checksum; SHA-256 is a local fingerprint, not a verified
publisher signature. PostgreSQL's unsigned/unverified-publisher-checksum limit remains documented.
Historical HTTP 500/502 causes remain unresolved. Test dependencies and skipped-test behavior are
explicitly documented. An existing API process on 5050 was not modified or stopped.
The dedicated Keycloak/PostgreSQL/browser/API verification listeners are stopped; data is preserved.
That historical 5B.1 candidate comprised 31 text files (11 modified, 20 new), recorded in AUTHENTICATION.md.
Scoped secret/binary checks and git diff whitespace checks passed; the index was empty at that checkpoint.

## Milestone 5A verification and scope (historical)

See [Review Foundation](REVIEW_FOUNDATION.md) for schema, setup, security boundaries and evidence.
Core has pure review state rules; Infrastructure has an internal IReviewStore implementation,
immutable UTF-8 artifact bytes/SHA-256, version publication and optimistic concurrency, three
PostgreSQL tables, append-only system events, and an EF migration. The API is not registered
with this store; existing routes and workflows remain unchanged and ephemeral. No approval
endpoint, OIDC, project membership authorization, decision writer, or command idempotency exists.
The database CHECK allows PendingReview only. The following 5A evidence is historical; the current 5B.2 implementation is described above.

Environment inspection found no Docker/PostgreSQL installation or listener and WSL reported not
installed. Official PostgreSQL 18.6 portable binaries were used under ignored artifacts, with a
SCRAM-authenticated loopback cluster at port 55432. No Windows service/Docker/WSL was installed.
EF Core/Relational/Design 10.0.12 and Npgsql/provider 10.0.3 are pinned and restored successfully.

Executed: InitialReviewFoundation migration on real PostgreSQL; repeat migration (already current);
EF pending-model check (none). Full dotnet build using bin/Milestone5A/net10.0/: **0 warnings/errors**.
Full dotnet test with FACTORY_REVIEW_TEST_CONNECTION set: **170 passed, 0 failed, 0 skipped**,
including five real PostgreSQL cases with the restricted writer role. Without that variable,
those five cases skip explicitly and a run is not full verification. No live OpenAI calls were made.

Seven existing artifact versions and recomputed SHA-256 hashes survived a real database restart
and separate client processes unchanged. The immutable trigger rejected owner-issued UPDATE;
restricted-role tests reject mutation/DDL and persisted approval. Database administrator-level
tamper resistance is not claimed. The API has not been connected to persistence, so API restart
persistence is not claimed. See the setup report for resolved native-path/launcher/NuGet issues.
The final full suite passed after restart with synchronized concurrent-publisher reads. The
isolated database is stopped; its data is preserved under ignored artifacts for a later restart.

## Implemented architecture

The historical 5A closure review reran the full suite against the preserved PostgreSQL cluster: **170 passed,
0 failed, 0 skipped**, build **0 warnings/errors**. All 19 candidates were reviewed; no secrets or
unintended binary/build/data files were found. The index was empty at that checkpoint. See Review Foundation's
closure section for the unsigned binary/checksum limitation and store-versus-database invariants.
The cluster is stopped. Milestone 5A was subsequently committed as b3e95f4.

Five net10.0 projects: Factory.Api (Minimal API/DI/HTTP handling), Factory.Core (contracts,
schemas, validators), Factory.Orchestration (AnalystAgent, ArchitectAgent, and sequential
AnalysisArchitectureWorkflow), Factory.Infrastructure (OpenAI provider), Factory.Tests.
Orchestration references Core only. OpenAI 2.14.0 remains confined to Infrastructure/API
usage; IAgent/IModelProvider signatures are unchanged. 5A adds pinned EF Core 10.0.12 and Npgsql/provider 10.0.3 to Infrastructure.

| Milestone | Status and evidence |
| --- | --- |
| 1 | Foundation, Analyst, ChatClient integration, safe credential-format validation |
| 2 | Strict typed analysis and provenance validation; historical live 200, see STRUCTURED_OUTPUTS.md |
| 3 | Traceable proposed architecture; historical live 502 then 200, first cause unresolved, see ARCHITECT_AGENT.md |
| 4 | Sequential workflow, per-execution IDs, explicit statuses, snapshots, cancellation and safe partial progress; see WORKFLOW.md |
| 5A | Committed persistent review foundation; see REVIEW_FOUNDATION.md |
| 5B.1 | Committed/pushed JWT authentication and project authorization; see AUTHENTICATION.md |
| 5B.2 | Committed/pushed secure human approval transactions, e1bced7; see HUMAN_DECISIONS.md |
| 6 | Committed/pushed product lifecycle and atomic review publication, 1b93fc4; initializer verified 2026-10-15 (700 passed), see PRODUCT_LIFECYCLE.md |
| 7.1 | Committed/pushed durable runs, separate worker, leases/checkpoints, bcd50fa (734 passed; live worker checks), see DURABLE_WORKFLOWS.md |
| 7.2 | Committed/pushed human-decision waiting and cancellation, 9cee79c (745 passed); parallel steps moved to M8, see DURABLE_WORKFLOWS.md |
| 7.3 | Committed/pushed real-time run change notifications via PostgreSQL NOTIFY and SignalR, 73f83ab (754 passed), see DURABLE_WORKFLOWS.md |
| 8.1 | Committed/pushed per-call usage metering and operator prices, 7da963f (763 passed; one live metered call), see COST_CONTROLS.md |
| 8.2 | Committed/pushed tenancy, organization-admin decisions and organization usage, 4c7789d (767 passed), see COST_CONTROLS.md |
| 8.3 | Committed/pushed budgets, reservations, retired sync generation, durable regeneration, 2564a85 (786 passed), see COST_CONTROLS.md |
| 8.4a | Committed/pushed customer-provided OpenAI-compatible models, c7eb11d (828 passed), see COST_CONTROLS.md |
| 8.4b | Committed/pushed Anthropic provider, 8d0f3d6 (841 passed; live call pending account credit), see COST_CONTROLS.md |
| 8.5a | Committed/pushed model routing policy and route evidence, fe83818 (853 passed), see COST_CONTROLS.md |
| 8.5b/c | Committed/pushed decomposition, plan approval, parallel parts, 2b29be2; Planner fix plus one successful live run (881 passed), see COST_CONTROLS.md |
| 12a | Committed/pushed Product Workspace slice, 8d30e71 (885 passed, 8 web tests, browser e2e), see PRODUCT_WORKSPACE.md |
| 12b | Committed/pushed review page, a93f1bd (885 passed, 15 web tests, browser e2e with approval), see PRODUCT_WORKSPACE.md |
| 9.1 | Committed/pushed starter template, registry, deterministic generation, 9bbdec7 (913 passed), see TEMPLATES_AND_EXECUTION.md |
| 9.2 | Committed/pushed offline sandbox runner, d97e9c5 (930 passed), see TEMPLATES_AND_EXECUTION.md |
| 9.3 | Committed/pushed verification jobs and immutable evidence, b95bf13 (937 passed), see TEMPLATES_AND_EXECUTION.md |
| 10.1 | Committed/pushed developer workspace, sandbox session, Git snapshot, 80f3759 (967 passed), see DEVELOPER_AGENT.md |
| 10.2 | Committed/pushed Developer Agent loop, a667e3c (977 passed), see DEVELOPER_AGENT.md |
| 10.3 | Committed/pushed development jobs, evidence, Develop UI, 8846c67 (986 passed), see DEVELOPER_AGENT.md |
| 11.1–11.2 | Committed/pushed verification checks and correction loop, db9e00c (992 passed), see VERIFICATION_AND_PULL_REQUESTS.md |
| 11.3 | Committed/pushed pull request publisher, 7ea0d6b (1004 passed); live PR stockflow#1 opened, see VERIFICATION_AND_PULL_REQUESTS.md |
| 13A.1 | Deployment engine, Azure module (1.1.0), sandbox release packages, operator plan/approve/apply; StockFlow live on Azure staging (France Central), see DEPLOYMENT.md |

StockFlow is the first planned end-to-end pilot; it is not an implemented product or Factory
dependency. No implementation/test/review agent,
code execution, MCP integration, GitHub automation, queue or generic workflow engine is implemented.
5A supplies persistent artifacts and publication audit; 5B.1 supplies authentication/project
authorization; 5B.2 adds human decision audit and durable receipts for existing stored cases.

## API and workflow

- GET /health: local application health, not provider availability.
- POST /api/agents/analyst and /api/agents/architect: contracts retained; 5B.1 adds authentication/project authorization.
- POST /api/workflows/analysis-architecture: WorkflowRequest to WorkflowResult; 5B.1 adds authentication/project authorization.
- Three authenticated review routes provide case/artifact reads and human decisions on existing cases; see [Human Decisions](HUMAN_DECISIONS.md#implementation-and-endpoint-contracts).

The workflow snapshots project ID, task, context, and caller evidence before its first await.
Analyst runs first; its validated typed analysis and original evidence/context feed Architect.
Requirement IDs, statuses, source bindings, assumptions, and proposed architecture are preserved.
No context/task prose can grant confirmation. Confirmation is caller assertion, not authenticated
human approval. Each execution generates a GUID in the response body, X-Workflow-Id header,
and safe completion/failure logs. Requests rejected by framework binding do not start execution.

Overall statuses: AwaitingHumanReview (generation succeeded), Failed, Cancelled. Agent stage
statuses: NotStarted, Completed, Failed, Cancelled. Completed never means approved. Success
always ends AwaitingHumanReview with architecture.status=proposed. No human pause between agents.
Validated analysis is retained if Architect fails; invalid architecture is never returned.
Empty requirements yield 422 and skip Architect. Input errors yield 400, refusals 422,
unusable provider output/SDK exceptions 502, unexpected exceptions 500, observed cancellation 499.
A disconnected client may not receive its cancellation body. No application-level retries.

The API owns SDK exception classification. Workflow logs omit request/output payloads and
exception messages/objects; safe metadata includes IDs, stages, statuses, static diagnostics,
exception type, and allowlisted provider request IDs. The committed hardening changes remove raw
standalone exception logging and add safe request
correlation and an exception boundary; see AUTHENTICATION.md for scope and limitations.

## Milestone 4 executed verification (historical)

Final commands on 2026-10-13:

```powershell
dotnet build -p:OutputPath=bin/Milestone4Verification/net10.0/
dotnet test --no-restore -p:OutputPath=bin/Milestone4Verification/net10.0/
```

Build: **0 warnings, 0 errors**. Tests: **155 passed, 0 failed, 0 skipped**.
All 123 baseline cases are retained, including one empty template test; 32 cases were added.
Tests are deterministic and use fake providers/synthetic credentials. Captured-response tests
validate saved evidence offline; they are not fresh provider requests.

New coverage includes sequence/schema selection, exact typed handoff, evidence mutation during
execution, concurrent project isolation and unique IDs, partial failure, empty requirements,
cancellation before/during/between stages, HTTP errors, safe logs and header/body correlation,
and captured live success/failure validation. HTTP cancellation verifies provider cancellation
and correlated 499 server diagnostics, not delivery of a body after disconnection.

## Live Milestone 4 verification

### Closure review (2026-10-13)

Re-read the rules/design documents, reviewed all five tracked-file diffs and all nine
untracked files, and re-ran verification without source changes:

```powershell
dotnet build -p:OutputPath=bin/Milestone4Closure/net10.0/
dotnet test --no-restore -p:OutputPath=bin/Milestone4Closure/net10.0/
```

Build: **0 warnings, 0 errors**. Tests: **155 passed, 0 failed, 0 skipped**.
Content review and limited credential/machine-path/binary/artifact scanning of all 58
tracked and nonignored untracked files found no findings. Generated outputs remain ignored;
git diff --check passed. No new live call was made during closure. No source edits, staging,
commit, or Milestone 5 work occurred. The proposed commit remains subject to explicit approval.

Workflow diagnostics correlate 500/502 failures with workflow ID, stage, failure category,
static validation diagnostics, and exception type; SDK failures also log status and an
allowlisted upstream request ID when available. These support investigation, not a guarantee
of root-cause identification: there are no stack traces, nested exception details, or raw
payloads. Existing tests verify HTTP failure handling, ID correlation, and suppression of
synthetic private messages; they do not exhaustively test every diagnostic field or header
allowlist branch. Standalone agent endpoints have no workflow ID; returned validation codes
must be retained by their caller because that path does not log failed AgentResult details.
Both historical causes remain unresolved. These are documented diagnostic limits, not fixes.

### Previously executed live attempts

Two explicit attempts used the same nonsensitive task-list request and existing Development
configuration (gpt-4o-mini). Credentials were neither printed nor changed.

1. Sandboxed attempt: HTTP 500, WorkflowId `af8d6568-3e0d-49b5-9a12-53ccc6c21e9a`.
   Failed at Analyst, Architect not started, no accepted artifacts. Safe error:
   workflow_execution_failed. The original exception type was not captured. Cause remains
   unresolved; sandbox networking is a possibility, not a verified diagnosis.
2. After adding safe exception-type diagnostics, an explicitly authorized execution outside
   the network-restricted sandbox returned HTTP 200, WorkflowId
   `be1a8a36-6a99-4470-a2d5-3d9a1828e77a`. Header/body/log IDs matched. Both stages completed,
   overall status awaitingHumanReview, architecture proposed. Two suggested requirements,
   zero confirmed constraints. Two components, one technology decision, two alternatives,
   two risks, one ADR. All design bases are recommendations.

Evidence: [request](evidence/workflow-live-request.json),
[failed response](evidence/workflow-live-failed-response.json),
[successful response](evidence/workflow-live-response.json). Content review and limited sensitive
pattern scanning found no credentials or personal/machine-specific information in these files.
The successful run does not explain the first failure or prove general reliability. The model
returned two alternatives despite the request asking for one; exact requested cardinality is
not a validator guarantee. React and other technology claims are unverified model proposals,
not accepted Factory architecture or measured performance.

Both verification API processes were stopped; unrelated processes were not stopped.
A future authorized local run can use:

```powershell
./scripts/Run-FactoryApi.ps1
```

Check the port first. Development uses existing User Secrets; do not enumerate credentials.
Separate output directories avoid locked DLLs. Tests use Testing configuration and console/captured
logging. Prior sandbox limitations include NuGet access and Windows Event Log permissions.

## Remaining limitations and next work

- Historical Architect 502 and first workflow 500 remain unresolved. Capture safe diagnostics
  on recurrence; do not infer a cause from later success or silently retry to hide failures.
- Schemas/validators enforce structure and provenance, not semantic truth or completeness.
- Live confirmed-source flow and intentionally induced provider failures remain unverified;
  deterministic tests cover positive confirmation and failure paths.
- M6 adds explicit synchronous generation/publication and a durable command journal. Durable scheduling, step resume and automatic generation submission remain absent.
- Snapshotting isolates mutations after the initial synchronous copy, not concurrent writes
  during that copy. Treat input collections as stable while submitting a request.
- Existing Class1 files, empty template test, and stale weatherforecast HTTP example remain;
  unrelated cleanup was deliberately excluded.
- Explicit user authorization is required before future staging, commits, pushes or milestone implementation. M6 is committed as 1b93fc4; the committed lifecycle initializer was verified on 2026-10-15.

See [workflow design](WORKFLOW.md), [decisions](DECISIONS.md),
[Analyst evidence](STRUCTURED_OUTPUTS.md), and [Architect evidence](ARCHITECT_AGENT.md).

## Committed security hardening (historical)

5B.1 is the committed/pushed baseline; commit ff24cd5 hardens API diagnostics and restores
standard configuration precedence (command line > environment > Development User Secrets > JSON).
All authentication/project membership rules and provider contracts are retained. See
[Authentication](AUTHENTICATION.md#focused-security-hardening-2026-10-13-committed) for the
error-path behavior, targeted regression coverage and limits. The later 5B.2 authorization is recorded above.

Executed on 2026-10-13: targeted regressions **15 passed**, full build **0 warnings/errors**,
full suite **220 passed, 0 failed, 0 skipped**, including nine PostgreSQL and two live Keycloak
cases with fresh browser PKCE evidence. No live OpenAI call. Diff review and whitespace check
passed; the nine-file candidate was subsequently committed as ff24cd5. The dedicated verification services
are stopped with their data preserved. See Authentication for initial test-harness failures and
scope limits. The 205-test numbers above describe the historical 5B.1 baseline.
