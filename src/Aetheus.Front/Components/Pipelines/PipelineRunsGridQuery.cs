// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>Server paging requires sort and generic column filters on every request. Sending
/// additional typed filters for the same columns would apply duplicate constraints.</summary>
internal static class PipelineRunsGridQuery
{
    public static PipelineRunPaginationRequest From(GridLoadArgs args)
    {
        var (sortBy, descending) = args.ToSortRequest(
            nameof(PipelineRunDto.StartedAt), fallbackDescending: true);

        return new PipelineRunPaginationRequest
        {
            SortBy = sortBy,
            SortDescending = descending,
            Filters = args.ToApiFilters()
        };
    }
}
