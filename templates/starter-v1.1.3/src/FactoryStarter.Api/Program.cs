using FactoryStarter.Api;
using FactoryStarter.Application;
using FactoryStarter.Application.Abstractions;
using FactoryStarter.Infrastructure;
using Microsoft.AspNetCore.Diagnostics;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
// Endpoints may be minimal APIs (app.Map...) or [ApiController] classes; both are wired here.
builder.Services.AddControllers();

var app = builder.Build();
// Domain rule violations (DomainException) answer 400 with their code; see ErrorResponses.
app.UseExceptionHandler(errors => errors.Run(async context =>
{
    var (status, body) = ErrorResponses.For(context.Features.Get<IExceptionHandlerFeature>()?.Error);
    context.Response.StatusCode = status;
    await context.Response.WriteAsJsonAsync(body);
}));
if (app.Configuration.GetValue<bool>("Database:InitializeSchema")) await app.Services.InitializeDatabaseAsync();

// The Angular build is served from wwwroot (the release package puts it there); /api stays the API.
app.UseDefaultFiles();
app.UseStaticFiles();

// Liveness: the process answers. Readiness: the database answers too.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/health/ready", async (IDatabaseHealth database, CancellationToken cancellationToken) =>
    await database.CanConnectAsync(cancellationToken)
        ? Results.Ok(new { status = "ok", database = "ok" })
        : Results.Json(new { status = "unavailable", database = "unreachable" }, statusCode: StatusCodes.Status503ServiceUnavailable));

app.MapControllers();

// Client-side routes of the web app. Paths with a file extension (scripts, styles) and unknown /api paths
// never fall back to index.html: the static file middleware skips requests that already matched an endpoint.
app.MapFallbackToFile("{*path:regex(^(?!api/).*$):nonfile}", "index.html");

app.Run();

public partial class Program;
