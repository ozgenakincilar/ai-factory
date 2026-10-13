namespace Factory.Core.Analysis;

public sealed record AnalysisResult(
    string Summary,
    IReadOnlyList<FunctionalRequirement> FunctionalRequirements,
    IReadOnlyList<Assumption> Assumptions,
    IReadOnlyList<Risk> Risks,
    IReadOnlyList<OpenQuestion> OpenQuestions);

public sealed record FunctionalRequirement(
    string Id,
    string Description,
    RequirementStatus Status,
    string? ConfirmedSourceId);

public enum RequirementStatus
{
    Suggested,
    Confirmed,
    UnverifiedAssumption
}

// These collections never confer confirmation on a requirement.
public sealed record Assumption(string Description, string ValidationNeeded);
public sealed record Risk(string Description, string SuggestedMitigation);
public sealed record OpenQuestion(string Question);

// Supplied explicitly by the caller, never extracted by the model from context.
public sealed record ConfirmedRequirement(string Id, string Description);
