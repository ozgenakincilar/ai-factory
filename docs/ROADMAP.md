# Development roadmap

Planning baseline: `e1bced7230d89352ddf6e69aeae4bee71fd106bf` (Milestone 5B.2).
This document records user-provided product planning, including future deployment and delivery.
It does not authorize implementation, infrastructure provisioning or automatic execution.

## Planning source and status

The M6–M12 scopes and priorities below were supplied by the user from an earlier ChatGPT
architecture and product-roadmap discussion. They were not committed requirements or implemented
functionality in the baseline. This document now records that planning context without changing
its priorities. The user also supplied the dependency rules, M13A–M16 priorities and preliminary
estimates, and the M15 beta-hardening scope below from that planning discussion.

**M6–M8 are implemented and committed (see each milestone's status). M12a and M12b, the first Product Workspace slices, are implemented. M9 (starter template, offline sandbox runner, verification jobs with immutable evidence) is implemented, with Git workspaces deferred to M10; M9–M16 otherwise remain planned.**
Estimated durations are preliminary planning ranges, not commitments or validated delivery
forecasts. M6 architectural approval was subsequently supplied explicitly; its implementation and verification are recorded in [Product Lifecycle](PRODUCT_LIFECYCLE.md). No later milestone is authorized.

Milestone 5B.2 is the implemented baseline, not a deployment milestone. Its stored human
decisions do not automatically authorize infrastructure apply or trigger execution.

## M6 — Product Lifecycle & Review Integration

Status: implemented and committed as 1b93fc4; the committed lifecycle initializer was verified on 2026-10-15 with a 700-test full run (see [evidence levels](PRODUCT_LIFECYCLE.md#evidence-levels-after-commit)). Priority: **P0**. Original preliminary estimate: **1–2 weeks**; the corrected database/security design reassessed this to **3–5 weeks**, still a planning range, not a validated commitment.

Goal: Connect the existing Analyst → Architect workflow to persistent human review.

Tasks:

- Define Project, WorkItem, ReviewCase and WorkflowRun identity and ownership semantics.
- Design a minimal behavior-owning ReviewCase aggregate where justified.
- Define work-item lineage and revision invariants.
- Introduce an authorized application service using the existing IReviewStore.
- Persist validated Analyst → Architect results as immutable review artifacts.
- Test authorization, persistence failures, concurrency and idempotency.
- Introduce the minimum APIs required to initiate and track review cases.

Acceptance criteria: A Generator can initiate an analysis/architecture workflow, its successful
results are securely persisted, and a Human Reviewer can inspect and approve the exact artifact
version/hash. Human approval must not automatically execute any Developer Agent.

## M7 — Durable Workflow Orchestration

Status: design approved 2026-10-15; **M7.1 committed (`bcd50fa`)**; **M7.2 committed (`9cee79c`)**
(waiting for human decisions, cancellation); **M7.3 implemented** (real-time run change
notifications over SignalR); parallel steps moved to M8
([decision](DECISIONS.md#milestone-72-scope-2026-10-15)). M7 scope is otherwise complete.
See [Durable Workflows](DURABLE_WORKFLOWS.md). Priority: **P0**.
Original estimate: **2–3 weeks**; the added asynchronous requirements below likely exceed it.

Goal: Make long-running workflows independent from individual HTTP requests and API process lifetimes.

Tasks:

- Define WorkflowRun, WorkflowStep and StepAttempt persistence.
- Implement durable job leasing and checkpoints.
- Support running, waiting-for-approval, failed, cancelled and completed states.
- Add safe retries, timeouts, cancellation and idempotency policies.
- Support worker restart and crash recovery.
- Add status/history endpoints and concurrency tests.

Asynchronous execution requirements (user-approved, 2026-10-15; not designed or implemented):

- Generation requests are accepted and return immediately with a run identity; work continues
  in background workers, not inside the HTTP request. The M6 synchronous path is not extended.
- Independent parts of a decomposed request (see model routing under M8) can run in parallel,
  each as its own step with its own attempts, model selection and evidence. Dependencies between
  parts are explicit; a part starts only when its inputs are complete and approved where required.
- A step can enter a durable waiting state for a customer decision (see the decision popup under
  M8) and wait indefinitely without an open request, without a timeout, and without holding
  worker execution capacity. It resumes from its checkpoint when the decision is recorded.
- Workflow and decision state changes are pushed to connected Product Workspace clients in real
  time (for example via SignalR) so pending-decision popups appear while the application is open.
  Pushed messages are notifications only; clients re-read authoritative state from the server.
- Parallel steps never modify the same aggregate concurrently without the aggregate's own
  concurrency control (DDD rule). Human approval gates apply to asynchronous steps unchanged.

Acceptance criteria: A workflow survives worker restart without unnecessarily repeating completed
steps. External LLM calls must not be falsely described as exactly-once. Independent parts run in
parallel (moved to M8 on 2026-10-15), a step can wait for a customer decision across worker restarts without consuming worker
capacity, and connected clients receive state changes without polling.

## M8 — Tenant Isolation & AI Cost Controls

Status: plan approved 2026-10-15 in five phases; **M8.1 usage metering committed as `7da963f`**;
**M8.2 tenancy committed as `4c7789d`**; **M8.3 budgets and reservations committed as `2564a85`**;
**M8.4a customer-provided OpenAI-compatible models committed as `c7eb11d`**; **M8.4b Anthropic
provider committed as `8d0f3d6`** (live call pending account credit); **M8.5a model routing policy
committed as `fe83818`**; **M8.5b request decomposition with plan approval and M8.5c parallel parts
committed as `2b29be2`**, plus a Planner fix after live evidence; one live decomposed run succeeded;
see [Cost Controls](COST_CONTROLS.md). All M8 phases are implemented.
Priority: **P0**. Original estimate: **2–3 weeks**; the approved scope substantially exceeds it.

Goal: Establish organization/project isolation and observable, limited AI spending.

Tasks:

- Define Organization → Project ownership and permissions.
- Enforce tenant-scoped access and data isolation.
- Add cross-tenant negative security tests.
- Record provider, model and input/output token usage.
- Track estimated and actual usage costs separately.
- Add workflow budgets, reservations, limits and quotas.
- Add initial cost-aware routing policies.
- Run independent decomposed parts in parallel: step dependencies and more than one running
  attempt per run (moved from M7; see [decision](DECISIONS.md#milestone-72-scope-2026-10-15)).
- Support customer-provided models: customer API keys (BYOK) and customer-hosted model endpoints.
- Preserve provider-neutral model interfaces.

Acceptance criteria: Usage and estimated costs are attributable to a specific workflow/project;
tenant isolation and budget policies have automated tests.

### Cost-aware model routing — open design questions

Status: requirement recorded 2026-10-15; questions 1–7 and 9 answered by the [M8.5a decision](DECISIONS.md#model-routing-policy--m85a-2026-10-16-delegated) and implemented as routing-v1; decomposition is M8.5b. Customers building
applications need cost management through task-appropriate model selection. Today one configured
model (`OpenAI:Model`, default `gpt-4o-mini`) serves all agents and projects, and no token usage
is recorded. The questions below must be answered in M8 design before implementation.

User requirement (2026-10-15): a customer request is decomposed into parts, and a router decides
which model handles each part. Complex or important work uses strong models; simpler work uses
cheaper or different models. Example: for a requested accounting application, ledger/posting
rules, tax logic, data model and security design would be complex/important, while boilerplate
CRUD screens, test-data generation, formatting or summaries would be simpler.

Implications to resolve in design:

- Decomposition is itself an LLM task whose quality bounds every later step; which model plans
  the decomposition, and is the resulting plan human-reviewed before routing?
- The router needs a classification of each part (complexity and importance/risk). Importance
  should be able to override low complexity: a short financial rule is still high risk.
- Per-part routing depends on step-level workflow records, so it builds on M7 durable workflow
  steps and M8 usage recording rather than on the current single synchronous M6 generation.
- Cheaper models do not remove human approval gates; high-risk parts keep their review gates.

Proposed direction (recommendation, not a decision):

- Record usage first; routing without measured cost and quality data is guesswork.
- Route by configurable policy keyed on agent role and task type, with tenant/project overrides
  bounded by budgets.
- Implement routing behind `IModelProvider` so agent contracts stay unchanged.
- Record the selected model, the policy reason and actual usage per run as evidence. Never
  silently downgrade quality; report validation failures explicitly.

Open questions:

1. Routing unit: the user requirement sets it at decomposed parts of a request; how granular a
   part is (feature, workflow step, or individual model call) remains open.
2. Criteria: complexity and importance are required; are input size, budget remaining, latency
   targets and provider availability also used, and who or what assigns the classification
   (decomposition model, rules, or human review)?
3. Who configures policies: platform defaults, tenant administrators, project owners, or all
   three with defined precedence?
4. Fallback and escalation: when a cheaper model's output fails schema/provenance validation,
   may the router retry with a stronger model? Each retry consumes budget and must be bounded,
   recorded and visible; M6 currently forbids automatic application retries.
5. Customer-provided models (required, see below): how do customer keys and endpoints join
   the routing pool, and what happens when a policy selects a model the customer cannot access?
6. Multi-provider: are non-OpenAI providers in scope for M8, or is initial routing limited to
   models of a single provider?
7. Quality guardrails: which roles (for example Architect or Developer) require a minimum model
   tier regardless of budget, and how is quality measured to justify routing changes?
8. Cost data: source and refresh of model price tables, and treatment of estimated versus
   provider-reported actual cost.
9. Determinism and audit: must a replayed or reviewed run identify the exact model and policy
   version that produced each artifact?

### Customer-provided models — requirement

Status: **user requirement recorded 2026-10-15; not designed or implemented**. Today a single
Factory-wide OpenAI key and model serve every project; `IModelProvider` has one implementation.

Customers may use their own models for flexibility and security, in two forms:

- **A. Customer API keys (BYOK):** a customer supplies its own key for a supported commercial
  provider (for example OpenAI, Anthropic or Azure OpenAI). Provider cost is billed to the customer.
- **B. Customer-hosted models:** a customer supplies its own model endpoint, such as a
  self-hosted, fine-tuned or private-network deployment, so prompts and generated content can
  stay within customer-controlled infrastructure.

Security requirements (both forms):

- Store customer credentials per tenant, encrypted in a secret store; never place them in
  prompts, logs, artifacts, responses or agent-accessible tools. Support rotation and revocation.
- Only authorized tenant administrators can register, change or remove a key or endpoint; every
  change is audited. A project uses only its own tenant's keys and endpoints.
- Customer-hosted endpoints require HTTPS with certificate validation, explicit registration and
  egress controls that prevent server-side request forgery into Factory or other tenants'
  networks. Private-network connectivity requires a separately designed, approved mechanism.
- Model responses remain untrusted input: the same strict schema, provenance and safety
  validation applies to every model; validation is never relaxed for a customer model.

Capability and quality requirements:

- No vendor, model or hosting restriction (user requirement, 2026-10-15): customers may register
  any provider or self-hosted model. OpenAI-compatible endpoints are supported generically; other
  protocols are supported through provider adapters added as needed. The Factory never limits
  which customer model may be chosen; it only informs.
- Maintain a capability profile per registered model (for example strict structured output,
  context window, tool use) and measure it with a registration test.
- When a customer model lacks a capability or falls below the recommended tier for a part, the
  customer is informed and decides (see below); the Factory does not reject the choice on its own.
- Freedom of choice does not relax security or validation: credential protection, egress/SSRF
  controls, transport security and strict output validation still apply. Output that fails
  validation is reported as a failure and is never accepted as a valid artifact.
- Record provider/endpoint identity, model, usage and validation outcome per call as evidence,
  so an artifact identifies which customer model produced it.

Informed customer decision (user requirement, 2026-10-15): the Factory does not decide on the
customer's behalf. When a choice affects where customer data is processed, cost or quality, the
Factory pauses the affected part, informs the customer and acts only on the customer's decision:

- Situations: a customer model lacks a required capability or minimum tier; a customer endpoint
  is unavailable or fails validation; a part would run on a Factory-hosted model instead of the
  customer's own; and whether all parts must run only on customer models (data residency mode).
- The notice states, in plain language, the affected part, the reason, the available options
  (for example use a Factory-hosted model for this part, retry later, change the model, or stop)
  and each option's data-location, cost and quality implications.
- There is no automatic fallback to Factory-hosted models. Until the customer decides, the
  affected part waits; work that does not depend on it may continue only if the customer allowed it.
- Only an authorized tenant administrator or project owner decides. Decisions are scoped
  (single part, project or tenant default), audited with the decider's identity and time, and
  revocable for future work. A standing default applies only within the scope the customer chose.

Notice delivery (user requirement, 2026-10-15): notices are delivered only inside the Factory
application (Product Workspace, M12), as a popup shown to authorized users while they have the
application open. There is no email or external API notification channel. A notice raised while
no authorized user is online appears when one next opens the application.

- The popup is a view of a durable, server-side pending decision, not the decision record itself.
  A notice is not lost if the user is offline, closes the popup or the browser; it remains in an
  in-app list of pending decisions until decided, cancelled or expired.
- The popup shows decision actions only to users authorized to decide; other project members see
  the pending state without actions.
- The decision is submitted to and validated by the server (authorization, scope, current state
  and freshness); the browser is never the authority. A stale or already-decided notice is rejected.
- A popup must not preselect an option or make "use a Factory-hosted model" the easiest default
  action; the data-location implications must be visible before confirmation.
- Blocking (user requirement, 2026-10-15): the affected work cannot continue until the popup is
  resolved by a decision. The popup is modal for authorized users and closes only when one of
  its options is chosen ("stop" is always one of them); there is no dismiss-without-decision.
  There is no automatic timeout or cancellation: the part waits until a decision is made.
- Consequence: undecided work stays paused indefinitely and keeps its workflow state. Whether a
  paused part holds budget reservations or execution capacity while waiting must be defined in
  M7/M8 design so that waiting does not block other projects.

Remaining open question: which provider adapters beyond OpenAI-compatible endpoints are built
first. Waiting for customer decisions depends on M7 durable
workflow states; the popup depends on M12.

## M9 — Application Templates & Secure Execution

Status: **implemented** as M9.1–M9.3 (see [Templates and Execution](TEMPLATES_AND_EXECUTION.md)); per-job
Git workspaces are deferred to M10. Priority: **P0**. Estimated effort: **3–4 weeks**.

Goal: Establish a reproducible Angular + ASP.NET Core + PostgreSQL application template and
isolated build environment.

Tasks:

- Build a versioned starter template with a mandatory Domain-Driven Design layer structure
  (domain layer free of infrastructure/framework dependencies; see [DDD decision](DECISIONS.md#domain-driven-design-2026-10-15-user-direction)).
- Add a minimal template registry.
- Create isolated per-job Git workspaces.
- Add ephemeral build/test runners.
- Enforce CPU, memory, time, disk and network restrictions.
- Isolate credentials and secrets.
- Record immutable verification evidence.
- Test malicious-code and cross-tenant isolation scenarios.

Acceptance criteria: Factory can generate a starter application deterministically and build/test
it in an isolated environment without giving untrusted code access to Factory infrastructure.

### M9 proposed execution design (2026-10-16; local runtime decided, see below)

Update 2026-10-16: the user approved Docker Desktop + WSL 2 as the local runtime; it is installed and
smoke-tested (see [Decisions](DECISIONS.md#m9-local-execution-runtime-2026-10-16-user-decision)). The
user then confirmed the template stack and the DDD layer layout ("evet").

Inspected on the development machine (2026-10-16): no Docker, Podman or installed WSL distribution.
Generated code is untrusted, so it must never build or run in the Factory's own processes or on the
host directly. Proposal, not a decision:

- **Local development:** Docker Desktop on WSL 2 (requires installation, administrator rights and a
  restart; the user decides). One short-lived container per job, built from a pinned, digest-locked
  .NET SDK + Node image; non-root user, read-only root filesystem, a per-job writable workspace,
  `--network none` by default (package restore through an allow-listed proxy or a pre-populated
  cache), CPU/memory/PID/time/disk limits, no host mounts besides the job workspace, no Factory
  credentials or Docker socket inside.
- **Production (with M13A):** the same image on Azure Container Apps jobs or a dedicated runner pool
  without access to the Factory database, Key Vault or tenant networks.
- **Evidence:** build/test logs, exit codes, image digest and resource usage stored as immutable,
  hashed run evidence attached to the work item.
- **Before M9 starts:** choose the local runtime, confirm the template stack (Angular + ASP.NET Core +
  PostgreSQL) and the DDD layer layout of the starter template.

## M10 — Developer Agent

Status: **M10.1 implemented** (see [Developer Agent](DEVELOPER_AGENT.md)); M10.2 and M10.3 planned. Priority: **P0**. Estimated effort: **3–4 weeks**.

Goal: Implement source-code development based on explicitly approved requirements and architecture.

Tasks:

- Create typed Developer Agent input/output contracts.
- Require authorization against an approved immutable artifact version/hash.
- Implement task planning and scoped repository tools.
- Support safe file search/read/edit operations.
- Prevent path traversal and workspace escapes.
- Add controlled build-feedback correction loops.
- Enforce iteration, tool-call, context and token budgets.
- Associate code changes with requirements and tasks.
- Implement business rules in aggregates and value objects per the mandatory DDD rule.

Acceptance criteria: The Developer Agent can implement the StockFlow Product CRUD feature in
an isolated Git workspace, produce a reviewable diff and associate changes with approved requirements.
The implementation follows the approved bounded contexts and aggregates.

### M10 design (2026-10-16, decided)

Design accepted by the user; model, budget and live-run timing decided (see
[Decisions](DECISIONS.md#m10-developer-agent-model-and-budget-2026-10-16-user-decision)). Builds on M5 review cases, M7/M8 durable runs and budgets, and M9 templates,
sandbox and evidence.

- **Trigger:** explicit only. A Generator starts a *development job* for a review case whose current
  version is **Approved**, naming that version and its SHA-256; a changed or unapproved case is refused.
  No approval-triggered automatic execution.
- **Workspace:** one sandbox per job (M9.2 limits, no network). The worker generates the starter
  application (M9.1) and makes it the first commit of a Git repository inside the sandbox; every agent
  iteration that changes files becomes a commit. Output: the unified diff from the starter commit, the
  final tree id, and the M9 verification report.
- **Agent loop (provider-neutral):** no shell and no function-calling dependency. Each iteration is one
  Structured Outputs call returning bounded actions: `list`, `read`, `search`, `write` (whole file),
  `delete`, `build`, `test`, `finish`. The worker executes them through fixed commands in the sandbox;
  paths are validated (relative, inside the repository, no `.git`, size limits). Build/test feedback
  (bounded output) goes into the next iteration.
- **Budgets:** at most 12 iterations, 40 actions, 20,000 input / 4,000 output tokens per call, and a
  per-job cost cap enforced through the M8.3 reservations before every call. Exceeding any bound ends
  the job as failed with evidence; nothing is retried silently.
- **Traceability and DDD:** every `write`/`delete` names the requirement ids it serves (validated
  against the approved artifact) and the job records them per file. The template's architecture tests
  run in every build; business rules belong in aggregates and value objects (prompt rule plus tests).
- **Result:** the job succeeds only when the final offline verification (restore, build, test, web
  test, web build) passes. The diff, per-file requirement links and the report are stored as immutable,
  hashed evidence and shown in the Product Workspace for human review. GitHub branches and pull
  requests remain M11.
- **Model:** `gpt-5.4-mini` for the Developer role (user decision; Chat Completions with Structured
  Outputs; verified price USD 0.75 input / 4.50 output per 1M tokens, standard tier, 2026-10-16).
- **Budget:** USD 0.40 per development job and USD 2 in total for M10 live runs (user decision).
  Worst case per job 240,000 in + 48,000 out ≈ USD 0.40; reasoning tokens are billed as output, so real
  jobs may stop at the cap. Model quality for this task is not measured yet.
- **Slices:** M10.1 contracts, path-safe workspace actions and Git in the sandbox (no model);
  M10.2 agent loop with budgets and traceability (scripted fake model in tests, then a small live run);
  M10.3 development jobs, evidence, API and Product Workspace diff view; StockFlow Product CRUD as the
  acceptance run.

## M11 — Verification Agent & GitHub Pull Request

Status: planned. Priority: **P0**. Estimated effort: **2–3 weeks**.

Goal: Produce evidence-backed Pull Requests rather than relying on AI completion claims.

Tasks:

- Create deterministic .NET and Angular verification pipelines.
- Run build, tests, static checks and dependency/security scanning.
- Run deterministic DDD architecture checks (for example dependency-direction tests) and fail on violations.
- Record exit codes, commit SHA and test-run evidence.
- Distinguish verified results from agent-reported results.
- Analyze failures and generate corrective tasks.
- Integrate least-privileged GitHub branch/PR creation.
- Respect branch protection and human merge approval.
- Attach requirement traceability and verification evidence to PRs.

Acceptance criteria: Factory can produce a GitHub PR for StockFlow with traceable code changes
and independently recorded build/test results.

## M12 — Angular Product Workspace

Status: **M12a and M12b slices implemented** (sign-in, projects, runs, blocking decision popup, live
updates; requirements/architecture proposal review and approval; see [Product Workspace](PRODUCT_WORKSPACE.md));
the rest is planned. Priority: **P1**. Estimated effort: **3–4 weeks**.

Goal: Provide a customer-facing Angular web portal.

Tasks:

- Establish Angular UI, routing and API integration.
- Integrate secure OIDC authentication.
- Add project management and new-project wizard.
- Display requirements and architecture proposals.
- Provide human approval UI.
- Add roadmap, milestones, backlog and task views.
- Display workflow history and agent activity.
- Display model/token costs and budgets.
- Show pending customer model decisions as in-app popups backed by a durable pending-decision
  list (see Customer-provided models under M8).
- Display PR links, verification results and downloadable artifacts.

Acceptance criteria: A user can initiate a project, track its workflow, review/approve outputs
and inspect generated PR evidence through the web application. A minimal portal slice may be
implemented earlier, but the full Product Workspace belongs to M12.

## Deployment and delivery extension

All milestones below are planned/future work, not implemented features. Estimates are preliminary,
not validated commitments. TBD means that no duration estimate has been supplied.

| Milestone | Scope | Priority | Preliminary estimate | Required outcome |
| --- | --- | --- | --- | --- |
| M13A | Terraform Deployment Engine and Azure staging deployment | P1 | 2–4 weeks, provisional; see qualification below | An engine independent of Terraform details, a versioned/tested Azure module set, and verified Azure staging deployment through both BYOC and Factory-managed hosting paths. |
| M13B | AWS deployment provider | P2 | TBD | Versioned/tested AWS modules and provider-specific verification for both ownership models, reusing the engine's authorization, approval and evidence boundaries. |
| M13C | Google Cloud deployment provider | P2 | TBD | Versioned/tested Google Cloud modules and provider-specific verification for both ownership models, with documented capability differences. |
| M14 | Azure-only StockFlow end-to-end pilot | P1 | 2–3 weeks | Exercise the approved Factory workflow through an Azure deployment, human approval, health verification, release evidence and a documented recovery exercise. AWS/GCP support is not a prerequisite. |
| M15 | SaaS beta hardening | P2 | 3–5 weeks | Document verified tenant isolation, credential security, operational recovery and access controls before handling real customer data in a limited commercial beta; detailed scope below. |
| M16 | Desktop application generation, packaging and delivery | P2 | TBD | Generate Electron-based desktop applications (see [decision](DECISIONS.md#desktop-application-technology-2026-10-15-future-m16-direction)) and deliver versioned desktop packages with installation/verification evidence and documented recovery procedures, separately from cloud application deployment and source-code artifact delivery. |

The M13A **2–4 week estimate originated from the earlier Azure deployment milestone**. It is
provisional and **subject to re-estimation for Terraform/BYOC**, not a validated estimate for
the expanded scope. Terraform is a future architectural preference, not a current implementation.

Provider delivery order is Azure, then AWS, then Google Cloud. M14 depends on the relevant
existing Milestones 6–12 and the M13A Azure path; it must not be gated on completing M13B or
M13C. The pilot's Azure ownership model and workload-specific acceptance criteria must be
selected during pilot planning; this document does not extend StockFlow's supplied Product CRUD scope.
User decision of 2026-10-16: the pilot uses the user's own Azure subscription in West Europe with a
USD 5/month budget (see [Decisions](DECISIONS.md#azure-for-the-m14-pilot-2026-10-16-user-decision));
the account had no subscription yet on that date.

## M15 — SaaS Beta Hardening

Status: planned. Priority: **P2**. Estimated effort: **3–5 weeks**, preliminary and not a commitment.

Tasks:

1. Tenant isolation and cross-tenant security verification.
2. Production OIDC/TLS/MFA and secret rotation.
3. Rate limits, abuse controls, quotas and usage consistency.
4. Backup/restore, RPO/RTO and disaster recovery tests.
5. Supply-chain and isolated-runner security hardening.
6. Customer data retention, deletion and export policies.
7. Observability, alerting, support operations and SLOs.
8. Limited customer beta with explicit go/no-go criteria.

Acceptance criteria: Before handling real customer data in a limited commercial beta, tenant
isolation, credential security, operational recovery and access controls must have documented
verification evidence. Recording this requirement does not assert beta readiness or authorize
onboarding customers. M8's planned isolation controls remain prerequisites to verify and harden,
not evidence that M15 has already been completed.

## Dependency rules

- M6 builds on M5B.2.
- M7 builds on M6.
- M8 builds on M7.
- M9 requires the relevant foundations from M7 and M8.
- M10 depends on M9.
- M11 depends on M10.
- M12 can be developed incrementally alongside backend milestones, but full acceptance depends
  on the necessary APIs. An earlier minimal portal slice does not complete M12.
- M13A uses Terraform for Azure staging deployment; M13B and M13C extend deployment to AWS and GCP.
- M14 is the Azure-only end-to-end StockFlow pilot; AWS/GCP deployment is not a prerequisite.
- M15 focuses on SaaS beta hardening.
- M16 covers desktop application generation, packaging and delivery, using Electron (user decision, not implemented).

## Common deployment acceptance requirements

- Use provider-specific, versioned, tested Terraform modules with pinned execution inputs.
  Keep the engine's deployment lifecycle independent of Terraform CLI, module and state formats.
- Isolate remote state by tenant and deployment target, with access control, locking and
  protected recovery. Use least-privilege, short-lived cloud identities for both hosting models.
- Bind human approval to an immutable plan identity, exact application release and target.
  Verify that binding and current authorization before apply; a changed or stale plan requires
  a new plan and approval. Existing review approval alone is insufficient.
- Execute infrastructure operations only in isolated workers with constrained identities,
  tooling and network access; never permit unrestricted agent-issued infrastructure commands.
- Retain safe deployment evidence, health-check results and release history. Test failure paths
  and document provider-specific rollback/recovery, including state and data recovery limits.
  Do not claim automatic infrastructure undo or cross-provider zero downtime.
- Verify tenant isolation, wrong-target denial, stale/tampered-plan rejection, approval checks,
  concurrent operation handling and credential-safe diagnostics before enabling a provider.

These are future acceptance criteria, not executed test results. Provider services, module
versions, regions, runtime choices, costs and operational support commitments require separate
design and verification. See [Product Vision](PRODUCT_VISION.md) and
[Architectural Decisions](DECISIONS.md#multi-cloud-deployment--byoc-future-architecture).
