using Factory.Core.Authorization;
using Factory.Core.Orchestration;

namespace Factory.Core.Tenancy;

// Tenancy bounded context. Organizations own projects; ownership never moves. Organization
// authority is human only. Organizations, roles and ownership are operator-managed in M8.2.
public enum OrganizationRole { Owner, Admin }

public sealed record ProjectUsageTotal(string ProjectId, UsageTotals Totals);
public sealed record OrganizationUsage(string OrganizationId, DateTimeOffset From, DateTimeOffset To, UsageTotals Totals,
    IReadOnlyList<ProjectUsageTotal> Projects, IReadOnlyList<ModelUsageTotal> Models);

public interface ITenantUsageStore
{
    // Active Owner/Admin of the organization only; half-open interval [from, to), at most 366 days.
    Task<OrganizationUsage> GetOrganizationUsageAsync(TrustedIdentity actor, string organizationId, DateTimeOffset from, DateTimeOffset to,
        CancellationToken ct = default);
    // Current organization and project budgets of the organization; Owner/Admin only.
    // Factory-level provider caps are operator data and are not shown to tenants.
    Task<IReadOnlyList<BudgetStatus>> GetBudgetsAsync(TrustedIdentity actor, string organizationId, CancellationToken ct = default);
}
