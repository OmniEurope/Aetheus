// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>Application adapters are language-agnostic; the stack selects the implemented web
/// analytics package template.</summary> <param name="Key">Technology name used in the wizard
/// address.</param> <param name="LabelKey">Resource key for the visible name.</param> <param
/// name="AnalyticsPackageTemplate">Template that builds its web analytics package.</param>
public sealed record PipelineSetupStack(string Key, string LabelKey, string AnalyticsPackageTemplate)
{
    public static readonly PipelineSetupStack DotNet =
        new("dotnet", "PipelineStackDotNet", "package-web-analytics-dotnet");

    /// <summary>Every technology the wizard can really generate for.</summary>
    public static readonly IReadOnlyList<PipelineSetupStack> All = [DotNet];

    public static PipelineSetupStack? Find(string? key) =>
        All.FirstOrDefault(stack => string.Equals(stack.Key, key, StringComparison.OrdinalIgnoreCase));
}
