// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Aetheus.Shared.Components.Pipelines;

/// <summary>R-26: the signals the pipeline setup wizard prepares for the monitored application of a project: the technical
/// telemetry (performance, logs, errors) and the audience (visitors).</summary>
public sealed record PipelineSetupCollectionRequest
{
    [Range(1, int.MaxValue)]
    public int ProjectId { get; init; }

    public bool Telemetry { get; init; }

    public bool Audience { get; init; }
}

/// <summary>One thing the preparation looked at, so the wizard can name what it did for each prerequisite of a signal.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PipelineSetupCollectionKind>))]
public enum PipelineSetupCollectionKind
{
    /// <summary>The monitored application of the project exists and is enabled.</summary>
    AppEnabled,

    /// <summary>The application holds an ingestion key.</summary>
    IngestKey,

    /// <summary>The audience measurement is enabled.</summary>
    AudienceEnabled,

    /// <summary>The audience site identifier; the detail carries it.</summary>
    SiteId,

    /// <summary>The HTTPS origin of the production address is allowed; the detail carries it.</summary>
    Origins,

    /// <summary>The browser can send its events to the public ingestion.</summary>
    PublicIngest,

    /// <summary>The pseudonymisation key exists in the project's vault.</summary>
    PseudonymKey
}

/// <summary>What happened to one item: it was done by this call, it was already there (kept as it was), or it failed.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PipelineSetupCollectionOutcome>))]
public enum PipelineSetupCollectionOutcome
{
    Done,
    AlreadyPresent,
    Failed
}

public sealed record PipelineSetupCollectionItemDto
{
    public PipelineSetupCollectionKind Kind { get; init; }

    public PipelineSetupCollectionOutcome Outcome { get; init; }

    /// <summary>The value an item names (the site identifier, the origin), or, for a failure, the reason in the server's own
    /// words. Never a secret.</summary>
    public string? Detail { get; init; }
}

/// <summary>What the preparation did, item by item: nothing it prepared is invisible and nothing missing is silent.</summary>
public sealed record PipelineSetupCollectionResultDto
{
    public List<PipelineSetupCollectionItemDto> Items { get; init; } = [];

    public bool HasFailure => Items.Exists(item => item.Outcome == PipelineSetupCollectionOutcome.Failed);
}
