// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Analysis;

/// <summary> The Gate tab's column filters and sort, applied by the database to a run set's
/// findings. The finding's own fields (status, category, severity, title, rule) narrow the
/// occurrences before they are grouped; "new", which is worked out per group, filters the grouped
/// rows. </summary>
internal static class AnalysisRunFindingFilters
{
    public static IQueryable<AnalysisFindingOccurrence> Narrow(
        IQueryable<AnalysisFindingOccurrence> occurrences, AnalysisRunFindingsRequest request)
    {
        // A fixed finding is no decision (recette R1-35): it is listed with neither the open nor the decided.
        occurrences = request.IncludeDecided
            ? occurrences.Where(occurrence => occurrence.AnalysisFinding.Status != AnalysisFindingStatus.Fixed)
            : occurrences.Where(occurrence => occurrence.AnalysisFinding.Status == AnalysisFindingStatus.Open);
        if (request.Statuses is { Count: > 0 } statuses)
            occurrences = occurrences.Where(occurrence => statuses.Contains(occurrence.AnalysisFinding.Status));
        if (request.Categories is { Count: > 0 } categories)
            occurrences = occurrences.Where(occurrence => categories.Contains(occurrence.AnalysisFinding.Category));
        if (request.Severities is { Count: > 0 } severities)
            occurrences = occurrences.Where(occurrence => severities.Contains(occurrence.AnalysisFinding.Severity));
        // The ID column's number filter.
        if (request.IdFrom is { } idFrom)
            occurrences = occurrences.Where(occurrence => occurrence.AnalysisFindingId >= idFrom);
        if (request.IdTo is { } idTo)
            occurrences = occurrences.Where(occurrence => occurrence.AnalysisFindingId <= idTo);
        if (request.IdNot is { } idNot)
            occurrences = occurrences.Where(occurrence => occurrence.AnalysisFindingId != idNot);
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var search = request.Search.Trim().ToLowerInvariant();
            occurrences = occurrences.Where(occurrence => occurrence.AnalysisFinding.Title.ToLower().Contains(search)
                || occurrence.AnalysisFinding.RuleId.ToLower().Contains(search));
        }
        return occurrences;
    }

    public static IQueryable<AnalysisRunGateFindingDto> Apply(
        IQueryable<AnalysisRunGateFindingDto> findings, AnalysisRunFindingsRequest request) =>
        request.IsNew is { } isNew ? findings.Where(finding => finding.IsNew == isNew) : findings;

    /// <summary>Most severe first by default, as the Gate tab always listed them.</summary>
    public static IQueryable<AnalysisRunGateFindingDto> Sort(
        IQueryable<AnalysisRunGateFindingDto> findings, AnalysisRunFindingsRequest request)
    {
        var descending = request.SortDescending;
        return request.SortBy switch
        {
            "Id" => descending ? findings.OrderByDescending(f => f.FindingId) : findings.OrderBy(f => f.FindingId),
            "Severity" => (descending ? findings.OrderByDescending(f => f.Severity) : findings.OrderBy(f => f.Severity)).ThenBy(f => f.FindingId),
            "Category" => (descending ? findings.OrderByDescending(f => f.Category) : findings.OrderBy(f => f.Category)).ThenBy(f => f.FindingId),
            "RuleId" => (descending ? findings.OrderByDescending(f => f.RuleId) : findings.OrderBy(f => f.RuleId)).ThenBy(f => f.FindingId),
            "Title" => (descending ? findings.OrderByDescending(f => f.Title) : findings.OrderBy(f => f.Title)).ThenBy(f => f.FindingId),
            "Status" => (descending ? findings.OrderByDescending(f => f.Status) : findings.OrderBy(f => f.Status)).ThenBy(f => f.FindingId),
            _ => findings.OrderByDescending(f => f.Severity).ThenBy(f => f.Category).ThenBy(f => f.FindingId)
        };
    }
}
