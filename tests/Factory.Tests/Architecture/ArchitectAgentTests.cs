using System.ClientModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Factory.Core.Agents;
using Factory.Core.Analysis;
using Factory.Core.Architecture;
using Factory.Core.Models;
using Factory.Orchestration.Agents;

namespace Factory.Tests.Architecture;

internal static class ArchitectSamples
{
    internal static AgentRequest Request => new("sample", "Propose a minimal architecture", "No chosen stack",
        [new("USER-1", "Add tasks")], new("Task list",
        [new("FR-1", "Add tasks", RequirementStatus.Confirmed, "USER-1"),
         new("FR-2", "Save tasks", RequirementStatus.Suggested, null)],
        [new("Local storage may be sufficient", "Ask the user about device sharing")], [], []));
    internal static DesignBasis Basis => new(true,
        [new("FR-1", RequirementStatus.Confirmed), new("FR-2", RequirementStatus.Suggested)], []);
    internal static ArchitectureProposal Proposal => new("Proposed task-list design", ProposalStatus.Proposed,
        ["FR-1"], [new("C-1", "Task module", "Manage task creation and tentative persistence", Basis)],
        [new("T-1", "Local file", "Minimal prototype persistence", [new("SQLite", "Queries", "Schema management")],
            ["Simple storage but limited concurrent writes"], Basis)],
        [new("R-1", "Storage needs may change", "Confirm sharing needs", Basis)],
        [new("ADR-1", "T-1", "Small prototype", "Revisit persistence after review")]);
    internal static string Json => JsonSerializer.Serialize(Proposal, ArchitectureJson.Options);
}

internal sealed class ArchitectStub : IModelProvider
{
    public ModelRequest? LastRequest { get; private set; }
    public string Content { get; init; } = ArchitectSamples.Json;
    public ModelResponseStatus Status { get; init; } = ModelResponseStatus.Completed;
    public Exception? Error { get; init; }
    public Task<ModelResponse> GenerateAsync(ModelRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastRequest = request;
        if (Error is not null) throw Error;
        return Task.FromResult(new ModelResponse(Content, "synthetic", Status));
    }
}

public class ArchitectAgentTests
{
    [Fact]
    public async Task ValidInput_UsesSchemaAndReturnsUnapprovedTypedProposal()
    {
        var provider = new ArchitectStub();
        IAgent agent = new ArchitectAgent(provider);
        var result = await agent.ExecuteAsync(ArchitectSamples.Request);
        Assert.True(result.Success);
        Assert.Null(result.Analysis);
        Assert.Equal(ProposalStatus.Proposed, result.Architecture!.Status);
        Assert.Equal(ArchitectSamples.Json, result.Output);
        Assert.Equal(ArchitectureJson.OutputSchema, provider.LastRequest!.OutputSchema);
        var input = JsonSerializer.Deserialize<AgentRequest>(provider.LastRequest.UserPrompt, ArchitectureJson.Options)!;
        Assert.Equal(RequirementStatus.Suggested, input.Analysis!.FunctionalRequirements[1].Status);
        Assert.True(ArchitectureValidator.IsValid(result.Architecture, input));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("unsupportedConfirmation")]
    [InlineData("duplicate")]
    [InlineData("nullItem")]
    public async Task InvalidAnalysis_FailsBeforeCallingProvider(string scenario)
    {
        var request = ArchitectSamples.Request;
        request = scenario switch
        {
            "missing" => request with { Analysis = null },
            "empty" => request with { Analysis = request.Analysis! with { FunctionalRequirements = [] } },
            "unsupportedConfirmation" => request with { ConfirmedRequirements = null },
            "duplicate" => request with { Analysis = request.Analysis! with { FunctionalRequirements = [request.Analysis.FunctionalRequirements[0], request.Analysis.FunctionalRequirements[0]] } },
            _ => request with { Analysis = request.Analysis! with { FunctionalRequirements = [null!] } }
        };
        var provider = new ArchitectStub();
        var result = await new ArchitectAgent(provider).ExecuteAsync(request);
        Assert.Equal(AgentFailureKind.InvalidRequest, result.FailureKind);
        Assert.Null(provider.LastRequest);
    }

    [Theory]
    [InlineData(ModelResponseStatus.Refused, AgentFailureKind.Refused)]
    [InlineData(ModelResponseStatus.Incomplete, AgentFailureKind.IncompleteResponse)]
    [InlineData(ModelResponseStatus.Empty, AgentFailureKind.EmptyResponse)]
    public async Task UnusableResponse_IsNotAccepted(ModelResponseStatus status, AgentFailureKind expected)
    {
        var result = await new ArchitectAgent(new ArchitectStub { Status = status }).ExecuteAsync(ArchitectSamples.Request);
        Assert.Equal(expected, result.FailureKind);
        Assert.Null(result.Architecture);
        Assert.Empty(result.Output);
    }

