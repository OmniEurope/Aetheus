// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Analysis;

/// <summary>What to do about one vulnerable package of a project (PLAN-004, "Action par CVE"). Computed, never written by a model.</summary>
public enum CveRecommendationKind
{
    /// <summary>A fixed version exists in the major the project is on.</summary>
    BumpDependency,

    /// <summary>The fix only exists in a higher major.</summary>
    MajorUpgrade,

    /// <summary>The base image of a Dockerfile moves to a fixed one.</summary>
    BumpBaseImage,

    /// <summary>A runtime (dotnet, node, php, go) moves to a fixed version.</summary>
    BumpRuntime,

    /// <summary>The operating system package is updated on the server.</summary>
    OsUpdate,

    /// <summary>No fix is published: mitigate and follow.</summary>
    NoFixYet,

    /// <summary>The version is out of support: no fix is to be expected.</summary>
    EndOfLife
}

/// <summary>The recommendation of one match of the vulnerability watch.</summary>
public sealed record CveWatchRecommendationDto
{
    public CveRecommendationKind Kind { get; init; }

    /// <summary>The version to move to, null for <see cref="CveRecommendationKind.NoFixYet"/>, <see cref="CveRecommendationKind.EndOfLife"/>
    /// and <see cref="CveRecommendationKind.OsUpdate"/> without a stated version.</summary>
    public string? TargetVersion { get; init; }

    /// <summary>The files of the project that name the package, to change for a bump. Empty when the inventory has no path.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>
    /// True when <see cref="TargetVersion"/> was read from the package's registry and found free of every known
    /// advisory; false when it is the version the advisory states, which no registry confirmed (the list
    /// request does no network call, the detail read does).
    /// </summary>
    public bool VerifiedAgainstRegistry { get; init; }
}
