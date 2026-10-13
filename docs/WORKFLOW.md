# Milestone 4: sequential Analyst to Architect workflow

Implemented on 2026-10-13 with user approval. The workflow is synchronous, in memory,
project-agnostic, and uses the existing agents and strict schemas. No new packages,
provider interface changes, application-level retries, database, queues, or generic engine.

## HTTP contract

`POST /api/workflows/analysis-architecture`

```json
{
  "projectId": "example",
  "task": "Analyze and propose a small task-list application.",
  "context": "No technology stack has been selected.",
  "confirmedRequirements": []
}
```

The request has no analysis input. It always invokes Analyst first, then passes its
validated typed analysis to Architect. Existing standalone routes remain unchanged.
`ProjectId` and `Task` must be nonblank; `Context` can be empty but cannot be null.
Confirmation evidence must have unique IDs and nonempty descriptions.

Every execution generates a new GUID, returned as `workflowId` and `X-Workflow-Id`.
Repeated identical requests are independent executions, not idempotent operations.
No caller-selected ID is accepted. Framework JSON binding errors occur before workflow
execution and therefore have no workflow result or ID.

Response fields: workflowId, status, analystStatus, architectStatus, analysis,
architecture, failedStage, failureKind, error, success. Results contain typed accepted
artifacts without duplicating them as canonical JSON strings.

## Status and approval semantics

| Field/value | Meaning |
| --- | --- |
| status: awaitingHumanReview | Both generation stages succeeded; human review is still required. success is true only here. |
| status: failed | Input validation, generation, or execution failed; success is false. |
| status: cancelled | Caller cancellation was observed; success is false. |
| stage: completed | That agent returned validated output. This never means approved. |
| stage: notStarted | That agent was skipped or execution has not reached it. |
| stage: failed | The stage failed without accepted output. |
| stage: cancelled | Cancellation was observed at that stage/boundary. |

`Completed` is deliberately a stage status, not an alternative overall success status:
a successful proposal-producing workflow always ends `AwaitingHumanReview`. There is
no persisted pending job, resume endpoint, approval operation, or human pause between agents.
`failedStage` identifies the stopping point, including cancellation; `input` means no agent ran.
Cancellation at the Architect boundary can mark that stage cancelled before its provider call.

Suggestions remain suggested, assumption-backed requirements remain unverified, and all
architecture remains proposed. Confirmation can only come from explicit caller evidence,
not task/context prose. Caller evidence is not authenticated human approval.

## Snapshot and handoff

Before validation or the first await, the workflow copies project ID, task, context, and
the evidence collection into its own AgentRequest. Strings and ConfirmedRequirement
records are immutable; copying the collection detaches its mutable storage. It never
reads the original request again. Mutating/replacing the caller's evidence or replacing
its request after execution starts cannot affect the running workflow. Callers must not
mutate a collection concurrently with the initial synchronous copy itself.

Analyst deserializes fresh typed output; Architect receives that analysis with unchanged
IDs, statuses, source IDs, assumptions, context, and evidence, and revalidates/snapshots it.
No output text is parsed back into confirmation evidence or used as executable instructions.
Each execution keeps local progress; concurrent calls share no workflow state.

## Failure behavior and logs

| Condition | HTTP / accepted artifacts |
| --- | --- |
| Both stages succeed | 200, analysis and proposed architecture |
| Invalid caller input | 400, neither artifact |
| Analyst fails | 422 for refusal, otherwise 502; Architect skipped |
| Valid analysis with zero requirements | 422, no_requirements_for_architecture; analysis retained, Architect not started |
| Architect fails | 422 for refusal, otherwise 502; validated analysis retained |
| SDK ClientResultException | 502; progress retained |
| Other unexpected exception | 500; progress retained |
| Observed caller cancellation | 499, cancelled; previously validated analysis retained |

Cancellation is checked before execution, between agents, and after Architect returns;
the same token reaches both agents/providers. A disconnected client may receive no body.
Unrelated OperationCanceledException is an unexpected failure, not caller cancellation.
No failed proposal, raw provider body, parser message, or exception message is returned.

Orchestration wraps unexpected exceptions with safe partial progress and preserves the
original exception for classification only at the API boundary. This keeps OpenAI types
out of Core and Orchestration. The API logs workflow ID, status, stage, static diagnostics,
exception type, and (for SDK errors) HTTP status and allowlisted request ID. It does not
log workflow inputs, output payloads, or exception objects. Broader preexisting standalone
endpoint logging has not been audited; its generic handler still logs exception objects.

These diagnostics support correlation and failure classification; they do not guarantee
root-cause identification for arbitrary unexpected exceptions. Stack traces, nested exception
details, and raw payloads are deliberately absent. Standalone agent routes have no workflow
ID and do not log failed AgentResult validation details; callers must retain their safe error
responses. The historical workflow 500 and standalone Architect 502 remain unexplained.

## Verification

See CURRENT_STATE.md for final executed counts and live evidence. Deterministic tests
cover typed sequencing, exact evidence/context handoff, mutation during Analyst execution,
concurrent project separation, failure short-circuiting, retained partial results, unique IDs,
HTTP status/header/body/log correlation, and cancellation before/during/between stages.
HTTP cancellation verifies provider cancellation and correlated server 499 diagnostics;
it does not claim a disconnected client receives the response body.

## Official references

- [Minimal API binding and request cancellation](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis/parameter-binding?view=aspnetcore-10.0)
- [Guid.NewGuid](https://learn.microsoft.com/en-us/dotnet/api/system.guid.newguid?view=net-10.0)

These inform framework use; executed repository tests establish the behavior verified here.
