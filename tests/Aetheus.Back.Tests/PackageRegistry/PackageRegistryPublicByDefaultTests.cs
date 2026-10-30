// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.PackageRegistry;
using BackDefaults = Aetheus.Back.Components.PackageRegistry.PackageRegistryDefaults;

namespace Aetheus.Back.Tests.PackageRegistry;

/// <summary>R-58: Aetheus.Telemetry and Aetheus.WebAnalytics are public whatever their version; every other package starts private.</summary>
public sealed class PackageRegistryPublicByDefaultTests
{
    [Theory]
    [InlineData(PackageRegistryKind.NuGet, "aetheus.telemetry", true)]
    [InlineData(PackageRegistryKind.NuGet, "aetheus.webanalytics", true)]
    [InlineData(PackageRegistryKind.Npm, "@aetheus/web-analytics", true)]
    [InlineData(PackageRegistryKind.NuGet, "aetheus.back", false)]
    [InlineData(PackageRegistryKind.Npm, "aetheus.telemetry", false)]
    [InlineData(PackageRegistryKind.Maven, "aetheus.telemetry", false)]
    public void OnlyTheObservabilityPackages_StartPublic(PackageRegistryKind kind, string normalizedName, bool expected) =>
        Assert.Equal(expected, BackDefaults.IsPublicByDefault(kind, normalizedName));
}
