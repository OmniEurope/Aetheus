// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Projects;

/// <summary>Sets five retention defaults that individual projects can override. Standard is zero so
/// unspecified values resolve to that profile.</summary>
public enum DataRetentionProfile
{
    Standard = 0,
    Light = 1,
    Complete = 2
}
