// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Shared.Components.Projects;

public sealed record ProjectPaginationRequest : PaginationRequest
{
    public ProjectStatus? ProjectStatus { get; init; }
}
