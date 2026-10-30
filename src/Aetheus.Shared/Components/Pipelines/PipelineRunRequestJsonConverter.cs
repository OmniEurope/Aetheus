// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aetheus.Shared.Components.Pipelines;

/// <summary>
/// Expand-contract reader for the run endpoint. It accepts both the current request object and the
/// former top-level variable dictionary so cached clients can cross a blue-green backend switch safely.
/// </summary>
public sealed class PipelineRunRequestJsonConverter : JsonConverter<PipelineRunRequest>
{
    private const string BranchVariable = "AETHEUS_RUN_BRANCH";
    private const string IdempotencyVariable = "AETHEUS_RUN_IDEMPOTENCY_KEY";
    private const string RunToEndVariable = "AETHEUS_RUN_TO_END";
    /// <summary>R-72: the legacy (flat dictionary) spelling of <see cref="PipelineRunRequest.GateOverride"/>.</summary>
    public const string GateOverrideVariable = "AETHEUS_RUN_GATE_OVERRIDE";
    private static readonly string[] CurrentShapeNames = ["parameters", "sourceBranch", "idempotencyKey", "runToEnd", "gateOverride"];

    public override PipelineRunRequest? Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        using var document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("Pipeline run input must be a JSON object.");

        var root = document.RootElement;
        if (IsCurrentShape(root))
        {
            TryGetProperty(root, "parameters", out var parametersElement);
            TryGetProperty(root, "sourceBranch", out var branchElement);
            TryGetProperty(root, "idempotencyKey", out var idempotencyElement);
            TryGetProperty(root, "runToEnd", out var runToEndElement);
            TryGetProperty(root, "gateOverride", out var gateOverrideElement);
            return new PipelineRunRequest
            {
                Parameters = parametersElement.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                    ? null
                    : ReadStringDictionary(parametersElement, PipelineRunRequest.MaxParameterCount),
                SourceBranch = branchElement.ValueKind == JsonValueKind.String
                    ? branchElement.GetString()
                    : null,
                IdempotencyKey = idempotencyElement.ValueKind == JsonValueKind.String
                    ? idempotencyElement.GetString()
                    : null,
                RunToEnd = runToEndElement.ValueKind == JsonValueKind.True,
                GateOverride = gateOverrideElement.ValueKind == JsonValueKind.True
            };
        }

        var legacy = ReadStringDictionary(root, PipelineRunRequest.MaxParameterCount + 4);
        legacy.Remove(BranchVariable, out var sourceBranch);
        legacy.Remove(IdempotencyVariable, out var idempotencyKey);
        legacy.Remove(RunToEndVariable, out var runToEnd);
        legacy.Remove(GateOverrideVariable, out var gateOverride);
        if (legacy.Count > PipelineRunRequest.MaxParameterCount)
            throw new JsonException(
                $"Pipeline run parameters cannot contain more than {PipelineRunRequest.MaxParameterCount} entries.");
        return new PipelineRunRequest
        {
            Parameters = legacy.Count == 0 ? null : legacy,
            SourceBranch = sourceBranch,
            IdempotencyKey = idempotencyKey,
            RunToEnd = PipelineRunToEndSetting.IsAllowed(runToEnd),
            // Only the literal "true" asks for the override, like run-to-end.
            GateOverride = PipelineRunToEndSetting.IsAllowed(gateOverride)
        };
    }

    public override void Write(Utf8JsonWriter writer, PipelineRunRequest value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (value.Parameters is not null)
        {
            writer.WritePropertyName("parameters");
            JsonSerializer.Serialize(writer, value.Parameters, options);
        }
        if (!string.IsNullOrWhiteSpace(value.SourceBranch))
            writer.WriteString("sourceBranch", value.SourceBranch);
        if (!string.IsNullOrWhiteSpace(value.IdempotencyKey))
            writer.WriteString("idempotencyKey", value.IdempotencyKey);
        if (value.RunToEnd)
            writer.WriteBoolean("runToEnd", true);
        if (value.GateOverride)
            writer.WriteBoolean("gateOverride", true);
        writer.WriteEndObject();
    }

    private static Dictionary<string, string> ReadStringDictionary(JsonElement element, int maxEntries)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new JsonException("Pipeline run parameters must be a JSON object.");

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
        {
            if (result.Count >= maxEntries && !result.ContainsKey(property.Name))
                throw new JsonException($"Pipeline run parameters cannot contain more than {maxEntries} entries.");
            if (property.Name.Length > 128)
                throw new JsonException("Pipeline run parameter names cannot exceed 128 characters.");
            if (property.Value.ValueKind != JsonValueKind.String)
                throw new JsonException($"Pipeline run parameter '{property.Name}' must be a string.");
            var value = property.Value.GetString() ?? string.Empty;
            if (value.Length > 4096)
                throw new JsonException($"Pipeline run parameter '{property.Name}' cannot exceed 4096 characters.");
            result[property.Name] = value;
        }
        return result;
    }

    /// <summary>
    /// SHARED-002: the legacy shape is a flat dictionary of strings, and its keys are parameter names a
    /// pipeline may legally choose, <c>parameters</c> and <c>sourceBranch</c> included. Seeing one of
    /// the current names is therefore not enough. The current shape has only those five names, its
    /// <c>parameters</c> is an object or null, never a string, and its <c>runToEnd</c> and <c>gateOverride</c> are never strings
    /// either; anything else is a legacy dictionary.
    /// A body made only of <c>sourceBranch</c> and/or <c>idempotencyKey</c> strings is intrinsically
    /// ambiguous and is read as the current shape, the one today's clients send.
    /// </summary>
    private static bool IsCurrentShape(JsonElement root)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!CurrentShapeNames.Contains(property.Name, StringComparer.OrdinalIgnoreCase)) return false;
            if ((property.Name.Equals("parameters", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("runToEnd", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("gateOverride", StringComparison.OrdinalIgnoreCase))
                && property.Value.ValueKind == JsonValueKind.String) return false;
        }
        return true;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            value = property.Value;
            return true;
        }
        value = default;
        return false;
    }
}
