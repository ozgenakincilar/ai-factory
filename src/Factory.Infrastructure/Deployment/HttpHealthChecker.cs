using System.Diagnostics;
using System.Text.Json;
using Factory.Core.Deployment;

namespace Factory.Infrastructure.Deployment;

// Healthy means GET <base>/health answers 200 with {"status":"ok"} (the starter template's endpoint).
// A free-tier App Service starts cold, so the check retries for a bounded time.
public sealed class HttpHealthChecker(HttpClient http, int attempts = 20, TimeSpan? delay = null) : IHealthChecker
{
    private readonly TimeSpan _delay = delay ?? TimeSpan.FromSeconds(15);

    public async Task<HealthResult> CheckAsync(string baseUrl, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new DeploymentException(DeploymentError.InvalidTarget, "health_url_invalid");
        var health = new Uri(uri, "/health");
        var clock = Stopwatch.StartNew();
        int? status = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, health);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                using var response = await http.SendAsync(request, timeout.Token);
                status = (int)response.StatusCode;
                if (status == 200 && IsOk(await response.Content.ReadAsStringAsync(timeout.Token)))
                    return new(health.ToString(), true, status, attempt, clock.Elapsed);
            }
            catch (HttpRequestException) { status = null; }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { status = null; }
            if (attempt < attempts) await Task.Delay(_delay, ct);
        }
        return new(health.ToString(), false, status, attempts, clock.Elapsed);
    }

    private static bool IsOk(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("status", out var s) &&
                s.ValueKind == JsonValueKind.String && s.GetString() == "ok";
        }
        catch (JsonException) { return false; }
    }
}
