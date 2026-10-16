using System.Text.Json;
using System.Text.Json.Serialization;
using Factory.Core.Agents;
using Factory.Core.Analysis;
using Factory.Core.Architecture;
using Factory.Core.Models;
using Factory.Core.Orchestration;

namespace Factory.Core.Decomposition;

// A Planner proposal: the request's functional requirements split into parts, each classified for
// routing. It is a proposal until a human approves it; nothing executes before approval.
public sealed record DecompositionPlan(string Summary, IReadOnlyList<PlanPart> Parts)
{
    public const int MaxParts = 12;
    public static string Key(int index) => "P" + (index + 1);
    // Index of a valid key ("P1" → 0); callers validate the plan first.
    public static int Index(string key) => int.Parse(key.AsSpan(1), System.Globalization.CultureInfo.InvariantCulture) - 1;
}

// Keys are P1..Pn in order; a part may depend only on earlier parts, so plans are acyclic by construction.
public sealed record PlanPart(string Key, string Title, string Scope, IReadOnlyList<string> RequirementIds,
    TaskComplexity Complexity, TaskImportance Importance, IReadOnlyList<string> DependsOn);

public static class DecompositionJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true,
            RespectNullableAnnotations = true,
            AllowDuplicateProperties = false
        };
        options.Converters.Add(new JsonStringEnumConverter<TaskComplexity>(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.Converters.Add(new JsonStringEnumConverter<TaskImportance>(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    public static ModelOutputSchema OutputSchema { get; } = new("decomposition_plan", """
        {
          "type": "object",
          "properties": {
            "summary": { "type": "string" },
            "parts": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "key": { "type": "string" },
                  "title": { "type": "string" },
                  "scope": { "type": "string" },
                  "requirementIds": { "type": "array", "items": { "type": "string" } },
                  "complexity": { "type": "string", "enum": ["low", "medium", "high"] },
                  "importance": { "type": "string", "enum": ["normal", "high"] },
                  "dependsOn": { "type": "array", "items": { "type": "string" } }
                },
                "required": ["key", "title", "scope", "requirementIds", "complexity", "importance", "dependsOn"],
                "additionalProperties": false
              }
            }
          },
          "required": ["summary", "parts"],
          "additionalProperties": false
        }
        """);
}

public static class DecompositionPlanValidator
{
    public const int MaxTitle = 200, MaxText = 2000;

    public static bool IsValid(DecompositionPlan? plan, AnalysisResult? analysis) => GetError(plan, analysis) is null;

    // Static diagnostic codes only: never include model text in errors.
    public static string? GetStructureError(DecompositionPlan? plan)
    {
        if (plan is null || !Text(plan.Summary, MaxText) || plan.Parts is not { Count: > 0 and <= DecompositionPlan.MaxParts }) return "invalid_plan_structure";
        for (var i = 0; i < plan.Parts.Count; i++)
        {
            var part = plan.Parts[i];
            if (part is null || part.Key != DecompositionPlan.Key(i) || !Text(part.Title, MaxTitle) || !Text(part.Scope, MaxText) ||
                !Enum.IsDefined(part.Complexity) || !Enum.IsDefined(part.Importance) ||
                part.RequirementIds is not { Count: > 0 } || !Unique(part.RequirementIds) || part.DependsOn is null || !Unique(part.DependsOn))
                return "invalid_part";
            // Dependencies name earlier parts only: no cycles, no self-dependency.
            for (var j = 0; j < part.DependsOn.Count; j++)
                if (!Enumerable.Range(0, i).Any(k => DecompositionPlan.Key(k) == part.DependsOn[j])) return "invalid_dependency";
        }
        return null;
    }

    public static string? GetError(DecompositionPlan? plan, AnalysisResult? analysis)
    {
        if (GetStructureError(plan) is { } structure) return structure;
        if (analysis?.FunctionalRequirements is not { Count: > 0 }) return "no_requirements";
        var known = analysis.FunctionalRequirements.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        if (plan!.Parts.SelectMany(p => p.RequirementIds).Any(id => !known.Contains(id))) return "unknown_requirement";
        // Every requirement belongs to at least one part; none can disappear from the design.
        return known.SetEquals(plan.Parts.SelectMany(p => p.RequirementIds)) ? null : "missing_requirement_coverage";
    }

    private static bool Text(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max;
    private static bool Unique(IReadOnlyList<string> values) =>
        values.All(v => Text(v, MaxTitle)) && values.Distinct(StringComparer.Ordinal).Count() == values.Count;
}

// Builds the Architect input for one part: the same project and analysis evidence, restricted to the
// part's requirements, with the part's scope and its dependencies' proposal summaries appended.
public static class PartScope
{
    public const int MaxDependencyNote = 1000;

    public static AgentRequest Request(AgentRequest request, PlanPart part, IEnumerable<(PlanPart Part, ArchitectureProposal Proposal)> dependencies)
    {
        var analysis = request.Analysis ?? throw new OrchestrationException(OrchestrationError.Integrity);
        var ids = part.RequirementIds.ToHashSet(StringComparer.Ordinal);
        var scoped = analysis with { FunctionalRequirements = analysis.FunctionalRequirements.Where(r => ids.Contains(r.Id)).ToArray() };
        var notes = dependencies.Select(d =>
            $"Dependency {d.Part.Key} ({d.Part.Title}) proposed: {Truncate(d.Proposal.Summary)} Components: {string.Join(", ", d.Proposal.Components.Select(c => c.Name))}.");
        var context = string.Join("\n", new[] { request.Context }.Concat(notes).Where(s => !string.IsNullOrWhiteSpace(s)));
        return request with
        {
            Task = $"{request.Task}\n\nDesign only part {part.Key} - {part.Title}: {part.Scope}",
            Context = context,
            Analysis = scoped
        };
    }

    private static string Truncate(string value) => value.Length <= MaxDependencyNote ? value : value[..MaxDependencyNote];
}

// Merges validated part proposals into one proposal for the whole request. Ids are prefixed with the
// part key so they stay globally unique; every part's provenance is kept unchanged. The caller
// validates the result against the full analysis before it is checkpointed.
public static class ProposalMerger
{
    public static ArchitectureProposal Merge(DecompositionPlan plan, IReadOnlyList<ArchitectureProposal> proposals)
    {
        if (DecompositionPlanValidator.GetStructureError(plan) is not null || proposals.Count != plan.Parts.Count)
            throw new OrchestrationException(OrchestrationError.Integrity);
        var parts = plan.Parts.Zip(proposals).ToList();
        string Id(PlanPart part, string id) => $"{part.Key}.{id}";
        return new ArchitectureProposal(
            string.Join("\n", parts.Select(p => $"{p.First.Key} {p.First.Title}: {p.Second.Summary}")),
            ProposalStatus.Proposed,
            parts.SelectMany(p => p.Second.ConfirmedConstraintRequirementIds).Distinct(StringComparer.Ordinal).ToArray(),
            parts.SelectMany(p => p.Second.Components.Select(c => c with { Id = Id(p.First, c.Id) })).ToArray(),
            parts.SelectMany(p => p.Second.TechnologyDecisions.Select(d => d with { Id = Id(p.First, d.Id) })).ToArray(),
            parts.SelectMany(p => p.Second.Risks.Select(r => r with { Id = Id(p.First, r.Id) })).ToArray(),
            parts.SelectMany(p => p.Second.Adrs.Select(a => a with { Id = Id(p.First, a.Id), TechnologyDecisionId = Id(p.First, a.TechnologyDecisionId) })).ToArray());
    }
}
