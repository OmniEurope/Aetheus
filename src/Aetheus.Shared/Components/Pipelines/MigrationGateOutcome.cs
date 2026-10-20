// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json.Serialization;

namespace Aetheus.Shared.Components.Pipelines;

/// <summary>
/// What the expand/contract gate of a blue-green <c>migrate</c> step concluded (recette R1-43).
/// Written as a name on the wire, so an agent and a control plane of different builds read the same value.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<MigrationGateOutcome>))]
public enum MigrationGateOutcome
{
    /// <summary>Every migration in the tree is already applied; nothing was inspected.</summary>
    NoPending = 0,

    /// <summary>The schema history was empty: initial creation, no live colour to stay compatible with.</summary>
    InitialCreation = 1,

    /// <summary>Every pending migration is expand-compatible; the bundle was allowed to run.</summary>
    Compatible = 2,

    /// <summary>At least one pending migration would break the colour still serving; the step stopped.</summary>
    Refused = 3,

    /// <summary>The gate could not see its inputs (sources or schema history) and failed the step.</summary>
    Unavailable = 4
}
