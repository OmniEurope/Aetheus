// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

/// <summary> The states the pipelines summary strip counts and filters on. Every one is derived
/// from data the catalogue already holds (the newest run of <see
/// cref="PipelineDependencyDto.RecentRuns"/> and the fleet freshness), so the strip costs no
/// request of its own. </summary>
public enum PipelineCatalogStatus
{
    /// <summary>The newest run failed.</summary>
    Failed,

    /// <summary>The newest run has not finished: pending, running or waiting for an approval.</summary>
    Running,

    /// <summary>The pipeline has no run at all.</summary>
    NeverRun,

    /// <summary>The pipeline is pinned to an older version of its model (fleet data only).</summary>
    Outdated,

    /// <summary>Identify pipelines using the latest model version from the fleet data.</summary>
    Current,

    /// <summary>Identify pipelines without a catalogue model from the fleet data.</summary>
    OffCatalog
}
