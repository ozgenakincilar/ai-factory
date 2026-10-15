# Analyst Structured Outputs

Milestone 3 adds an optional typed `analysis` input and `architecture` output to the shared
agent DTOs. The Analyst behavior below is unchanged. See [Architect Agent](ARCHITECT_AGENT.md)
for the architecture endpoint, current verification counts, and live evidence.

## Contract and ownership

`IModelProvider.GenerateAsync` remains unchanged. `ModelRequest.OutputSchema` is an optional,
provider-neutral schema descriptor. `OpenAiModelProvider` maps it to OpenAI 2.14.0
`ChatResponseFormat.CreateJsonSchemaFormat` with `jsonSchemaIsStrict: true`.
Requests without a schema continue to support plain text. No OpenAI SDK upgrade is required.
HTTP integration tests use Microsoft.AspNetCore.Mvc.Testing 10.0.12 in the test project.

`AnalysisResult`, `FunctionalRequirement`, `Assumption`, `Risk`, and `OpenQuestion` live in
Factory.Core. The JSON schema and strict serialization options are in `AnalysisJson`.
`AnalystAgent` deserializes and validates before returning success.

The response retains `success`, `output`, and `error`, and adds `analysis` and `failureKind`.
**Compatibility change:** successful `output` now contains serialized analysis JSON rather
than Markdown. Consumers should use the typed `analysis` object. Enum values use camelCase strings.

## Confirmation boundary

The caller may explicitly supply `confirmedRequirements`:

```json
{
  "projectId": "sample-project",
  "task": "Analyze task export requirements and identify open questions.",
  "context": "Task-list application. Supported export formats are undecided.",
  "confirmedRequirements": [
    { "id": "USER-1", "description": "Export task lists" }
  ]
}
```

A generated requirement may have status `confirmed` only if `confirmedSourceId` matches
a supplied source ID and its entire description matches that source exactly, using ordinal
comparison. Matching a quote or ID while adding unsupported details fails validation.
Paraphrases are deliberately not accepted as confirmed. Without explicit sources, nothing
can be confirmed, even if task or context text claims approval.

`suggested` and `unverifiedAssumption` requirements must have `confirmedSourceId: null`.
Assumptions require a description and a validation action. Risk mitigations are suggestions;
open questions are unanswered. Summary text is AI synthesis, not approval evidence.

This is a provenance check against caller assertions, **not authentication of a human approval**.
The current API has no approval identity or persistence mechanism. Callers must populate
confirmed sources only from actual user decisions, never from prior model output. Authentication
and a persisted approval workflow remain future work. Schema validation cannot prove facts
or establish the completeness or quality of the analysis.

## Failure behavior

| Condition | Behavior |
| --- | --- |
| Missing project/task or malformed confirmed sources | 400; no provider request |
| Refusal or content filtering | 422; no analysis or raw refusal text returned |
| Truncation, unexpected finish reason, empty output | 502; no analysis accepted |
| Invalid JSON, missing/extra/duplicate properties, invalid enum, invalid provenance | 502; no analysis accepted |
| OpenAI SDK API error | Existing 502 handler with safe diagnostic metadata |
| Caller cancellation | Existing 499 handler |

Failures do not return raw model output or parser exception messages. No silent repair,
automatic promotion to confirmed, or fallback to JSON mode is performed.

The schema uses required properties and `additionalProperties: false` on every object;
the nullable source ID is still required. Local deserialization checks required constructor
parameters, unknown members, duplicate properties, and non-nullable properties. Explicit
validation checks null collection elements because .NET nullable enforcement does not cover them.

## Verification evidence

End-to-end verification executed on 2026-10-13:

```powershell
dotnet build -p:OutputPath=bin/EndToEnd/net10.0/
dotnet test -p:OutputPath=bin/EndToEnd/net10.0/ --no-restore
```

- Final build succeeded with 0 errors and 0 warnings.
- Final tests: 71 passed, 0 failed, 0 skipped (60 existing tests plus 10 HTTP endpoint
  cases and one offline typed validation of the captured live response).
