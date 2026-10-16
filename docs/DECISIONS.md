# Architectural decisions

Implementation decisions were initially recorded on **2026-10-13**. Sections distinguish
implemented Factory choices from approved future architecture; neither generated sample ADRs
nor a future decision authorize implementation or provisioning. The deployment planning baseline
is commit `e1bced7230d89352ddf6e69aeae4bee71fd106bf` (Milestone 5B.2). See
[CURRENT_STATE.md](CURRENT_STATE.md) for verification and [Accuracy Protocol](AI_ENGINEERING_ACCURACY_PROTOCOL.md)
for required working rules.

| Decision | Status | Rationale and trade-off |
| --- | --- | --- |
| Keep a project-agnostic .NET 10 factory | Implemented foundation | Example target applications are samples, not dependencies. Domain prompts/contracts can serve other projects; other execution stacks remain future work. |
| Separate API, Core, Orchestration, Infrastructure, Tests | Implemented | Core owns contracts and validators; Orchestration depends only on Core. OpenAI-specific integration stays in Infrastructure. Milestone 4 adds only a fixed sequential workflow service. |
| Preserve IAgent and IModelProvider signatures | Implemented | Existing callers/providers remain compatible. Optional Analysis input and Architecture output extend shared DTOs; this is a small explicit union rather than a generic agent framework. More agent types may justify revisiting it later. |
| Keep ChatClient and the current SDK | Implemented | OpenAI 2.14.0 already supports the required strict output format. No Responses migration or SDK upgrade was needed. OpenAI is currently the only concrete provider. |
| Pass schema through ModelRequest | Implemented | ModelOutputSchema is provider-neutral; agents select schemas, the provider enforces them remotely. Plain-text use remains possible when no schema is supplied. Domain deserialization remains the agent's responsibility. |
| Enforce strict remote schema plus local validation | Implemented | Required fields and no extra properties constrain structure; strict System.Text.Json parsing and validators enforce local invariants. Schemas and typed DTOs are manually maintained; tests check correspondence. Schema validity is not factual accuracy. |
| Confirm only from explicit caller evidence | Implemented | Exact source ID and description matching prevents invented extensions from becoming confirmed. Paraphrases remain unconfirmed. Caller identity and authorization are not verified by this mechanism. |
| Preserve trace status through architecture generation | Implemented | Every trace refers to an existing requirement and retains its original status. Unconfirmed or assumption-backed bases must be recommendations. Confirmed constraints are IDs rather than generated prose. This cannot prove the semantic accuracy of a rationale. |
| Always return proposed architectures | Implemented | Success means accepted by validators, not approved by a human. The only supported proposal status is proposed. No execution/file/approval tools are available to ArchitectAgent. |
| Require alternatives and ADRs for every technology decision | Implemented | Avoids a model-controlled significance exemption. Every decision needs an alternative, benefits/drawbacks, trade-offs, and one ADR. More verbose than allowing trivial choices to bypass comparison. |
| Reuse a shared DesignBasis and referenced ADR provenance | Implemented | Components, technology decisions, and risks carry traces/recommendation/assumption references. ADRs reference validated decisions instead of duplicating status and alternatives. IDs and links must be validated. |
| Require component coverage of all input requirements | Implemented | Omissions fail instead of silently losing requirements. Suggested requirements can have tentative components; coverage does not approve their implementation. |
| Keep agent endpoints explicit | Implemented | Analyst and Architect have separate POST routes and share the existing HTTP error handler. Direct Architect injection avoids changing the unkeyed IAgent default (Analyst). Milestone 4 adds a separate workflow endpoint while preserving these routes. |
| Fail closed on unusable model results | Implemented | Invalid JSON/provenance, incomplete or empty responses return failure with no accepted output; refusals are distinct. No application-level output repair, fallback to JSON mode, or auto-approval. SDK retries are separate behavior. |
| Expose static validator diagnostics | Implemented | Safe rule codes improve future Architect failure diagnosis without returning request/model text. Added after an unexplained live 502; not evidence that its root cause was fixed. |
| Keep unit/HTTP tests deterministic | Implemented | Synthetic credentials, fake model providers, SDK HTTP handlers, and WebApplicationFactory avoid paid calls during tests. Captured real responses are separate evidence fixtures; they do not prove ongoing service availability. |
| Retain output string alongside typed fields | Implemented compatibility choice | Output now holds canonical JSON, while Analysis/Architecture offer typed consumption. This duplicates payload data; old Markdown expectations are no longer valid. |
| Use existing Development secret configuration | Implemented | Credentials remain outside documentation; malformed key values fail early without echoing them. The security hardening pass removes duplicate secret registration: command line overrides environment, which overrides Development User Secrets and JSON. Invalid winning security values fail closed. |

## Deferred implementation (not authorized by this document)

