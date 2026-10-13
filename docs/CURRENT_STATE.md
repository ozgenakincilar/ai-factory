# Current state — fresh-session handoff

Last inspected and verified: **2026-10-13**, Europe/Istanbul.

## Start here

Read [AGENTS.md](../AGENTS.md), the [Accuracy Protocol](AI_ENGINEERING_ACCURACY_PROTOCOL.md),
this document, and [DECISIONS.md](DECISIONS.md) before editing. Repository files and executed
verification are authoritative. This handoff does not authorize new features, commits, pushes,
merges, destructive operations, or production deployment.

Handoff checks: all local links in these handoff documents and AGENTS.md resolve. Review and
credential-pattern scanning of the 10 documentation/evidence files identified no credentials
or sensitive data. An initial token-pattern hit was verified to be a substring of a generated
risk ID, not a credential. Pattern scanning is a review aid, not a comprehensive security audit.

**Git caveat:** inspection found no tracked files and no committed HEAD. `git status --short`
shows AGENTS.md, the solution, docs, src, and tests as untracked. Both `git diff` and
`git diff --cached` are empty because nothing is tracked, not because the project is committed
or clean. There is no commit SHA to identify this state. Preserve the working directory.
Do not run clean/reset/delete or create a baseline commit without approval.

## Mission and architecture

Build a reusable, project-agnostic AI Software Factory on .NET 10. The long-term workflow is
requirements analysis, architecture, implementation, testing, review, and preparation of pull
requests with human approval. RiftMind AI (League of Legends coaching) is the first intended
sample application, not a hard-coded factory dependency and not an implemented product here.

Solution: `UniversalAiFactory.sln`. All five projects target net10.0.

| Project | Implemented responsibility / references |
| --- | --- |
| Factory.Api | Minimal API, configuration, DI, shared HTTP error handling; references Core, Orchestration, Infrastructure |
| Factory.Core | Agent/model contracts, Analyst/Architect DTOs, strict schemas, business validators; no OpenAI dependency |
| Factory.Orchestration | AnalystAgent and ArchitectAgent; references Core only |
| Factory.Infrastructure | OpenAiModelProvider and safe credential-format validation; references Core, OpenAI 2.14.0 |
| Factory.Tests | xUnit unit, SDK transport, WebApplicationFactory HTTP, and captured-response tests; references all projects |

API package Microsoft.AspNetCore.OpenApi and test package Microsoft.AspNetCore.Mvc.Testing
are 10.0.12. Other test dependencies include xUnit 2.9.3, runner 3.1.4,
Microsoft.NET.Test.Sdk 17.14.1, and coverlet.collector 6.0.4. See project files for source of truth.

## Milestones and evidence

| Milestone | Implemented status | Evidence and limits |
| --- | --- | --- |
| Foundation / initial Analyst | Solution, health endpoint, OpenAI ChatClient provider, Analyst agent, key-format guard | An earlier malformed credential was detected without printing it. User subsequently reported a successful plain-text call. No credential values belong in the repository. |
| Milestone 2 | Strict Structured Outputs, typed AnalysisResult, provenance validation, failure handling, HTTP tests | Recorded live HTTP 200 using gpt-4o-mini; two suggested requirements, zero confirmed. Historical final suite: 71 passes. See STRUCTURED_OUTPUTS.md and Analyst evidence files. |
| Milestone 3 (current) | Architect agent, typed proposal, traceability/business validation, endpoint, tests | Final suite: 123 passes. First live call returned unexplained 502; second returned 200. Proposal remained proposed, with no confirmed constraints. See ARCHITECT_AGENT.md. |

**Milestone 3 implementation is complete within its documented scope.** This does not mean all
production concerns are solved, every generated claim is accurate, or the first 502 is diagnosed.
This handoff changes documentation only. No later milestone has been specified or implemented.

Evidence files, all nonsensitive task-list examples:

- [Analyst request](evidence/analyst-live-request.json) and [response](evidence/analyst-live-response.json).
- [Architect request](evidence/architect-live-request.json) and [response](evidence/architect-live-response.json).
- Architect input was copied from the actual Analyst response. It had no confirmed sources.
- Captured-response tests validate these files offline; passing them is not a fresh live call.
- The successful Architect response has two components, one technology decision, two alternatives,
  two risks, one ADR, and zero confirmed constraints. All design bases are recommendations.

## Existing interfaces and request flow

`IAgent`: Name and `Task<AgentResult> ExecuteAsync(AgentRequest, CancellationToken)`.
`IModelProvider`: `Task<ModelResponse> GenerateAsync(ModelRequest, CancellationToken)`.
Neither interface signature changed for Milestone 3.

- AgentRequest: ProjectId, Task, Context, optional ConfirmedRequirements, optional Analysis.
- AgentResult: Success, Output, optional Error, Analysis, FailureKind, Architecture.
- ModelRequest: SystemPrompt, UserPrompt, optional ModelOutputSchema(Name, JsonSchema).
- ModelResponse: Content, Model, Status (Completed, Refused, Incomplete, Empty).
- Successful Output is canonical JSON, not Markdown; typed Analysis or Architecture is also returned.
- JSON enum names are camelCase strings. Existing Analyst callers can omit the Architect input.

AnalystAgent requests AnalysisJson.OutputSchema, parses AnalysisResult, and runs AnalysisValidator.
Confirmation requires an explicit source ID plus exact ordinal description match against
caller-supplied ConfirmedRequirements. Context/task text cannot confer confirmation. Nonconfirmed
requirements must have null source IDs. Assumptions, risks, and questions are separate typed lists.

