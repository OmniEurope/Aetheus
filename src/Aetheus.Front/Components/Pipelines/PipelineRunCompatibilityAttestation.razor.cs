// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// The COMPATIBILITY_MODE, PREVIOUS_VERSION_TESTED and COMPATIBILITY_VERDICT outputs that the
/// <c>persist-compatibility-mode</c> pipeline function publishes, read from the run's steps (the
/// last value of each wins). Runs stored before the function, or still produced by
/// <c>deploy/scripts/qa/persist-compatibility-mode.sh</c>, carry the PascalCase names
/// (CompatibilityMode, PreviousVersionTested, CompatibilityVerdict): each is read when its upper snake
/// case name is absent from every step or empty. The known codes are given in words with what they prove; an unknown code
/// is shown as it is, never guessed.
/// </summary>
public partial class PipelineRunCompatibilityAttestation
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public PipelineRunDto Run { get; set; } = default!;

    /// <summary>The run's repository when it resolved to one; the commit link falls back to the
    /// project's repository list.</summary>
    [Parameter] public int? RepoId { get; set; }

    /// <summary>What the function writes as PREVIOUS_VERSION_TESTED when there is no V-1 (bootstrap).</summary>
    internal const string NoPrevious = "false";

    private static readonly HashSet<string> ModeCodes = new(StringComparer.Ordinal) { "NMinusOne", "Bootstrap" };

    private static readonly HashSet<string> VerdictCodes = new(StringComparer.Ordinal)
    {
        "NMinusOneVerified", "NMinusOneDegraded", "CurrentVersionVerified", "CurrentVersionDegraded"
    };

    internal const string ModeOutput = "COMPATIBILITY_MODE";
    internal const string PreviousOutput = "PREVIOUS_VERSION_TESTED";
    internal const string VerdictOutput = "COMPATIBILITY_VERDICT";

    /// <summary>Each output with the name the legacy script published it under.</summary>
    private static readonly Dictionary<string, string> LegacyNames = new(StringComparer.Ordinal)
    {
        [ModeOutput] = "CompatibilityMode",
        [PreviousOutput] = "PreviousVersionTested",
        [VerdictOutput] = "CompatibilityVerdict"
    };

    private Dictionary<string, string> _outputs = new(StringComparer.Ordinal);

    private string? Mode => _outputs.GetValueOrDefault(ModeOutput);

    private string? Previous => _outputs.GetValueOrDefault(PreviousOutput);

    private string? Verdict => _outputs.GetValueOrDefault(VerdictOutput);

    /// <summary>The V-1 commit, when PREVIOUS_VERSION_TESTED holds one (the function writes a full SHA).</summary>
    private string? PreviousSha => Previous is { } value && CommitSha().IsMatch(value) ? value : null;

    private string? PreviousHref => PreviousSha is not { } sha
        ? null
        : RepoId is { } repoId
            ? $"/git-repositories/{repoId}/commits/{Uri.EscapeDataString(sha)}"
            : Run.ProjectId is { } projectId ? $"/git-repositories?projectId={projectId}" : null;

    protected override void OnParametersSet()
    {
        var published = Run.Steps
            .SelectMany(step => step.OutputVariables)
            .Where(output => LegacyNames.ContainsKey(output.Key) || LegacyNames.ContainsValue(output.Key))
            .GroupBy(output => output.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal);
        _outputs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, legacy) in LegacyNames)
        {
            if ((published.TryGetValue(name, out var value) && value.Length > 0) || published.TryGetValue(legacy, out value))
                _outputs[name] = value;
        }
    }

    private string Readable(HashSet<string> known, string keyPrefix, string code) =>
        known.Contains(code) ? L[keyPrefix + code].Value : code;

    private string? Hint(HashSet<string> known, string keyPrefix, string code) =>
        known.Contains(code) ? L[keyPrefix + code].Value : null;

    private static OmniTone VerdictVariant(string verdict) => verdict switch
    {
        "NMinusOneVerified" or "CurrentVersionVerified" => OmniTone.Success,
        "NMinusOneDegraded" or "CurrentVersionDegraded" => OmniTone.Warning,
        _ => OmniTone.Neutral
    };

    [GeneratedRegex("^[0-9a-fA-F]{7,64}$")]
    private static partial Regex CommitSha();
}
