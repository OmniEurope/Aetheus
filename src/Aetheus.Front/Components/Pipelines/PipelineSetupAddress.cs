// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>The address of the wizard's summary: what a page opened from it comes back to. Nothing in it is
/// sensitive (a pipeline name prefix, option names, a branch name).</summary>
internal static class PipelineSetupAddress
{
    public static string Summary(
        int projectId, string prefix, IEnumerable<string> options, PipelineArchitecture? architecture,
        bool? sameProject, string? branch, string stepSlug) =>
        $"/pipelines/setup?projectId={projectId}&prefix={Uri.EscapeDataString(prefix)}"
        + $"&options={Uri.EscapeDataString(string.Join(",", options))}"
        + (architecture is { } chosen ? $"&architecture={PipelineSetupArchitecture.Slug(chosen)}" : string.Empty)
        + (architecture == PipelineArchitecture.Application && sameProject is { } same ? $"&sameProject={(same ? "yes" : "no")}" : string.Empty)
        + (branch is not null ? $"&branch={Uri.EscapeDataString(branch)}" : string.Empty)
        + $"&step={stepSlug}";
}
