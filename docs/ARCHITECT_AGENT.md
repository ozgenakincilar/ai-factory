# Milestone 3: Software Architect Agent

## Scope and contracts

ArchitectAgent implements the existing IAgent and depends only on IModelProvider. Neither
interface signature changed. No new package, orchestrator, database, queue, execution service,
or approval subsystem was introduced.

Two optional DTO fields support typed handoff without embedding JSON into Context:

- AgentRequest.Analysis: the Analyst AnalysisResult to revalidate and consume.
- AgentResult.Architecture: the validated ArchitectureProposal returned on success.

Existing request constructors and Analyst payloads remain usable. Responses also include a
nullable architecture field. Output contains canonical JSON for the typed result. Success
means generation and validation succeeded, never human approval. Proposal status is restricted
to `proposed`.

The new endpoint is `POST /api/agents/architect`. Supply projectId, task, context, analysis,
and the original confirmedRequirements evidence if any Analyst requirements are confirmed.
Both agent endpoints share the existing error handler. The unkeyed IAgent registration still
resolves AnalystAgent; the Architect endpoint injects ArchitectAgent directly.

See the [complete live request](evidence/architect-live-request.json). Its analysis was copied
from the [actual Milestone 2 response](evidence/analyst-live-response.json).

## Models and business rules

Strongly typed models reside in Factory.Core/Architecture:

- ArchitectureProposal: summary, proposed status, confirmed constraint requirement IDs,
  components, technology decisions, risks, and ADRs.
- Component: ID, name, responsibility, and design basis.
- TechnologyDecision: ID, technology, rationale, alternatives, trade-offs, and design basis.
- ArchitectureRisk: ID, description, suggested mitigation, and design basis.
- ADR: ID, technology decision ID, context, and consequences. It inherits the referenced
  decision's provenance, alternatives, trade-offs, and proposed status.
- RequirementTrace: requirement ID and its original RequirementStatus.
- DesignBasis and TechnologyAlternative support explicit provenance and comparisons.

Input is revalidated using AnalysisValidator and must contain at least one functional requirement.
Confirmed descriptions must exactly match caller-supplied evidence. A strict JSON round-trip
snapshots input collections before generation so the prompt and validator use the same evidence.
Invalid or missing input returns 400 without calling the provider.

Every component, technology decision, and risk must reference applicable requirements or set
isDesignRecommendation to true. Any unconfirmed reference or assumption also requires this flag.
Traces must preserve the original requirement status; unknown IDs and status promotion fail.

ConfirmedConstraintRequirementIds contains only IDs of validated confirmed input requirements,
never model-written constraint prose. A technology chosen to support a confirmed requirement is
still only proposed. Assumption references must match input assumption descriptions exactly and
remain unverified. Invented assumption references fail; new ideas must be recommendations or risks.

Additional rules:

- At least one component and technology decision, nonempty text, globally unique entity IDs.
- Component mappings cover every supplied requirement, including tentative ones.
- Every technology decision has a distinct alternative with benefits and drawbacks, nonempty
  trade-offs, and exactly one linked ADR. No model-controlled significance flag bypasses this rule.
- No null elements, unknown or duplicate references, invalid statuses, or missing ADRs.

## Schema, errors, and authority

ArchitectureJson reuses the Analyst's strict System.Text.Json settings and ModelOutputSchema.
OpenAiModelProvider is unchanged and sends json_schema with strict=true. The schema uses $defs,
requires every property, and disallows extra properties on every object. Local typed parsing
and business validation run before success; no repair, downgrade, or self-approval occurs.

Refusal maps to 422. Empty, incomplete, malformed, or business-invalid results map to 502 with
no proposal. SDK errors use the existing safe 502 handler. Cancellation propagates to the
existing 499 handler. Validation errors contain only static rule codes, not request/model text.

The Architect Agent has no command execution, file, or approval tools. Generated text is data,
never executed or persisted by the agent. Evidence files were saved by the verification workflow.

## Executed verification (2026-10-13)

```powershell
dotnet build -p:OutputPath=bin/Milestone3/net10.0/
dotnet test -p:OutputPath=bin/Milestone3/net10.0/ --no-restore
```

- Final build: **0 warnings, 0 errors**.
- Final tests: **123 passed, 0 failed, 0 skipped**. The previous 71 cases are retained.
- Coverage includes invalid inputs, serialization/schema correspondence, unknown references,
  status promotion, unsupported assumptions, unapproved constraints, missing alternatives,
  trade-offs, ADRs and coverage, refusals, truncation, empty/malformed responses, API failures,
  agent cancellation, and HTTP behavior for both endpoints.
- An SDK transport test checks the exact Architect schema and strict=true on the serialized request.
- WebApplicationFactory tests replace IModelProvider, use synthetic credentials, and make no
  live OpenAI requests. No new dependency was needed for this milestone.

### Live verification and failed attempt

Started the existing Development HTTP profile with existing credentials, overriding only the
listening URL to http://127.0.0.1:5168. Configured model: gpt-4o-mini.

Two real POST requests were made:

1. The initial attempt returned **502**. The caller script retained only HTTP status, not the
   error body or model output. Inspecting server output yielded no additional diagnostic.
   Its exact cause remains **unresolved** and cannot definitively be attributed to OpenAI,
   parsing, or business validation. No failed proposal was accepted.
2. After adding static business-validation diagnostic codes, the same request returned **200**,
   success=true, error=null, and a typed proposed architecture. Neither schema nor validation
   rules were relaxed. The agent does not retry automatically.

The successful response has two components, one technology decision, two alternatives, two
risks, and one ADR. Both referenced Analyst requirements remain suggested, all design bases
are recommendations, and confirmedConstraintRequirementIds is empty.

- [Saved request](evidence/architect-live-request.json)
- [Actual successful response](evidence/architect-live-response.json)

The captured-response test deserializes the saved response into the actual C# models, validates
against the saved request, checks proposed status and zero confirmed constraints, and compares
canonical serialization with output. This is offline verification of real evidence, not another
live call. The verification API process was stopped afterward.

## Limitations and unverified claims

The live success establishes schema acceptance for this model/account and this response only.
It does not establish reliability, completeness, technical correctness, or performance. The
model's browser-storage choice, capacity figures, and performance claims are **unverified design
recommendations**, not approved requirements or measured facts. Human review remains necessary.

ID/status checks cannot prove that free-text rationale follows a requirement or detect every
unsupported assumption embedded in prose. Matching an assumption description does not verify
it. Confirmation evidence remains caller-asserted, not authenticated human approval. Analysis
origin and project ownership are not cryptographically verified; no persisted approval workflow
was added.

Real refusals, outages, rate limits, and truncation were not deliberately induced against
OpenAI; tests simulate them. Endpoint cancellation is not integration-tested, although agent
cancellation propagation is tested. The first live 502 remains unexplained.

Official reference: [OpenAI Structured Outputs](https://developers.openai.com/api/docs/guides/structured-outputs?api-mode=chat).
