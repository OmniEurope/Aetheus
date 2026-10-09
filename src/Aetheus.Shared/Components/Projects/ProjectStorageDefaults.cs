// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Projects;

/// <summary>PLAN-013 lot 5 (recette R2-115): the storage budget of a project.</summary>
public static class ProjectStorageDefaults
{
    public const int DefaultBudgetMegabytes = 200;
    public const int MinimumBudgetMegabytes = 10;
    public const int MaximumBudgetMegabytes = 100_000;

    /// <summary>The usage at which the project's followers are alerted.</summary>
    public const int AlertPercent = 80;

    /// <summary>Over budget, the oldest detail is purged until the usage is back to this share of it,
    /// so the project does not sit at the budget waiting for the next sweep (as the audience budget).</summary>
    public const int PurgeTargetPercent = 90;

    public static long BudgetBytes(int? megabytes) =>
        (long)Math.Clamp(megabytes ?? DefaultBudgetMegabytes, MinimumBudgetMegabytes, MaximumBudgetMegabytes) * 1024 * 1024;
}
