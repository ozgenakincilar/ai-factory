using Factory.Core.Authorization;
using Factory.Core.Orchestration;

namespace Factory.Core.Tenancy;

// Tenancy bounded context. Organizations own projects; ownership never moves. Organization
// authority is human only. Organizations, roles and ownership are operator-managed in M8.2.
public enum OrganizationRole { Owner, Admin }

public sealed record ProjectUsageTotal(string ProjectId, UsageTotals Totals);
public sealed record OrganizationUsage(string OrganizationId, DateTimeOffset From, DateTimeOffset To, UsageTotals Totals,
    IReadOnlyList<ProjectUsageTotal> Projects, IReadOnlyList<ModelUsageTotal> Models);

// A project the caller can read; CanDecide: the caller is an active Human Owner/Admin of its organization.
public sealed record WorkspaceProject(string ProjectId, string? OrganizationId, IReadOnlyList<string> Roles, bool CanDecide);

public interface IWorkspaceDirectory
{
    // Empty for unknown or inactive identities.
    Task<IReadOnlyList<WorkspaceProject>> GetMyProjectsAsync(TrustedIdentity actor, CancellationToken ct = default);
}

public interface ITenantUsageStore
{
    // Active Owner/Admin of the organization only; half-open interval [from, to), at most 366 days.
    Task<OrganizationUsage> GetOrganizationUsageAsync(TrustedIdentity actor, string organizationId, DateTimeOffset from, DateTimeOffset to,
        CancellationToken ct = default);
    // Current organization and project budgets of the organization; Owner/Admin only.
    // Factory-level provider caps are operator data and are not shown to tenants.
    Task<IReadOnlyList<BudgetStatus>> GetBudgetsAsync(TrustedIdentity actor, string organizationId, CancellationToken ct = default);
}