- Further approval-driven orchestration. Existing-case human decisions and persistent decision records are implemented in committed 5B.2; automatic execution remains deferred.
- Implementation, test, review, and pull-request agents.
- Sandboxed command execution, MCP tool integration, GitHub automation.
- Additional LLM providers, persistent workflow coordination, queues, dynamic multi-agent orchestration. Review artifact storage is implemented in 5A.
- Broader logging audit and reliability evaluation. Workflow HTTP cancellation propagation is now integration-tested.
- Multi-Cloud Deployment & BYOC: approved future direction below; no Deployment Engine, Terraform modules or cloud hosting implementation exists at this baseline.
- Further commits and pushes: require explicit user approval. Milestone 4 is committed as b9ce705; Milestone 5A is committed as b3e95f4; Milestone 5B.1 is committed/pushed as 4dbd0de; security hardening is committed as ff24cd5; 5B.2 is committed and pushed as e1bced7 (pushed status confirmed by the user's independent GitHub inspection).

## Interpretation rules

The captured Architect response recommends browser storage for a task-list example. Its technologies,
capacity/performance statements, and ADR are generated recommendations, **not accepted Factory architecture**.
Do not turn them into implementation constraints. Significant future architecture changes require human
approval under the Accuracy Protocol.

The first Architect live 502 is unresolved. A subsequent successful call and passing tests verify specific
behaviors, not a root-cause fix or general reliability. Keep facts, recommendations, and assumptions separate.

## Milestone 4 decisions (2026-10-13)

| Decision | Rationale and trade-off |
| --- | --- |
| Fixed synchronous Analyst → Architect service | Reuses concrete agents without a dispatcher, framework, packages, queues, or persistence. Requests occupy an HTTP request until generation finishes. |
| Separate workflow request/result contracts | Preserve existing agent interfaces and responses. Return typed artifacts and explicit stage progress without duplicate JSON strings. |
| Workflow GUID generated inside each execution | Independent runs, including failures/cancellation, get IDs. HTTP responses and safe logs correlate; IDs are not authentication, idempotency keys, or persisted records. |
| AwaitingHumanReview overall; Completed per stage | Generation completion cannot be confused with approval. No approved status, approval pause, or automatic execution exists. |
| Snapshot before validation/await | Copy immutable strings/records and detach the evidence collection once. Later caller mutations cannot change either stage's evidence/context. Concurrent modification during the initial copy is not supported. |
| Retain validated analysis on Architect failure | Makes partial progress useful without accepting invalid proposals. No automatic retry or resume capability. |
| Explicit empty-analysis boundary | Analyst permits zero requirements; Architect does not. Return 422 with static code and retained analysis; skip Architect without weakening either validator. |
| SDK-neutral exception wrapper carrying progress | Core/Orchestration remain provider-neutral; API classifies SDK errors and emits safe correlated metadata. Unexpected failures remain 500. |
| Cancellation is a result at the workflow boundary | Preserve ID/progress and log 499. A disconnected client may receive no response; unrelated cancellation exceptions remain failures. |
| Local baseline only | Approved root commit c8c3330fe7e8a4d15f73208a52d3fdd24250c534 contains Milestones 1–3 plus reviewed .gitignore. Milestone 4 was later committed as b9ce705; no push or rewrite. |

The first workflow live attempt returned 500 at Analyst with no accepted artifacts. Its cause
is unresolved. A second explicit attempt outside the restricted sandbox succeeded; that is not
proof the first failure was caused by the sandbox. See [CURRENT_STATE.md](CURRENT_STATE.md)
and [WORKFLOW.md](WORKFLOW.md) for executed results, evidence, and limitations.

## Milestone 5A decisions (2026-10-13)

- Implement the approved persistence phase only: three tables (ReviewCases, ArtifactVersions,
  PublicationEvents). Identity/membership, workflow-run coordination, actual decision audit and
  idempotency receipts await 5B; do not simulate authenticated users in their absence.
- Keep IReviewStore internal to application composition, unregistered in the API. This preserves
  existing behavior and prevents an unauthenticated approval or artifact-publishing endpoint.
- Store immutable exact JSON bytes in bytea with format/validator versions, input evidence,
  source workflow, proposed outputs and SHA-256. Use database hash CHECK, immutable triggers,
  restrictive runtime grants, and application verification; DB administrators remain trusted.
- Use an explicit bigint Revision concurrency token and one transaction for the case update,
  artifact and publication event. A deferred current-artifact FK checks commit consistency.
  Domain terminal-state rules exist, but a 5A CHECK forbids stored terminal decisions.
- Pin EF Core/Relational/Design 10.0.12 and Npgsql/provider 10.0.3; use a standalone design-time
  context so migrations do not require API/OpenAI startup. Apply migrations explicitly.
- Docker and WSL were absent. Use an isolated official PostgreSQL 18.6 Windows binary cluster
  for executed verification; do not install OS components/services. Preserve data under ignored
  artifacts and use only loopback/SCRAM locally. Production provisioning remains unverified.
- Persisted artifact provenance is consistency-validated, not authenticated origin. Callers of
  the internal store are trusted until 5B supplies identity and authorization. Approval never
  modifies requirement provenance or automatically executes code.

See [Review Foundation](REVIEW_FOUNDATION.md) for real database results and limitations.

## Milestone 5B.1 decisions (2026-10-13)

- Use pinned JwtBearer 10.0.12 and local Keycloak 26.8.0 with the existing Java 25 installation.
  No Docker/WSL installation. Keycloak-specific token-profile checks stay at the API boundary.
- Validate issuer/audience/signature/lifetime and access-token semantics, plus hardcoded client
  classification. Identity is issuer/subject, never a request reviewer ID or email. IdP administrators
  remain trusted; role claims cannot create Factory membership.
- Require resource authorization for the actual ProjectId on all three generation routes. Keep
  successful payloads unchanged; anonymous access deliberately becomes 401. Health remains public.
- Add Projects, Principals and ProjectMemberships in access schema with active flags and Revision
  concurrency. Composite kind FK and CHECK prevent Service Reviewer grants. Use a separate SELECT-only
  API login; operator-only provisioning supplies explicit memberships. No online admin or enrollment API.
- Keep review artifacts and their PendingReview-only constraint unchanged. Generator and Reviewer are
  independent grants. Reviewer eligibility exists without a decision endpoint or execution permission.
- Permit HTTP discovery only for the exact Development loopback issuer; reject remote transport for
  operations using that issuer. Production requires HTTPS. No token propagation to agent/model inputs.
- Use explicit migrations and per-request membership reads. Cached JWT signing keys permit offline
  validation; no introspection or instantaneous IdP-side token revocation is claimed. Missing validation
  material and unavailable membership storage fail closed. See [Authentication](AUTHENTICATION.md).

## Milestone 5B.2 decisions (2026-10-14)

- User-approved scope: decide only existing stored cases. Preserve all agents and provider contracts;
  no automatic submission, Developer Agent, execution queue or approval-triggered agent call.
- Add DecisionEvents and CommandReceipts, keeping immutable AI provenance untouched. An event is
  both the human decision and its append-only audit; receipts reference its versioned historical
  projection instead of duplicating response JSON. Future response formats must preserve format 1.
- Bind case/project/version/hash with composite FKs. Share the existing bigint Revision with
  publication. Require exact strong If-Match and verify exact artifact bytes before deciding.
- Lock project, principal and Reviewer membership FOR SHARE through commit. A narrowly granted,
  NOLOGIN-owned SECURITY DEFINER helper permits locks without giving the runtime access-table
  mutation rights. PostgreSQL does not validate JWTs; the API identity boundary remains trusted.
- Replace PendingReview-only CHECK with legal-transition guards and deferred constraint triggers.
  Terminal case state, event, receipt and revision must agree at COMMIT even for raw SQL. Keep all
  audit and artifact immutable guards. No downgrade that could erase decision history is supplied.
- Scope durable idempotency to project/principal/command-version/key. Reauthorize every replay;
  return the original historical body with Idempotency-Replayed and no case ETag. Conflicting reuse
  is 409; stale revisions are 412. No automatic write retry after an uncertain response.
- Provision ownership/grants explicitly after migration, never at startup. Retain the SELECT-only
  authorization reader and a separate decision runtime. Dedicated factory databases revoke PUBLIC
  database CREATE/TEMP and public-schema CREATE; this provisioning is not for shared databases.

See [Human Decisions](HUMAN_DECISIONS.md) for implementation details, executed evidence and limits.

### Milestone 5B.2 invariant correction (2026-10-14)

- Pin the same 25 whitespace code points and 4,000 UTF-16-unit reason limit in domain, command and
  database validation. Reject NUL/malformed Unicode; preserve exact accepted text and optional
  Approve reasons. Avoid assuming SQL POSIX whitespace equals .NET Unicode categories.
- Add `20261014010250_AlignDecisionReasonPolicy`; preserve all previously applied migration bytes.
  Fail and roll back if legacy audit rows violate the new invariant; never silently rewrite them.
- Classify direct and EF-wrapped PostgreSQL integrity exceptions as safe 500 responses, preserving
  correlation without database payloads. No changes to locks, grants, transactions or idempotency.
- The corrected 5B.2 candidate was subsequently committed as e1bced7 after approval and staged-content verification, and is confirmed pushed. Its historical evidence remains in Human Decisions. M6 subsequently received explicit architectural and implementation approval; see the M6 entry below.

## Multi-Cloud Deployment & BYOC (future architecture)

Status: **approved future product and architecture direction; not implemented**.
This records the user's deployment requirements against the 5B.2 baseline. It authorizes only
this documentation update. Milestones 6–12 and their priorities remain unchanged.
See [Product Vision](PRODUCT_VISION.md) and [Roadmap](ROADMAP.md).

Customers must be able to deploy generated applications into Azure, AWS or Google Cloud using
customer-owned accounts (BYOC) or Factory-managed hosting. Azure is first (M13A), then AWS
(M13B), then Google Cloud (M13C). The first end-to-end StockFlow pilot (M14) uses Azure and
does not require AWS/GCP completion. Desktop application generation, packaging and delivery remain separate in M16.

| Decision | Rationale and boundary |
| --- | --- |
| Prefer Terraform for cross-cloud Infrastructure as Code | Establish one preferred provisioning technology while retaining provider-specific infrastructure behavior. This is a future choice, not an installed dependency or implemented integration. |
| Keep the Deployment Engine independent of Terraform details | Scope abstractions to deployment intent, target, immutable plan reference, apply result and verification evidence. Terraform CLI arguments, module layout, provider configuration and state formats belong behind an implementation boundary. Exact interfaces are deferred until implementation design; no generic multi-tool framework is required now. |
| Use provider-specific, versioned, tested modules | Pin module/provider/tool versions and test supported combinations per cloud. A common engine must not hide unsupported capabilities or assume resource equivalence. |
| Isolate remote state by tenant and deployment target | Tenant-scoped access, locking, encryption, audit and recovery must cover state and plan artifacts. State must not be shared across tenants or exposed in prompts/logs; naming conventions alone are not an authorization boundary. |
| Use least-privilege, short-lived identities | Scope identities to the tenant, cloud target, environment and required operation for both BYOC and Factory-managed hosting. Define delegation and revocation explicitly; do not give agents reusable cloud credentials. |
| Approve and verify the exact immutable plan before apply | Bind plan identity to its content hash, application release, tenant/project/environment, cloud account, module versions and execution inputs. Check approval, authorization and plan validity before applying that exact plan; changed inputs, target or relevant state invalidate approval and require replanning. |
| Run infrastructure only in isolated workers | Constrain executable tooling, identity, network access and job lifetime. The engine schedules controlled operations; agents cannot bypass it with unrestricted shell or infrastructure commands. |
| Retain deployment evidence and release history | Record plan/approval identity, target, release, module versions, outcome and health checks, with sensitive details protected. Verification determines release health; a successful apply alone is insufficient. |
| Document and test rollback/recovery per provider and workload | Include application rollback, infrastructure reconciliation, state recovery and data recovery limits. Destructive recovery requires explicit authorization; rollback is not assumed to be an automatic inverse of apply. |
| Separate cloud deployment, source delivery and desktop delivery | Handing over source or a desktop package does not mean a running cloud application. M16 packaging is independent of the cloud deployment control path. |
| Expose provider capabilities and availability limits | Do not assume identical services or promise zero-downtime deployment across providers. Document and verify supported strategies and recovery expectations for each target. |

Trade-offs: shared deployment governance reduces duplicated orchestration, but each provider's
modules, permissions, health checks and recovery paths still require independent maintenance and
testing. BYOC retains customer resource ownership while requiring explicit delegated access;
Factory-managed hosting adds tenant-isolation and operational responsibilities. Terraform remains
the preferred implementation, while the engine boundary limits coupling without committing to
additional IaC implementations.

Milestone 5B.2 currently approves immutable review artifacts only. It does not implement deployment
plans, cloud authorization, worker execution, state management or release operations. Future
deployment approval must use its own verified plan/target binding; an existing review receipt
must never be treated as permission to apply infrastructure. No application contract, database
schema or runtime behavior changes as part of this decision.

## Milestone 6 lifecycle integration (2026-10-14, committed as 1b93fc4)

Explicitly approved architecture, implemented and committed as 1b93fc4. See
[Product Lifecycle](PRODUCT_LIFECYCLE.md) for contracts, verification and limitations.

- One managed ReviewCase per WorkItem; server-generated WorkItemId and RunId. RunId is the
  workflow result/envelope WorkflowId. Legacy cases remain an immutable closed, unlinked cohort.
- A minimal ReviewCase aggregate owns publication/decision rules; PostgreSQL independently
  enforces lineage, publication audit and successful-command consistency at COMMIT.
- Admit a scoped, canonically fingerprinted command atomically before generation. Persist only
  the digest of independent execution ownership. Identical admission replays never start models.
- Generate outside transactions; publish artifacts, audit, lineage and terminal command outcome
  atomically. Reauthorize Generator membership under locks at admission and publication.
- Retain Reviewer-only artifact/reason access and human decisions. Lifecycle reads disclose only
  safe metadata to Generators or Human Reviewers. Strong WorkItem ETags include case revision;
  historical command replay carries no current ETag. Existing review validators are unchanged.
- Terminal commands are immutable; clear active ownership, retain latest-run/history, increment
  WorkItem revision. Explicit retries can abandon expired attempts; late finalizers are fenced.
- Bound execution to 180 seconds, four attempts and two provider calls with SDK retries disabled
  for the lifecycle path. No exact LLM cost or exactly-once external-call guarantee is claimed.
- Use one additive migration and explicit least-privilege provisioning. Legacy CreateAsync keeps
  its signature but cannot create new unlinked cases after migration. Historical bytes/audits stay intact.
- M6 is synchronous with a command journal, not M7 scheduling: no workers, queues, automatic retry,
  step resume or approval-triggered execution. Unknown outcomes require status/replay reconciliation.

## Milestone 7 durable orchestration design (2026-10-15, approved)

User-approved design; M7.1 committed as `bcd50fa`. See [Durable Workflows](DURABLE_WORKFLOWS.md).

- New Workflow Orchestration bounded context: `WorkflowRun` aggregate, steps and attempts.
- Separate `Factory.Worker` process; the API only admits (202) and reads status.
- M6 synchronous routes, functions and guards are unchanged; M6's 180-second command deadline
  cannot host indefinite waits, so durable runs have their own schema. Publication creates the
  M6 work item, published command and managed case in one transaction under the M6 guards.
- PostgreSQL leases with `FOR UPDATE SKIP LOCKED`; checkpoints after each model step.
- No automatic retry of model failures; lost leases may repeat an unfinished step's provider call
  within a fixed attempt bound. Nothing is described as exactly-once.
- Phases M7.1 (this), M7.2 (waiting, parallel steps, cancellation), M7.3 (real-time push), each
  stopping for review.

## Milestone 7.2 scope (2026-10-15)

Decided under the user's full approval for M7.2; reversible on request.

- **Implemented:** durable waiting for human decisions (no lease, no timeout, `stop` always
  offered) and run cancellation, as an additive migration on top of released M7.1.
- **Deferred to M8: parallel steps.** The only workflow (Analysis → Architecture → Publication)
  is strictly sequential, and the producer of independent parts is M8's request decomposition and
  model routing. Building DAG scheduling now would require an invented test-only plan and
  speculative abstractions (AGENTS.md rule 6). The M7 "independent parts run in parallel"
  acceptance criterion therefore moves to M8, which must add step dependencies and allow more
  than one running attempt per run.
- **Interim decider authority:** active Human project Reviewers decide until M8 defines tenant
  administrator/project owner roles. Service identities can never decide.
- **No production decision producer yet:** the waiting path is infrastructure for M8 customer-model
  decisions; it is tested through the worker port rather than triggered by current steps.

## Milestone 7.3 real-time notifications (2026-10-15)

- PostgreSQL `NOTIFY` from a run trigger is the event source: delivered only at COMMIT, no extra
  broker, works across API instances. Payloads carry only project id, run id and status because
  any database login can LISTEN; notifications are hints and clients re-read over HTTP.
- SignalR hub with JWT in the `Authorization` header only. Query-string tokens for browser
  WebSockets are deferred to M12 because tokens in URLs can be logged.
- Watch authorization is checked at subscription time; continuous revocation enforcement for
  hint traffic is not implemented and is a documented limit.
- Test client pinned to `Microsoft.AspNetCore.SignalR.Client` 10.0.13 after the restore reported
  GHSA-334f-vvp4-7g6p in 10.0.12; deployments must use a patched ASP.NET Core runtime.

## Milestone 8 plan (2026-10-15, user-approved)

- Phases, each verified and reviewed: M8.1 usage metering → M8.2 tenancy (Organization → Project,
  Owner/Admin) → M8.3 budgets and reservations → M8.4 customer-provided models → M8.5 request
  decomposition, model routing and parallel steps. The original 2–3 week estimate no longer holds.
- Customer credentials: an `ISecretStore` abstraction; local implementation encrypts with AES-GCM
  in PostgreSQL using a master key from configuration; an Azure Key Vault implementation follows
  with M13A. Credentials never enter prompts, logs, artifacts or agent tools.
- Decomposition plans (parts, complexity/importance classification and selected models) require
  human approval through the decision popup before any part executes.
- Usage first: token counts come from provider responses; estimated cost uses an operator-maintained,
  versioned price table. The Factory never invents prices; unpriced usage is reported as such.
- Providers (2026-10-15): OpenAI is the first Factory provider; **Anthropic is the second, for demo
  use**. Anthropic integration requires a separate Anthropic API key (pay-as-you-go, Anthropic
  Console) stored like other secrets; a consumer Claude subscription or an assistant session is
  not an API credential and must not be used by the Factory. The Anthropic adapter is built in M8.4
  after the OpenAI-compatible adapter and model registry. Further providers are added only when
  needed; there is no subscription to every provider.
- Anthropic demo budget: **USD 2**, set by the user as a spend limit in the Anthropic Console
  (provider-side). M8.3 must also enforce a Factory-side cap of no more than this for the
  Anthropic demo credential, so the Factory stops before the provider limit is reached.

## Retire synchronous generation routes (2026-10-16, user decision)

The ephemeral agent/workflow endpoints and the M6 synchronous lifecycle POST routes bypass usage
metering and budgets, so they are closed by default (410 Gone, no model call). They can be
re-enabled only in Development/Testing for regression suites; other environments refuse to start.
Trade-off: existing security regression coverage is kept without exposing the routes in
production. Durable runs are the only production generation path; regeneration of an existing
work item moved to `POST /api/projects/{projectId}/work-items/{workItemId}/runs` (same strong-ETag
precondition, next case version, stale publication instead of overwrite).

## Customer-provided models — M8.4a (2026-10-16)

- The planned `ISecretStore` is realized as `ISecretProtector` (AES-256-GCM with context-bound
  authenticated data); ciphertext lives in the registry row, the master key only in configuration.
  Trade-off: no separate secret table, and a Key Vault implementation can replace the protector.
- No restriction on which customer model may be used (user direction); weaker or unverified
  capability is shown and requires explicit acceptance instead of being blocked.
- No silent fallback: an unusable customer model becomes a `customer_model_unavailable` decision,
  so customer data never reaches a Factory model without the customer's choice.
- Egress is checked at connect time against every resolved address (SSRF and DNS-rebinding
  defence); private-network endpoints stay out of scope until a separate mechanism is approved.
- Each run snapshots its model at submission, so reassignment never changes a running workflow.

## Anthropic provider — M8.4b (2026-10-16)

- Claude calls use the official Anthropic .NET SDK (`Anthropic` NuGet), not an OpenAI-compatible
  shim; structured output uses `output_config.format` with the agents' existing JSON schemas, which
  fit the documented Anthropic schema subset (one type array, no unsupported keywords).
- The Factory default provider is chosen per worker with `Generation:Provider` (`openai` default,
  `anthropic`); only the selected provider's key is required. Per-step routing remains M8.5.
- Demo model `claude-haiku-5-5` (lowest current price) for the user's USD 2 Anthropic limit.
  Its price depends on prompt length; the flat price table records the higher (>100K) tier, so
  every Anthropic estimate and reservation is an upper bound. Trade-off: the Factory cap stops
  earlier than real spend would require, but it can never be overrun by long prompts.
- Customers may register Anthropic credentials (`Anthropic` protocol) under the same rules as
  OpenAI-compatible endpoints: encrypted key, egress policy, verification, explicit risk acceptance,
  no fallback. The SDK client always receives an explicit key and never auto-resolves environment or
  profile credentials; SDK retries are disabled.

## Model routing policy — M8.5a (2026-10-16, delegated)

Taken under the user's delegated authority of 2026-10-16 ("soru sormadan projemizi ilerlet");
each point answers an open routing question in the Roadmap and remains open to the user's review.

- **Unit (Q1):** routing happens per workflow step attempt; with M8.5b the unit becomes the
  decomposed part. Each attempt records one route before its first provider call.
- **Criteria (Q2):** deterministic rules, no model call: tier = max(role minimum, complexity tier,
  importance tier). Importance High forces Strong even for low complexity. Budget, latency and
  availability do not change the tier; budgets refuse calls instead (M8.3), never downgrade them.
- **Configuration (Q3):** platform operators map tiers to models per worker
  (`Generation:Tiers:{Economy|Standard|Strong}`); a project's customer model overrides routing
  entirely (customer choice). Tenant-level policy overrides are not implemented.
- **Escalation (Q4):** no automatic retry with a stronger model; failures stay explicit (M6 rule).
- **Customer models (Q5):** an assigned customer model serves every step (`customer_model` reason);
  routing among several customer models is not implemented.
- **Providers (Q6):** tiers may map to OpenAI or Anthropic models in the same worker.
- **Quality floors (Q7):** Analysis never below Standard, Architecture never below Strong.
- **Audit (Q9):** every attempt records tier, reason code, policy version (`routing-v1`) and the
  provider/model actually used; the run view shows them.
- Trade-off: with a single configured model every tier maps to it; the evidence still records the
  intended tier, so the gap between intended and actual quality is visible rather than hidden.

## Request decomposition — M8.5b/c (2026-10-16, delegated)

Taken under the same delegated authority; open to the user's review.

- **Merge instead of a new artifact format.** Part proposals are merged into one
  `ArchitectureProposal` with part-prefixed ids and validated against the full analysis, so review
  artifacts, publication and human review (M5/M6) stay unchanged. Trade-off: the review artifact does
  not carry the plan itself; the plan stays readable through the run's plan endpoint.
- **Fixed ordinals, explicit edges.** Ordinals 0–2 keep their kinds; decomposition steps use 3–16 and
  execution order comes from dependency rows. analysis-architecture-v1 keeps implicit linear order.
- **Plan approval only (Q2, review gate).** Plans are approved or stopped, not edited; parts run only
  the Architect; at most 12 parts and 4 concurrent attempts per run.
- **Per-step decisions.** One pending decision per step (was per run), so parallel parts can each
  ask; a run runs again only when no decision is pending.
- **Failure semantics.** Any failed part fails the run (no partial publication); running siblings
  lose their lease and their late results are refused.

## M9 local execution runtime (2026-10-16, user decision)

- The user approved **Docker Desktop on WSL 2** ("onaylıyorum") after the trade-offs against Podman,
  a Hyper-V VM, Azure Container Apps, GitHub Actions and running on the host were explained.
- Installed on 2026-10-16: WSL 2 (`wsl --install --no-distribution`, exit 0; default version 2) and
  Docker Desktop via winget (installer hash verified); Docker client/server 29.8.2, linux/amd64,
  after a Windows restart.
- Executed smoke test: `hello-world` ran; an `alpine:3.22` container with `--network none --read-only
  --user 65534:65534 --cpus 1 --memory 256m --pids-limit 64 --cap-drop ALL --security-opt
  no-new-privileges` reported uid 65534, no network (HTTP request failed), read-only root filesystem
  and the cgroup limits (memory 268435456, pids 64, cpu 100000/100000). This verifies the isolation
  flags on this machine only; it is not M9's sandbox implementation or a security review.
- The user then confirmed ("evet") the template stack **Angular + ASP.NET Core + PostgreSQL** and the
  starter template's DDD layer layout **Domain / Application / Infrastructure / Api** (Domain without
  dependencies; Application on Domain only; Infrastructure implements Application ports; Api is the
  composition root).

