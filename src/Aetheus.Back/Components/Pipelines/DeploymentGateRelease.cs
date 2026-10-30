// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Pipelines;

/// <summary>The release a deployment names, as the grade gate reads it before the run starts. A
/// null <paramref name="AssuranceGrade"/> means the release carries no sealed grade; a null
/// <paramref name="Deployable"/> means the Gate gave it no verdict (accepted or rejected).
/// <paramref name="BlockingTestCount"/>, <paramref name="KeptArtifactCount"/> and
/// <paramref name="OpenSecurityFindingCount"/> feed the R-72 warnings and the artifact check of a
/// deployment launched despite the gate.</summary>
public sealed record DeploymentGateRelease(
    int Id, string Version, AnalysisGrade? AssuranceGrade, bool? Deployable = null,
    int? BlockingTestCount = null, int KeptArtifactCount = 0, int OpenSecurityFindingCount = 0);
