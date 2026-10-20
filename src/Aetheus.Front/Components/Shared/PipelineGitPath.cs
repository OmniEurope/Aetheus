// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

/// <summary>Uses the backend’s <c>.pipeline/&lt;slug&gt;.yaml</c> storage convention for
/// project-owned pipeline indicators (ADR-015).</summary>
public static class PipelineGitPath
{
    public static string ForPipeline(string name) => $".pipeline/{PipelineGitPathPolicy.Slugify(name)}.yaml";
}
