namespace Factory.Api;

// The synchronous generation routes (ephemeral agent/workflow endpoints and M6 lifecycle POSTs)
// are neither metered nor budgeted, so they are retired: disabled by default in every environment,
// answering 410 Gone without calling a model. Durable runs (POST /api/projects/{id}/runs) replace
// them. They can be re-enabled only in Development or Testing, for regression tests; any other
// environment refuses to start with them enabled.
public static class LegacyGeneration
{
    public const string Setting = "Generation:EnableLegacyEndpoints";

    public static bool Enabled(IConfiguration configuration, IHostEnvironment environment)
    {
        var enabled = configuration.GetValue<bool>(Setting);
        if (enabled && !(environment.IsDevelopment() || environment.IsEnvironment("Testing")))
            throw new InvalidOperationException("legacy_generation_endpoints_forbidden_outside_development");
        return enabled;
    }

    public static IResult Retired() => Results.Problem(statusCode: StatusCodes.Status410Gone, title: "generation_route_retired",
        detail: "Submit durable runs with POST /api/projects/{projectId}/runs.");
}
