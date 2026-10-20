// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.ServiceConnections;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.ExternalRepos;

/// <summary>
/// An external forge whose name resolves to a private address is never contacted: the connectivity
/// test says why, and a first mirror sync ends in error with that reason and
/// leaves the mirror's branch, path and dates untouched. The resolver is injected, so no DNS query
/// and no remote git call happen.
/// </summary>
public sealed class ExternalRepoMirrorServiceRefusalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aetheus-mirror-" + Guid.NewGuid().ToString("N"));
    private readonly IGitLightCliService _cli = Substitute.For<IGitLightCliService>();
    private readonly IServiceConnectionRepository _serviceConnections = Substitute.For<IServiceConnectionRepository>();
    private readonly List<string> _resolved = [];

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private ExternalRepoMirrorService Service()
    {
        var options = new GitLightOptions { RepositoriesPath = _root };
        return new ExternalRepoMirrorService(
            new CreditedGitRunner(NullLogger<CreditedGitRunner>.Instance),
            new ExternalGitEndpointGuard(options, (host, _) =>
            {
                _resolved.Add(host);
                return Task.FromResult(new[] { IPAddress.Parse("10.0.0.5") });
            }),
            _serviceConnections,
            Substitute.For<IEncryptionService>(),
            _cli,
            Options.Create(options),
            new MemoryCache(new MemoryCacheOptions()),
            new FakeTimeProvider(new DateTimeOffset(2026, 10, 16, 9, 0, 0, TimeSpan.Zero)),
            NullLogger<ExternalRepoMirrorService>.Instance);
    }

    private static GitConnection Connection(int? serviceConnectionId = null) => new()
    {
        Id = 3,
        ProjectId = 8,
        ProviderType = GitProviderType.GitHub,
        OwnerOrGroup = "acme",
        RepositoryName = "shop.git",
        ServiceConnectionId = serviceConnectionId,
        MirrorStatus = GitMirrorStatus.Ready,
    };

    private static GitInternalRepo Mirror() => new() { Id = 4, ProjectId = 8, Slug = "shop", DefaultBranch = "main", IsEmpty = true };

    [Fact]
    public async Task TheConnectivityTest_RefusesAForgeThatResolvesToAPrivateAddress()
    {
        var (ok, error) = await Service().TestConnectivityAsync(
            GitProviderType.GitHub, null, "acme", "shop", GitAuthType.HttpsToken, credential: null,
            TestContext.Current.CancellationToken);

        Assert.False(ok);
        Assert.StartsWith("The host 'github.com' resolves to a private or reserved address.", error, StringComparison.Ordinal);
        Assert.Equal(["github.com"], _resolved);
    }

    [Fact]
    public async Task AFirstSync_ThatIsRefused_LeavesTheMirrorInErrorWithTheReason_AndCreatesNothing()
    {
        var connection = Connection(serviceConnectionId: 77);
        var mirror = Mirror();

        var status = await Service().SyncAsync(connection, mirror, TestContext.Current.CancellationToken);

        Assert.Equal(GitMirrorStatus.Error, status);
        Assert.Equal(GitMirrorStatus.Error, connection.MirrorStatus);
        Assert.StartsWith("The host 'github.com' resolves to a private or reserved address.", connection.LastFetchError, StringComparison.Ordinal);
        Assert.Null(connection.MirrorPath);
        Assert.Null(connection.LastSyncedAt);
        Assert.Null(mirror.LastPushAt);
        Assert.Equal("main", mirror.DefaultBranch);
        // The service connection named by the mirror was looked up; it no longer exists, so no credential.
        await _serviceConnections.Received(1).FindAsync(77, TestContext.Current.CancellationToken);
        Assert.False(Directory.Exists(Path.Combine(_root, "8")));
        await _cli.DidNotReceiveWithAnyArgs().DetectDefaultBranchAsync(default!, TestContext.Current.CancellationToken);
    }
}
