using System.Text.Json;
using Factory.Core.Analysis;
using Factory.Core.Architecture;
using Factory.Tests.Reviews;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Factory.Tests.Orchestration;

// Loopback-only stand-in for a customer's OpenAI-compatible endpoint (chat completions only).
internal sealed class FakeOpenAiServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private int _requests, _completions;
    public string BaseUrl { get; private set; } = "";
    public int Requests => _requests;
    public int Completions => _completions;
    public string? LastAuthorization { get; private set; }
    public bool Fail { get; set; }
    public bool NoStructuredOutput { get; set; }

    private FakeOpenAiServer(WebApplication app) => _app = app;

    public static async Task<FakeOpenAiServer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var server = new FakeOpenAiServer(builder.Build());
        server._app.Use(async (context, next) => { Interlocked.Increment(ref server._requests); await next(context); });
        server._app.MapGet("/v1/models", () => Results.Json(new { data = Array.Empty<object>() }));
        // A Func (not RequestDelegate) so the returned IResult is written to the response.
        server._app.MapPost("/v1/chat/completions", (Func<HttpContext, Task<IResult>>)server.Complete);
        await server._app.StartAsync();
        var address = server._app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        server.BaseUrl = address.TrimEnd('/') + "/v1";
        return server;
    }

    private async Task<IResult> Complete(HttpContext http)
    {
        Interlocked.Increment(ref _completions);
        LastAuthorization = http.Request.Headers.Authorization.ToString();
        if (Fail) return Results.StatusCode(500);
        using var body = await JsonDocument.ParseAsync(http.Request.Body);
        var model = body.RootElement.GetProperty("model").GetString();
        var schema = body.RootElement.TryGetProperty("response_format", out var format) ? format.GetProperty("json_schema").GetProperty("name").GetString() : null;
        var content = schema switch
        {
            "analysis_result" => JsonSerializer.Serialize(ReviewSamples.Result.Analysis, AnalysisJson.Options),
            "architecture_proposal" => JsonSerializer.Serialize(ReviewSamples.Result.Architecture, ArchitectureJson.Options),
            _ => NoStructuredOutput ? "I cannot produce JSON." : """{"ok":true}"""
        };
        return Results.Json(new
        {
            id = "chatcmpl-test", @object = "chat.completion", created = 1_700_000_000, model,
            choices = new[] { new { index = 0, message = new { role = "assistant", content, refusal = (string?)null }, finish_reason = "stop" } },
            usage = new { prompt_tokens = 11, completion_tokens = 7, total_tokens = 18 }
        });
    }

    public async ValueTask DisposeAsync() { await _app.StopAsync(); await _app.DisposeAsync(); }
}
