# Current state — fresh-session handoff

Last inspected and verified: **2026-10-13**, Europe/Istanbul.

## Start here

Read [AGENTS.md](../AGENTS.md), [Accuracy Protocol](AI_ENGINEERING_ACCURACY_PROTOCOL.md),
this document, and [DECISIONS.md](DECISIONS.md) before editing. Actual source and executed
verification are authoritative. Preserve uncommitted Milestone 4 work. No second commit,
push, branch rename, history rewrite, deletion, or deployment is authorized.

## Git baseline

The user approved and this session created exactly one local root commit on `master`:
`c8c3330fe7e8a4d15f73208a52d3fdd24250c534`

Message: `Initial baseline: Universal AI Factory milestones 1–3`.

The baseline contains 49 reviewed text files, including .gitignore and four historical
JSON evidence files. Staged file manifest, binary/artifact checks, sensitive-pattern checks,
and whitespace checks passed. Evidence was read and contains nonsensitive task-list examples.
Configuration contains no API key; launch settings use localhost, and the UserSecretsId is
configuration metadata, not a credential. Pattern checks are not a comprehensive security audit.
Build artifacts, IDE state, test output, logs, local secrets, and private keys are ignored.
Git was clean immediately after the baseline. Milestone 4 source, tests, docs, and three
new evidence files remain uncommitted and unstaged. No push or history rewrite occurred.
Git index writes required approved sandbox escalation; a per-command safe.directory exception
was used for this workspace, without changing global Git configuration.

## Implemented architecture

Five net10.0 projects: Factory.Api (Minimal API/DI/HTTP handling), Factory.Core (contracts,
schemas, validators), Factory.Orchestration (AnalystAgent, ArchitectAgent, and sequential
AnalysisArchitectureWorkflow), Factory.Infrastructure (OpenAI provider), Factory.Tests.
Orchestration references Core only. OpenAI 2.14.0 remains confined to Infrastructure/API
usage; package versions and IAgent/IModelProvider signatures are unchanged.

| Milestone | Status and evidence |
| --- | --- |
| 1 | Foundation, Analyst, ChatClient integration, safe credential-format validation |
| 2 | Strict typed analysis and provenance validation; historical live 200, see STRUCTURED_OUTPUTS.md |
| 3 | Traceable proposed architecture; historical live 502 then 200, first cause unresolved, see ARCHITECT_AGENT.md |
| 4 | Sequential workflow, per-execution IDs, explicit statuses, snapshots, cancellation and safe partial progress; see WORKFLOW.md |

RiftMind remains an intended sample application, not a dependency or implemented product.
No implementation/test/review agent, code execution, MCP integration, GitHub automation,
database, queue, generic workflow engine, authentication, or approval ledger was added.

## API and workflow

- GET /health: local application health, not provider availability.
- POST /api/agents/analyst and /api/agents/architect: existing contracts/behavior retained.
- POST /api/workflows/analysis-architecture: WorkflowRequest to WorkflowResult.

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
exception type, and allowlisted provider request IDs. Standalone unexpected-error logging
still logs exception objects and has not undergone a broader redaction audit.

## Executed verification

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
dotnet run --project src/Factory.Api --launch-profile http --no-build -p:OutputPath=bin/Milestone4Verification/net10.0/ -- --urls http://127.0.0.1:5168
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
- No persistent workflow status, resume, idempotency, authenticated ownership, or approval exists.
- Snapshotting isolates mutations after the initial synchronous copy, not concurrent writes
  during that copy. Treat input collections as stable while submitting a request.
- Existing Class1 files, empty template test, and stale weatherforecast HTTP example remain;
  unrelated cleanup was deliberately excluded.
- User approval is required before a second commit or push. Preserve all new/untracked files.

See [workflow design](WORKFLOW.md), [decisions](DECISIONS.md),
[Analyst evidence](STRUCTURED_OUTPUTS.md), and [Architect evidence](ARCHITECT_AGENT.md).
