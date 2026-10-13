using Factory.Core.Analysis;

namespace Factory.Core.Agents;

public sealed record AgentRequest(
    string ProjectId,
    string Task,
    string Context,
    IReadOnlyList<ConfirmedRequirement>? ConfirmedRequirements = null,
    AnalysisResult? Analysis = null);
