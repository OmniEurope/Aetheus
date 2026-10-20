// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>Refused automated launches are recorded outside the caller's transaction so rollback
/// cannot erase their evidence. Ancillary failures must not replace the original refusal.</summary>
public sealed class PipelineRefusedLaunchRecorderTests
{
    private readonly IPipelineRepository _repo = Substitute.For<IPipelineRepository>();
    private readonly IHubContext<PipelineHub> _hub = Substitute.For<IHubContext<PipelineHub>>();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly IUserNotificationService _notifications = Substitute.For<IUserNotificationService>();
    private readonly IDbTransactionScope _transaction = Substitute.For<IDbTransactionScope>();
    private readonly Pipeline _pipeline = new() { Id = 1, Name = "aetheus-nightly", Runs = [] };

    private PipelineRefusedLaunchRecorder Build() => new(
        _repo, _hub, _audit, new PipelineRunNotificationPublisher(_repo, _notifications), TimeProvider.System,
        Substitute.For<ILogger<PipelineRefusedLaunchRecorder>>(), _transaction);

    [Fact]
    public async Task InsideACallersTransaction_NothingIsRecorded()
    {
        _transaction.InTransaction.Returns(true);

        await Build().RecordAsync(_pipeline, null, "Webhook", "refused", null, TestContext.Current.CancellationToken);

        await _repo.DidNotReceive().AddPipelineRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>());
        await _audit.DidNotReceive().LogAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _notifications.DidNotReceive().RecordProjectEventAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AFailingSideEffect_LeavesTheRunSaved_AndTheOtherEffectsSent()
    {
        _hub.Clients.Groups(Arg.Any<IReadOnlyList<string>>()).Throws(new InvalidOperationException("hub down"));
        _repo.GetPipelineProjectIdAsync(_pipeline, Arg.Any<CancellationToken>()).Returns(7);

        await Build().RecordAsync(_pipeline, null, "Scheduler", "refused", null, TestContext.Current.CancellationToken);

        await _repo.Received(1).AddPipelineRunAsync(
            Arg.Is<PipelineRun>(run => run.Status == PipelineStatus.Failed && run.WarningsJson!.Contains("refused")),
            Arg.Any<CancellationToken>());
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync("LaunchRefused", "PipelineRun", Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _notifications.Received(1).RecordProjectEventAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task R1_3_TheSameRefusalAgain_CountsOnTheLastRefusedRun_WithoutANewRun()
    {
        // A frequent schedule refused for one reason left one failed run per passage.
        var refused = new PipelineRun
        {
            Id = 40,
            PipelineId = 1,
            Status = PipelineStatus.Failed,
            WarningsJson = System.Text.Json.JsonSerializer.Serialize(new[] { PipelineRefusedLaunchRecorder.BuildReason("Scheduler", "locked") })
        };
        _repo.GetLatestRunTrackedAsync(1, Arg.Any<CancellationToken>()).Returns(refused);

        await Build().RecordAsync(_pipeline, null, "Scheduler", "locked", null, TestContext.Current.CancellationToken);
        await Build().RecordAsync(_pipeline, null, "Scheduler", "locked", null, TestContext.Current.CancellationToken);

        await _repo.DidNotReceive().AddPipelineRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>());
        var warnings = System.Text.Json.JsonSerializer.Deserialize<string[]>(refused.WarningsJson!)!;
        Assert.Equal(2, warnings.Length);
        Assert.StartsWith("Refused 3 times, last at ", warnings[1], StringComparison.Ordinal);
        Assert.NotNull(refused.CompletedAt);
        // Each refusal stays audited; the subscribers were told the first time only.
        await _audit.Received(2).LogAsync("LaunchRefused", "PipelineRun", 40, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _notifications.DidNotReceive().RecordProjectEventAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task R1_3_ADifferentRefusal_OrARunInBetween_StillAddsARun()
    {
        _repo.GetLatestRunTrackedAsync(1, Arg.Any<CancellationToken>()).Returns(new PipelineRun
        {
            Id = 41,
            PipelineId = 1,
            Status = PipelineStatus.Failed,
            WarningsJson = System.Text.Json.JsonSerializer.Serialize(new[] { PipelineRefusedLaunchRecorder.BuildReason("Scheduler", "locked") })
        });

        await Build().RecordAsync(_pipeline, null, "Scheduler", "another reason", null, TestContext.Current.CancellationToken);

        await _repo.Received(1).AddPipelineRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>());
    }
}
