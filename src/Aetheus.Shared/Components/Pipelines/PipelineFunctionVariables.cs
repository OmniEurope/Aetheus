// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aetheus.Shared.Components.Pipelines;

/// <summary>
/// The environment-variable names a <c>type: aetheus</c> step travels under, from the control plane
/// that writes them to the agent that reads them. The task target carries the function name; the
/// <c>with:</c> values travel here.
///
/// They live in Shared rather than beside either side, because both sides must agree on them and the
/// control plane deliberately does not reference the agent. A name or an encoding that differed on one
/// side would not fail to compile, it would produce a function that silently ignores its parameters.
/// </summary>
public static class PipelineFunctionVariables
{
    /// <summary>The step's <c>with:</c> map, after run-variable substitution, as a JSON object of
    /// string values.</summary>
    public const string ParametersJson = "AETHEUS_FUNCTION_PARAMETERS_JSON";

    /// <summary>Encodes the <c>with:</c> values for <see cref="ParametersJson"/>.</summary>
    public static string SerializeParameters(IReadOnlyDictionary<string, string> parameters) =>
        JsonSerializer.Serialize(parameters);

    /// <summary>Decodes <see cref="ParametersJson"/>. An absent or empty value is an empty map; a value
    /// that is not a JSON object of strings is refused rather than read as no parameters, because a
    /// function run without the parameters its step declared would report on the wrong input.</summary>
    public static bool TryParseParameters(string? json, out IReadOnlyDictionary<string, string> parameters)
    {
        parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json))
            return true;
        try
        {
            if (JsonSerializer.Deserialize(json, PipelineFunctionParametersJsonContext.Default.DictionaryStringString) is not { } decoded
                || decoded.Values.Any(value => value is null))
                return false;
            parameters = decoded.ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.Ordinal);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>Source-generated decoding of <see cref="PipelineFunctionVariables.ParametersJson"/>, so the
/// trimmed <c>aetheus-fn</c> executable decodes parameters without reflection.</summary>
[JsonSerializable(typeof(Dictionary<string, string?>))]
internal sealed partial class PipelineFunctionParametersJsonContext : JsonSerializerContext;
