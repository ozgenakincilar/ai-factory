using System.Text.Json;
using System.Text.Json.Serialization;
using Factory.Core.Models;

namespace Factory.Core.Analysis;

public static class AnalysisJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true,
            RespectNullableAnnotations = true,
            AllowDuplicateProperties = false
        };
        options.Converters.Add(new JsonStringEnumConverter<RequirementStatus>(
            JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    public static ModelOutputSchema OutputSchema { get; } = new("analysis_result", """
        {
          "type": "object",
          "properties": {
            "summary": { "type": "string" },
            "functionalRequirements": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "id": { "type": "string" },
                  "description": { "type": "string" },
                  "status": { "type": "string", "enum": ["suggested", "confirmed", "unverifiedAssumption"] },
                  "confirmedSourceId": { "type": ["string", "null"] }
                },
                "required": ["id", "description", "status", "confirmedSourceId"],
                "additionalProperties": false
              }
            },
            "assumptions": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "description": { "type": "string" },
                  "validationNeeded": { "type": "string" }
                },
                "required": ["description", "validationNeeded"],
                "additionalProperties": false
              }
            },
            "risks": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "description": { "type": "string" },
                  "suggestedMitigation": { "type": "string" }
                },
                "required": ["description", "suggestedMitigation"],
                "additionalProperties": false
              }
            },
            "openQuestions": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": { "question": { "type": "string" } },
                "required": ["question"],
                "additionalProperties": false
              }
            }
          },
          "required": ["summary", "functionalRequirements", "assumptions", "risks", "openQuestions"],
          "additionalProperties": false
        }
        """);
}
