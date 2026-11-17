// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Operations;
using Aetheus.Agent.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// Recette R-8: the agent side of a scheduled reboot. The reboot command goes through a substituted shell runner, so
/// nothing is ever rebooted; what is asserted is when the command is run, with which argv, and when it is refused.
/// </summary>
public sealed class ServerRebootOperationExecutorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 1, 6, 0, TimeSpan.Zero);

    private readonly IShellRunner _shell = Substitute.For<IShellRunner>();
    private readonly IRebootOrderConfirmer _confirmer = Substitute.For<IRebootOrderConfirmer>();
    private readonly List<string> _output = [];
    private readonly ServerRebootOperationExecutor _executor;

    public ServerRebootOperationExecutorTests()
    {
        _executor = new ServerRebootOperationExecutor(_shell, _confirmer, new FakeTimeProvider(Now),
            NullLogger<ServerRebootOperationExecutor>.Instance);
        _shell.RunExecAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(new ShellExecResult(0, string.Empty, string.Empty));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task OnOutput(string line, TaskLogLevel level)
    {
        _output.Add(line);
        return Task.CompletedTask;
    }

    private static Dictionary<string, string> Order(string notAfter = "2026-11-07T02:05:00.0000000Z") => new()
    {
        ["AETHEUS_TASK_ID"] = "42",
        [ServerRebootOrder.ScheduleVersionEnvVar] = "3",
        [ServerRebootOrder.NotAfterEnvVar] = notAfter
    };

    private void Confirm(bool allowed, RebootOrderRefusal refusal = RebootOrderRefusal.None) =>
        _confirmer.ConfirmAsync(42, Arg.Any<CancellationToken>())
            .Returns(new RebootOrderConfirmationDto { Allowed = allowed, Refusal = refusal });

    [Fact]
    public async Task A_confirmed_order_runs_the_argv_exact_reboot_through_sudo()
    {
        Confirm(allowed: true);

        var result = await _executor.RebootAsync(Order(), OnOutput, Ct);

        Assert.Equal(0, result.ExitCode);
        await _shell.Received(1).RunExecAsync("sudo",
            Arg.Is<IReadOnlyList<string>>(argv => argv.SequenceEqual(new[] { "-n", "/bin/systemctl", "reboot" })),
            Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>());
    }

    [Theory]
    [InlineData(RebootOrderRefusal.ScheduleChanged)]
    [InlineData(RebootOrderRefusal.ScheduleDisabled)]
    [InlineData(RebootOrderRefusal.ScheduleRemoved)]
    [InlineData(RebootOrderRefusal.ServerBusy)]
    public async Task An_order_the_backend_refuses_never_reboots(RebootOrderRefusal refusal)
    {
        Confirm(allowed: false, refusal);

        var result = await _executor.RebootAsync(Order(), OnOutput, Ct);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(TaskFailureCodes.RebootRefused, result.FailureCode);
        Assert.Equal(refusal.ToString(), result.FailureReason);
        await _shell.DidNotReceiveWithAnyArgs().RunExecAsync(default!, default!, Arg.Any<CancellationToken>(), default);
    }

    [Fact]
    public async Task An_expired_order_is_refused_without_asking_the_backend()
    {
        var result = await _executor.RebootAsync(Order(notAfter: "2026-11-07T01:00:00.0000000Z"), OnOutput, Ct);

        Assert.Equal(RebootOrderRefusal.Expired.ToString(), result.FailureReason);
        await _confirmer.DidNotReceiveWithAnyArgs().ConfirmAsync(default, Arg.Any<CancellationToken>());
        await _shell.DidNotReceiveWithAnyArgs().RunExecAsync(default!, default!, Arg.Any<CancellationToken>(), default);
    }

    [Fact]
    public async Task No_answer_from_the_backend_fails_closed()
    {
        _confirmer.ConfirmAsync(42, Arg.Any<CancellationToken>()).Returns((RebootOrderConfirmationDto?)null);

        var result = await _executor.RebootAsync(Order(), OnOutput, Ct);

        Assert.Equal(TaskFailureCodes.RebootRefused, result.FailureCode);
        await _shell.DidNotReceiveWithAnyArgs().RunExecAsync(default!, default!, Arg.Any<CancellationToken>(), default);
        Assert.Contains(_output, line => line.Contains("not rebooted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_order_without_task_id_or_deadline_is_refused()
    {
        var noTask = Order();
        noTask.Remove("AETHEUS_TASK_ID");
        var noDeadline = Order();
        noDeadline.Remove(ServerRebootOrder.NotAfterEnvVar);

        Assert.Equal(TaskFailureCodes.RebootRefused, (await _executor.RebootAsync(noTask, OnOutput, Ct)).FailureCode);
        Assert.Equal(TaskFailureCodes.RebootRefused, (await _executor.RebootAsync(noDeadline, OnOutput, Ct)).FailureCode);
        await _shell.DidNotReceiveWithAnyArgs().RunExecAsync(default!, default!, Arg.Any<CancellationToken>(), default);
    }

    [Fact]
    public async Task A_failing_reboot_command_is_reported_as_a_failure()
    {
        Confirm(allowed: true);
        _shell.RunExecAsync("sudo", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(new ShellExecResult(1, string.Empty, "sudo: a password is required"));

        var result = await _executor.RebootAsync(Order(), OnOutput, Ct);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(_output, line => line.Contains("a password is required", StringComparison.Ordinal));
    }
}
