// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Analysis;

/// <summary>Whether every required measure behind a grade was observed (recette R-73, R-74). It is not a state of the
/// grade: a grade is always given, from the measures that were observed, and a domain or a run is never shown as
/// "incomplete". A required measure nobody observed is listed unobserved, with the step that should have produced it
/// (<see cref="AnalysisGradeMeasureDto.ProducerStep"/>). The flag is kept for the sealed candidate contract, which holds
/// a grade resting on missing evidence back as unavailable, and for the stored evaluations.</summary>
public enum AnalysisGradeCompleteness
{
    Incomplete = 0,
    Complete = 1
}
