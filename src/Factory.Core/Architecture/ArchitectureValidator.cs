using Factory.Core.Agents;
using Factory.Core.Analysis;

namespace Factory.Core.Architecture;

public static class ArchitectureValidator
{
    public static bool IsValidInput(AgentRequest request) =>
        Text(request.ProjectId) && Text(request.Task) &&
        AnalysisValidator.IsValid(request.Analysis, request.ConfirmedRequirements) &&
        request.Analysis!.FunctionalRequirements.Count > 0;

    public static bool IsValid(ArchitectureProposal? proposal, AgentRequest request) => GetError(proposal, request) is null;

    // Static diagnostic codes only: never include model text or caller data in errors.
    public static string? GetError(ArchitectureProposal? proposal, AgentRequest request)
    {
        if (!IsValidInput(request) || proposal is null || !Text(proposal.Summary) ||
            proposal.Status != ProposalStatus.Proposed ||
            proposal.Components is not { Count: > 0 } ||
            proposal.TechnologyDecisions is not { Count: > 0 } ||
            proposal.Risks is null || proposal.Adrs is null ||
            !UniqueText(proposal.ConfirmedConstraintRequirementIds)) return "invalid_structure_or_status";

        var requirements = request.Analysis!.FunctionalRequirements.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var assumptions = request.Analysis.Assumptions.Select(a => a.Description).ToHashSet(StringComparer.Ordinal);
        if (proposal.ConfirmedConstraintRequirementIds.Any(id =>
                !requirements.TryGetValue(id, out var r) || r.Status != RequirementStatus.Confirmed)) return "unconfirmed_constraint";

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var covered = new HashSet<string>(StringComparer.Ordinal);
        bool ValidBasis(DesignBasis? basis)
        {
            if (basis is null || basis.Requirements is null || !UniqueText(basis.AssumptionDescriptions) ||
                basis.AssumptionDescriptions.Any(a => !assumptions.Contains(a))) return false;
            var refs = new HashSet<string>(StringComparer.Ordinal);
            var needsRecommendation = basis.Requirements.Count == 0 || basis.AssumptionDescriptions.Count > 0;
            foreach (var trace in basis.Requirements)
            {
                if (trace is null || !Text(trace.RequirementId) || !refs.Add(trace.RequirementId) ||
                    !requirements.TryGetValue(trace.RequirementId, out var requirement) ||
                    trace.Status != requirement.Status) return false;
                needsRecommendation |= trace.Status != RequirementStatus.Confirmed;
            }
            return !needsRecommendation || basis.IsDesignRecommendation;
        }

        foreach (var component in proposal.Components)
        {
            if (component is null || !Text(component.Id) || !ids.Add(component.Id) ||
                !Text(component.Name) || !Text(component.Responsibility) || !ValidBasis(component.Basis)) return "invalid_component_or_basis";
            covered.UnionWith(component.Basis.Requirements.Select(r => r.RequirementId));
        }
        // No supplied requirement can disappear from the component mapping.
        if (!covered.SetEquals(requirements.Keys)) return "missing_requirement_coverage";

        var decisions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var decision in proposal.TechnologyDecisions)
        {
            if (decision is null || !Text(decision.Id) || !ids.Add(decision.Id) ||
                !Text(decision.Technology) || !Text(decision.Rationale) || !ValidBasis(decision.Basis) ||
                decision.Alternatives is not { Count: > 0 } ||
                !UniqueText(decision.TradeOffs) || decision.TradeOffs.Count == 0) return "invalid_technology_decision_or_basis";
            var technologies = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { decision.Technology.Trim() };
            foreach (var alternative in decision.Alternatives)
                if (alternative is null || !Text(alternative.Technology) ||
                    !technologies.Add(alternative.Technology.Trim()) ||
                    !Text(alternative.Benefits) || !Text(alternative.Drawbacks)) return "invalid_or_duplicate_alternative";
            decisions.Add(decision.Id);
        }
        foreach (var risk in proposal.Risks)
            if (risk is null || !Text(risk.Id) || !ids.Add(risk.Id) ||
                !Text(risk.Description) || !Text(risk.Mitigation) || !ValidBasis(risk.Basis)) return "invalid_risk_or_basis";

        var documented = new HashSet<string>(StringComparer.Ordinal);
        foreach (var adr in proposal.Adrs)
            if (adr is null || !Text(adr.Id) || !ids.Add(adr.Id) ||
                !Text(adr.TechnologyDecisionId) || !decisions.Contains(adr.TechnologyDecisionId) ||
                !documented.Add(adr.TechnologyDecisionId) || !Text(adr.Context) || !Text(adr.Consequences)) return "invalid_or_duplicate_adr";
        return documented.SetEquals(decisions) ? null : "missing_adr";
    }

    private static bool Text(string? value) => !string.IsNullOrWhiteSpace(value);
    private static bool UniqueText(IReadOnlyList<string>? values) =>
        values is not null && values.All(Text) && values.Distinct(StringComparer.Ordinal).Count() == values.Count;
}