## M9.3 verification jobs (2026-10-16, delegated)

Taken under the user's delegated authority after "evet"; open to review.

- **Own queue instead of a workflow version.** Verification jobs get their own table, leases and
  functions in the orchestration schema rather than a third workflow version, which would have meant
  rewriting the claim/complete/check functions shared by the model workflows. Trade-off: a second, small
  lease mechanism to maintain.
- **Evidence as exact bytes plus hash**, written once in the same transaction that finishes the job and
  validated against the job; readers can re-hash the bytes they receive.
- **Bounded sandbox lifetime** (`--rm`, `sleep` for job timeout + 60 s) instead of a startup sweep: a sweep
  could remove another live worker's sandboxes on a shared Docker host.
- **Polling for job status** in the UI instead of extending the run notification channel, whose payload
  is typed to workflow runs.
- **Git workspaces deferred to M10**: the in-sandbox workspace is a tmpfs per job; versioned
  repositories belong with the Developer Agent's repository tools.

## M9 slicing and template design (2026-10-16, delegated)

Taken under the user's delegated authority after the confirmations above; open to review.

- M9 is split into M9.1 (template, registry, deterministic generation), M9.2 (offline sandbox runner,
  malicious-code tests) and M9.3 (per-job workspaces, immutable evidence, worker/UI integration).
