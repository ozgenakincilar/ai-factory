using System.Text.Json;
using System.Text.Json.Serialization;
using Factory.Core.Analysis;
using Factory.Core.Models;

namespace Factory.Core.Architecture;

public static class ArchitectureJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(AnalysisJson.Options);
        options.Converters.Add(new JsonStringEnumConverter<ProposalStatus>(JsonNamingPolicy.CamelCase, false));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    public static ModelOutputSchema OutputSchema { get; } = new("architecture_proposal", """
        {
          "type": "object",
          "properties": {
            "summary": {"type":"string"},
            "status": {"type":"string","enum":["proposed"]},
            "confirmedConstraintRequirementIds": {"type":"array","items":{"type":"string"}},
            "components": {"type":"array","items":{"$ref":"#/$defs/component"}},
            "technologyDecisions": {"type":"array","items":{"$ref":"#/$defs/technologyDecision"}},
            "risks": {"type":"array","items":{"$ref":"#/$defs/risk"}},
            "adrs": {"type":"array","items":{"$ref":"#/$defs/adr"}}
          },
          "required":["summary","status","confirmedConstraintRequirementIds","components","technologyDecisions","risks","adrs"],
          "additionalProperties":false,
          "$defs": {
            "trace": {
              "type":"object",
              "properties": {
                "requirementId":{"type":"string"},
                "status":{"type":"string","enum":["suggested","confirmed","unverifiedAssumption"]}
              },
              "required":["requirementId","status"],"additionalProperties":false
            },
            "basis": {
              "type":"object",
              "properties": {
                "isDesignRecommendation":{"type":"boolean"},
                "requirements":{"type":"array","items":{"$ref":"#/$defs/trace"}},
                "assumptionDescriptions":{"type":"array","items":{"type":"string"}}
              },
              "required":["isDesignRecommendation","requirements","assumptionDescriptions"],"additionalProperties":false
            },
            "component": {
              "type":"object",
              "properties": {
                "id":{"type":"string"},"name":{"type":"string"},"responsibility":{"type":"string"},
                "basis":{"$ref":"#/$defs/basis"}
              },
              "required":["id","name","responsibility","basis"],"additionalProperties":false
            },
            "alternative": {
              "type":"object",
              "properties": {
                "technology":{"type":"string"},"benefits":{"type":"string"},"drawbacks":{"type":"string"}
              },
              "required":["technology","benefits","drawbacks"],"additionalProperties":false
            },
            "technologyDecision": {
              "type":"object",
              "properties": {
                "id":{"type":"string"},"technology":{"type":"string"},"rationale":{"type":"string"},
                "alternatives":{"type":"array","items":{"$ref":"#/$defs/alternative"}},
                "tradeOffs":{"type":"array","items":{"type":"string"}},
                "basis":{"$ref":"#/$defs/basis"}
              },
              "required":["id","technology","rationale","alternatives","tradeOffs","basis"],"additionalProperties":false
            },
            "risk": {
              "type":"object",
              "properties": {
                "id":{"type":"string"},"description":{"type":"string"},"mitigation":{"type":"string"},
                "basis":{"$ref":"#/$defs/basis"}
              },
              "required":["id","description","mitigation","basis"],"additionalProperties":false
            },
            "adr": {
              "type":"object",
              "properties": {
                "id":{"type":"string"},"technologyDecisionId":{"type":"string"},
                "context":{"type":"string"},"consequences":{"type":"string"}
              },
              "required":["id","technologyDecisionId","context","consequences"],"additionalProperties":false
            }
          }
        }
        """);
}
