namespace Factory.Core.Orchestration;

// Model capability tiers. Operators map each tier to a concrete provider/model; a deployment with
// one configured model serves every tier with it (recorded as such on every route).
public enum ModelTier { Economy, Standard, Strong }
public enum TaskComplexity { Low, Medium, High }
public enum TaskImportance { Normal, High }

// Evidence for one attempt: the tier the policy chose, the static reason code and the policy version.
// Provider/Model is what was actually called (null without a configured identity).
public sealed record ModelRoute(ModelTier Tier, string Reason, string PolicyVersion);
public sealed record StepRoute(int Ordinal, int Attempt, ModelTier Tier, string Reason, string PolicyVersion, string? Provider, string? Model);

// Domain service: deterministic, versioned routing rules (no model calls, no tenant data).
// The chosen tier is the highest of: the role minimum, the complexity tier and the importance tier,
// so importance overrides low complexity (a short financial rule is still high risk) and a cheaper
// tier never undercuts a role's quality floor. The reason names the rule that set the tier.
public static class ModelRoutingPolicy
{
    public const string Version = "routing-v1";
    public const string RoleMinimum = "role_minimum";
    public const string HighImportance = "high_importance";
    public const string Complexity = "complexity";
    public const string CustomerModel = "customer_model";

    // Architecture decisions bound every later artifact, so the Architect never runs below Strong.
    // Steps without model calls (plan approval, publication) have no route.
    public static ModelTier MinimumTier(StepKind kind) => kind switch
    {
        StepKind.Analysis => ModelTier.Standard,
        StepKind.Architecture => ModelTier.Strong,
        // The plan bounds every part, so planning runs Strong; parts are routed by classification.
        StepKind.Planning => ModelTier.Strong,
        StepKind.Part => ModelTier.Economy,
        _ => throw new OrchestrationException(OrchestrationError.InvalidCommand)
    };

    public static ModelRoute Route(StepKind kind, TaskComplexity complexity = TaskComplexity.Medium, TaskImportance importance = TaskImportance.Normal)
    {
        if (!Enum.IsDefined(complexity) || !Enum.IsDefined(importance)) throw new OrchestrationException(OrchestrationError.InvalidCommand);
        var role = MinimumTier(kind);
        var byComplexity = complexity switch { TaskComplexity.Low => ModelTier.Economy, TaskComplexity.Medium => ModelTier.Standard, _ => ModelTier.Strong };
        var byImportance = importance == TaskImportance.High ? ModelTier.Strong : ModelTier.Economy;
        var tier = (ModelTier)Math.Max((int)role, Math.Max((int)byComplexity, (int)byImportance));
        var reason = tier == byImportance && importance == TaskImportance.High && tier > role ? HighImportance
            : tier == byComplexity && tier > role ? Complexity
            : RoleMinimum;
        return new ModelRoute(tier, reason, Version);
    }
}
