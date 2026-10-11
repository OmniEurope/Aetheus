// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// Recette R1-18: the technology the setup wizard generates for. The application templates are
/// language-agnostic (they run the project's own adapters); what depends on the technology is the web
/// analytics package of the observability set. .NET is the only technology implemented today, so it is
/// the only entry: another one is added here with its own package template, never offered before.
/// </summary>
/// <param name="Key">The technology's name in the wizard's address.</param>
/// <param name="LabelKey">The resource key of its visible name.</param>
/// <param name="AnalyticsPackageTemplate">The template that builds its web analytics package.</param>
public sealed record PipelineSetupStack(string Key, string LabelKey, string AnalyticsPackageTemplate)
{
    public static readonly PipelineSetupStack DotNet =
        new("dotnet", "PipelineStackDotNet", "package-web-analytics-dotnet");

    /// <summary>Every technology the wizard can really generate for.</summary>
    public static readonly IReadOnlyList<PipelineSetupStack> All = [DotNet];

    public static PipelineSetupStack? Find(string? key) =>
        All.FirstOrDefault(stack => string.Equals(stack.Key, key, StringComparison.OrdinalIgnoreCase));
}
