// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.PortRegistry;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.PortRegistry;

/// <summary>
/// The behaviour the feature exists for: a deployment asking for a port another project holds on the
/// same host is refused at launch, with the holder named, instead of dying an hour later at
/// <c>docker up</c> on "Bind for 0.0.0.0:10031 failed: port is already allocated".
/// </summary>
public sealed class PipelinePortRegistryGuardTests : IDisposable
{
    private const int ServerId = 7;

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc)));
    private readonly AppDbContext _db;
    private readonly PortRegistryService _registry;
    private readonly IPipelineDispatchServerResolver _dispatch = Substitute.For<IPipelineDispatchServerResolver>();
    private readonly PipelinePortRegistryGuard _sut;

    public PipelinePortRegistryGuardTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options, _clock);
        var server = new Server { Id = ServerId, Name = "vps2577917", OrganizationId = 1 };
        _db.Servers.Add(server);
        _db.SaveChanges();

        _registry = new PortRegistryService(
            new PortRegistryRepository(_db), _clock, NullLogger<PortRegistryService>.Instance);
        _dispatch.ResolveServerForTargetAsync(
                Arg.Any<PipelineStageDefinition>(), Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<int?>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(server);
        _sut = new PipelinePortRegistryGuard(
            _dispatch, _registry, NullLogger<PipelinePortRegistryGuard>.Instance);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Dictionary<string, string> DeployVariables =
        new(StringComparer.OrdinalIgnoreCase) { ["PORT_FRONT"] = "10031", ["PORT_BACK"] = "10032" };

    /// <summary>A stage carrying a native deploy step, i.e. one that will bind the run's ports.</summary>
    private static PipelineYamlDefinition DeployDefinition(string? observedPortPolicy = null) => new()
    {
        Name = "aetheus-nightly",
        ObservedPortPolicy = observedPortPolicy,
        Stages =
        [
            new PipelineStageDefinition
            {
                Name = "Deploy",
                Agent = "vps2577917",
                Steps = [new PipelineStepDefinition { Name = "Deploy demo", Type = "deploy", App = "aetheus-demo" }]
            }
        ]
    };

    /// <summary>A blue-green stage declaring its four ports inline rather than through variables.</summary>
    private static PipelineYamlDefinition BlueGreenDefinition() => new()
    {
        Name = "aetheus-release-fast",
        Stages =
        [
            new PipelineStageDefinition
            {
                Name = "Cutover",
                Agent = "vps2577917",
                Steps =
                [
                    new PipelineStepDefinition
                    {
                        Name = "Up", Type = "bluegreen-up", Ports = "10025,10026,10027,10028"
                    }
                ]
            }
        ]
    };

    [Fact]
    public async Task FindPortConflictsAsync_RefusesAPortHeldByAnotherProject_AndNamesTheHolder()
    {
        await _registry.DeclareAsync(
            ServerId, [10031], "project:4", "portfolio-prod-front", 4, Ct);

        var problems = await _sut.FindPortConflictsAsync(
            DeployDefinition(), DeployVariables, organizationId: 1, projectId: 9, Ct);

        var found = Assert.Single(problems.Blocking);
        Assert.Equal(PipelineProblemCodes.PortRegisteredToAnother, found.Code);
        Assert.Equal("10031", found.Parameters["port"]);
        Assert.Equal("portfolio-prod-front", found.Parameters["owner"]);
        var problem = found.English;
        Assert.Contains("10031", problem, StringComparison.Ordinal);
        Assert.Contains("portfolio-prod-front", problem, StringComparison.Ordinal);
        Assert.Contains("vps2577917", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindPortConflictsAsync_AllowsARedeploymentOfTheSameProject()
    {
        await _registry.DeclareAsync(
            ServerId, [10031, 10032], "project:9", "aetheus-demo", 9, Ct);

        var problems = await _sut.FindPortConflictsAsync(
            DeployDefinition(), DeployVariables, organizationId: 1, projectId: 9, Ct);

        Assert.Empty(problems.Blocking);
        Assert.Empty(problems.Warnings);
    }

    [Fact]
    public async Task FindPortConflictsAsync_IsSilent_WhenNothingHoldsThePorts()
    {
        var report = await _sut.FindPortConflictsAsync(
            DeployDefinition(), DeployVariables, organizationId: 1, projectId: 9, Ct);
        Assert.Empty(report.Blocking);
        Assert.Empty(report.Warnings);
    }

    [Fact]
    public async Task FindPortConflictsAsync_ReadsTheInlinePortsOfABlueGreenStep()
    {
        await _registry.DeclareAsync(ServerId, [10027], "project:4", "portfolio-prod-front", 4, Ct);

        var problems = await _sut.FindPortConflictsAsync(
            BlueGreenDefinition(), new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            organizationId: 1, projectId: 9, Ct);

        Assert.Contains("10027", Assert.Single(problems.Blocking).English, StringComparison.Ordinal);
    }

    /// <summary>
    /// A stage that only builds must not claim the run's ports: doing so would let a CI-only pipeline
    /// lock the fleet's ports on whatever runner happened to take it.
    /// </summary>
    [Fact]
    public async Task FindPortConflictsAsync_IgnoresAStageThatDeploysNothing()
    {
        await _registry.DeclareAsync(ServerId, [10031], "project:4", "portfolio-prod-front", 4, Ct);
        var buildOnly = new PipelineYamlDefinition
        {
            Name = "aetheus-ci",
            Stages =
            [
                new PipelineStageDefinition
                {
                    Name = "Build",
                    Steps = [new PipelineStepDefinition { Name = "Compile", Shell = "dotnet build" }]
                }
            ]
        };

        var buildReport = await _sut.FindPortConflictsAsync(
            buildOnly, DeployVariables, organizationId: 1, projectId: 9, Ct);
        Assert.Empty(buildReport.Blocking);
        Assert.Empty(buildReport.Warnings);
    }

    [Fact]
    public async Task DeclareReservationsAsync_RecordsTheDeploymentPorts()
    {
        await _sut.DeclareReservationsAsync(
            DeployDefinition(), DeployVariables, organizationId: 1, projectId: 9, "aetheus-demo", Ct);

        var reservations = await _registry.GetServerReservationsAsync(ServerId, Ct);
        Assert.Equal([10031, 10032], reservations.Select(item => item.Port).ToList());
        Assert.All(reservations, reservation => Assert.Equal("aetheus-demo", reservation.OwnerLabel));
    }

    [Fact]
    public void OwnerKeyFor_PrefersTheProject_ThenThePipeline_ThenNothing()
    {
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["BUILD_PIPELINEID"] = "42"
        };

        Assert.Equal("project:9", PipelinePortRegistryGuard.OwnerKeyFor(9, variables));
        Assert.Equal("pipeline:42", PipelinePortRegistryGuard.OwnerKeyFor(null, variables));
        Assert.Null(PipelinePortRegistryGuard.OwnerKeyFor(
            null, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
    }

    // --- observed_port_policy: what an undeclared listener does to the run ---

    private async Task ObserveAsync(int port, string holder = "grafana") =>
        await _registry.ReplaceObservedAsync(
            ServerId,
            [new ObservedPortDto { Port = port, Protocol = "tcp", Holder = holder, Interface = "0.0.0.0" }],
            _clock.GetUtcNow().UtcDateTime,
            Ct);

    [Fact]
    public async Task ObservedPort_ByDefault_WarnsWithoutRefusingTheLaunch()
    {
        await ObserveAsync(10031);

        var report = await _sut.FindPortConflictsAsync(
            DeployDefinition(), DeployVariables, organizationId: 1, projectId: 9, Ct);

        // The default must not block: an observation may be stale, and refusing on it alone would stop
        // a deployment over a scan nobody confirmed.
        Assert.Empty(report.Blocking);
        var warningProblem = Assert.Single(report.Warnings);
        Assert.Equal(PipelineProblemCodes.PortSeenListening, warningProblem.Code);
        var warning = warningProblem.English;
        Assert.Contains("10031", warning, StringComparison.Ordinal);
        Assert.Contains("grafana", warning, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("error")]
    [InlineData("Error")]
    [InlineData("ERROR")]
    public async Task ObservedPort_WithErrorPolicy_RefusesTheLaunch(string policy)
    {
        await ObserveAsync(10031);

        var report = await _sut.FindPortConflictsAsync(
            DeployDefinition(policy), DeployVariables, organizationId: 1, projectId: 9, Ct);

        Assert.Contains("10031", Assert.Single(report.Blocking).English, StringComparison.Ordinal);
        Assert.Empty(report.Warnings);
    }

    [Fact]
    public async Task ObservedPort_WithIgnorePolicy_IsNotReportedAtAll()
    {
        await ObserveAsync(10031);

        var report = await _sut.FindPortConflictsAsync(
            DeployDefinition("ignore"), DeployVariables, organizationId: 1, projectId: 9, Ct);

        Assert.Empty(report.Blocking);
        Assert.Empty(report.Warnings);
    }

    [Fact]
    public async Task DeclaredPort_IsRefusedWhateverThePolicySays()
    {
        // The policy governs observations only. A project that STATED it owns the port must never be
        // overridden by a YAML setting in somebody else's pipeline.
        await _registry.DeclareAsync(ServerId, [10031], "project:4", "portfolio-prod-front", 4, Ct);

        foreach (var policy in new[] { "ignore", "warning", "error" })
        {
            var report = await _sut.FindPortConflictsAsync(
                DeployDefinition(policy), DeployVariables, organizationId: 1, projectId: 9, Ct);

            Assert.Contains("portfolio-prod-front", Assert.Single(report.Blocking).English, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task UnknownPolicyValue_FallsBackToWarningRatherThanRefusing()
    {
        await ObserveAsync(10031);

        var report = await _sut.FindPortConflictsAsync(
            DeployDefinition("bloque-tout"), DeployVariables, organizationId: 1, projectId: 9, Ct);

        // A typo in an advisory setting must not block a deployment.
        Assert.Empty(report.Blocking);
        Assert.Single(report.Warnings);
    }

    [Theory]
    [InlineData(null, ObservedPortPolicy.Warning)]
    [InlineData("", ObservedPortPolicy.Warning)]
    [InlineData("  ", ObservedPortPolicy.Warning)]
    [InlineData("warning", ObservedPortPolicy.Warning)]
    [InlineData(" error ", ObservedPortPolicy.Error)]
    [InlineData("Ignore", ObservedPortPolicy.Ignore)]
    [InlineData("nonsense", ObservedPortPolicy.Warning)]
    public void ParseObservedPortPolicy_ReadsTheThreeValuesAndDefaultsSafely(
        string? value, ObservedPortPolicy expected)
        => Assert.Equal(expected, _sut.ParseObservedPortPolicy(value));

    public void Dispose() => _db.Dispose();
}
