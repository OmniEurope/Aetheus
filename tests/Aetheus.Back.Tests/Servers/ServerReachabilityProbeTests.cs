// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Back.Tests;

/// <summary>
/// PLAN-010 lot 1: classification of the external probe. A fake prober stands in for the network
/// and a fake clock for the backoff, so no socket is opened and no real time passes.
/// </summary>
public class ServerReachabilityProbeTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _clock = new(Start);
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class FakeProber : ITcpReachabilityProber
    {
        public Func<int, int, TcpProbeOutcome> Answer { get; set; } = (_, _) => TcpProbeOutcome.NoAnswer;
        public Dictionary<string, IPAddress[]> Dns { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<(IPAddress Address, int Port, TimeSpan Timeout)> Connects { get; } = [];
        private readonly Dictionary<int, int> _attemptsByPort = [];

        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string hostname, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<IPAddress>>(Dns.TryGetValue(hostname, out var found) ? found : []);

        public Task<TcpProbeOutcome> ConnectAsync(IPAddress address, int port, TimeSpan timeout, CancellationToken ct)
        {
            lock (Connects)
            {
                Connects.Add((address, port, timeout));
                _attemptsByPort[port] = _attemptsByPort.GetValueOrDefault(port) + 1;
                return Task.FromResult(Answer(port, _attemptsByPort[port]));
            }
        }
    }

    private ServerReachabilityProbe CreateProbe(FakeProber prober, ServerReachabilityProbeOptions? options = null) =>
        new(prober, new ServerReachabilityRepository(_db), Options.Create(options ?? new ServerReachabilityProbeOptions()),
            _clock, NullLogger<ServerReachabilityProbe>.Instance);

    private static Server PublicServer(int id = 1) => new()
    {
        Id = id,
        Name = $"vps-{id}",
        Hostname = "vps.example",
        IpAddress = "203.0.113.5",
        Status = ServerStatus.Offline,
        LastHeartbeat = Start.UtcDateTime.AddMinutes(-3)
    };

    [Fact]
    public async Task Classify_PortAccepts_HostReachableWithThatPort()
    {
        var prober = new FakeProber { Answer = (port, _) => port == 22 ? TcpProbeOutcome.Open : TcpProbeOutcome.NoAnswer };

        var (reachability, target) = await CreateProbe(prober).ClassifyAsync(PublicServer(), TestContext.Current.CancellationToken);

        Assert.Equal(ServerReachability.HostReachable, reachability);
        Assert.Equal("203.0.113.5:22", target);
        Assert.Equal([22, 443], prober.Connects.Select(c => c.Port).Order());
        Assert.All(prober.Connects, c => Assert.Equal(TimeSpan.FromSeconds(3), c.Timeout));
    }

    [Fact]
    public async Task Classify_PortRefused_HostStillCountsAsReachable()
    {
        // A refused connect is an answer from the host's own TCP stack: the machine is up.
        var prober = new FakeProber { Answer = (port, _) => port == 443 ? TcpProbeOutcome.Refused : TcpProbeOutcome.NoAnswer };

        var (reachability, target) = await CreateProbe(prober).ClassifyAsync(PublicServer(), TestContext.Current.CancellationToken);

        Assert.Equal(ServerReachability.HostReachable, reachability);
        Assert.Equal("203.0.113.5:443", target);
    }

    [Fact]
    public async Task Classify_NoPortAnswers_RetriesWithDoublingBackoffThenUnreachable()
    {
        var prober = new FakeProber();
        var probe = CreateProbe(prober);

        var task = probe.ClassifyAsync(PublicServer(), TestContext.Current.CancellationToken);

        // First round done, now waiting on the fake clock: nothing else happens until it moves.
        Assert.False(task.IsCompleted);
        Assert.Equal(2, prober.Connects.Count);
        _clock.Advance(TimeSpan.FromSeconds(1));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(2, prober.Connects.Count);

        var (reachability, target) = await AdvanceUntilDoneAsync(task);

        Assert.Equal(ServerReachability.HostUnreachable, reachability);
        Assert.Equal("203.0.113.5:22,443", target);
        Assert.Equal(6, prober.Connects.Count);
        // Three rounds separated by 2 s then 4 s of backoff.
        Assert.True(_clock.GetUtcNow() - Start >= TimeSpan.FromSeconds(6));
    }

    [Fact]
    public async Task Classify_AnswerOnSecondRound_HostReachable()
    {
        var prober = new FakeProber { Answer = (port, attempt) => port == 22 && attempt == 2 ? TcpProbeOutcome.Open : TcpProbeOutcome.NoAnswer };
        var task = CreateProbe(prober).ClassifyAsync(PublicServer(), TestContext.Current.CancellationToken);

        var (reachability, _) = await AdvanceUntilDoneAsync(task);

        Assert.Equal(ServerReachability.HostReachable, reachability);
        Assert.Equal(4, prober.Connects.Count);
    }

    [Fact]
    public async Task Classify_Disabled_UnknownWithoutAnyConnect()
    {
        var prober = new FakeProber();

        var (reachability, target) = await CreateProbe(prober, new ServerReachabilityProbeOptions { Enabled = false })
            .ClassifyAsync(PublicServer(), TestContext.Current.CancellationToken);

        Assert.Equal(ServerReachability.UnknownProbeDisabled, reachability);
        Assert.Null(target);
        Assert.Empty(prober.Connects);
    }

    [Fact]
    public async Task Classify_NoIpAndUnresolvableHostname_UnknownNoAddress()
    {
        var prober = new FakeProber();
        var server = PublicServer();
        server.IpAddress = "unknown";
        server.Hostname = "does-not-resolve";

        var (reachability, _) = await CreateProbe(prober).ClassifyAsync(server, TestContext.Current.CancellationToken);

        Assert.Equal(ServerReachability.UnknownNoAddress, reachability);
        Assert.Empty(prober.Connects);
    }

    [Fact]
    public async Task Classify_NoIp_FallsBackToResolvedHostname()
    {
        var prober = new FakeProber { Answer = (_, _) => TcpProbeOutcome.Open };
        prober.Dns["vps.example"] = [IPAddress.Parse("198.51.100.7")];
        var server = PublicServer();
        server.IpAddress = string.Empty;

        var (reachability, target) = await CreateProbe(prober).ClassifyAsync(server, TestContext.Current.CancellationToken);

        Assert.Equal(ServerReachability.HostReachable, reachability);
        Assert.StartsWith("198.51.100.7:", target);
    }

    [Fact]
    public async Task Classify_PrivateAddress_RefusedByOutboundPolicyUnlessAllowed()
    {
        var server = PublicServer();
        server.IpAddress = "192.168.1.20";

        var blocked = new FakeProber { Answer = (_, _) => TcpProbeOutcome.Open };
        var (refused, refusedTarget) = await CreateProbe(blocked).ClassifyAsync(server, TestContext.Current.CancellationToken);
        Assert.Equal(ServerReachability.UnknownAddressNotAllowed, refused);
        Assert.Equal("192.168.1.20:22,443", refusedTarget);
        Assert.Empty(blocked.Connects);

        var allowed = new FakeProber { Answer = (_, _) => TcpProbeOutcome.Open };
        var (reachable, _) = await CreateProbe(allowed, new ServerReachabilityProbeOptions { AllowPrivateTargets = true })
            .ClassifyAsync(server, TestContext.Current.CancellationToken);
        Assert.Equal(ServerReachability.HostReachable, reachable);
    }

    [Fact]
    public async Task Classify_ConfiguredPortsReplaceDefaults()
    {
        var prober = new FakeProber { Answer = (_, _) => TcpProbeOutcome.Open };

        await CreateProbe(prober, new ServerReachabilityProbeOptions { Ports = [2222, 0, 70000, 2222] })
            .ClassifyAsync(PublicServer(), TestContext.Current.CancellationToken);

        Assert.Equal([2222], prober.Connects.Select(c => c.Port));
    }

    [Fact]
    public async Task ProbeAndRecord_StoresResultTimeAndTarget()
    {
        var server = PublicServer(7);
        _db.Servers.Add(server);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var prober = new FakeProber { Answer = (_, _) => TcpProbeOutcome.Open };

        var results = await CreateProbe(prober).ProbeAndRecordAsync([server], TestContext.Current.CancellationToken);

        Assert.Equal(ServerReachability.HostReachable, results[7]);
        var stored = await _db.Servers.AsNoTracking().SingleAsync(s => s.Id == 7, TestContext.Current.CancellationToken);
        Assert.Equal(ServerReachability.HostReachable, stored.ExternalProbe);
        Assert.Equal(Start.UtcDateTime, stored.ExternalProbeAt);
        Assert.StartsWith("203.0.113.5:", stored.ExternalProbeTarget);
    }

    [Fact]
    public async Task RecheckDue_ProbesOnlyOfflineServersWithMissingOrOldResult()
    {
        var now = Start.UtcDateTime;
        var neverProbed = PublicServer(1);
        var fresh = PublicServer(2);
        fresh.ExternalProbe = ServerReachability.HostUnreachable;
        fresh.ExternalProbeAt = now.AddMinutes(-1);
        var old = PublicServer(3);
        old.ExternalProbeAt = now.AddMinutes(-11);
        var earlierEpisode = PublicServer(4);
        earlierEpisode.ExternalProbeAt = earlierEpisode.LastHeartbeat.AddMinutes(-1);
        var online = PublicServer(5);
        online.Status = ServerStatus.Online;
        var neverReported = PublicServer(6);
        neverReported.LastHeartbeat = default;
        _db.Servers.AddRange(neverProbed, fresh, old, earlierEpisode, online, neverReported);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var prober = new FakeProber { Answer = (_, _) => TcpProbeOutcome.Open };

        await CreateProbe(prober).RecheckDueAsync(TestContext.Current.CancellationToken);

        var byId = await _db.Servers.AsNoTracking().ToDictionaryAsync(s => s.Id, TestContext.Current.CancellationToken);
        Assert.Equal(ServerReachability.HostReachable, byId[1].ExternalProbe);
        Assert.Equal(ServerReachability.HostUnreachable, byId[2].ExternalProbe);
        Assert.Equal(ServerReachability.HostReachable, byId[3].ExternalProbe);
        Assert.Equal(ServerReachability.HostReachable, byId[4].ExternalProbe);
        Assert.Null(byId[5].ExternalProbeAt);
        Assert.Null(byId[6].ExternalProbeAt);
    }

    private async Task<T> AdvanceUntilDoneAsync<T>(Task<T> task)
    {
        // The continuation after a fake-clock advance may run on the thread pool: move the clock in
        // small steps and give it a moment each time, never real seconds.
        for (var i = 0; i < 200 && !task.IsCompleted; i++)
        {
            _clock.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
        return await task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }
}
