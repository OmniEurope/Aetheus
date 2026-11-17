// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.Servers;

/// <summary>The bounds of a host package report (R-41), shared by the agent that writes it and the back that reads it.</summary>
public static class HostPackageLimits
{
    public const int MaxPackages = 20_000;
    public const int MaxNameLength = 256;
    public const int MaxVersionLength = 256;
    public const int MaxArchitectureLength = 32;
    public const int MaxOsIdLength = 64;
    public const int MaxOsVersionLength = 128;
    public const int MaxOsPrettyNameLength = 256;
    public const int MaxKernelReleaseLength = 128;
}

/// <summary>One installed Debian-family package: its binary name, version and architecture, and the source package it
/// was built from (the name the distribution advisories use), which dpkg leaves empty when it is the binary's own.</summary>
public sealed record HostPackageDto
{
    [Required]
    [StringLength(HostPackageLimits.MaxNameLength, MinimumLength = 1)]
    public string Name { get; init; } = string.Empty;

    [Required]
    [StringLength(HostPackageLimits.MaxVersionLength, MinimumLength = 1)]
    public string Version { get; init; } = string.Empty;

    /// <summary>The dpkg architecture (<c>amd64</c>, <c>i386</c>, <c>all</c>); null from an agent older than this field.
    /// Two architectures of one multiarch package are two packages.</summary>
    [StringLength(HostPackageLimits.MaxArchitectureLength)]
    public string? Architecture { get; init; }

    [StringLength(HostPackageLimits.MaxNameLength)]
    public string? SourceName { get; init; }

    [StringLength(HostPackageLimits.MaxVersionLength)]
    public string? SourceVersion { get; init; }
}

/// <summary>
/// R-41: what an agent publishes of its host, once a day and at start: the system release read from
/// <c>/etc/os-release</c> and every installed package. The whole stored inventory of that server is replaced by it, so a
/// package missing here is a package removed, never a truncated report (the agent sends nothing rather than a partial list).
/// A host whose package manager the watch cannot read (an RPM system) says so with <see cref="PackageManagerSupported"/>
/// false and no package: the server is then "system not covered", never "nothing found".
/// </summary>
public sealed record HostPackageInventoryReportDto : IValidatableObject
{
    /// <summary>The <c>ID</c> of os-release (<c>ubuntu</c>, <c>debian</c>).</summary>
    [Required]
    [StringLength(HostPackageLimits.MaxOsIdLength, MinimumLength = 1)]
    public string OsId { get; init; } = string.Empty;

    /// <summary>The <c>VERSION_ID</c> of os-release (<c>24.04</c>, <c>12</c>); absent on a rolling release.</summary>
    [StringLength(HostPackageLimits.MaxOsIdLength)]
    public string? OsVersionId { get; init; }

    /// <summary>The <c>VERSION</c> of os-release (<c>24.04.1 LTS (Noble Numbat)</c>), which says whether a release is LTS.</summary>
    [StringLength(HostPackageLimits.MaxOsVersionLength)]
    public string? OsVersion { get; init; }

    [StringLength(HostPackageLimits.MaxOsPrettyNameLength)]
    public string? OsPrettyName { get; init; }

    /// <summary>The release of the kernel the host is running, read from <c>/proc/sys/kernel/osrelease</c> (<c>6.8.0-147-generic</c>):
    /// the CVE watch counts the findings of that kernel's packages only, not those of older kernels left installed. Null from
    /// an agent older than this field or when the file cannot be read: every installed kernel is then matched.</summary>
    [StringLength(HostPackageLimits.MaxKernelReleaseLength)]
    public string? RunningKernelRelease { get; init; }

    /// <summary>False when the host has no dpkg (an RPM system): the report then names the system only. True by default,
    /// which is what an agent older than this field sends.</summary>
    public bool PackageManagerSupported { get; init; } = true;

    [Required]
    [MaxLength(HostPackageLimits.MaxPackages)]
    public List<HostPackageDto> Packages { get; init; } = [];

    /// <summary>A dpkg host always lists packages: an empty list there is a failed read, which would pass for a clean
    /// host. Only a host without dpkg, on a system that is not of the dpkg family, may send none, and then must.</summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (PackageManagerSupported && Packages.Count == 0)
            yield return new ValidationResult("A host with a supported package manager reports at least one package.", [nameof(Packages)]);
        if (!PackageManagerSupported && Packages.Count > 0)
            yield return new ValidationResult("A host without a supported package manager reports no package.", [nameof(Packages)]);
        if (!PackageManagerSupported && DpkgHostPackages.IsDpkgFamily(OsId))
            yield return new ValidationResult("A Debian-family system always has dpkg.", [nameof(PackageManagerSupported), nameof(OsId)]);
    }
}
