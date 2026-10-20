// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Pipelines;

/// <summary>The release a deployment names, as the grade gate reads it before the run starts. A
/// null <paramref name="AssuranceGrade"/> means the release carries no sealed grade.</summary>
public sealed record DeploymentGateRelease(int Id, string Version, AnalysisGrade? AssuranceGrade);
