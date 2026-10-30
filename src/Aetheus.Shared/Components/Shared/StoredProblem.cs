// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Aetheus.Shared.Components.Shared;

/// <summary>
/// A problem written where only a string fits (a warning stored on a pipeline run, an advisory warning of the
/// launch preview): <c>problem:</c> followed by the JSON of its code, its parameters and its English sentence.
/// The front reads the code and says the sentence in the user's language; a reader that does not know the code,
/// or an API client, shows the English sentence. The same stored-string idea as <c>PipelineRunNotices</c>, for the
/// problems that are defects of the run rather than information.
/// <para>A problem that wraps the refusal of another (a stage that could not be dispatched because its
/// configuration was refused) carries that refusal as <see cref="StoredProblemContent.Inner"/>: the front says it
/// in the user's language and writes it where the sentence names <c>{detail}</c>.</para>
/// </summary>
public static class StoredProblem
{
    private const string Prefix = "problem:";

    // Relaxed escaping keeps the English sentence readable inside the JSON (quotes of names are not written
    // as '); the string is only ever rendered as text.
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>The stored form of a problem and its English sentence, with the problems that explain it.</summary>
    public static string Compose(ProblemDto problem, string english, IReadOnlyList<ProblemDto>? inner = null)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(english);
        return Prefix + JsonSerializer.Serialize(
            new Payload(problem.Code, problem.Parameters, english, inner is { Count: > 0 } ? [.. inner] : null), Options);
    }

    /// <summary>Whether <paramref name="stored"/> is a stored problem, and what it holds.</summary>
    public static bool TryRead(string? stored, [NotNullWhen(true)] out StoredProblemContent? content)
    {
        content = null;
        if (stored is null || !stored.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        try
        {
            var payload = JsonSerializer.Deserialize<Payload>(stored.AsSpan(Prefix.Length), Options);
            if (payload is null || string.IsNullOrWhiteSpace(payload.Code)) return false;
            content = new StoredProblemContent(
                new ProblemDto(payload.Code, new Dictionary<string, string>(payload.Parameters ?? [], StringComparer.Ordinal)),
                payload.English ?? string.Empty,
                payload.Inner ?? []);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The code of a stored problem, else null.</summary>
    public static string? CodeOf(string? stored) => TryRead(stored, out var content) ? content.Problem.Code : null;

    /// <summary>One parameter of a stored problem, else null.</summary>
    public static string? ParameterOf(string? stored, string name) =>
        TryRead(stored, out var content) && content.Problem.Parameters.TryGetValue(name, out var value) ? value : null;

    /// <summary>The English sentence of a stored problem; any other string is returned unchanged.</summary>
    public static string English(string stored) => TryRead(stored, out var content) ? content.English : stored;

    private sealed record Payload(
        string Code, Dictionary<string, string>? Parameters, string? English, List<ProblemDto>? Inner);
}

/// <summary>What a stored problem holds: the problem, its English sentence and the problems that explain it.</summary>
public sealed record StoredProblemContent(ProblemDto Problem, string English, IReadOnlyList<ProblemDto> Inner);
