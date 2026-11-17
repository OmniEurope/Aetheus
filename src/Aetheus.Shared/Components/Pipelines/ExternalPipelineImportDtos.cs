// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.Pipelines;

/// <summary>R-11 (decision of 2026-11-10, option A): where a pipeline file of an external repository's mirror stands
/// against the project's pipelines.</summary>
public enum ExternalPipelineImportState
{
    /// <summary>In the mirror, absent from the database, and accepted by the import policy: it can be imported.</summary>
    Available = 0,

    /// <summary>Imported, and the mirror still holds the same definition.</summary>
    Imported = 1,

    /// <summary>Imported, but the mirror's definition changed since: a new explicit import is needed to take it.</summary>
    ChangedSinceImport = 2,

    /// <summary>Refused by the import policy (unreadable, too large, or asking for something an external definition
    /// is never granted); the reasons are listed.</summary>
    Refused = 3,

    /// <summary>A pipeline defined in Aetheus already has this name: the file is never imported over it.</summary>
    NameTaken = 4
}

/// <summary>One <c>.pipeline/*.yaml</c> file of an external repository's mirror, read at the mirror's head commit.</summary>
public sealed record ExternalPipelineCandidateDto
{
    public int RepositoryId { get; init; }
    public string RepositoryName { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;

    /// <summary>The mirror commit the file was read at; an import names it, so what is imported is what was reviewed.</summary>
    public string CommitSha { get; init; } = string.Empty;

    public ExternalPipelineImportState State { get; init; }

    /// <summary>The project's pipeline of that name, when there is one.</summary>
    public int? PipelineId { get; init; }

    /// <summary>The commit of the import, for an imported pipeline.</summary>
    public string? ImportedCommitSha { get; init; }

    public List<string> Problems { get; init; } = [];
}

/// <summary>The pipeline files of every external repository mirror of a project.</summary>
public sealed record ExternalPipelineCandidatesDto
{
    /// <summary>False when the project has no external repository mirror: nothing to show.</summary>
    public bool HasExternalRepository { get; init; }

    public List<ExternalPipelineCandidateDto> Items { get; init; } = [];

    /// <summary>A mirror that could not be read, by name, with the reason.</summary>
    public List<string> UnreadableRepositories { get; init; } = [];
}

/// <summary>Imports one file of a mirror at the commit the user reviewed.</summary>
public sealed record ImportExternalPipelineRequest
{
    [Range(1, int.MaxValue)]
    public int RepositoryId { get; init; }

    [Required]
    [StringLength(255)]
    public string FilePath { get; init; } = string.Empty;

    [Required]
    [StringLength(64, MinimumLength = 40)]
    public string CommitSha { get; init; } = string.Empty;
}
