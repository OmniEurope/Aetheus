// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary> The run's per-file coverage as a tree, projects first, then each project's files.
/// Projects are grouped by <see cref="CoverageProjectName"/>, the rule the per-project table of the
/// API uses, and both levels list the least covered first, as the report stores its files.
/// </summary>
internal static class CoverageProjectTree
{
    internal sealed record Project(string Name, int LinesCovered, int LinesValid, IReadOnlyList<CoverageFileDto> Files)
    {
        public double LineRate => LinesValid == 0 ? 0 : (double)LinesCovered / LinesValid;
    }

    public static IReadOnlyList<Project> Build(IEnumerable<CoverageFileDto> files) =>
        files
            .GroupBy(CoverageProjectName.Of, StringComparer.Ordinal)
            .Select(group => new Project(
                group.Key,
                group.Sum(file => file.LinesCovered),
                group.Sum(file => file.LinesValid),
                group.OrderBy(file => file.LineRate).ThenBy(file => file.File, StringComparer.Ordinal).ToList()))
            .OrderBy(project => project.LineRate)
            .ThenBy(project => project.Name, StringComparer.Ordinal)
            .ToList();
}
