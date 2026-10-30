// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Rkhunter;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class RkhunterServiceTests
{
    private readonly IRkhunterRepository _repoMock = Substitute.For<IRkhunterRepository>();
    private readonly IAuditService _auditMock = Substitute.For<IAuditService>();
    private readonly Aetheus.Back.Components.Tasks.ITaskService _taskService = Substitute.For<Aetheus.Back.Components.Tasks.ITaskService>();
    private readonly RkhunterService _sut;

    public RkhunterServiceTests()
    {
        _sut = new RkhunterService(_repoMock, _auditMock, _taskService);
    }

    // --- GetStateAsync ---

    [Fact]
    public async Task GetStateAsync_NoState_ReturnsDefaultDto()
    {
        _repoMock.GetStateAsync(1, Arg.Any<CancellationToken>())
            .Returns((RkhunterState?)null);

        var result = await _sut.GetStateAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.False(result.IsInstalled);
        Assert.Equal(string.Empty, result.Version);
    }

    [Fact]
    public async Task GetStateAsync_WithState_ReturnsMappedDto()
    {
        var state = new RkhunterState
        {
            ServerId = 1,
            Version = "1.4.6",
            DatabaseVersion = "2026060100",
            LastScanTime = new DateTime(2026, 6, 10, 8, 0, 0, DateTimeKind.Utc),
            LastScanStatus = "Clean",
            WarningCount = 2,
            DatabaseLastUpdated = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            ScanScheduleCron = "0 3 * * *"
        };
        _repoMock.GetStateAsync(1, Arg.Any<CancellationToken>()).Returns(state);

        var result = await _sut.GetStateAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.True(result.IsInstalled);
        Assert.Equal("1.4.6", result.Version);
        Assert.Equal("2026060100", result.DatabaseVersion);
        Assert.Equal("Clean", result.LastScanStatus);
        Assert.Equal(2, result.WarningCount);
        Assert.Equal("0 3 * * *", result.ScanScheduleCron);
    }

    // --- ExecuteActionAsync ---

    [Theory]
    [InlineData(RkhunterAction.RunScan)]
    [InlineData(RkhunterAction.UpdateDatabase)]
    [InlineData(RkhunterAction.UpdateProperties)]
    public async Task ExecuteActionAsync_ValidAction_CreatesTaskAndAudits(RkhunterAction action)
    {
        var request = new RkhunterActionRequest { Action = action };

        await _sut.ExecuteActionAsync(1, request, ct: TestContext.Current.CancellationToken);

        await _repoMock.Received(1).AddTaskAsync(
            Arg.Is<ServerTask>(t => t.ServerId == 1 && t.Executor == ExecutorType.Operation),
            Arg.Any<CancellationToken>());
        await _auditMock.Received(1).LogAsync(
            $"Rkhunter{action}", "Rkhunter", 1, action.ToString(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteActionAsync_RunScan_UsesLongerTimeout()
    {
        var request = new RkhunterActionRequest { Action = RkhunterAction.RunScan };

        await _sut.ExecuteActionAsync(1, request, ct: TestContext.Current.CancellationToken);

        await _repoMock.Received(1).AddTaskAsync(
            Arg.Is<ServerTask>(t => t.TimeoutSeconds == 300),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteActionAsync_UpdateDatabase_Uses60SecondTimeout()
    {
        var request = new RkhunterActionRequest { Action = RkhunterAction.UpdateDatabase };

        await _sut.ExecuteActionAsync(1, request, ct: TestContext.Current.CancellationToken);

        await _repoMock.Received(1).AddTaskAsync(
            Arg.Is<ServerTask>(t => t.TimeoutSeconds == 60),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteActionAsync_InvalidAction_ThrowsBadRequestException()
    {
        var request = new RkhunterActionRequest { Action = (RkhunterAction)999 };

        await Assert.ThrowsAsync<BadRequestException>(() => _sut.ExecuteActionAsync(1, request, ct: TestContext.Current.CancellationToken));
    }

    // --- SetupAsync ---

    // R-119: the install was an `apt-get install ... && rkhunter ... && sed -i` shell chain, run without root and
    // carrying no kind, so the agent never collected its RKHunter inventory again. It is a typed operation now; the
    // only variable, the warning address, travels in its environment, never in a command line.
    [Fact]
    public async Task SetupAsync_QueuesTheTypedOperation_WithTheAddressInItsEnvironment_AndAudits()
    {
        var request = new RkhunterSetupRequest { MailOnWarning = "admin@example.com" };

        await _sut.SetupAsync(1, request, ct: TestContext.Current.CancellationToken);

        await _repoMock.Received(1).AddTaskAsync(
            Arg.Is<ServerTask>(t => t.ServerId == 1
                && t.Executor == ExecutorType.Operation
                && t.Operation == OperationKind.RkhunterSetup
                && t.Command == "-"
                && t.Name.StartsWith("RKHunter", StringComparison.Ordinal)
                && Environment(t)[RkhunterSetupEnv.MailOnWarning] == "admin@example.com"),
            Arg.Any<CancellationToken>());
        await _auditMock.Received(1).LogAsync(
            "RkhunterSetup", "Rkhunter", 1, "admin@example.com", Arg.Any<CancellationToken>());
        await _taskService.Received(1).NotifyTaskQueuedAsync(Arg.Any<ServerTask>(), ct: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetupAsync_NoMail_AuditsNoMail()
    {
        var request = new RkhunterSetupRequest();

        await _sut.SetupAsync(1, request, ct: TestContext.Current.CancellationToken);

        await _auditMock.Received(1).LogAsync(
            "RkhunterSetup", "Rkhunter", 1, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _repoMock.Received(1).AddTaskAsync(
            Arg.Is<ServerTask>(t => t.Operation == OperationKind.RkhunterSetup
                && !Environment(t).ContainsKey(RkhunterSetupEnv.MailOnWarning)),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("admin@example.com\"; rm -rf /; echo \"")]
    [InlineData("admin@example.com\nMAIL-CMD=x")]
    [InlineData("admin@example.com\n")]
    public async Task SetupAsync_InvalidAddress_ThrowsBadRequest_AndQueuesNothing(string address)
    {
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.SetupAsync(
            1, new RkhunterSetupRequest { MailOnWarning = address }, ct: TestContext.Current.CancellationToken));
        await _repoMock.DidNotReceive().AddTaskAsync(Arg.Any<ServerTask>(), Arg.Any<CancellationToken>());
    }

    private static Dictionary<string, string> Environment(ServerTask task) =>
        string.IsNullOrEmpty(task.EnvironmentVariables)
            ? []
            : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(task.EnvironmentVariables)!;

    // --- GetLogsAsync ---

    [Fact]
    public async Task GetLogsAsync_ClampsLinesToRange()
    {
        var request = new RkhunterLogRequest { Lines = 10000 };

        await _sut.GetLogsAsync(1, request, ct: TestContext.Current.CancellationToken);

        // Lines are clamped to [1, 5000]: the generated shell command must use the clamped
        // upper bound (tail -n 5000), never the requested 10000.
        await _repoMock.Received(1).AddTaskAsync(
            Arg.Is<ServerTask>(t => t.ServerId == 1
                && t.Executor == ExecutorType.Shell
                && t.Command.Contains("tail -n 5000")
                && !t.Command.Contains("10000")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetLogsAsync_LinesBelowRange_ClampsToMinimumOne()
    {
        var request = new RkhunterLogRequest { Lines = 0 };

        await _sut.GetLogsAsync(1, request, ct: TestContext.Current.CancellationToken);

        // 0 is clamped up to the minimum of 1 (tail -n 1).
        await _repoMock.Received(1).AddTaskAsync(
            Arg.Is<ServerTask>(t => t.Command.Contains("tail -n 1 ")),
            Arg.Any<CancellationToken>());
    }

    // --- GetWarningsAsync ---

    [Fact]
    public async Task GetWarningsAsync_ReturnsMappedList()
    {
        _repoMock.GetWarningsAsync(1, false, Arg.Any<CancellationToken>())
            .Returns([
                new RkhunterWarning
                {
                    Id = 1,
                    Category = "rootkits",
                    Detail = "Suspicious file found",
                    Severity = "Warning",
                    FoundAt = new DateTime(2026, 6, 10, 0, 0, 0, DateTimeKind.Utc),
                    IsArchived = false
                }
            ]);

        var result = await _sut.GetWarningsAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal("rootkits", result[0].Category);
        Assert.Equal("Warning", result[0].Severity);
        Assert.False(result[0].IsArchived);
    }

    [Fact]
    public async Task GetWarningsAsync_IncludeArchived_PassesFlagToRepository()
    {
        _repoMock.GetWarningsAsync(1, true, Arg.Any<CancellationToken>())
            .Returns(new List<RkhunterWarning>());

        await _sut.GetWarningsAsync(1, includeArchived: true, ct: TestContext.Current.CancellationToken);

        await _repoMock.Received(1).GetWarningsAsync(1, true, Arg.Any<CancellationToken>());
    }

    // --- GetScanHistoryAsync ---

    [Fact]
    public async Task GetScanHistoryAsync_ReturnsMappedList()
    {
        _repoMock.GetScanHistoryAsync(1, 50, Arg.Any<CancellationToken>())
            .Returns([
                new RkhunterScanResult
                {
                    Id = 1,
                    ScanTime = new DateTime(2026, 6, 10, 8, 0, 0, DateTimeKind.Utc),
                    Status = "Warning",
                    WarningCount = 3,
                    Summary = "3 warnings found"
                }
            ]);

        var result = await _sut.GetScanHistoryAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal("Warning", result[0].Status);
        Assert.Equal(3, result[0].WarningCount);
    }

    [Fact]
    public async Task GetScanHistoryAsync_ClampsLimitTo200()
    {
        _repoMock.GetScanHistoryAsync(1, 200, Arg.Any<CancellationToken>())
            .Returns(new List<RkhunterScanResult>());

        await _sut.GetScanHistoryAsync(1, limit: 999, ct: TestContext.Current.CancellationToken);

        await _repoMock.Received(1).GetScanHistoryAsync(1, 200, Arg.Any<CancellationToken>());
    }

    // --- SetScheduleAsync ---

    [Fact]
    public async Task SetScheduleAsync_ValidCron_UpdatesAndAudits()
    {
        var request = new RkhunterScheduleRequest { CronExpression = "0 3 * * *" };

        await _sut.SetScheduleAsync(1, request, ct: TestContext.Current.CancellationToken);

        await _repoMock.Received(1).UpdateScanScheduleAsync(1, "0 3 * * *", Arg.Any<CancellationToken>());
        await _auditMock.Received(1).LogAsync(
            "RkhunterSchedule", "Rkhunter", 1, "0 3 * * *", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetScheduleAsync_NullCron_DisablesSchedule()
    {
        var request = new RkhunterScheduleRequest { CronExpression = null };

        await _sut.SetScheduleAsync(1, request, ct: TestContext.Current.CancellationToken);

        await _repoMock.Received(1).UpdateScanScheduleAsync(1, null, Arg.Any<CancellationToken>());
        await _auditMock.Received(1).LogAsync(
            "RkhunterSchedule", "Rkhunter", 1, "disabled", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetScheduleAsync_InvalidCron_ThrowsCronFormatException()
    {
        var request = new RkhunterScheduleRequest { CronExpression = "not a cron" };

        await Assert.ThrowsAsync<Cronos.CronFormatException>(() => _sut.SetScheduleAsync(1, request, ct: TestContext.Current.CancellationToken));
    }
}
