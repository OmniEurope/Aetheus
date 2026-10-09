// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Projects;

/// <summary>
/// PLAN-013 lot 4 (recette R2-115): how much data a project keeps. Each profile sets the five values of
/// <see cref="DataRetentionValues"/>; a project may override any of them. Standard is zero so every
/// existing project, and every project created without a choice, is Standard.
/// </summary>
public enum DataRetentionProfile
{
    Standard = 0,
    Light = 1,
    Complete = 2
}
