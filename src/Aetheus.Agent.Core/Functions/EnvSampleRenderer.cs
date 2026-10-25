// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Functions;

/// <summary>The run values an environment template may name: <c>#{PUBLIC_API_URL}#</c>,
/// <c>#{PUBLIC_APP_URL}#</c> and <c>#{APP_VERSION}#</c>.</summary>
internal sealed record EnvSampleValues(string? PublicApiUrl, string? PublicAppUrl, string? AppVersion);

/// <summary>
/// The rendering rules of <c>render-env-sample.sh</c> (the <c>RENDER_ENV_SAMPLE_PROGRAM</c> heredoc of
/// <c>.pipeline/aetheus-deploy-prod.yaml</c>): the repository owns the shape of the environment file
/// (<c>deploy/env/prod.env.sample</c>) and the host owns its secrets, so only the template's declared
/// placeholders are replaced, and a malformed or incomplete template is refused before anything is
/// written.
///
/// A line is blank, a <c>#</c> comment, or <c>KEY=value</c> with a key that starts with an upper-case
/// letter and holds only upper case, digits and underscores, declared once. A value is one of the
/// three run values, a <c>#{SECRET_HEX_&lt;N&gt;}#</c> marker (N bytes, at least 16, drawn from the
/// random source and written as 2N lowercase hexadecimal characters: what <c>openssl rand -hex N</c>
/// printed), or a non-empty literal holding no placeholder. A carriage return anywhere is refused.
/// </summary>
internal static partial class EnvSampleRenderer
{
    private const string SecretPrefix = "#{SECRET_HEX_";
    private const string SecretSuffix = "}#";
    private const int MinimumSecretBytes = 16;
    private const int MaximumSecretBytes = 1024;

    [GeneratedRegex("^[0-9]+\\z")]
    private static partial Regex Digits();

    /// <summary>The environment file the template renders to, or why it is refused (the script's
    /// <c>FATAL</c> text, without the prefix).</summary>
    public static (string? Content, string? Error) Render(
        string sample, string template, EnvSampleValues values, Func<int, string> hexSource)
    {
        var lines = template.Split('\n');
        var count = lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;
        var output = new StringBuilder();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < count; index++)
        {
            var (resolved, error) = RenderLine(sample, index + 1, lines[index], keys, values, hexSource);
            if (error is not null)
                return (null, error);
            output.Append(resolved);
        }
        return keys.Count == 0
            ? (null, $"Environment template declares no key: {sample}")
            : (output.ToString(), null);
    }

    private static (string? Rendered, string? Error) RenderLine(
        string sample, int number, string line, HashSet<string> keys, EnvSampleValues values, Func<int, string> hexSource)
    {
        var at = $"{sample}:{number}";
        if (line.Contains('\r', StringComparison.Ordinal))
            return (null, $"{at} carries a carriage return.");
        if (line.Length == 0 || line[0] == '#')
            return (string.Empty, null);

        var equals = line.IndexOf('=', StringComparison.Ordinal);
        if (equals < 0)
            return (null, $"{at} is not KEY=VALUE.");
        var key = line[..equals];
        if (KeyRefusal(at, key, keys) is { } refusal)
            return (null, refusal);

        var (resolved, error) = ResolveValue(at, key, line[(equals + 1)..], values, hexSource);
        return error is not null ? (null, error) : ($"{key}={resolved}\n", null);
    }

    /// <summary>The refusal of a key, or null; an accepted key is recorded as declared.</summary>
    private static string? KeyRefusal(string at, string key, HashSet<string> declared)
    {
        if (key.Length == 0 || key[0] is not (>= 'A' and <= 'Z'))
            return $"{at}: key '{key}' must start with an upper-case letter.";
        if (key.Any(character => character is not (>= 'A' and <= 'Z' or >= '0' and <= '9' or '_')))
            return $"{at}: key '{key}' must be upper case, digits and underscores.";
        return declared.Add(key) ? null : $"{at}: key {key} is declared twice.";
    }

    private static (string? Resolved, string? Error) ResolveValue(
        string at, string key, string value, EnvSampleValues values, Func<int, string> hexSource) =>
        value switch
        {
            "#{PUBLIC_API_URL}#" => Required("PUBLIC_API_URL", values.PublicApiUrl),
            "#{PUBLIC_APP_URL}#" => Required("PUBLIC_APP_URL", values.PublicAppUrl),
            "#{APP_VERSION}#" => Required("APP_VERSION", values.AppVersion),
            _ when IsSecretMarker(value) => Secret(at, key, value[SecretPrefix.Length..^SecretSuffix.Length], hexSource),
            _ when value.Contains("#{", StringComparison.Ordinal) || value.Contains("}#", StringComparison.Ordinal) =>
                (null, $"{at}: undeclared placeholder in {key}."),
            _ => value.Length == 0 ? (null, $"{at} {key} has an empty value.") : (value, null)
        };

    private static (string? Resolved, string? Error) Required(string name, string? value) =>
        string.IsNullOrEmpty(value) ? (null, $"{name} is required") : (value, null);

    private static bool IsSecretMarker(string value) =>
        value.Length >= SecretPrefix.Length + SecretSuffix.Length
        && value.StartsWith(SecretPrefix, StringComparison.Ordinal)
        && value.EndsWith(SecretSuffix, StringComparison.Ordinal);

    private static (string? Resolved, string? Error) Secret(string at, string key, string length, Func<int, string> hexSource)
    {
        if (!Digits().IsMatch(length))
            return (null, $"{at}: secret length '{length}' is not a number.");
        if (!int.TryParse(length, out var bytes) || bytes < MinimumSecretBytes)
            return (null, $"{at}: a secret shorter than {MinimumSecretBytes} bytes is refused.");
        if (bytes > MaximumSecretBytes)
            return (null, $"{at}: a secret longer than {MaximumSecretBytes} bytes is refused.");

        var drawn = hexSource(bytes);
        if (drawn.Length != bytes * 2)
            return (null, $"The random source produced a secret of the wrong length for {key}.");
        return drawn.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
            ? (null, $"The random source produced a non-hexadecimal secret for {key}.")
            : (drawn, null);
    }
}
