using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Factory.Core.Deployment;

namespace Factory.Infrastructure.Deployment;

// M13A.2: after apply, the web app's Key Vault references must resolve (M13A.1 found a deployment that
// was healthy while its connection string reference was denied). Read-only ARM calls with the
// deployment identity's client-credentials token; App Service resolves references lazily, so the
// check retries for a bounded time. AuthorityBase and ManagementBase are replaceable for tests.
public sealed partial class AzureConfigurationChecker(HttpClient http, AzureCredentials credentials, int attempts = 8, TimeSpan? delay = null,
    string authorityBase = "https://login.microsoftonline.com/", string managementBase = "https://management.azure.com/") : IConfigurationChecker
{
    private readonly TimeSpan _delay = delay ?? TimeSpan.FromSeconds(15);

    public async Task<ConfigurationCheck> CheckAsync(DeploymentTarget target, IReadOnlyDictionary<string, string> outputs, CancellationToken ct = default)
    {
        target.Validate();
        if (!outputs.TryGetValue("web_app_name", out var site) || !SiteName().IsMatch(site))
            throw new DeploymentException(DeploymentError.ApplyFailed, "web_app_name_output_invalid");
        var token = await Token(ct);
        var url = $"{managementBase}subscriptions/{target.SubscriptionId}/resourceGroups/{target.ResourceGroup}/providers/Microsoft.Web/sites/{site}" +
            "/config/configreferences/appsettings?api-version=2023-12-01";
        IReadOnlyList<ConfigurationReference> references = [];
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) throw new DeploymentException(DeploymentError.ApplyFailed, "configuration_check_http_" + (int)response.StatusCode);
            references = Parse(await response.Content.ReadAsStringAsync(ct));
            if (references.All(r => r.Status == "Resolved")) return new(true, references);
            if (attempt < attempts) await Task.Delay(_delay, ct);
        }
        return new(false, references);
    }

    private async Task<string> Token(CancellationToken ct)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = credentials.ClientId, ["client_secret"] = credentials.ClientSecret,
            ["scope"] = managementBase + ".default", ["grant_type"] = "client_credentials",
        });
        using var response = await http.PostAsync($"{authorityBase}{Uri.EscapeDataString(credentials.TenantId)}/oauth2/v2.0/token", form, ct);
        if (!response.IsSuccessStatusCode) throw new DeploymentException(DeploymentError.EngineUnavailable, "token_http_" + (int)response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return document.RootElement.TryGetProperty("access_token", out var value) && value.GetString() is { Length: > 0 } token ? token
            : throw new DeploymentException(DeploymentError.EngineUnavailable, "token_missing");
    }

    // Only names and statuses are kept; references (vault URIs) and values never leave this method.
    internal static IReadOnlyList<ConfigurationReference> Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.GetProperty("value").EnumerateArray()
                .Select(r => new ConfigurationReference(r.GetProperty("name").GetString() ?? "", r.GetProperty("properties").GetProperty("status").GetString() ?? ""))
                .OrderBy(r => r.Name, StringComparer.Ordinal).ToList();
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new DeploymentException(DeploymentError.ApplyFailed, "configuration_check_invalid");
        }
    }

    [GeneratedRegex(@"\A[a-z0-9][a-z0-9-]{0,58}[a-z0-9]\z")]
    private static partial Regex SiteName();
}
