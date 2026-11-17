// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Analysis;

/// <summary>Where an analysis report comes from: the run of a pipeline, or the CVE watch that writes its own
/// reports with no pipeline. Stored nowhere: derived from the report. Other sources will be added here.</summary>
public enum AnalysisScanSource
{
    Pipeline = 0,
    CveWatch = 1
}
