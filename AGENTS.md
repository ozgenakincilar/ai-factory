# Universal AI Factory — Agent Instructions

## Project Mission

Build a reusable, project-agnostic AI Software Factory using .NET 10.

The factory will use AI agents to analyze requirements, design software, implement code, run tests, review changes, and prepare pull requests.

StockFlow is the first planned end-to-end pilot, on Azure in M14. RiftMind AI, a League of Legends coaching application, remains a separate intended product. Neither is an implemented Factory dependency.

## Current Architecture

- Factory.Api — ASP.NET Core Minimal API
- Factory.Core — Contracts and domain models
- Factory.Orchestration — Agent implementations
- Factory.Infrastructure — LLM provider integrations
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
- Historical full M5B.2 verification: build 0 warnings/errors; 624 tests passed, 0 failed, 0 skipped, with PostgreSQL and live Keycloak. The later documentation-only run reported 332 passed, 0 failed, 36 skipped without those integrations configured; it does not replace full-suite evidence. Evidence and remaining limits: see [Human Decisions](docs/HUMAN_DECISIONS.md). Milestone 5B.2 adds authenticated transactional decisions on existing cases; generation remains ephemeral.

- Milestone 5A supplies immutable review artifacts. Milestone 5B.2 connects a separate decision store to three review endpoints. See [Review Foundation](docs/REVIEW_FOUNDATION.md) and [Human Decisions](docs/HUMAN_DECISIONS.md).

## Immediate Priority

Analyst and Architect Structured Outputs each have a verified successful live sample;
the first Architect attempt returned 502 with unresolved cause. Milestone 4 had an initial unexplained 500 followed by a verified live 200. Milestone 4 is committed as b9ce705. Milestone 5A is committed as b3e95f4. Milestone 5B.1 authentication/authorization is committed and pushed as 4dbd0dec8721cea0d95047430c1ba2e927abe60b. Security hardening is committed as ff24cd5a13a99aa02138ac4fbd29f40baf1091ac. Milestone 5B.2 is committed and pushed as e1bced7230d89352ddf6e69aeae4bee71fd106bf; pushed status was confirmed by the user's independent GitHub inspection. M6 is the next planned milestone and requires architectural approval before implementation. Future commits, pushes and milestone implementation require explicit user authorization. No automatic submission, Developer Agent or execution queue is implemented. See [Roadmap](docs/ROADMAP.md) and [Product Vision](docs/PRODUCT_VISION.md) for planned capabilities.
Preserve IModelProvider and the distinction between confirmed requirements, suggestions, and assumptions.
Keep verification evidence and remaining limitations current as development continues.
Do not refactor unrelated components.

## Long-term Architecture

The factory must support multiple projects, different technology stacks, interchangeable LLM providers, MCP tools, isolated code execution, GitHub integration and human approval gates.