- The template is a buildable solution under placeholder names (`FactoryStarter`, `factory-starter`)
  instead of a token syntax, so the template itself can be built and tested; generation is plain
  ordinal replacement plus a content hash pinned in a file registry.
- Generation is pure domain code in Factory.Core; file access stays in Factory.Infrastructure.
- The layer rule is enforced inside every generated application by reflection-based architecture
  tests (no extra package); M11 may add stronger static checks.
- Offline execution (M9.2) will rely on a package cache baked into a digest-pinned runner image for the
  template's dependencies; adding new packages needs a later allow-listed restore path.

## Azure for the M14 pilot (2026-10-16, user decision)

- The M14 StockFlow pilot will deploy into the **user's own Azure subscription** (customer-owned /
  BYOC path), region **West Europe**, with an Azure budget and alert of **USD 5 per month** on the
  pilot's resource group. Azure is not used for M9 execution (see the M9 runtime decision below).
- Prerequisite found on 2026-10-16: Azure CLI 2.91.0 was installed and `az login` succeeded, but the
  account has **no Azure subscription**. No Azure resources exist and none were created. A
  subscription must exist before any M13A/M14 work touches Azure.

## Review page — M12b (2026-10-16, delegated)

Taken under the user's delegated authority ("devam et geliştirmeye kaldığın yerden"); open to review.

