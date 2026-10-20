// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Agent.Core.Tests;

/// <summary>Verifies that workspace reclamation preserves live runs and treats unavailable backend
/// state conservatively.</summary>
public sealed class RunWorkspaceReaperTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"aetheus-reaper-{Guid.NewGuid():N}");

    private readonly IServerApiClient _api = Substitute.For<IServerApiClient>();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-09-07T10:00:00Z"));

    private RunWorkspaceReaper CreateReaper() =>
        new(_api,
            Options.Create(new AetheusAgentOptions { WorkDirectory = _root }),
            _time,
            NullLogger<RunWorkspaceReaper>.Instance);

    private string CreateWorkspace(string slot, DateTime? lastWriteUtc = null)
    {
        var sources = Path.Combine(_root, "w", slot, "s");
        Directory.CreateDirectory(sources);
        File.WriteAllText(Path.Combine(sources, "checkout.txt"), "sources");
        if (lastWriteUtc is { } stamp)
        {
            Directory.SetLastWriteTimeUtc(Path.Combine(_root, "w", slot), stamp);
            Directory.SetLastWriteTimeUtc(sources, stamp);
            File.SetLastWriteTimeUtc(Path.Combine(sources, "checkout.txt"), stamp);
        }

        return Path.Combine(_root, "w", slot);
    }

    [Fact]
    public async Task Sweep_DeletesWorkspacesOfRunsThatAreNoLongerActive()
    {
        var finished = CreateWorkspace("aaaaaaaa");
        var active = CreateWorkspace("bbbbbbbb");
        _api.GetActiveWorkspaceSlotsAsync(Arg.Any<CancellationToken>()).Returns(new[] { "bbbbbbbb" });

        var removed = await CreateReaper().SweepAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(finished));
        Assert.True(Directory.Exists(active));
    }

    /// <summary>
    /// The failure that matters. An unreachable backend means "unknown", and answering it as "no run is
    /// active" would delete the workspace of every run in flight - exactly the bug this change fixes.
    /// </summary>
    [Fact]
    public async Task Sweep_KeepsEverythingRecentWhenTheBackendCannotBeReached()
    {
        var live = CreateWorkspace("cccccccc");
        _api.GetActiveWorkspaceSlotsAsync(Arg.Any<CancellationToken>()).Returns((IReadOnlyList<string>?)null);

        var removed = await CreateReaper().SweepAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, removed);
        Assert.True(Directory.Exists(live));
    }

    /// <summary>
    /// The safety net: an agent that stays disconnected must still reclaim its disk eventually, or a
    /// long outage fills the host with workspaces nobody will ever collect.
    /// </summary>
    [Fact]
    public async Task Sweep_ReclaimsLongUntouchedWorkspacesWhenTheBackendCannotBeReached()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var stale = CreateWorkspace("dddddddd", now - RunWorkspaceReaper.FallbackRetention - TimeSpan.FromHours(1));
        var recent = CreateWorkspace("eeeeeeee", now - TimeSpan.FromHours(1));
        _api.GetActiveWorkspaceSlotsAsync(Arg.Any<CancellationToken>()).Returns((IReadOnlyList<string>?)null);

        var removed = await CreateReaper().SweepAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(stale));
        Assert.True(Directory.Exists(recent));
    }

    /// <summary>
    /// A run parked on an approval writes nothing for days. Age alone would reclaim it, so the run-state
    /// answer has to win over the clock whenever the backend actually answered.
    /// </summary>
    [Fact]
    public async Task Sweep_KeepsAnActiveRunWorkspaceThatHasBeenIdleLongerThanTheFallback()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var idleButActive = CreateWorkspace("ffffffff", now - RunWorkspaceReaper.FallbackRetention - TimeSpan.FromDays(30));
        _api.GetActiveWorkspaceSlotsAsync(Arg.Any<CancellationToken>()).Returns(new[] { "ffffffff" });

        var removed = await CreateReaper().SweepAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, removed);
        Assert.True(Directory.Exists(idleButActive));
    }

    [Fact]
    public async Task Sweep_IsANoOpWhenNoRunHasEverUsedThisAgent()
    {
        _api.GetActiveWorkspaceSlotsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<string>());

        var removed = await CreateReaper().SweepAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, removed);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
