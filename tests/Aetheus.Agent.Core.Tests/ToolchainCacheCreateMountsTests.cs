// SPDX-License-Identifier: EUPL-1.2
using System.Runtime.InteropServices;
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Toolchains;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Agent.Core.Tests;

/// <summary>The toolchain caches mounted into a container step: one host directory per cache under the
/// trust domain's namespace, the parents of a cache mounted below the container home, the freshness stamp
/// that keeps the live cache out of pruning, and the pruning of the stale ones. The platform decision is
/// injected, so the whole creation runs on a temporary directory on any platform.</summary>
public sealed class ToolchainCacheCreateMountsTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 8, 0, 0, TimeSpan.Zero);

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("aetheus-cache-mounts-");
    private readonly string _work;
    private readonly string _home;

    public ToolchainCacheCreateMountsTests()
    {
        _work = Path.Combine(_root.FullName, "work");
        _home = Path.Combine(_root.FullName, "home");
        Directory.CreateDirectory(_home);
    }

    public void Dispose() => _root.Delete(recursive: true);

    private static ToolchainResolution Caches(params ToolchainCacheMount[] caches) =>
        new() { IsSuccess = true, Image = "img", Caches = caches };

    private IReadOnlyList<KeyValuePair<string, string>> Create(
        ContainerSpec spec, ToolchainResolution resolution, bool isWindows = false) =>
        ToolchainCacheManager.CreateMountsCore(
            _work, spec, resolution, _home, NullLogger.Instance, new FakeTimeProvider(Now), isWindows);

    private string CacheRoot(string trustDomain, string name) =>
        Path.Combine(
            _work, "toolchain-cache", ToolchainCacheManager.BuildNamespace(trustDomain, RuntimeInformation.OSArchitecture), name);

    [Fact]
    public void OnWindows_NoCacheIsMounted_AndNothingIsCreated()
    {
        var mounts = Create(
            new ContainerSpec { WorkspaceKey = 1 },
            Caches(new ToolchainCacheMount("nuget", "/home/aetheus/.nuget/packages", "k1")),
            isWindows: true);

        Assert.Empty(mounts);
        Assert.False(Directory.Exists(_work));
    }

    [Fact]
    public void EachCache_GetsAHostDirectoryUnderItsTrustDomainNamespace_MountedAtItsContainerTarget()
    {
        var spec = new ContainerSpec { WorkspaceKey = 7, CacheTrustDomain = "org-1-project-2" };

        var mounts = Create(spec, Caches(
            new ToolchainCacheMount("nuget", "/home/aetheus/.nuget/packages", "lock-a"),
            new ToolchainCacheMount("npm", "/home/aetheus/.npm", "lock-b")));

        var nuget = Path.Combine(CacheRoot("org-1-project-2", "nuget"), "lock-a");
        var npm = Path.Combine(CacheRoot("org-1-project-2", "npm"), "lock-b");
        Assert.Equal(
            [new KeyValuePair<string, string>(nuget, "/home/aetheus/.nuget/packages"),
             new KeyValuePair<string, string>(npm, "/home/aetheus/.npm")],
            mounts);
        Assert.True(Directory.Exists(nuget));
        Assert.True(Directory.Exists(npm));
    }

    [Fact]
    public void WithoutATrustDomain_TheNamespaceIsTheRunsOwn()
    {
        var mounts = Create(
            new ContainerSpec { WorkspaceKey = 42 },
            Caches(new ToolchainCacheMount("nuget", "/home/aetheus/.nuget/packages", "k")));

        Assert.Equal(Path.Combine(CacheRoot("run-42", "nuget"), "k"), Assert.Single(mounts).Key);
    }

    [Fact]
    public void ACacheMountedBelowTheContainerHome_GetsItsParentDirectoriesCreatedInTheHomeMount()
    {
        Create(
            new ContainerSpec { WorkspaceKey = 1 },
            Caches(new ToolchainCacheMount("nuget", "/home/aetheus/.nuget/packages", "k")));

        Assert.True(Directory.Exists(Path.Combine(_home, ".nuget")));
        Assert.False(Directory.Exists(Path.Combine(_home, ".nuget", "packages")));
    }

    [Fact]
    public void ACacheMountedOutsideTheContainerHome_CreatesNothingInTheHomeMount()
    {
        Create(
            new ContainerSpec { WorkspaceKey = 1 },
            Caches(new ToolchainCacheMount("go", "/go/pkg", "k")));

        Assert.Empty(Directory.GetFileSystemEntries(_home));
    }

    [Fact]
    public void AnExistingCache_IsReused_KeepsItsContent_AndIsStampedWithTheCurrentTime()
    {
        var spec = new ContainerSpec { WorkspaceKey = 1, CacheTrustDomain = "d" };
        var cache = Caches(new ToolchainCacheMount("nuget", "/home/aetheus/.nuget/packages", "k"));
        var host = Create(spec, cache).Single().Key;
        File.WriteAllText(Path.Combine(host, "pkg.bin"), "cached");
        Directory.SetLastWriteTimeUtc(host, Now.UtcDateTime.AddDays(-30));

        var again = Create(spec, cache).Single().Key;

        Assert.Equal(host, again);
        Assert.Equal("cached", File.ReadAllText(Path.Combine(host, "pkg.bin")));
        Assert.Equal(Now.UtcDateTime, Directory.GetLastWriteTimeUtc(host));
    }

    [Fact]
    public void WhenMoreCachesThanTheLimitExist_TheOldestStaleOnesAreRemovedAndTheLiveOneKept()
    {
        var spec = new ContainerSpec { WorkspaceKey = 1, CacheTrustDomain = "d" };
        var cacheRoot = CacheRoot("d", "nuget");
        for (var i = 0; i < 9; i++)
        {
            var stale = Directory.CreateDirectory(Path.Combine(cacheRoot, $"old-{i}")).FullName;
            Directory.SetLastWriteTimeUtc(stale, Now.UtcDateTime.AddDays(-(i + 1)));
        }

        var live = Create(spec, Caches(new ToolchainCacheMount("nuget", "/home/aetheus/.nuget/packages", "live")))
            .Single().Key;

        Assert.True(Directory.Exists(live));
        var remaining = Directory.GetDirectories(cacheRoot).Select(Path.GetFileName).Order().ToArray();
        Assert.Equal(["live", "old-0", "old-1", "old-2", "old-3", "old-4", "old-5", "old-6"], remaining);
    }
}