    [Theory]
    [InlineData("not JSON", AgentFailureKind.InvalidOutput)]
    [InlineData("{}", AgentFailureKind.InvalidOutput)]
    [InlineData("null", AgentFailureKind.InvalidOutput)]
    [InlineData(" ", AgentFailureKind.EmptyResponse)]
    public async Task MalformedOrEmptyOutput_IsNotAccepted(string content, AgentFailureKind expected)
    {
        var result = await new ArchitectAgent(new ArchitectStub { Content = content }).ExecuteAsync(ArchitectSamples.Request);
        Assert.False(result.Success);
        Assert.Equal(expected, result.FailureKind);
        Assert.Null(result.Architecture);
        Assert.Empty(result.Output);
    }

    [Fact]
    public async Task ApiErrorsAndCancellation_PropagateToHttpBoundary()
    {
        await Assert.ThrowsAsync<ClientResultException>(() => new ArchitectAgent(new ArchitectStub
        { Error = new ClientResultException("synthetic") }).ExecuteAsync(ArchitectSamples.Request));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ArchitectAgent(new ArchitectStub())
            .ExecuteAsync(ArchitectSamples.Request, cancellation.Token));
    }
}

public class ArchitectureValidationTests
{
    [Fact]
    public void UnverifiedRequirements_CannotBecomeConstraintsOrLoseRecommendationLabel()
    {
        var request = ArchitectSamples.Request;
        request = request with { Analysis = request.Analysis! with { FunctionalRequirements =
            [request.Analysis.FunctionalRequirements[0],
             request.Analysis.FunctionalRequirements[1] with { Status = RequirementStatus.UnverifiedAssumption }] } };
        var basis = ArchitectSamples.Basis with { Requirements =
            [new("FR-1", RequirementStatus.Confirmed), new("FR-2", RequirementStatus.UnverifiedAssumption)] };
        var p = ArchitectSamples.Proposal;
        p = p with
        {
            Components = [p.Components[0] with { Basis = basis }],
            TechnologyDecisions = [p.TechnologyDecisions[0] with { Basis = basis }],
            Risks = [p.Risks[0] with { Basis = basis }]
        };
        Assert.True(ArchitectureValidator.IsValid(p, request));
        Assert.False(ArchitectureValidator.IsValid(p with { ConfirmedConstraintRequirementIds = ["FR-2"] }, request));
        Assert.False(ArchitectureValidator.IsValid(p with
        { TechnologyDecisions = [p.TechnologyDecisions[0] with { Basis = basis with { IsDesignRecommendation = false } }] }, request));
        Assert.False(ArchitectureValidator.IsValid(p with
        { Risks = [p.Risks[0] with { Basis = basis with { IsDesignRecommendation = false } }] }, request));
    }

    [Fact]
    public void ValidationDiagnostics_ContainOnlyStaticRuleCodes()
    {
        var proposal = ArchitectSamples.Proposal with { ConfirmedConstraintRequirementIds = ["private fabricated requirement"] };
        Assert.Equal("unconfirmed_constraint", ArchitectureValidator.GetError(proposal, ArchitectSamples.Request));
    }

    [Theory]
    [InlineData("unknownReference")]
    [InlineData("promotedStatus")]
    [InlineData("unapprovedConstraint")]
    [InlineData("unknownConstraint")]
    [InlineData("unmarkedSuggestion")]
    [InlineData("unmarkedRecommendation")]
    [InlineData("inventedAssumption")]
    [InlineData("unmarkedAssumption")]
    [InlineData("missingCoverage")]
    [InlineData("noAlternatives")]
    [InlineData("sameAlternative")]
    [InlineData("noTradeOffs")]
    [InlineData("unknownAdrDecision")]
    [InlineData("missingAdr")]
    [InlineData("duplicateEntityId")]
    [InlineData("duplicateTrace")]
    [InlineData("nullComponent")]
    [InlineData("approvedStatus")]
    public async Task InvalidProposal_FailsClosedInAgent(string scenario)
    {
        var p = ArchitectSamples.Proposal;
        var b = ArchitectSamples.Basis;
        var modifiedBasis = scenario switch
        {
            "unknownReference" => b with { Requirements = [new("missing", RequirementStatus.Suggested)] },
            "promotedStatus" => b with { Requirements = [new("FR-1", RequirementStatus.Confirmed), new("FR-2", RequirementStatus.Confirmed)] },
            "unmarkedSuggestion" => b with { IsDesignRecommendation = false },
            "unmarkedRecommendation" => b with { Requirements = [], IsDesignRecommendation = false },
            "inventedAssumption" => b with { AssumptionDescriptions = ["Millions of users are verified"] },
            "unmarkedAssumption" => b with { Requirements = [new("FR-1", RequirementStatus.Confirmed)], AssumptionDescriptions = ["Local storage may be sufficient"], IsDesignRecommendation = false },
            "missingCoverage" => b with { Requirements = [new("FR-1", RequirementStatus.Confirmed)] },
            "duplicateTrace" => b with { Requirements = [new("FR-1", RequirementStatus.Confirmed), new("FR-1", RequirementStatus.Confirmed)] },
            _ => b
        };
        p = p with { Components = [p.Components[0] with { Basis = modifiedBasis }] };
        p = scenario switch
        {
            "unapprovedConstraint" => p with { ConfirmedConstraintRequirementIds = ["FR-2"] },
            "unknownConstraint" => p with { ConfirmedConstraintRequirementIds = ["missing"] },
            "noAlternatives" => p with { TechnologyDecisions = [p.TechnologyDecisions[0] with { Alternatives = [] }] },
            "sameAlternative" => p with { TechnologyDecisions = [p.TechnologyDecisions[0] with { Alternatives = [new(" local FILE ", "A", "B")] }] },
            "noTradeOffs" => p with { TechnologyDecisions = [p.TechnologyDecisions[0] with { TradeOffs = [] }] },
            "unknownAdrDecision" => p with { Adrs = [p.Adrs[0] with { TechnologyDecisionId = "missing" }] },
            "missingAdr" => p with { Adrs = [] },
            "duplicateEntityId" => p with { Risks = [p.Risks[0] with { Id = "C-1" }] },
            "nullComponent" => p with { Components = [null!] },
            "approvedStatus" => p with { Status = (ProposalStatus)1 },
            _ => p
        };
        Assert.False(ArchitectureValidator.IsValid(p, ArchitectSamples.Request));
        // Undefined enum values cannot be serialized by the strict serializer.
        if (scenario == "approvedStatus") return;
        var result = await new ArchitectAgent(new ArchitectStub { Content = JsonSerializer.Serialize(p, ArchitectureJson.Options) })
            .ExecuteAsync(ArchitectSamples.Request);
        Assert.False(result.Success);
        Assert.Equal(AgentFailureKind.InvalidOutput, result.FailureKind);
        Assert.Null(result.Architecture);
    }

