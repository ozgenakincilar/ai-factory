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
| Use existing Development secret configuration | Implemented | Credentials remain outside documentation; malformed key values fail early without echoing them. The security hardening pass removes duplicate secret registration: command line overrides environment, which overrides Development User Secrets and JSON. Invalid winning security values fail closed. |

## Deferred work (not implemented or approved by this document)

- Further approval-driven orchestration. Existing-case human decisions and persistent decision records are implemented in the local 5B.2 candidate; automatic execution remains deferred.
- Implementation, test, review, and pull-request agents.
- Sandboxed command execution, MCP tool integration, GitHub automation.
- Additional LLM providers, persistent workflow coordination, queues, dynamic multi-agent orchestration. Review artifact storage is implemented in 5A.
- Broader logging audit and reliability evaluation. Workflow HTTP cancellation propagation is now integration-tested.
- Further commits and pushes: require explicit user approval. Milestone 4 is committed as b9ce705; Milestone 5A is committed as b3e95f4; Milestone 5B.1 is committed/pushed as 4dbd0de; security hardening is committed as ff24cd5; the 5B.2 candidate remains uncommitted.

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
- The current 5B.2 candidate remains uncommitted and must stop for review after correction verification.
