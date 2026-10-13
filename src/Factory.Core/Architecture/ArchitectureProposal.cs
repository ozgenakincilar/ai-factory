using Factory.Core.Analysis;

namespace Factory.Core.Architecture;

public sealed record ArchitectureProposal(
    string Summary,
    ProposalStatus Status,
    IReadOnlyList<string> ConfirmedConstraintRequirementIds,
    IReadOnlyList<Component> Components,
    IReadOnlyList<TechnologyDecision> TechnologyDecisions,
    IReadOnlyList<ArchitectureRisk> Risks,
    IReadOnlyList<ADR> Adrs);

public enum ProposalStatus { Proposed }

public sealed record RequirementTrace(string RequirementId, RequirementStatus Status);

// A rationale is never approval. Assumptions must reference the input verbatim.
public sealed record DesignBasis(
    bool IsDesignRecommendation,
    IReadOnlyList<RequirementTrace> Requirements,
    IReadOnlyList<string> AssumptionDescriptions);

public sealed record Component(string Id, string Name, string Responsibility, DesignBasis Basis);

public sealed record TechnologyDecision(
    string Id, string Technology, string Rationale,
    IReadOnlyList<TechnologyAlternative> Alternatives,
    IReadOnlyList<string> TradeOffs, DesignBasis Basis);

public sealed record TechnologyAlternative(string Technology, string Benefits, string Drawbacks);

public sealed record ArchitectureRisk(string Id, string Description, string Mitigation, DesignBasis Basis);

// Inherits the referenced decision's provenance, alternatives, and proposed status.
public sealed record ADR(string Id, string TechnologyDecisionId, string Context, string Consequences);
