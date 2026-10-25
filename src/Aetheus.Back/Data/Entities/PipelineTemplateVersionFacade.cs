// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Data.Entities;

/// <summary>The content of a published pipeline template version, as the template compatibility facade reads and writes it.</summary>
public interface IPipelineTemplateVersionContent
{
    int Version { get; set; }
    string YamlContent { get; set; }
    string ChangelogEntry { get; set; }
    string CreatedByUsername { get; set; }
}

/// <summary>
/// The logic behind the <see cref="PipelineTemplate"/> compatibility properties (<c>YamlContent</c>, <c>Changelog</c>). It
/// works on the version collection through <see cref="IPipelineTemplateVersionContent"/>, so the template entity keeps
/// pointing at its version entity only through the <c>Versions</c> navigation (ADR-056, EF navigations are not cycles).
/// </summary>
internal static class PipelineTemplateVersionFacade
{
    public static string LatestYaml<TVersion>(IEnumerable<TVersion> versions) where TVersion : IPipelineTemplateVersionContent =>
        versions.OrderByDescending(version => version.Version).FirstOrDefault()?.YamlContent ?? string.Empty;

    public static void SetYaml<TVersion>(ICollection<TVersion> versions, int latestVersion, string yaml)
        where TVersion : IPipelineTemplateVersionContent, new()
    {
        var existing = versions.FirstOrDefault(item => item.Version == latestVersion);
        if (existing is null)
        {
            versions.Add(new TVersion
            {
                Version = latestVersion,
                YamlContent = yaml,
                ChangelogEntry = "Initial version",
                CreatedByUsername = "system"
            });
        }
        else
        {
            existing.YamlContent = yaml;
        }
    }

    public static string Changelog<TVersion>(IEnumerable<TVersion> versions) where TVersion : IPipelineTemplateVersionContent =>
        string.Join('\n', versions.OrderBy(version => version.Version)
            .Select(version => $"v{version.Version}: {version.ChangelogEntry}"));
}
