using System.Text.Json;
using System.Text.Json.Serialization;
using Factory.Core.Analysis;
using Factory.Core.Architecture;
using Factory.Core.Execution;
using Factory.Core.Models;
using Factory.Core.Templates;

namespace Factory.Core.Development;

// M10.2: the Developer Agent loop. The task carries an approved analysis and architecture; every edit
// must serve one of its functional requirements.
public sealed record DevelopmentTask(string Task, AnalysisResult Analysis, ArchitectureProposal Architecture)
{
    public IReadOnlySet<string> RequirementIds => Analysis.FunctionalRequirements.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
}

public sealed record DevelopmentLimits(int MaxTurns, int MaxActionsPerTurn, int MaxActions, int MaxOutputTokens, int MaxPromptChars,
    int MaxObservationChars, long CostCapMicros)
{
    // USD 0.40 per job (user decision of 2026-10-16).
    public static DevelopmentLimits Default { get; } = new(12, 8, 40, 12_000, 120_000, 24_000, 400_000);
}

public enum DeveloperActionKind { List, Read, Search, Write, Delete, Build, Test, Finish }

public sealed record DeveloperAction(DeveloperActionKind Kind, string? Path, string? Content, string? Text,
    IReadOnlyList<string> RequirementIds, string? Summary);

public sealed record DeveloperTurn(string Notes, IReadOnlyList<DeveloperAction> Actions);

public enum DevelopmentStatus { Verified, VerificationFailed, NotFinished, BudgetExhausted, ModelFailed, SandboxFailed }

// One model call as observed by the loop (usage is null when the provider reported none).
public sealed record DeveloperCall(int Turn, ModelResponseStatus Status, ModelUsage? Usage, long ReservedMicros, long CostMicros);

public sealed record DevelopmentResult(DevelopmentStatus Status, string? FailureCode, int Turns, int Actions,
    IReadOnlyList<DeveloperCall> Calls, IReadOnlyList<FileChange> Changes, IReadOnlyList<TemplateFile> Files,
    SandboxReport? Verification, long CostMicros, IReadOnlyList<string> Log);

// Ports: the sandbox session the agent builds and tests in (M10.1 DockerWorkspaceSession behind it).
public interface IWorkspaceSession : IAsyncDisposable
{
    Task WriteAsync(string path, string content, CancellationToken ct = default);
    Task DeleteAsync(string path, CancellationToken ct = default);
    Task<StepResult> RunAsync(SandboxStep step, CancellationToken ct = default);
}

public interface IWorkspaceRunner
{
    Task<IWorkspaceSession> StartAsync(IReadOnlyList<TemplateFile> files, CancellationToken ct = default);
}

// Worst-case pricing before every call: a call is made only if its reservation fits under the cap
// together with what was already spent or still reserved. Unknown usage keeps the worst case.
public sealed class CostGuard(long inputMicrosPerMillion, long outputMicrosPerMillion, long capMicros)
{
    public long SpentMicros { get; private set; }

    public long Price(long inputTokens, long outputTokens) =>
        (inputTokens * inputMicrosPerMillion + outputTokens * outputMicrosPerMillion + 999_999) / 1_000_000;

    public bool TryReserve(long inputTokens, long maxOutputTokens, out long reservation)
    {
        reservation = Price(inputTokens, maxOutputTokens);
        if (SpentMicros + reservation > capMicros) return false;
        SpentMicros += reservation;
        return true;
    }

    public long Settle(long reservation, ModelUsage? usage)
    {
        if (usage is null) return reservation;
        var actual = Math.Min(reservation, Price(usage.InputTokens, usage.OutputTokens));
        SpentMicros -= reservation - actual;
        return actual;
    }

    // Conservative: about three UTF-8 bytes per token plus fixed overhead for the schema and roles.
    public static long EstimateInputTokens(string systemPrompt, string userPrompt, string schema) =>
        (System.Text.Encoding.UTF8.GetByteCount(systemPrompt) + System.Text.Encoding.UTF8.GetByteCount(userPrompt) +
         System.Text.Encoding.UTF8.GetByteCount(schema)) / 3 + 1_000;
}

public static class DeveloperJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    // Strict Structured Outputs schema: every property required, nullable where unused.
    public static readonly ModelOutputSchema OutputSchema = new("developer_turn", """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["notes", "actions"],
          "properties": {
            "notes": { "type": "string" },
            "actions": {
              "type": "array",
              "minItems": 1,
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["kind", "path", "content", "text", "requirementIds", "summary"],
                "properties": {
                  "kind": { "type": "string", "enum": ["list", "read", "search", "write", "delete", "build", "test", "finish"] },
                  "path": { "type": ["string", "null"] },
                  "content": { "type": ["string", "null"] },
                  "text": { "type": ["string", "null"] },
                  "requirementIds": { "type": "array", "items": { "type": "string" } },
                  "summary": { "type": ["string", "null"] }
                }
              }
            }
          }
        }
        """);
}
