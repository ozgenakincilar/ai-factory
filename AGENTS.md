# Universal AI Factory — Agent Instructions

## Project Mission

Build a reusable, project-agnostic AI Software Factory using .NET 10.

The factory will use AI agents to analyze requirements, design software, implement code, run tests, review changes, and prepare pull requests.

StockFlow is the first planned end-to-end pilot, on Azure in M14. It is not an implemented Factory dependency.

## Current Architecture

- Factory.Api — ASP.NET Core Minimal API
- Factory.Core — Contracts and domain models
- Factory.Orchestration — Agent implementations
- Factory.Infrastructure — LLM provider integrations, PostgreSQL stores, template catalog and Docker sandbox
- Factory.Worker — Durable workflow worker process (M7.1)
- Factory.Web — Angular Product Workspace (M12a/M12b slices)
- Factory.Tests — Automated tests

## Development Rules

Follow the [AI Engineering Accuracy Protocol](docs/AI_ENGINEERING_ACCURACY_PROTOCOL.md).
Before continuing development, read [Current State](docs/CURRENT_STATE.md) and
[Architectural Decisions](docs/DECISIONS.md). These include the fresh-session handoff,
executed verification, unresolved issues, and the committed/pushed Milestone 5B.2 baseline.

1. Inspect existing code before proposing changes.
2. Never claim tests passed unless they were executed successfully.
3. Distinguish verified facts from assumptions.
4. Consult current official documentation for SDK and API questions.
5. Never expose API keys or secrets.
6. Follow SOLID principles without introducing unnecessary abstractions.
   Domain-Driven Design is a mandatory rule for the Factory and for every application it generates;
   deviations require explicit user approval. See [Architectural Decisions](docs/DECISIONS.md#domain-driven-design-2026-10-15-user-direction).
7. Prefer small, testable changes.
8. Run `dotnet build` and `dotnet test` after changes.
9. Do not commit, push, merge, or delete files without approval.
10. Explain architectural decisions and trade-offs.

## Current Status

- .NET 10 solution created.
- AnalystAgent implemented.
- OpenAI ChatClient integrated.
- Local API health endpoint working.
- Credential validation rejects malformed API keys without exposing them.
- Structured Outputs implementation and executed verification evidence are documented in
  [Structured Outputs](docs/STRUCTURED_OUTPUTS.md). A real Development request with strict schema
  returned HTTP 200 on 2026-10-13; typed response and unconfirmed provenance checks passed.
- Software Architect Agent produces traceable proposals using the existing strict schema provider.
  See [Milestone 3 evidence and limitations](docs/ARCHITECT_AGENT.md), including an initial unexplained
  live 502 followed by a successful 200 response. All architecture output remains proposed.
- Milestone 4 sequential Analyst → Architect workflow is implemented with IDs, snapshots, explicit review status, safe partial results, and deterministic/live verification. See [Workflow](docs/WORKFLOW.md).
- Historical full M5B.2 verification: build 0 warnings/errors; 624 tests passed, 0 failed, 0 skipped, with PostgreSQL and live Keycloak. The later documentation-only run reported 332 passed, 0 failed, 36 skipped without those integrations configured; it does not replace full-suite evidence. Evidence and remaining limits: see [Human Decisions](docs/HUMAN_DECISIONS.md). Milestone 5B.2 adds authenticated transactional decisions on existing cases. The old workflow endpoint remains ephemeral; M6 (committed as 1b93fc4) adds explicit authenticated persistent generation routes.

- Milestone 5A supplies immutable review artifacts. Milestone 5B.2 connects a separate decision store to three review endpoints. See [Review Foundation](docs/REVIEW_FOUNDATION.md) and [Human Decisions](docs/HUMAN_DECISIONS.md).

## Immediate Priority

Analyst and Architect Structured Outputs each have a verified successful live sample;
the first Architect attempt returned 502 with unresolved cause. Milestone 4 had an initial unexplained 500 followed by a verified live 200. Milestone 4 is committed as b9ce705. Milestone 5A is committed as b3e95f4. Milestone 5B.1 authentication/authorization is committed and pushed as 4dbd0dec8721cea0d95047430c1ba2e927abe60b. Security hardening is committed as ff24cd5a13a99aa02138ac4fbd29f40baf1091ac. Milestone 5B.2 is committed and pushed as e1bced7230d89352ddf6e69aeae4bee71fd106bf; pushed status was confirmed by the user's independent GitHub inspection. M6 was explicitly approved and is committed as 1b93fc4e0f82e942d82be1ba1372d496961eb0cb on master; `git ls-remote` showed origin master at 1b93fc4 on 2026-10-15, and the user confirmed the commit/push was authorized. Historical pre-correction verification: build 0 warnings/errors; 669 passed, 0 failed, 0 skipped, with PostgreSQL and live Keycloak; older cases used a separate M5B.2 database. The targeted correction has a full run of 700 passed, 0 failed, 0 skipped, including upgraded-database compatibility and four live Keycloak cases; build 0 warnings/errors. That run preceded the final change to scripts/Initialize-FactoryLifecycle.ps1. On 2026-10-15 an approved run executed the committed initializer (normal and -BaselineOnly) on new isolated databases followed by the full suite: build 0 warnings/errors, 700 passed, 0 failed, 0 skipped, including four live Keycloak cases (commit aa0d346, SDK 10.0.103). No remote CI result is recorded. Older regression cases still use a separate M5B.2 database. See [Product Lifecycle](docs/PRODUCT_LIFECYCLE.md#evidence-levels-after-commit) for evidence levels and limitations. M7 (Durable Workflow Orchestration) design was approved on 2026-10-15; M7.1 is committed and pushed as bcd50fa (734 passed, plus a live worker crash/restart check). M7.2 (human-decision waiting, cancellation) is committed and pushed as 9cee79c (745 passed); parallel steps moved to M8. M7.3 (real-time run change notifications via PostgreSQL NOTIFY and SignalR) is committed and pushed as 73f83ab (754 passed). See [Durable Workflows](docs/DURABLE_WORKFLOWS.md). The M8 plan was approved on 2026-10-15; M8.1 usage metering is committed and pushed as 7da963f (763 passed; one live metered call); OpenAI is the first and Anthropic (demo, separate API key required) the second Factory provider (see [Cost Controls](docs/COST_CONTROLS.md)). M8.2 tenancy (organizations, Owner/Admin roles, immutable project ownership; decisions require an organization Owner/Admin) is committed and pushed as 4c7789d. M8.3 budgets and pre-call reservations (budget or price refusals become customer decisions) is committed and pushed as 2564a85. The unmetered synchronous generation routes (ephemeral agent/workflow endpoints, M6 lifecycle POSTs) are retired by user decision: 410 Gone by default, re-enable only in Development/Testing (781 passed). Durable runs are the only production generation path, including durable regeneration of existing work items (786 passed). M8.4a customer-provided OpenAI-compatible models (encrypted credentials, registry, verification, explicit risk acceptance, SSRF-guarded egress, no silent fallback) is committed and pushed as c7eb11d. M8.4b Anthropic provider (official SDK, Generation:Provider selection, customer Anthropic credentials) is committed and pushed as 8d0f3d6; no live Anthropic generation call yet. M8.5a model routing policy (tiers, deterministic rules, per-attempt route evidence) is committed and pushed as fe83818. M8.5b/c request decomposition (Planner, blocking plan approval, parallel parts on routed tiers, merge into the unchanged artifact format) is committed and pushed as 2b29be2; a Planner fix from live evidence followed and one live decomposed run succeeded (881 passed). All M8 phases are implemented. M12a, a first Product Workspace slice (src/Factory.Web, Angular 22), is committed and pushed as 8d30e71. M12b (review page) is committed and pushed as a93f1bd; see docs/PRODUCT_WORKSPACE.md. M9 runs on Docker Desktop + WSL 2 (approved, installed, smoke-tested); the user confirmed the Angular + ASP.NET Core + PostgreSQL stack and the Domain/Application/Infrastructure/Api layout. M9.1 (starter template in templates/, registry, deterministic generation) is committed and pushed as 9bbdec7. M9.2 (runner/Dockerfile, scripts/Build-FactoryRunner.ps1, DockerSandbox) is committed and pushed as d97e9c5. M9.3 (verification jobs in orchestration, immutable hashed evidence, worker -Verification, /api/projects/{id}/verifications, Product Workspace panel and page) is verified with 937 passed, 19 web tests, a browser end-to-end run and a live crash recovery; Git workspaces are deferred to M10. M9.3 is committed and pushed as b95bf13. M10 (Developer Agent) design, model gpt-5.4-mini and budget (USD 0.40/job, USD 2 total) are user decisions; M10.1 (DevelopmentWorkspace, DockerWorkspaceSession, GitSnapshot) is committed and pushed as 80f3759. M10.2 (DeveloperAgent loop, CostGuard, scripts/Test-DeveloperAgentLive.ps1 with a USD 2 ledger) is verified with 977 passed and one paid gpt-5.4-mini run (Verified, USD 0.009); see docs/DEVELOPER_AGENT.md. See docs/TEMPLATES_AND_EXECUTION.md. Future commits, pushes and milestone implementation require explicit user authorization. Only explicitly submitted durable runs publish generation results; no approval-triggered execution or Developer Agent is implemented. See [Roadmap](docs/ROADMAP.md) and [Product Vision](docs/PRODUCT_VISION.md) for planned capabilities.
Preserve IModelProvider and the distinction between confirmed requirements, suggestions, and assumptions.
Keep verification evidence and remaining limitations current as development continues.
Do not refactor unrelated components.

## Long-term Architecture

The factory must support multiple projects, different technology stacks, interchangeable LLM providers, MCP tools, isolated code execution, GitHub integration and human approval gates.