- No backend change: the page uses the M5B.2 review endpoints as they are (Reviewer role, If-Match,
  Idempotency-Key, artifact version and SHA-256 in the body).
- The browser hashes the exact artifact bytes it renders (Web Crypto) and sends that hash; deciding is
  disabled when it differs from the published hash or the envelope does not match the case/version.
  The server remains authoritative; this only ensures the decision names what the reviewer saw.
- The reason rule is mirrored client-side (DecisionReasonPolicy v1) only to enable buttons; the server
  still validates.
- Requirement provenance stays visible (confirmed with source, suggested, unverified assumption) and
  the architecture is labelled as a proposal.
- Review decisions are not blocking popups: unlike run decisions they do not stop a run.

## Product Workspace slice — M12a (2026-10-16, delegated)

Taken under the user's delegated authority after "kaldığımız yerden devam edelim" (M9 blocked on a
container-runtime decision); open to review.

- Angular 22.2 (standalone, signals, zoneless) per the roadmap; English UI strings for now.
- OIDC via `angular-oauth2-oidc` (Authorization Code + PKCE, new public client `factory-web`) instead
  of a hand-written flow; versions pinned exactly.
- The API accepts the web client through an optional `Authentication:WebClient`, keeping one
  hardcoded `factory_kind` profile per client; any other client stays rejected.
