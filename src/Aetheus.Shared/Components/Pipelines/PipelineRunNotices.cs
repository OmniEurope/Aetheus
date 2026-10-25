// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Pipelines;

/// <summary>
/// An information the back records on a run beside its warnings (<see cref="PipelineRunDto.Warnings"/>) that is
/// not a defect of the run (recette R-16). It travels as a message code the front localizes, never as a sentence
/// the server wrote in one language: <c>notice:{code}</c>, optionally followed by <c>|</c> and a technical detail
/// for the log reader, which the front does not show.
/// </summary>
public static class PipelineRunNotices
{
    private const string Prefix = "notice:";
    private const char DetailSeparator = '|';

    /// <summary>The restore step found no baseline (the version already deployed) to restore because the
    /// project has no earlier release: the comparison and rollback checks that read it are skipped.</summary>
    public const string BaselineAbsent = "baseline-absent";

    /// <summary>Recette R-39: the run waited for a resume after a missed confirmation and nobody resumed
    /// it in time; the previous version kept serving and the run ended rolled back.</summary>
    public const string ResumeWindowExpired = "resume-window-expired";

    /// <summary>How the earlier builds wrote the same fact, as a warning in English; runs recorded before the code
    /// existed keep it, and the front reads it as <see cref="BaselineAbsent"/>.</summary>
    private const string LegacyBaselineAbsentMarker = "explicit fallback selected";

    /// <summary>The stored form of a notice.</summary>
    public static string Compose(string code, string? detail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        return string.IsNullOrWhiteSpace(detail) ? Prefix + code : Prefix + code + DetailSeparator + detail;
    }

    /// <summary>The code of a stored warning when it is a notice, else null.</summary>
    public static string? CodeOf(string warning)
    {
        ArgumentNullException.ThrowIfNull(warning);
        if (warning.Contains(LegacyBaselineAbsentMarker, StringComparison.Ordinal)
            && warning.StartsWith("Restore-artifacts step", StringComparison.Ordinal))
            return BaselineAbsent;
        if (!warning.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var code = warning[Prefix.Length..];
        var separator = code.IndexOf(DetailSeparator, StringComparison.Ordinal);
        return separator < 0 ? code : code[..separator];
    }
}
