// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.VariableLibraries;

public sealed record VariableLibraryDto : OwnedResourceDto
{
    public int EntryCount { get; init; }
}

public sealed record VariableLibraryDetailDto : OwnedResourceDto
{
    public int EntryCount { get; init; }
    public List<VariableEntryDto> Entries { get; init; } = [];
}

public sealed record VariableEntryDto
{
    public int Id { get; init; }
    public string Key { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    public int VersionCount { get; init; }
}

public sealed record VariableEntryVersionDto
{
    public int Version { get; init; }
    public string Key { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    public DateTime ChangedAt { get; init; }
    public ChangeType ChangeType { get; init; }
}

[AtMostOneOwner]
public sealed record CreateVariableLibraryRequest : OwnedResourceRequest;

[AtMostOneOwner]
public sealed record UpdateVariableLibraryRequest : VersionedOwnedResourceRequest;

/// <summary>An empty value is a value: <c>HOST_PREFIX</c> is empty in production on purpose.</summary>
public sealed record CreateVariableEntryRequest : KeyValueRequest
{
    [StringLength(MaxValueLength)]
    public override string Value { get; init; } = string.Empty;
}

public sealed record UpdateVariableEntryRequest : KeyValueRequest
{
    [StringLength(MaxValueLength)]
    public override string Value { get; init; } = string.Empty;
}

/// <summary> The values the variable libraries list's checkable column filters offer. The list is
/// loaded page by page, so the project names present across every variable libraries the caller can
/// read come from the API rather than from the rows on screen. </summary>
public sealed record VariableLibraryFilterValuesDto
{
    public List<string> ProjectNames { get; init; } = [];
}
