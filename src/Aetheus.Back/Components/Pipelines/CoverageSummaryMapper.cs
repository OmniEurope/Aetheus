// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

internal static class CoverageSummaryMapper
{
    public static CoverageResult SelectCanonical(IEnumerable<CoverageResult> results) => results
        .OrderByDescending(result => result.LinesValid)
        .ThenByDescending(result => result.BranchesValid)
        .ThenByDescending(result => result.CreatedAt)
        .First();

    public static PipelineCoverageSummaryDto Map(CoverageResult result)
    {
        var files = string.IsNullOrEmpty(result.FilesJson)
            ? []
            : JsonSerializer.Deserialize<List<CoverageFileDto>>(result.FilesJson) ?? [];
        return new PipelineCoverageSummaryDto
        {
            LineRate = result.LineRate,
            BranchRate = result.BranchRate,
            LinesCovered = result.LinesCovered,
            LinesValid = result.LinesValid,
            BranchesCovered = result.BranchesCovered,
            BranchesValid = result.BranchesValid,
            RunId = result.PipelineRunId,
            Files = files
        };
    }

    public static List<CoverageAssemblyDto> MapAssemblies(CoverageResult result)
    {
        var files = string.IsNullOrEmpty(result.FilesJson)
            ? []
            : JsonSerializer.Deserialize<List<CoverageFileDto>>(result.FilesJson) ?? [];
        // Files without a recorded package are grouped by the project inferred from their path,
        // using the same rule as the coverage tree.
        return files
            .GroupBy(CoverageProjectName.Of, StringComparer.Ordinal)
            .Select(group =>
            {
                var covered = group.Sum(file => file.LinesCovered);
                var valid = group.Sum(file => file.LinesValid);
                return new CoverageAssemblyDto
                {
                    Name = group.Key,
                    LinesCovered = covered,
                    LinesValid = valid,
                    LineRate = valid == 0 ? 0 : (double)covered / valid
                };
            })
            .OrderBy(assembly => assembly.LineRate)
            .ThenBy(assembly => assembly.Name, StringComparer.Ordinal)
            .ToList();
    }
}
