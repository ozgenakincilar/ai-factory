using System.ClientModel;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Factory.Core.Agents;
using Factory.Core.Architecture;
using Factory.Core.Models;
using Factory.Tests.Architecture;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Factory.Tests.Integration;

public class ArchitectEndpointTests
{
    [Fact]
    public void CapturedLiveProposal_PreservesAnalystProvenanceAndRemainsProposed()
    {
        // Offline verification of real evidence, not a new paid API request.
        var request = JsonSerializer.Deserialize<AgentRequest>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Evidence", "architect-live-request.json")), Options())!;
        var result = JsonSerializer.Deserialize<AgentResult>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Evidence", "architect-live-response.json")), Options())!;
        Assert.True(result.Success);
        Assert.Null(result.Error);
        Assert.Null(result.FailureKind);
        Assert.True(ArchitectureValidator.IsValid(result.Architecture, request));
        var proposal = result.Architecture!;
        Assert.Equal(ProposalStatus.Proposed, proposal.Status);
        Assert.Empty(proposal.ConfirmedConstraintRequirementIds);
        Assert.Equal(result.Output, JsonSerializer.Serialize(proposal, ArchitectureJson.Options));
        var bases = proposal.Components.Select(c => c.Basis)
            .Concat(proposal.TechnologyDecisions.Select(d => d.Basis)).Concat(proposal.Risks.Select(r => r.Basis));
        Assert.All(bases, basis => Assert.True(basis.IsDesignRecommendation));
    }

    [Fact]
    public async Task ValidAnalysis_ReturnsTypedProposalWithPreservedProvenance()
    {
        using var factory = new ArchitectFactory(new ArchitectStub());
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/agents/architect", ArchitectSamples.Request, Options());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<AgentResult>(Options());
        Assert.True(result!.Success);
        Assert.Null(result.Analysis);
        Assert.True(ArchitectureValidator.IsValid(result.Architecture, ArchitectSamples.Request));
        Assert.Equal(ProposalStatus.Proposed, result.Architecture!.Status);
        Assert.Equal(result.Output, JsonSerializer.Serialize(result.Architecture, ArchitectureJson.Options));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unsupportedConfirmation")]
    public async Task InvalidInput_Returns400WithoutProviderCall(string scenario)
    {
        var provider = new ArchitectStub();
        using var factory = new ArchitectFactory(provider);
        using var client = factory.CreateClient();
        var request = scenario == "missing" ? ArchitectSamples.Request with { Analysis = null }
            : ArchitectSamples.Request with { ConfirmedRequirements = null };
        using var response = await client.PostAsJsonAsync("/api/agents/architect", request, Options());
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(provider.LastRequest);
    }

    [Theory]
    [InlineData("refusal", 422)]
    [InlineData("malformed", 502)]
    [InlineData("empty", 502)]
    [InlineData("incomplete", 502)]
    [InlineData("invalidReference", 502)]
    [InlineData("unapprovedConstraint", 502)]
    [InlineData("apiError", 502)]
    public async Task Failures_ReturnExpectedStatusWithoutLeakingContent(string scenario, int expectedStatus)
    {
        var proposal = ArchitectSamples.Proposal;
        var provider = new ArchitectStub
        {
            Status = scenario switch
            {
                "refusal" => ModelResponseStatus.Refused,
                "empty" => ModelResponseStatus.Empty,
                "incomplete" => ModelResponseStatus.Incomplete,
                _ => ModelResponseStatus.Completed
            },
            Error = scenario == "apiError" ? new ClientResultException("private upstream detail") : null,
            Content = scenario switch
            {
                "malformed" => "private upstream detail",
                "invalidReference" => JsonSerializer.Serialize(proposal with { ConfirmedConstraintRequirementIds = ["missing"] }, ArchitectureJson.Options),
                "unapprovedConstraint" => JsonSerializer.Serialize(proposal with { ConfirmedConstraintRequirementIds = ["FR-2"] }, ArchitectureJson.Options),
                _ => ArchitectSamples.Json
            }
        };
        using var factory = new ArchitectFactory(provider);
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/agents/architect", ArchitectSamples.Request, Options());
        Assert.Equal(expectedStatus, (int)response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("private upstream detail", json);
        if (scenario != "apiError")
        {
            var result = JsonSerializer.Deserialize<AgentResult>(json, Options())!;
            Assert.False(result.Success);
            Assert.Null(result.Architecture);
            Assert.Empty(result.Output);
        }
    }

    internal static JsonSerializerOptions Options()
    {
        var options = new JsonSerializerOptions(ArchitectureJson.Options);
        options.Converters.Add(new JsonStringEnumConverter<AgentFailureKind>(JsonNamingPolicy.CamelCase, false));
        return options;
    }

    private sealed class ArchitectFactory(IModelProvider provider) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureLogging(logging => logging.ClearProviders().AddConsole());
            builder.UseSetting("OpenAI:ApiKey", "synthetic-test-credential");
            builder.UseSetting("OpenAI:Model", "gpt-4o-mini");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IModelProvider>();
                services.AddSingleton(provider);
            });
        }
    }
}
