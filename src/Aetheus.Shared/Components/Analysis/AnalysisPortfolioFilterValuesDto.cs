// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Shared.Components.Analysis;

/// <summary> The values the analysis portfolio's checkable column filters offer, read across every
/// report the caller can see (the grid is loaded page by page, so the rows on screen are not
/// enough). </summary>
public sealed record AnalysisPortfolioFilterValuesDto
{
    public List<string> Organizations { get; init; } = [];
    public List<string> Projects { get; init; } = [];
    public List<string> Scanners { get; init; } = [];
}
