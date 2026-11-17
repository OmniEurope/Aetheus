// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.PackageRegistry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.PackageRegistry;

public sealed class PackageRegistryStorageReconcilerTests
{
    [Fact]
    public async Task ReconcileOnceAsync_UsesReferencedPathsAndOneHourSafetyCutoff()
    {
        var repository = Substitute.For<IPackageRegistryRepository>();
        var storage = Substitute.For<IPackageRegistryStorage>();
        var referenced = new HashSet<string>(StringComparer.Ordinal) { "nuget/pkg/1.0.0/pkg.nupkg" };
        repository.GetStoredFilePathsAsync(Arg.Any<CancellationToken>()).Returns(referenced);
        storage.ReconcileAsync(
                Arg.Any<IReadOnlySet<string>>(),
                Arg.Any<DateTime>(),
                Arg.Any<CancellationToken>())
            .Returns(1);
        var services = new ServiceCollection()
            .AddSingleton(repository)
            .AddSingleton(storage)
            .BuildServiceProvider();
        var now = new DateTimeOffset(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);
        var sut = new PackageRegistryStorageReconciler(
            services.GetRequiredService<IServiceScopeFactory>(),
            new FakeTimeProvider(now),
            NullLogger<PackageRegistryStorageReconciler>.Instance,
            leaderLease: LeaderLeaseDouble.Leader());

        await sut.ReconcileOnceAsync(TestContext.Current.CancellationToken);

        await storage.Received(1).ReconcileAsync(
            Arg.Is<IReadOnlySet<string>>(paths => paths.SetEquals(referenced)),
            now.UtcDateTime.AddHours(-1),
            TestContext.Current.CancellationToken);
    }
}
