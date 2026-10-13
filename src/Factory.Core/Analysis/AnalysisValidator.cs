namespace Factory.Core.Analysis;

public static class AnalysisValidator
{
    public static bool HasValidSources(IReadOnlyList<ConfirmedRequirement>? sources)
    {
        if (sources is null) return true;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        return sources.All(source => source is not null &&
            !string.IsNullOrWhiteSpace(source.Id) &&
            !string.IsNullOrWhiteSpace(source.Description) && ids.Add(source.Id));
    }

    public static bool IsValid(AnalysisResult? result, IReadOnlyList<ConfirmedRequirement>? sources)
    {
        if (!HasValidSources(sources) || result is null ||
            string.IsNullOrWhiteSpace(result.Summary) ||
            result.FunctionalRequirements is null || result.Assumptions is null ||
            result.Risks is null || result.OpenQuestions is null)
            return false;

        var confirmed = (sources ?? []).ToDictionary(s => s.Id, s => s.Description, StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var requirement in result.FunctionalRequirements)
        {
            if (requirement is null || string.IsNullOrWhiteSpace(requirement.Id) ||
                string.IsNullOrWhiteSpace(requirement.Description) || !ids.Add(requirement.Id) ||
                !Enum.IsDefined(requirement.Status))
                return false;

            if (requirement.Status == RequirementStatus.Confirmed)
            {
                // Matching an ID alone or quoting unrelated context is insufficient.
                if (requirement.ConfirmedSourceId is null ||
                    !confirmed.TryGetValue(requirement.ConfirmedSourceId, out var text) ||
                    !string.Equals(text, requirement.Description, StringComparison.Ordinal))
                    return false;
            }
            else if (requirement.ConfirmedSourceId is not null)
                return false;
        }

        return result.Assumptions.All(a => a is not null &&
                   !string.IsNullOrWhiteSpace(a.Description) && !string.IsNullOrWhiteSpace(a.ValidationNeeded)) &&
               result.Risks.All(r => r is not null &&
                   !string.IsNullOrWhiteSpace(r.Description) && !string.IsNullOrWhiteSpace(r.SuggestedMitigation)) &&
               result.OpenQuestions.All(q => q is not null && !string.IsNullOrWhiteSpace(q.Question));
    }
}
