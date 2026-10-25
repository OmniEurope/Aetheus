// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Aetheus.Shared.Components.Pipelines;

/// <summary>How the production setup treats one variable of the project's Compose file (ADR-057).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ProductionVariableKind>))]
public enum ProductionVariableKind
{
    /// <summary>The default deployment already provides it (release, commit, hosts, URLs, SDK version).</summary>
    Provided,

    /// <summary>A free port of the production server, reserved in the port registry.</summary>
    Port,

    /// <summary>A secret that belongs to the application alone (database password, signing key): a random
    /// value is generated into the vault <c>prod</c>.</summary>
    GeneratedSecret,

    /// <summary>A setting with a convention that leaves no doubt (application name, environment, database user)
    /// or a default the Compose file itself names: written into the library <c>prod</c>.</summary>
    DefaultValue,

    /// <summary>Only the operator knows it (an address, a mail server, an external credential): the wizard asks
    /// for it and never leaves it silently empty.</summary>
    NeedsValue,

    /// <summary>The Compose file names a default for it: nothing is created, the default applies.</summary>
    Optional
}

/// <summary>What happened to a variable when the production setup was applied.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ProductionEntryOutcome>))]
public enum ProductionEntryOutcome
{
    Created,
    Generated,
    Allocated,

    /// <summary>The library or vault already held the entry: it is never overwritten.</summary>
    Kept,

    /// <summary>Created empty because no value was given: the deployment stops until it is filled in.</summary>
    NeedsValue,

    /// <summary>No port could be reserved on the chosen server.</summary>
    PortUnavailable,

    /// <summary>The entry existed under a name the agent refuses and now carries its value under the scoped name.</summary>
    Renamed
}

/// <summary>One variable the Compose file of the production deployment reads.</summary>
public sealed record ProductionVariableDto
{
    public string Name { get; init; } = string.Empty;
    public ProductionVariableKind Kind { get; init; }

    /// <summary>The value belongs in the vault, not in a library, and is never shown.</summary>
    public bool Secret { get; init; }

    /// <summary>The library or vault <c>prod</c> already holds an entry of this name.</summary>
    public bool Exists { get; init; }

    /// <summary>The proposed value of a <see cref="ProductionVariableKind.DefaultValue"/>; never a secret.</summary>
    public string? DefaultValue { get; init; }
}

/// <summary>A server of the project the production deployment can run on.</summary>
public sealed record ProductionServerDto
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
}

/// <summary>The repository of a project's system configuration files (Apache virtual hosts, environment
/// sample): versioned in Git, editable by the user, read by the deployment (ADR-057).</summary>
public sealed record ProjectConfigRepositoryDto
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Slug { get; init; } = string.Empty;
    public string DefaultBranch { get; init; } = string.Empty;

    /// <summary>The repository did not exist before this call.</summary>
    public bool Created { get; init; }

    /// <summary>The files this call committed.</summary>
    public List<string> FilesAdded { get; init; } = [];

    /// <summary>The files the repository already held: never overwritten.</summary>
    public List<string> FilesKept { get; init; } = [];
}

/// <summary>What the wizard learns about the production deployment of a project from its repository.</summary>
public sealed record PipelineProductionAnalysisDto
{
    /// <summary>False when the repository could not be read: nothing below is then claimed.</summary>
    public bool Readable { get; init; }

    public string? Branch { get; init; }

    /// <summary>Every Compose file found, as a path relative to the repository root.</summary>
    public List<string> ComposeFiles { get; init; } = [];

    /// <summary>The Compose file the analysis read: the one asked for, else the one that looks like production.</summary>
    public string? ComposeFile { get; init; }

    /// <summary>The project's servers and those of its production environment, by name.</summary>
    public List<ProductionServerDto> Servers { get; init; } = [];

    /// <summary>The server proposed by default (recette R-21): the single server of the project's production
    /// environment (<c>prod</c> or <c>production</c>), else the project's only server; null when there is a choice
    /// to make or no server at all.</summary>
    public string? DefaultServer { get; init; }

    public List<ProductionVariableDto> Variables { get; init; } = [];

    /// <summary>The Compose file reads <c>PORT_FRONT</c>: the front vhost proxies to it.</summary>
    public bool DeclaresFrontPort { get; init; }

    /// <summary>The Compose file reads <c>PORT_BACK</c>: the API vhost proxies to it.</summary>
    public bool DeclaresBackPort { get; init; }

    /// <summary>The Compose file reads blue and green ports: the deployment is then a blue-green cutover (the
    /// pipeline's <c>DEPLOY_STRATEGY</c> is <c>bluegreen</c>), otherwise a replacement of the running services.</summary>
    public bool DeclaresBlueGreen { get; init; }

    /// <summary>The deployment strategy the wizard writes into the pipeline: <c>bluegreen</c> or <c>replace</c>.</summary>
    public string Strategy => DeclaresBlueGreen ? "bluegreen" : "replace";