- Same-origin development through the dev-server proxy (no CORS policy in the API).
- SignalR over long polling, because the hub deliberately accepts tokens only in headers.
- Two read-only listing functions instead of exposing tables; run lists keep the safe projection
  (no task text).

## Domain-Driven Design (2026-10-15, user direction)

Status: **mandatory rule set by the user, for the Factory and for every application it
generates; no refactoring is authorized by this entry**. New milestones (M7 onward) are designed
with Domain-Driven Design. Existing Factory code is aligned incrementally, only within explicitly
approved scopes, consistent with the rules against unrelated refactoring and unnecessary
abstractions in AGENTS.md. A design that deviates from this rule requires explicit user approval
recorded in this document; agents must not waive it on their own.

### Generated applications

Every customer application the Factory generates must follow Domain-Driven Design. This applies
regardless of application size; there is no automatic CRUD-only exemption.

- Analyst/Architect output identifies bounded contexts, a context map, aggregates with their
  invariants, value objects, domain events where justified, and a ubiquitous-language glossary.
  These are proposals subject to human review like all architecture output.
- Application templates (M9) provide a layered structure in which the domain layer has no
  dependency on infrastructure, web frameworks, persistence or SDKs.
- The Developer Agent (M10) implements business rules in aggregates and value objects, not in
  controllers, application services, UI components or database scripts alone.
