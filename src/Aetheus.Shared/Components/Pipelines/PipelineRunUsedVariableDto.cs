// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Shared.Components.Pipelines;

/// <summary>Whether a variable a run read came from a variable library or from a vault.</summary>
public enum PipelineRunResourceKind
{
    Library = 0,
    Vault = 1
}

/// <summary>
/// R-37: one variable a run read from a variable library or a vault, with the source that holds it. The run's
/// resolved variables are one merged dictionary that forgets where each key came from; this names the source,
/// so the run page can link each variable to the library or vault page.
/// </summary>
public sealed record PipelineRunUsedVariableDto
{
    public PipelineRunResourceKind Kind { get; init; }

    /// <summary>The library or vault that holds the value the run read (the one that wins when several declare
    /// the key); null when it could not be named.</summary>
    public int? SourceId { get; init; }

    public string SourceName { get; init; } = string.Empty;

    /// <summary>The variable's name (a library entry key or a vault secret name).</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The value the run recorded for a library entry. Always null for a vault secret: a secret's
    /// value never leaves the backend, only <see cref="IsAvailable"/> does.</summary>
    public string? Value { get; init; }

    /// <summary>A vault secret: it holds a non-empty value now. A library entry: the run recorded a non-empty value.</summary>
    public bool IsAvailable { get; init; }
}