ArchitectAgent revalidates the input analysis and requires at least one functional requirement.
It snapshots the input, requests ArchitectureJson.OutputSchema, parses ArchitectureProposal,
and runs ArchitectureValidator. Components/decisions/risks carry DesignBasis and RequirementTrace.
Referenced statuses must match input; unconfirmed or assumption-backed bases must be recommendations.
Confirmed constraints contain only validated confirmed requirement IDs. Every requirement needs
component coverage; every technology decision needs alternatives, trade-offs, and exactly one ADR.
Output is always proposed, with no approval or execution capability.

OpenAiModelProvider sends ChatResponseFormat.CreateJsonSchemaFormat with strict=true, checks
refusal/finish reason/empty content, and returns provider-neutral statuses. It does not parse domain
models. There is no application-level automatic retry or JSON-mode fallback; SDK behavior is separate.

## API and DI

| Endpoint | Behavior |
| --- | --- |
| GET /health | Local application/environment health; does not prove OpenAI availability |
| POST /api/agents/analyst | AgentRequest to typed Analyst result |
| POST /api/agents/architect | AgentRequest with Analysis/evidence to typed proposed architecture |

Both POST routes share ExecuteAgent in Program.cs. Invalid input: 400; refusal: 422;
invalid/empty/incomplete model output or SDK API exception: 502; caller cancellation: 499;
unexpected exceptions: 500. Invalid model output is not returned as accepted data.

The provider and ChatClient are singletons; agents are transient. Unkeyed IAgent DI still resolves
AnalystAgent. ArchitectAgent is injected directly into its route; there is no multi-agent dispatcher
or automatic Analyst-to-Architect orchestration.

## Current verification and local operation

Re-executed during this handoff, on 2026-10-13:

```powershell
dotnet build -p:OutputPath=bin/Milestone3/net10.0/
dotnet test -p:OutputPath=bin/Milestone3/net10.0/ --no-restore
```

**Build: 0 warnings, 0 errors. Tests: 123 passed, 0 failed, 0 skipped.**
One of the 123 is the existing empty template test in UnitTest1.cs; the count is not a coverage
percentage or a count of independently meaningful scenarios. No new live request was made for
this documentation handoff.

Tests use synthetic credentials and a Testing host; no real API calls are made by dotnet test.
SDK transport tests inspect the serialized strict schemas. HTTP tests replace IModelProvider.
Saved live response tests check typed parsing, canonical output, and provenance.

When an authorized task requires a live run, the previously used command is:

```powershell
dotnet run --project src/Factory.Api --launch-profile http --no-build -p:OutputPath=bin/Milestone3/net10.0/ -- --urls http://127.0.0.1:5168
```

Development loads existing User Secrets. Keys are configured under OpenAI:ApiKey; model under
OpenAI:Model (fallback gpt-4o-mini). Do not print/list secret values, copy them into docs, or change
credentials as part of routine verification. Do not assume a fresh session has network/secret access.
The earlier verification process was stopped; check current port/process state before starting one.
Separate output directories avoid locked DLLs from another running API. Do not stop unrelated apps.

Sandbox restore has previously failed at NuGet's signature endpoint and then succeeded with network
approval. Windows Event Log writes also failed in sandboxed HTTP tests; test hosts use console logging.
If Git rejects ownership, prior read-only inspection used a per-command `-c safe.directory=<workspace>`
exception, not a global Git configuration change. Resolve the actual workspace path in the new session.

## Known issues, risks, and unverified assumptions

- **Unresolved live failure:** the first Architect call returned 502. Its error body and model output
  were not retained. Server inspection added no diagnosis. The second success does not explain it.
  Static validation codes were added without relaxing rules. Do not invent a root cause.
- Caller-confirmed evidence is not authenticated approval. Analysis origin and project ownership are
  not independently verified. There is no authentication, authorization, approval ledger, or database.
- Strict schema/provenance validation cannot prove free-text technical claims, semantic entailment,
  performance, completeness, or suitability. Browser-storage choices in captured output are unverified
  model recommendations, not decisions about the Factory's own architecture.
- Real provider refusals, outages, rate limits, and truncation were not intentionally induced; tests
  simulate them. Agent cancellation is tested; endpoint-level 499 cancellation is not integration-tested.
- Positive confirmed-source architecture flow is tested synthetically; the live sample had suggestions only.
- Core/Orchestration/Infrastructure Class1 files and the empty template test remain. No cleanup was authorized.
- Unexpected-error HTTP handling still logs the exception object; safe SDK handling and validation errors
  are narrower. Broader log-redaction guarantees have not been audited.
- No committed baseline exists. Current files, including evidence used by tests, must survive session changes.

## Next steps — recommendations, not implemented or newly authorized

1. Start the fresh session by reading these documents, inspecting the actual files and Git state,
   and agreeing with the user on the next milestone. Do not assume implementation-agent work is approved.
2. Prioritize reliable diagnosis of any recurring Architect 502: capture safe application error category,
   static validation code, and allowed request ID. Never log raw credentials or unrestricted provider payloads.
3. Consider additional live samples and endpoint cancellation coverage when requested. Keep schema/provenance
   checks strict; do not silently retry until a failure disappears or relabel suggestions as confirmed.
4. Before executing future generated code, design human approval and isolation explicitly with user approval.
   MCP tools, isolated execution, GitHub integration, implementation/review/test agents, persistence, and
   multiple provider implementations are roadmap items, not current functionality.
5. Create a Git baseline only after explicit approval and appropriate review of what would be included.

For decision rationale, see [DECISIONS.md](DECISIONS.md). Detailed milestone reports remain
[STRUCTURED_OUTPUTS.md](STRUCTURED_OUTPUTS.md) and [ARCHITECT_AGENT.md](ARCHITECT_AGENT.md).