    [Fact]
    public void ValidAssumptionsAndPureRecommendations_AreExplicitlyTentative()
    {
        var p = ArchitectSamples.Proposal;
        var basis = new DesignBasis(true, [], ["Local storage may be sufficient"]);
        p = p with { TechnologyDecisions = [p.TechnologyDecisions[0] with { Basis = basis }] };
        Assert.True(ArchitectureValidator.IsValid(p, ArchitectSamples.Request));
    }

    [Theory]
    [InlineData("approved")]
    [InlineData("extra")]
    [InlineData("missing")]
    [InlineData("numericStatus")]
    public void StrictDeserialization_RejectsInvalidShape(string scenario)
    {
        var json = JsonNode.Parse(ArchitectSamples.Json)!;
        switch (scenario)
        {
            case "approved": json["status"] = "approved"; break;
            case "extra": json["executeCommands"] = true; break;
            case "missing": json.AsObject().Remove("adrs"); break;
            case "numericStatus": json["status"] = 0; break;
        }
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ArchitectureProposal>(json.ToJsonString(), ArchitectureJson.Options));
    }

    [Fact]
    public void SchemaAndTypedModels_RoundTripAndAllObjectsAreStrict()
    {
        var roundTrip = JsonSerializer.Deserialize<ArchitectureProposal>(ArchitectSamples.Json, ArchitectureJson.Options)!;
        Assert.Equal(ArchitectSamples.Json, JsonSerializer.Serialize(roundTrip, ArchitectureJson.Options));
        using var schema = JsonDocument.Parse(ArchitectureJson.OutputSchema.JsonSchema);
        using var sample = JsonDocument.Parse(ArchitectSamples.Json);
        Check(schema.RootElement, sample.RootElement, schema.RootElement);
    }

    private static void Check(JsonElement schema, JsonElement value, JsonElement root)
    {
        if (schema.TryGetProperty("$ref", out var reference))
            schema = root.GetProperty("$defs").GetProperty(reference.GetString()!.Split('/').Last());
        if (schema.TryGetProperty("enum", out var choices))
            Assert.Contains(choices.EnumerateArray(), c => JsonElement.DeepEquals(c, value));
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
                var properties = schema.GetProperty("properties").EnumerateObject().ToArray();
                Assert.Equal(properties.Select(p => p.Name).Order(), value.EnumerateObject().Select(p => p.Name).Order());
                Assert.Equal(properties.Select(p => p.Name).Order(), schema.GetProperty("required").EnumerateArray().Select(p => p.GetString()).Order());
                foreach (var property in properties) Check(property.Value, value.GetProperty(property.Name), root);
                break;
            case JsonValueKind.Array:
                Assert.Equal("array", schema.GetProperty("type").GetString());
                foreach (var item in value.EnumerateArray()) Check(schema.GetProperty("items"), item, root);
                break;
            case JsonValueKind.String: Assert.Equal("string", schema.GetProperty("type").GetString()); break;
            case JsonValueKind.True:
            case JsonValueKind.False: Assert.Equal("boolean", schema.GetProperty("type").GetString()); break;
            default: Assert.Fail("Unexpected sample kind"); break;
        }
    }
}
