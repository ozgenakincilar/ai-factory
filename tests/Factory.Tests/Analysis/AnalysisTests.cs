using System.Text.Json;
using System.Text.Json.Nodes;
using Factory.Core.Analysis;

namespace Factory.Tests.Analysis;

public class AnalysisTests
{
    internal static AnalysisResult Sample => new("Analysis awaiting review",
        [new("FR-1", "Import replay files", RequirementStatus.Suggested, null)],
        [new("Replay access is available", "Check the supported data source")],
        [new("Data access may be limited", "Evaluate available integrations")],
        [new("Which replay formats are needed?")]);

    internal static string SampleJson => JsonSerializer.Serialize(Sample, AnalysisJson.Options);

    [Theory]
    [InlineData(RequirementStatus.Suggested, "suggested")]
    [InlineData(RequirementStatus.Confirmed, "confirmed")]
    [InlineData(RequirementStatus.UnverifiedAssumption, "unverifiedAssumption")]
    public void Serialization_RoundTripsTypedModelsAndStringStatuses(RequirementStatus status, string wireValue)
    {
        var sample = Sample with { FunctionalRequirements = [Sample.FunctionalRequirements[0] with { Status = status }] };
        var json = JsonSerializer.Serialize(sample, AnalysisJson.Options);
        var parsed = JsonSerializer.Deserialize<AnalysisResult>(json, AnalysisJson.Options)!;
        Assert.Equal(sample.Summary, parsed.Summary);
        Assert.Equal(sample.FunctionalRequirements, parsed.FunctionalRequirements);
        Assert.Equal(sample.Assumptions, parsed.Assumptions);
        Assert.Equal(sample.Risks, parsed.Risks);
        Assert.Equal(sample.OpenQuestions, parsed.OpenQuestions);
        Assert.Contains($"\"status\":\"{wireValue}\"", json);
        Assert.Contains("\"confirmedSourceId\":null", json);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("null")]
    [InlineData("numericEnum")]
    [InlineData("unknownEnum")]
    [InlineData("nestedExtra")]
    [InlineData("missingNullable")]
    public void Deserialization_RejectsSchemaViolations(string mutation)
    {
        var node = JsonNode.Parse(SampleJson)!;
        switch (mutation)
        {
            case "missing": node.AsObject().Remove("summary"); break;
            case "extra": node["unrecognized"] = true; break;
            case "null": node["functionalRequirements"] = null; break;
            case "numericEnum": node["functionalRequirements"]![0]!["status"] = 1; break;
            case "unknownEnum": node["functionalRequirements"]![0]!["status"] = "approved"; break;
            case "nestedExtra": node["assumptions"]![0]!["confirmed"] = true; break;
            case "missingNullable": node["functionalRequirements"]![0]!.AsObject().Remove("confirmedSourceId"); break;
        }
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AnalysisResult>(node.ToJsonString(), AnalysisJson.Options));
    }

    [Fact]
    public void Deserialization_RejectsDuplicateProperties()
    {
        var json = SampleJson.Replace("\"summary\":", "\"summary\":\"duplicate\",\"summary\":");
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AnalysisResult>(json, AnalysisJson.Options));
    }

    [Theory]
    [InlineData(RequirementStatus.Suggested)]
    [InlineData(RequirementStatus.UnverifiedAssumption)]
    public void Validation_AllowsUnconfirmedRequirementsWithoutEvidence(RequirementStatus status)
    {
        Assert.True(AnalysisValidator.IsValid(Sample with
        {
            FunctionalRequirements = [Sample.FunctionalRequirements[0] with { Status = status }]
        }, null));
    }

    [Theory]
    [InlineData(null, "Import replay files", false)]
    [InlineData("unknown", "Import replay files", false)]
    [InlineData("USER-1", "Import replay files and upload them publicly", false)]
    [InlineData("USER-1", "import replay files", false)]
    [InlineData("USER-1", "Import replay files", true)]
    public void Validation_ConfirmationRequiresExactCallerEvidence(string? sourceId, string description, bool expected)
    {
        var result = Sample with { FunctionalRequirements = [new("FR-1", description, RequirementStatus.Confirmed, sourceId)] };
        Assert.Equal(expected, AnalysisValidator.IsValid(result, [new("USER-1", "Import replay files")]));
    }

    [Fact]
    public void Validation_RejectsFalseEvidenceAndDuplicateIds()
    {
        var requirement = Sample.FunctionalRequirements[0];
        Assert.False(AnalysisValidator.IsValid(Sample with { FunctionalRequirements = [requirement with { ConfirmedSourceId = "USER-1" }] }, null));
        Assert.False(AnalysisValidator.IsValid(Sample with { FunctionalRequirements = [requirement, requirement] }, null));
        Assert.False(AnalysisValidator.IsValid(Sample with { FunctionalRequirements = [requirement with { Status = (RequirementStatus)99 }] }, null));
        Assert.False(AnalysisValidator.HasValidSources([new("same", "First"), new("same", "Second")]));
        Assert.False(AnalysisValidator.HasValidSources([new("", "First")]));
    }

    [Fact]
    public void Validation_RejectsNullItemsAndBlankFields()
    {
        Assert.False(AnalysisValidator.IsValid(null, null));
        Assert.False(AnalysisValidator.IsValid(Sample with { Summary = " " }, null));
        Assert.False(AnalysisValidator.IsValid(Sample with { FunctionalRequirements = [null!] }, null));
        Assert.False(AnalysisValidator.IsValid(Sample with { Assumptions = [null!] }, null));
        Assert.False(AnalysisValidator.IsValid(Sample with { Risks = [null!] }, null));
        Assert.False(AnalysisValidator.IsValid(Sample with { OpenQuestions = [new(" ")] }, null));
        Assert.False(AnalysisValidator.IsValid(Sample with { Assumptions = [new("A", "")] }, null));
        Assert.False(AnalysisValidator.IsValid(Sample with { Risks = [new("R", "")] }, null));
    }

    [Fact]
    public void Schema_MatchesSerializedModelsAndRequiresEveryDeclaredProperty()
    {
        using var schema = JsonDocument.Parse(AnalysisJson.OutputSchema.JsonSchema);
        using var sample = JsonDocument.Parse(SampleJson);
        CheckSchema(schema.RootElement, sample.RootElement);
        var statusEnum = schema.RootElement.GetProperty("properties").GetProperty("functionalRequirements")
            .GetProperty("items").GetProperty("properties").GetProperty("status").GetProperty("enum");
        Assert.Equal(new[] { "suggested", "confirmed", "unverifiedAssumption" }, statusEnum.EnumerateArray().Select(e => e.GetString()));
    }

    private static void CheckSchema(JsonElement schema, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            Assert.Equal("object", schema.GetProperty("type").GetString());
            Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
            var properties = schema.GetProperty("properties").EnumerateObject().ToArray();
            Assert.Equal(properties.Select(p => p.Name).Order(), schema.GetProperty("required").EnumerateArray().Select(p => p.GetString()).Order());
            Assert.Equal(properties.Select(p => p.Name).Order(), value.EnumerateObject().Select(p => p.Name).Order());
            foreach (var property in properties) CheckSchema(property.Value, value.GetProperty(property.Name));
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            Assert.Equal("array", schema.GetProperty("type").GetString());
            foreach (var item in value.EnumerateArray()) CheckSchema(schema.GetProperty("items"), item);
        }
        else if (value.ValueKind == JsonValueKind.Null)
            Assert.Contains(schema.GetProperty("type").EnumerateArray(), t => t.GetString() == "null");
        else
            Assert.Equal("string", schema.GetProperty("type").GetString());
    }
}
