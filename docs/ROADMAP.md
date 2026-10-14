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

**M6 is now an approved, implemented local candidate awaiting review; M7–M16 remain planned/future work.**
Estimated durations are preliminary planning ranges, not commitments or validated delivery
forecasts. M6 architectural approval was subsequently supplied explicitly; its implementation and verification are recorded in [Product Lifecycle](PRODUCT_LIFECYCLE.md). No later milestone is authorized.

Milestone 5B.2 is the implemented baseline, not a deployment milestone. Its stored human
decisions do not automatically authorize infrastructure apply or trigger execution.

## M6 — Product Lifecycle & Review Integration

Status: implemented local candidate, awaiting review. Priority: **P0**. Original preliminary estimate: **1–2 weeks**; the corrected database/security design reassessed this to **3–5 weeks**, still a planning range, not a validated commitment.

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

Status: planned. Priority: **P0**. Estimated effort: **2–3 weeks**.

Goal: Make long-running workflows independent from individual HTTP requests and API process lifetimes.

Tasks:

- Define WorkflowRun, WorkflowStep and StepAttempt persistence.
- Implement durable job leasing and checkpoints.
- Support running, waiting-for-approval, failed, cancelled and completed states.
- Add safe retries, timeouts, cancellation and idempotency policies.
- Support worker restart and crash recovery.
- Add status/history endpoints and concurrency tests.

Acceptance criteria: A workflow survives worker restart without unnecessarily repeating completed
steps. External LLM calls must not be falsely described as exactly-once.

## M8 — Tenant Isolation & AI Cost Controls

Status: planned. Priority: **P0**. Estimated effort: **2–3 weeks**.

Goal: Establish organization/project isolation and observable, limited AI spending.

Tasks:

- Define Organization → Project ownership and permissions.
- Enforce tenant-scoped access and data isolation.
- Add cross-tenant negative security tests.
- Record provider, model and input/output token usage.
- Track estimated and actual usage costs separately.
- Add workflow budgets, reservations, limits and quotas.
- Add initial cost-aware routing policies.
- Preserve provider-neutral model interfaces.

Acceptance criteria: Usage and estimated costs are attributable to a specific workflow/project;
tenant isolation and budget policies have automated tests.

## M9 — Application Templates & Secure Execution

Status: planned. Priority: **P0**. Estimated effort: **3–4 weeks**.

Goal: Establish a reproducible Angular + ASP.NET Core + PostgreSQL application template and
isolated build environment.

Tasks:

- Build a versioned starter template.
- Add a minimal template registry.
- Create isolated per-job Git workspaces.
- Add ephemeral build/test runners.
- Enforce CPU, memory, time, disk and network restrictions.
- Isolate credentials and secrets.
- Record immutable verification evidence.
- Test malicious-code and cross-tenant isolation scenarios.

Acceptance criteria: Factory can generate a starter application deterministically and build/test
it in an isolated environment without giving untrusted code access to Factory infrastructure.

## M10 — Developer Agent

Status: planned. Priority: **P0**. Estimated effort: **3–4 weeks**.

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

Acceptance criteria: The Developer Agent can implement the StockFlow Product CRUD feature in
an isolated Git workspace, produce a reviewable diff and associate changes with approved requirements.

## M11 — Verification Agent & GitHub Pull Request

Status: planned. Priority: **P0**. Estimated effort: **2–3 weeks**.

Goal: Produce evidence-backed Pull Requests rather than relying on AI completion claims.

Tasks:

- Create deterministic .NET and Angular verification pipelines.
- Run build, tests, static checks and dependency/security scanning.
- Record exit codes, commit SHA and test-run evidence.
- Distinguish verified results from agent-reported results.
- Analyze failures and generate corrective tasks.
- Integrate least-privileged GitHub branch/PR creation.
- Respect branch protection and human merge approval.
- Attach requirement traceability and verification evidence to PRs.

Acceptance criteria: Factory can produce a GitHub PR for StockFlow with traceable code changes
and independently recorded build/test results.

## M12 — Angular Product Workspace

Status: planned. Priority: **P1**. Estimated effort: **3–4 weeks**.

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
| M16 | Desktop application generation, packaging and delivery | P2 | TBD | Generate desktop applications and deliver versioned desktop packages with installation/verification evidence and documented recovery procedures, separately from cloud application deployment and source-code artifact delivery. |

The M13A **2–4 week estimate originated from the earlier Azure deployment milestone**. It is
provisional and **subject to re-estimation for Terraform/BYOC**, not a validated estimate for
the expanded scope. Terraform is a future architectural preference, not a current implementation.

Provider delivery order is Azure, then AWS, then Google Cloud. M14 depends on the relevant
existing Milestones 6–12 and the M13A Azure path; it must not be gated on completing M13B or
M13C. The pilot's Azure ownership model and workload-specific acceptance criteria must be
selected during pilot planning; this document does not extend StockFlow's supplied Product CRUD scope.

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
- M16 covers desktop application generation, packaging and delivery.

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
