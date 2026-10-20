// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Pipelines;

/// <summary>Size bound of a pipeline definition entered through the API or the UI. The stored column is
/// unbounded text and the repository sync applies no limit, so this bound only has to stay above the
/// largest definition a repository carries (aetheus-ci and aetheus-deploy-prod exceed 60 000 characters).</summary>
public static class PipelineDefinitionLimits
{
    public const int MaxYamlLength = 200_000;
}
