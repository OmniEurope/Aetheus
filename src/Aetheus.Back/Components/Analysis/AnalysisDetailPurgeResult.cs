// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Analysis;

/// <summary>What one sweep deleted.</summary>
public sealed class AnalysisDetailPurgeResult
{
    public int Reports { get; set; }
    public long Metrics { get; set; }
    public long Components { get; set; }
    public int Batches { get; set; }
}
