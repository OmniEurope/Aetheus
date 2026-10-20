// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Configuration;

namespace Aetheus.Back.Tests.Architecture;

public sealed class OperationalDefaultsTests
{
    [Theory]
    [InlineData(PackageFeedType.NuGet, PackageRegistryDefaults.NuGet)]
    [InlineData(PackageFeedType.Npm, PackageRegistryDefaults.Npm)]
    [InlineData(PackageFeedType.PyPI, PackageRegistryDefaults.PyPi)]
    [InlineData(PackageFeedType.Maven, PackageRegistryDefaults.Maven)]
    public void PackageRegistryDefaults_MapEveryFeedType(PackageFeedType type, string expected)
    {
        Assert.Equal(expected, PackageRegistryDefaults.For(type));
        Assert.True(Uri.TryCreate(expected, UriKind.Absolute, out _));
    }

    [Theory]
    [InlineData(-1, PaginationDefaults.MinimumPageSize)]
    [InlineData(PaginationDefaults.DefaultPageSize, PaginationDefaults.DefaultPageSize)]
    [InlineData(999, PaginationDefaults.MaximumPageSize)]
    public void PaginationDefaults_ClampToSharedBounds(int requested, int expected) =>
        Assert.Equal(expected, PaginationDefaults.Clamp(requested));

    [Fact]
    public void AppMonitoringDefaults_HaveConsistentBounds()
    {
        Assert.InRange(
            AppMonitoringDefaults.DefaultProbeIntervalSeconds,
            AppMonitoringDefaults.MinimumProbeIntervalSeconds,
            AppMonitoringDefaults.MaximumProbeIntervalSeconds);
        Assert.InRange(
            AppMonitoringDefaults.DefaultProbeTimeoutSeconds,
            AppMonitoringDefaults.MinimumProbeTimeoutSeconds,
            AppMonitoringDefaults.MaximumProbeTimeoutSeconds);
        Assert.InRange(
            AppMonitoringDefaults.DefaultRawRetentionDays,
            AppMonitoringDefaults.MinimumRawRetentionDays,
            AppMonitoringDefaults.MaximumRawRetentionDays);
    }

    [Fact]
    public void BackendRuntimeDefaults_ArePositiveAndOrdered()
    {
        Assert.True(BackendRuntimeDefaults.InfrastructureCacheDuration > TimeSpan.Zero);
        Assert.True(BackendRuntimeDefaults.ReferenceDataCacheDuration > BackendRuntimeDefaults.InfrastructureCacheDuration);
        Assert.True(BackendRuntimeDefaults.GitWriteTimeout >= BackendRuntimeDefaults.GitProcessTimeout);
        Assert.True(
            BackendRuntimeDefaults.AuthenticatedAgentRateLimit
            > BackendRuntimeDefaults.AuthenticatedUserRateLimit);
        Assert.InRange(
            BackendRuntimeDefaults.TaskRecoveryCheckInterval,
            TimeSpan.FromSeconds(1),
            BackendRuntimeDefaults.SchedulerCheckInterval - TimeSpan.FromTicks(1));
        Assert.True(BackendRuntimeDefaults.MaintenanceInterval > BackendRuntimeDefaults.SchedulerCheckInterval);
    }
}
