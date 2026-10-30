// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// A copied pipeline reads the branch of the project it is copied INTO, never the one its source project worked on: a
/// definition that says <c>source_branch: "develop"</c> would otherwise keep reading <c>develop</c> in a project whose
/// repository only has <c>main</c> (recette R-76). Only the pipeline's own top-level <c>source_branch:</c> key is touched;
/// the indented <c>source_branch:</c> of a trigger step names another pipeline's branch and stays. The branch is the one the
/// setup wizard would choose for the target project (<c>PipelineSetupSourceBranch</c>); like the wizard, no branch means no
/// <c>source_branch:</c> line, so the copy reads what the target project reads by default.
/// </summary>
internal static partial class PipelineCopySourceBranch
{
    [GeneratedRegex(@"^source_branch[ \t]*:[^\r\n]*(?<eol>\r?\n)?", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex SourceBranchLine { get; }

    /// <summary>The definition with its top-level <c>source_branch:</c> set to <paramref name="targetBranch"/>, or removed when
    /// that is null or blank. A definition without the key is returned as it is: its pipeline row carries the target
    /// branch instead.</summary>
    /// <exception cref="BadRequestException">The branch is not a valid branch name (it is written into the definition).</exception>
    internal static string Rewrite(string yaml, string? targetBranch)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var branch = targetBranch?.Trim();
        if (!string.IsNullOrEmpty(branch) && !PipelineBranchValidator.IsValid(branch))
            throw new BadRequestException("The target branch is not a valid branch name.");

        return SourceBranchLine.Replace(yaml, match => string.IsNullOrEmpty(branch)
            ? string.Empty
            : $"source_branch: \"{branch}\"{match.Groups["eol"].Value}", 1);
    }
}
