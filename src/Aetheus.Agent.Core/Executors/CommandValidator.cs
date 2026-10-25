// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;
using Aetheus.Shared.Components.Pipelines;

namespace Aetheus.Agent.Core.Executors;

public interface ICommandValidator
{
    bool IsAllowed(string command);
    string? GetRejectionReason(string command);
    IReadOnlyList<string> GetDangerousEnvironmentVariableNames(Dictionary<string, string> environmentVariables);
    bool HasDangerousEnvironmentVariables(Dictionary<string, string> environmentVariables);
}

// SECURITY MODEL: this allow-list is a defense-in-depth GUARDRAIL that limits the blast radius
// of a pipeline/task command - it is NOT a sandbox and NOT the authorization boundary. A
// determined author can still reach arbitrary code via allow-listed interpreters (python/node/
// dotnet), package managers, or git/tar living-off-the-land. The real boundary is RBAC: free-form
// shell requires Server Admin on the target (F-15 on POST /api/tasks; F-EXEC-1 on the pipeline
// path). Do not rely on IsAllowed as a security control.
public sealed partial class CommandValidator(IOptions<AetheusAgentOptions> options) : ICommandValidator
{
    private static readonly TimeSpan AllowedPatternMatchTimeout = TimeSpan.FromSeconds(1);

    // Shared with the production setup, which never names a library entry after one of them.
    private static readonly IReadOnlySet<string> DangerousEnvVars = AgentEnvironmentPolicy.DangerousNames;

    private readonly List<Regex> _allowedPatterns = options.Value.AllowedCommandPatterns
        .Select(CreateAllowedPattern)
        .ToList();

    private static Regex CreateAllowedPattern(string pattern)
    {
        const RegexOptions commonOptions = RegexOptions.Compiled | RegexOptions.IgnoreCase;
        try
        {
            return new Regex(
                pattern,
                commonOptions | RegexOptions.NonBacktracking,
                AllowedPatternMatchTimeout);
        }
        catch (NotSupportedException)
        {
            // Operator-provided patterns may use lookarounds or backreferences that the linear
            // engine does not support. Preserve compatibility, keep the bounded timeout, and let
            // GetRejectionReason fail closed if such a pattern still exhausts its budget.
            return new Regex(pattern, commonOptions, AllowedPatternMatchTimeout);
        }
    }

    // Accepts only individually allow-listed commands joined by sequential &&, rejecting other
    // shell metacharacters in every segment. IsAllowed and GetRejectionReason share this policy so
    // their authorization decisions agree.
    public bool IsAllowed(string command) => GetRejectionReason(command) is null;

    public string? GetRejectionReason(string command)
    {
        var normalized = command.Trim();
        if (string.IsNullOrEmpty(normalized))
            return "Empty command";

        // Pipeline scripts are multi-line bash passed via `bash -c`. They contain newlines,
        // semicolons, and other metacharacters by design. The real authorization boundary is
        // RBAC (F-EXEC-1: Server.Admin required to trigger a pipeline), not this allow-list.
        // Multi-line scripts are recognized by the presence of a newline - single-line task
        // commands (the original use case) continue to be validated segment-by-segment.
        if (normalized.Contains('\n'))
            return null;

        foreach (var segment in normalized.Split("&&"))
        {
            var trimmed = segment.Trim();
            if (string.IsNullOrEmpty(trimmed))
                return "Empty segment in chain";
            if (ShellMetaCharsRegex().IsMatch(trimmed))
                return $"Blocked metacharacter in segment: {Truncate(trimmed)}";
            var matched = false;
            foreach (var pattern in _allowedPatterns)
            {
                try
                {
                    if (!pattern.IsMatch(trimmed))
                        continue;

                    matched = true;
                    break;
                }
                catch (RegexMatchTimeoutException)
                {
                    return $"Allow-list pattern evaluation timed out for segment: {Truncate(trimmed)}";
                }
            }

            if (!matched)
                return $"No allow-list pattern matched segment: {Truncate(trimmed)}";
        }
        return null;
    }

    private static string Truncate(string s) => s.Length <= 80 ? s : s[..80] + "…";

    [GeneratedRegex(@"[;|&`$(){}\<>\r\n\0]")]
    private static partial Regex ShellMetaCharsRegex();

    public IReadOnlyList<string> GetDangerousEnvironmentVariableNames(
        Dictionary<string, string> environmentVariables)
    {
        var blocked = new List<string>();
        foreach (var (key, value) in environmentVariables)
        {
            if (DangerousEnvVars.Contains(key))
            {
                blocked.Add(key);
                continue;
            }

            // Shellshock (CVE-2014-6271 / -7169) class: bash <4.3 imports an env var whose value
            // starts with "() {" as a function (executing trailing bytes); bash >=4.3 namespaces
            // them as BASH_FUNC_x%%. Block both forms regardless of the host's bash version.
            if (key.StartsWith("BASH_FUNC_", StringComparison.OrdinalIgnoreCase))
            {
                blocked.Add(key);
                continue;
            }
            if (value is not null && value.TrimStart().StartsWith("() {", StringComparison.Ordinal))
                blocked.Add(key);
        }
        return blocked.Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public bool HasDangerousEnvironmentVariables(Dictionary<string, string> environmentVariables)
        => GetDangerousEnvironmentVariableNames(environmentVariables).Count > 0;
}