- Verification (M11) includes deterministic architecture checks, such as dependency-direction
  tests, and reports DDD violations as failures. Agent self-assessment is not evidence of
  conformance.
- Trade-off: small applications carry additional structure and mapping code; this cost is
  accepted by the user's decision.

Strategic design. Candidate bounded contexts, to be confirmed with a context map during M7 design:

| Candidate context | Current or planned scope |
| --- | --- |
| Identity & Access | Principals, project memberships, Generator/Reviewer roles (5B.1, implemented) |
| Work Management (Product Lifecycle) | WorkItems, generation commands, runs, lineage (M6, implemented) |
| Review & Approval | ReviewCases, immutable artifacts, human decisions, receipts (5A/5B.2, implemented) |
| AI Generation | Agents, requirement analysis, architecture proposals, provider abstraction (M1–M4, implemented) |
| Workflow Orchestration | Durable runs, steps, attempts, leases (M7, planned) |
| Tenancy, Usage & Cost | Organizations, budgets, usage records, model routing (M8, planned) |
| Execution | Templates and isolated runners (M9, planned) |
| Delivery | Pull requests, cloud deployment, desktop packaging (M11, M13, M16, planned) |

Tactical rules:

- Aggregates own invariants and state transitions; application services coordinate aggregates,
  transactions and ports without containing business rules.
