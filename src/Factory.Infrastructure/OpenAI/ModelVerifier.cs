using System.ClientModel;
using System.Text.Json;
using Factory.Core.Models;
using Factory.Core.Tenancy;

namespace Factory.Infrastructure.OpenAI;

// Capability registration test: one tiny strict-schema call on the customer's own credential.
// Reachable with valid structured output => Verified/true; reachable but without it => Verified/false;
// authentication, network or protocol failure => Failed. The result informs; it never blocks a choice.
public sealed class ModelVerifier(IModelProviderFactory factory)
{
    private static readonly ModelOutputSchema Schema = new("verification", """
        {"type":"object","properties":{"ok":{"type":"boolean"}},"required":["ok"],"additionalProperties":false}
        """);

    public async Task<(ModelVerificationStatus Status, bool? StructuredOutput)> VerifyAsync(ModelEndpoint endpoint, CancellationToken ct)
    {
        try
        {
            var response = await factory.Create(endpoint).GenerateAsync(new ModelRequest(
                "Return only JSON matching the supplied schema.", "Set ok to true.", Schema, ModelRegistryLimits.VerificationMaxOutputTokens), ct);
            if (response.Status != ModelResponseStatus.Completed) return (ModelVerificationStatus.Verified, false);
            try
            {
                using var json = JsonDocument.Parse(response.Content);
                var root = json.RootElement;
                var structured = root.ValueKind == JsonValueKind.Object && root.EnumerateObject().Count() == 1
                    && root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;
                return (ModelVerificationStatus.Verified, structured);
            }
            catch (JsonException) { return (ModelVerificationStatus.Verified, false); }
        }
        catch (Exception ex) when (ex is ClientResultException { Status: 400 } or global::Anthropic.Exceptions.AnthropicBadRequestException)
        {
            // Reachable and authenticated, but the endpoint rejects strict structured output.
            return (ModelVerificationStatus.Verified, false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return (ModelVerificationStatus.Failed, null); }
    }
}
