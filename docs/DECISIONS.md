# Architectural decisions

Recorded from inspected implementation on **2026-10-13**. These describe implemented Factory
choices and their trade-offs, not a new approval or the model-generated sample ADRs. See
[CURRENT_STATE.md](CURRENT_STATE.md) for verification and [Accuracy Protocol](AI_ENGINEERING_ACCURACY_PROTOCOL.md)
for required working rules.

| Decision | Status | Rationale and trade-off |
| --- | --- | --- |
| Keep a project-agnostic .NET 10 factory | Implemented foundation | RiftMind is a sample target, not a dependency. Domain prompts/contracts can serve other projects; other execution stacks remain future work. |
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
| Use existing Development secret configuration | Implemented | Credentials remain outside documentation; malformed key values fail early without echoing them. Development re-adds User Secrets in Program.cs; do not assume environment overrides win without inspecting configuration order. |

## Deferred work (not implemented or approved by this document)

- Authenticated approval gates, persistent approval records, project ownership validation.
- Implementation, test, review, and pull-request agents.
- Sandboxed command execution, MCP tool integration, GitHub automation.
- Additional LLM providers, persistent storage, queues, dynamic multi-agent orchestration.
- Broader logging audit and reliability evaluation. Workflow HTTP cancellation propagation is now integration-tested.
- Further commits and pushes: require explicit user approval. The approved local Milestone 1–3 baseline exists; Milestone 4 is uncommitted.

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
| Local baseline only | Approved root commit c8c3330fe7e8a4d15f73208a52d3fdd24250c534 contains Milestones 1–3 plus reviewed .gitignore. Milestone 4 remains uncommitted; no push or rewrite. |

The first workflow live attempt returned 500 at Analyst with no accepted artifacts. Its cause
is unresolved. A second explicit attempt outside the restricted sandbox succeeded; that is not
proof the first failure was caused by the sandbox. See [CURRENT_STATE.md](CURRENT_STATE.md)
and [WORKFLOW.md](WORKFLOW.md) for executed results, evidence, and limitations.
