using Factory.Core.Analysis;
using Factory.Core.Architecture;

namespace Factory.Core.Agents;

public sealed record AgentResult(
    bool Success,
    string Output,
    string? Error = null,
    AnalysisResult? Analysis = null,
    AgentFailureKind? FailureKind = null,
    ArchitectureProposal? Architecture = null);

public enum AgentFailureKind
{
    InvalidRequest,
    InvalidOutput,
    Refused,
    IncompleteResponse,
    EmptyResponse
}