- The domain layer has no infrastructure, HTTP, serialization or SDK dependencies.
- Use value objects where an identifier or value carries validation rules (for example project
  identity or content hashes) rather than repeated primitive validation.
- Each context maintains a ubiquitous-language glossary; cross-context integration uses explicit
  contracts or anti-corruption layers.
- PostgreSQL guards, grants and deferred checks remain as independent defense in depth. DDD moves
  rule ownership into the domain model; it does not remove database enforcement.

Observed gaps in the current code (inspected 2026-10-15, not yet corrected):

- `ReviewCase` is a behavior-owning aggregate with state machine rules; this is the existing
  reference pattern.
- WorkItem and GenerationCommand have no C# aggregate; their transition and integrity rules live
  mainly in `ProductLifecycle.sql` functions and triggers, with records/DTOs in Core.
- `Factory.Core` mixes domain types with application/transport concerns: `GenerationOutcome`
  carries `HttpStatus`, `WorkItemSnapshot` formats an HTTP ETag, `LifecycleLimits` includes a
  request byte limit, and JSON schema classes sit beside analysis/architecture models.
- Identifiers and hashes are mostly primitives (`string` project IDs, `string` hashes, `Guid`).
- Contexts are namespaces within one Core project and one `ReviewDbContext`; no context map or
  glossary exists yet. Template `Class1.cs` files remain.

Trade-offs: explicit contexts and aggregates make rules testable and keep future milestones from
coupling, at the cost of more types and mapping code. Moving rules out of SQL must not weaken the
current database-enforced integrity evidence; any such change requires its own review and
verification.

## Desktop application technology (2026-10-15, future M16 direction)

Status: **user decision; not implemented**. Generated desktop applications (M16) will use
Electron. This records direction only; it adds no dependency, template, packaging or code, and
does not authorize M16 or any earlier milestone.

- Rationale: reuse the planned Angular application templates (M9) and Product Workspace UI
  patterns (M12) so web and desktop variants of a generated application can share one frontend.
- Boundary: desktop packaging remains separate from cloud deployment and source delivery (see
  the Multi-Cloud decision above). The Electron, packaging and signing toolchain versions are
  chosen and pinned during M16 design, verified against current official documentation.
- Security baseline to define at M16 design: follow the official Electron security guidance,
  including renderer process isolation, no Node.js access in renderers, validated IPC and
  restricted loading of remote content. Exact settings are not yet specified or verified.
- Trade-offs: larger installers and higher memory use than native .NET desktop options, and a
  recurring obligation to ship bundled Chromium security updates. Code signing, auto-update and
  installer formats per operating system remain open questions.
