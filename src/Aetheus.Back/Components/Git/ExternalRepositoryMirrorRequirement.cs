// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Git;

/// <summary>
/// Recette R-4 (decision of 2026-11-10, option B): every read of an external repository goes through a project mirror.
/// A project that names an external address without a mirror is refused with this message, never read from that
/// address: no branch resolution, no definition read and no checkout of the raw URL.
/// </summary>
public static class ExternalRepositoryMirrorRequirement
{
    public const string Message =
        "This project names an external repository address but has no project mirror. Aetheus reads an external "
        + "repository only through a project mirror: create the mirror from the project page (Repository card), then try again.";

    /// <summary>The refusal a caller throws instead of reading the external address.</summary>
    public static BadRequestException Refusal() => new(Message);
}
