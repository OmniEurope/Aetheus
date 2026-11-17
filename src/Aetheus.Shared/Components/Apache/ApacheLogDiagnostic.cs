// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Apache;

/// <summary>Why the agent shows no Apache log lines (recette R-19). A missing file and a file the agent account
/// may not read are different answers: the first is a host without that log, the second a host whose log exists
/// behind a permission the agent lacks.</summary>
public enum ApacheLogReadOutcome
{
    /// <summary>None of the searched paths exists.</summary>
    NotFound,

    /// <summary>The path, or a directory above it, refuses the agent account (EACCES).</summary>
    AccessDenied,

    /// <summary>Any other error opening the file.</summary>
    ReadError
}

/// <summary>The stable line the agent writes in place of the log lines when it cannot show them, so the front
/// shows a localized sentence instead of an agent diagnostic. Format: <c>[apache-log] {outcome} {path} [{path}...]</c>;
/// the paths are the agent's fixed candidates and hold no space.</summary>
public static class ApacheLogDiagnostic
{
    public const string Prefix = "[apache-log] ";

    public static string Format(ApacheLogReadOutcome outcome, params IReadOnlyList<string> paths) =>
        Prefix + outcome + " " + string.Join(' ', paths);

    /// <summary>Finds the diagnostic line in a task output; false when the output holds log lines instead.</summary>
    public static bool TryParse(string? output, out ApacheLogReadOutcome outcome, out IReadOnlyList<string> paths)
    {
        outcome = default;
        paths = [];
        if (string.IsNullOrEmpty(output)) return false;

        var line = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith(Prefix, StringComparison.Ordinal));
        if (line is null) return false;

        var parts = line[Prefix.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !Enum.TryParse(parts[0], ignoreCase: false, out outcome) || !Enum.IsDefined(outcome))
            return false;

        paths = parts[1..];
        return true;
    }
}