    /// <summary>The Compose file publishes a back port, so the API has its own host and virtual hosts.</summary>
    public bool DeclaresApi => DeclaresBackPort || DeclaresBlueGreen;

    /// <summary>The configuration repository, when the project already has one.</summary>
    public ProjectConfigRepositoryDto? ConfigRepository { get; init; }

    /// <summary>The tokens of the environment sample already held by the configuration repository that name a variable
    /// the agent refuses to hand a step (recette R-5): the deployment of such a project stops at its preparation until
    /// the sample is mended. Empty when the repository holds no sample or the sample is sound.</summary>
    public List<ProductionRefusedNameDto> RefusedEnvNames { get; init; } = [];
}

/// <summary>A token of the environment sample that names a variable the agent refuses to hand a step (<c>ENV</c> is the
/// startup file of <c>sh</c>): the entry is held under <see cref="ScopedName"/> instead.</summary>
public sealed record ProductionRefusedNameDto
{
    /// <summary>The sample, as a path of the configuration repository.</summary>
    public string File { get; init; } = string.Empty;

    /// <summary>The 1-based line of the sample.</summary>
    public int Line { get; init; }

    /// <summary>The variable the line defines, as Docker Compose reads it.</summary>
    public string Variable { get; init; } = string.Empty;

    /// <summary>The refused name the placeholder carries.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The name the entry travels under once mended.</summary>
    public string ScopedName { get; init; } = string.Empty;
}

/// <summary>Mends the environment sample the configuration repository already holds.</summary>
public sealed record PipelineProductionRepairRequest
{
    [Range(1, int.MaxValue)]
    public int ProjectId { get; init; }
}

/// <summary>What mending the environment sample did.</summary>
public sealed record PipelineProductionRepairResultDto
{
    /// <summary>The sample, as a path of the configuration repository.</summary>
    public string File { get; init; } = string.Empty;

    /// <summary>False when the sample held no refused token: nothing was written.</summary>
    public bool Repaired { get; init; }

    /// <summary>The tokens that were replaced, as they were found.</summary>
    public List<ProductionRefusedNameDto> Mended { get; init; } = [];

    /// <summary>The library entries that now carry the scoped names, with what happened to each.</summary>
    public List<ProductionAppliedEntryDto> Entries { get; init; } = [];

    public bool NeedsValues => Entries.Exists(entry => entry.Outcome == ProductionEntryOutcome.NeedsValue);
}

/// <summary>Asks what the production deployment of a project needs, from the Compose file of its repository.</summary>
public sealed record PipelineProductionAnalysisRequest
{
    [Range(1, int.MaxValue)]
    public int ProjectId { get; init; }

    /// <summary>The Compose file to read, relative to the repository root; empty to let the analysis propose one.</summary>
    [StringLength(240)]
    public string? ComposeFile { get; init; }
}

/// <summary>Applies the production setup the analysis proposed.</summary>
public sealed record PipelineProductionApplyRequest
{
    [Range(1, int.MaxValue)]
    public int ProjectId { get; init; }

    /// <summary>The Compose file, relative to the repository root.</summary>
    [Required]
    [StringLength(240)]
    public string ComposeFile { get; init; } = string.Empty;

    /// <summary>The production server, by the name it has in the project; empty when the project has none (the ports
    /// are then left empty, to be allocated from the library page).</summary>
    [StringLength(200)]
    public string Server { get; init; } = string.Empty;

    /// <summary>The values the operator gave to the variables that need one, by variable name.</summary>
    [BoundedDictionary(64, 128, 4096)]
    public Dictionary<string, string> Values { get; init; } = [];
}

/// <summary>One entry written, kept or still missing after the production setup.</summary>
public sealed record ProductionAppliedEntryDto
{
    public string Name { get; init; } = string.Empty;

    /// <summary>The library or vault <c>prod</c> holds it in.</summary>
    public bool Secret { get; init; }

    public ProductionEntryOutcome Outcome { get; init; }
}

/// <summary>What the production setup did, so nothing it created is invisible and nothing missing is silent.</summary>
public sealed record PipelineProductionApplyResultDto
{
    public string ComposeFile { get; init; } = string.Empty;

    /// <summary>The server the production pipeline is bound to (its <c>deployServer</c> parameter).</summary>
    public string Server { get; init; } = string.Empty;

    /// <summary><c>bluegreen</c> or <c>replace</c>: the pipeline's <c>DEPLOY_STRATEGY</c>.</summary>
    public string Strategy { get; init; } = "replace";

    /// <summary>The pipeline's <c>DEPLOY_HAS_API</c>: the API has its own host and virtual hosts.</summary>
    public bool DeclaresApi { get; init; }

    public List<ProductionAppliedEntryDto> Entries { get; init; } = [];
    public ProjectConfigRepositoryDto ConfigRepository { get; init; } = new();

    public bool NeedsValues => Entries.Exists(entry =>
        entry.Outcome is ProductionEntryOutcome.NeedsValue or ProductionEntryOutcome.PortUnavailable);
}
