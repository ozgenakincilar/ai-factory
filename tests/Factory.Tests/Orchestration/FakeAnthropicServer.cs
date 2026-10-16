using System.Text.Json;
using System.Text.Json.Nodes;
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

// Loopback-only stand-in for the Claude Messages API (POST /v1/messages, non-streaming).
// The structured-output schema is recognized by deep equality with the agents' own schemas,
// which also proves the adapter forwards the schema unchanged.
internal sealed class FakeAnthropicServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private int _messages;
    public string BaseUrl { get; private set; } = "";
    public int Messages => _messages;
    public string? LastApiKey { get; private set; }
    public string? LastVersion { get; private set; }
    public JsonNode? LastBody { get; private set; }
    public int? Status { get; set; }
    public string StopReason { get; set; } = "end_turn";
    public bool NoStructuredOutput { get; set; }
    public string ServedModel { get; set; } = "";

    private FakeAnthropicServer(WebApplication app) => _app = app;

    public static async Task<FakeAnthropicServer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var server = new FakeAnthropicServer(builder.Build());
        server._app.MapPost("/v1/messages", (Func<HttpContext, Task<IResult>>)server.Create);
        await server._app.StartAsync();
        server.BaseUrl = server._app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/');
        return server;
    }

    private async Task<IResult> Create(HttpContext http)
    {
        Interlocked.Increment(ref _messages);
        LastApiKey = http.Request.Headers["x-api-key"].ToString();
        LastVersion = http.Request.Headers["anthropic-version"].ToString();
        var body = LastBody = await JsonNode.ParseAsync(http.Request.Body);
        if (Status is { } status)
            return Results.Json(new { type = "error", error = new { type = status == 400 ? "invalid_request_error" : "api_error", message = "synthetic" } },
                statusCode: status);
        var model = body!["model"]!.GetValue<string>();
        var schema = body["output_config"]?["format"]?["schema"];
        string text;
        if (schema is not null && JsonNode.DeepEquals(schema, JsonNode.Parse(AnalysisJson.OutputSchema.JsonSchema)))
            text = JsonSerializer.Serialize(ReviewSamples.Result.Analysis, AnalysisJson.Options);
        else if (schema is not null && JsonNode.DeepEquals(schema, JsonNode.Parse(ArchitectureJson.OutputSchema.JsonSchema)))
            text = JsonSerializer.Serialize(ReviewSamples.Result.Architecture, ArchitectureJson.Options);
        else text = NoStructuredOutput ? "I cannot produce JSON." : """{"ok":true}""";
        return Results.Json(new
        {
            id = "msg_test", type = "message", role = "assistant",
            model = string.IsNullOrEmpty(ServedModel) ? model : ServedModel,
            content = new object[] { new { type = "text", text } },
            stop_reason = StopReason, stop_sequence = (string?)null,
            usage = new { input_tokens = 11, output_tokens = 7, cache_creation_input_tokens = 2, cache_read_input_tokens = 3 }
        });
    }

    public async ValueTask DisposeAsync() { await _app.StopAsync(); await _app.DisposeAsync(); }
}
