using Factory.Core.Agents;
using Factory.Core.Authorization;
using Factory.Core.Workflows;
using Microsoft.AspNetCore.Authorization;

namespace Factory.Api.Security;

public sealed record ProjectResource(string ProjectId, CancellationToken CancellationToken);
public sealed record ProjectRequirement(ProjectRole Role) : IAuthorizationRequirement;

public sealed class ProjectAuthorizationHandler(IProjectAccessStore store, FactoryAuthSettings settings)
    : AuthorizationHandler<ProjectRequirement, ProjectResource>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context,
        ProjectRequirement requirement, ProjectResource resource)
    {
        var identity = settings.Identity(context.User);
        if (identity is null || string.IsNullOrWhiteSpace(resource.ProjectId) || resource.ProjectId.Length > 200 ||
            (requirement.Role == ProjectRole.Reviewer && identity.Kind != PrincipalKind.Human)) return;
        if (await store.HasAccessAsync(identity, resource.ProjectId, requirement.Role, resource.CancellationToken))
            context.Succeed(requirement);
    }
}

public sealed class ProjectGenerationFilter(IAuthorizationService authorization, FactoryAuthSettings settings,
    ILogger<ProjectGenerationFilter> logger) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (settings.LocalHttp && (http.Connection.RemoteIpAddress is not { } address || !System.Net.IPAddress.IsLoopback(address)))
            return Results.Forbid();
        var project = context.Arguments.OfType<AgentRequest>().FirstOrDefault()?.ProjectId
            ?? context.Arguments.OfType<WorkflowRequest>().FirstOrDefault()?.ProjectId;
        if (string.IsNullOrWhiteSpace(project) || project.Length > 200)
            return Results.BadRequest(new { error = "invalid_project_id" });
        try
        {
            var result = await authorization.AuthorizeAsync(http.User,
                new ProjectResource(project, http.RequestAborted), new ProjectRequirement(ProjectRole.Generator));
            if (!result.Succeeded) return Results.Forbid();
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            return Results.StatusCode(499);
        }
        catch (Exception)
        {
            logger.LogWarning("Project authorization unavailable; request {TraceId}", http.TraceIdentifier);
            return Results.Problem(statusCode: 503, title: "authorization_unavailable");
        }
        return await next(context);
    }
}