- A separate output directory avoids replacing DLLs used by a running local API.
- SDK transport tests use an in-memory HTTP handler and synthetic credentials. They inspect
  the serialized request schema and exercise response classification and API error propagation.
- WebApplicationFactory exercises the real HTTP request binding, agent, validation, and response
  serialization, replacing only IModelProvider. Cases cover 200, 400, 422, and 502, explicit
  source binding, unsupported confirmation, malformed output, refusal, truncation, empty output,
  and safe handling of SDK exceptions. Tests use a Testing environment and a synthetic key;
  they do not load Development secrets or call OpenAI.
- Initial dependency restore failed with NU1301 because the sandbox blocked NuGet's signature
  endpoint. Restore succeeded with approved network access. The first integration run had
  70 passes and one failure caused by Windows Event Log write permissions. The test host now
  uses console logging; the final rerun above passed. Application logging was not changed.

### Live Development request

Started the existing Development HTTP profile with its existing User Secrets, overriding only
the listening URL to avoid interfering with another local API:

```powershell
dotnet run --project src/Factory.Api --launch-profile http --no-build -p:OutputPath=bin/Verification/net10.0/ -- --urls http://127.0.0.1:5168
```

The startup log confirmed Development, Factory.Api, and model `gpt-4o-mini`. No credentials
were displayed or changed. Sent a real POST to `/api/agents/analyst` for a single-user task-list
prototype with an **empty confirmedRequirements list**. Saved the nonsensitive evidence:

- [Exact request](evidence/analyst-live-request.json)
- [Actual successful HTTP response](evidence/analyst-live-response.json)

Observed response: HTTP **200**, `Content-Type: application/json; charset=utf-8`, `success: true`,
`error: null`, and `failureKind: null`. It contained two functional requirements, both `suggested`
with null source IDs; zero confirmed requirements; two assumptions; two risks; and two open questions.
Storage, notifications, and other undecided features were not promoted to confirmed requirements.

The real call used the strict schema path in OpenAiModelProvider with no JSON-mode fallback.
Its successful response establishes that OpenAI accepted this schema for this configured model
and account on this call. Separate SDK wire tests assert `response_format.type = json_schema`,
`strict = true`, and the exact schema body.

`CapturedLiveResponse_DeserializesAndPreservesUnconfirmedProvenance` loads the saved response into
the actual `AgentResult` / `AnalysisResult` C# types, runs AnalysisValidator against the saved
request, checks every generated requirement is unconfirmed, and verifies that serializing the
typed analysis reproduces `output`. This is an offline check of real captured evidence, not a
second live call. The verification API process was stopped afterward.

### Remaining limitations and assumptions

- One live sample verifies schema acceptance and this response, not all future model outputs,
  all supported models, or semantic accuracy/completeness. No benchmark was performed.
- The model's summary uses present-tense wording about the proposed application; this is not
  evidence that software has been implemented. No implementation claim was verified.
- Positive explicit confirmation and rejection paths were exercised through HTTP tests with
  a deterministic provider, not additional live OpenAI calls.
- Real refusals, API outages, rate limits, and truncation were not induced against OpenAI;
  their handling is covered by synthetic SDK/HTTP tests. The HTTP 499 cancellation path was
  not exercised by an endpoint integration test.
- Caller assertions still are not authenticated human approvals; the existing provenance
  limitations above remain applicable.

## Official references consulted

- [OpenAI Structured Outputs](https://developers.openai.com/api/docs/guides/structured-outputs?api-mode=chat)
- [OpenAI C# structured-format example](https://developers.openai.com/api/docs/guides/migrate-to-responses)
- The installed OpenAI 2.14.0 package README and XML API documentation.
- [.NET nullable enforcement and its limitations](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/nullable-annotations)
- [.NET 10 JSON improvements](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10/libraries)
- [ASP.NET Core 10 integration testing](https://learn.microsoft.com/en-us/aspnet/core/test/integration-tests?view=aspnetcore-10.0)
