// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.VariableLibraries;
using Aetheus.Back.Components.Vaults;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Aetheus.Back.Tests.Vaults;

/// <summary>
/// R2-12. The vault and library services are scoped: the request that reads the cached name list and
/// the request that creates the element are two different instances sharing one memory cache. The
/// invalidation used to walk a per-instance key set, so the creating instance evicted nothing and a
/// pipeline launched right after the setup wizard was refused for a vault that existed.
/// </summary>
public sealed class NamesCacheInvalidationAcrossInstancesTests
{
    private const int ProjectId = 4242;

    [Fact]
    public async Task AVaultCreatedByAnotherServiceInstanceIsSeenByTheNextNameRead()
    {
        using var cache = new MemoryCache(Options.Create(new MemoryCacheOptions()));
        var names = new List<string>();
        var repo = Substitute.For<IVaultRepository>();
        repo.GetVaultNamesAsync(ProjectId, Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(names.ToList()));
        repo.AddVaultAsync(Arg.Any<Vault>(), Arg.Any<CancellationToken>())
            .Returns(call => { names.Add(call.Arg<Vault>().Name); return Task.CompletedTask; });
        var reader = NewVaultService(repo, cache);
        var creator = NewVaultService(repo, cache);

        Assert.Empty(await reader.GetVaultNamesAsync(ProjectId, ct: TestContext.Current.CancellationToken));
        await creator.CreateVaultAsync(
            new CreateVaultRequest { Name = "wordmorrow.qa", ProjectId = ProjectId }, TestContext.Current.CancellationToken);

        Assert.Contains("wordmorrow.qa", await reader.GetVaultNamesAsync(ProjectId, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ALibraryCreatedByAnotherServiceInstanceIsSeenByTheNextNameRead()
    {
        using var cache = new MemoryCache(Options.Create(new MemoryCacheOptions()));
        var names = new List<string>();
        var repo = Substitute.For<IVariableLibraryRepository>();
        repo.GetLibraryNamesAsync(ProjectId, Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(names.ToList()));
        repo.AddLibraryAsync(Arg.Any<VariableLibrary>(), Arg.Any<CancellationToken>())
            .Returns(call => { names.Add(call.Arg<VariableLibrary>().Name); return Task.CompletedTask; });
        var reader = NewLibraryService(repo, cache);
        var creator = NewLibraryService(repo, cache);

        Assert.Empty(await reader.GetLibraryNamesAsync(ProjectId, ct: TestContext.Current.CancellationToken));
        await creator.CreateLibraryAsync(
            new CreateVariableLibraryRequest { Name = "wordmorrow.share", ProjectId = ProjectId }, TestContext.Current.CancellationToken);

        Assert.Contains("wordmorrow.share", await reader.GetLibraryNamesAsync(ProjectId, ct: TestContext.Current.CancellationToken));
    }

    private static VaultService NewVaultService(IVaultRepository repo, IMemoryCache cache) =>
        new(repo, Substitute.For<IEncryptionService>(), Substitute.For<IDbTransactionScope>(),
            Substitute.For<IAuditService>(), Substitute.For<IEntityChangeNotifier>(), TimeProvider.System, cache);

    private static VariableLibraryService NewLibraryService(IVariableLibraryRepository repo, IMemoryCache cache) =>
        new(repo, Substitute.For<IDbTransactionScope>(), Substitute.For<IAuditService>(),
            Substitute.For<IEntityChangeNotifier>(), TimeProvider.System, cache);
}
