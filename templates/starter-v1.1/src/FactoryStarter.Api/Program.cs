using FactoryStarter.Application;
using FactoryStarter.Application.Abstractions;
using FactoryStarter.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

var app = builder.Build();
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

// Client-side routes of the web app; unknown /api paths stay 404.
app.MapFallbackToFile("{*path:regex(^(?!api/).*$)}", "index.html");

app.Run();

public partial class Program;
