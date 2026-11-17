// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Certbot;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Shared.Components.Servers;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class CertbotServiceTests
{
    private readonly ICertbotRepository _repoMock = Substitute.For<ICertbotRepository>();
    private readonly IAuditService _auditMock = Substitute.For<IAuditService>();
    private readonly Aetheus.Back.Components.Tasks.ITaskService _taskService = Substitute.For<Aetheus.Back.Components.Tasks.ITaskService>();
    private readonly CertbotService _sut;

    public CertbotServiceTests()
    {
        // A server whose helper is current, unless a test says otherwise.
        _repoMock.GetHelperVersionAsync(1, Arg.Any<CancellationToken>()).Returns(CertbotHelperVersion.Current);
        _sut = new CertbotService(_repoMock, _auditMock, _taskService);
    }

    [Fact]
    public async Task GetCertificatesAsync_ReturnsMappedCerts()
    {
        _repoMock.GetCertificatesAsync(1, Arg.Any<CancellationToken>())
            .Returns([new CertbotCertificate
            {
                Name = "example.com",
                Domains = "[\"example.com\",\"www.example.com\"]",
                ExpiryDate = new DateTime(2025, 12, 31),
                CertPath = "/etc/letsencrypt/live/example.com/fullchain.pem",
                KeyPath = "/etc/letsencrypt/live/example.com/privkey.pem"
            }]);

        var result = await _sut.GetCertificatesAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal("example.com", result[0].Name);
        Assert.Equal(2, result[0].Domains.Count);
        Assert.Contains("www.example.com", result[0].Domains);
    }

    [Fact]
    public async Task GetCertificatesAsync_InvalidDomainsJson_ReturnsEmptyList()
    {
        _repoMock.GetCertificatesAsync(1, Arg.Any<CancellationToken>())
            .Returns([new CertbotCertificate
            {
                Name = "bad", Domains = "not json", ExpiryDate = DateTime.UtcNow,
                CertPath = "/cert", KeyPath = "/key"
            }]);

        var result = await _sut.GetCertificatesAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Empty(result[0].Domains);
    }

    [Theory]
    [InlineData(CertbotAction.Normalize, OperationKind.CertbotNormalize)]
    [InlineData(CertbotAction.RenewalCheck, OperationKind.CertbotRenewalCheck)]
    public async Task ExecuteActionAsync_ConventionActions_QueueTargetlessOperations(CertbotAction action, OperationKind operation)
    {
        await _sut.ExecuteActionAsync(1, new CertbotActionRequest { Action = action }, ct: TestContext.Current.CancellationToken);

        await _repoMock.Received(1).AddTaskAsync(Arg.Is<ServerTask>(t =>
            t.ServerId == 1 && t.Executor == ExecutorType.Operation &&
            t.Operation == operation && t.Command == "-" && t.Status == TaskExecutionStatus.Pending),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task R1_7_ExecuteActionAsync_RenewalCheck_GetsMoreTimeThanTheTwoMinutesOfTheOtherActions()
    {
        await _sut.ExecuteActionAsync(1, new CertbotActionRequest { Action = CertbotAction.RenewalCheck }, ct: TestContext.Current.CancellationToken);
        await _sut.ExecuteActionAsync(1, new CertbotActionRequest { Action = CertbotAction.RenewAll }, ct: TestContext.Current.CancellationToken);

        await _repoMock.Received(1).AddTaskAsync(Arg.Is<ServerTask>(t =>
            t.Operation == OperationKind.CertbotRenewalCheck && t.TimeoutSeconds == CertbotService.RenewalCheckTimeoutSeconds),
            Arg.Any<CancellationToken>());
        await _repoMock.Received(1).AddTaskAsync(Arg.Is<ServerTask>(t =>
            t.Operation == OperationKind.CertbotRenewAll && t.TimeoutSeconds == 120),
            Arg.Any<CancellationToken>());
        Assert.InRange(CertbotService.RenewalCheckTimeoutSeconds, 900, 3600);
    }

    [Fact]
    public async Task ExecuteActionAsync_RenewAll_CreatesTask()
    {
        var request = new CertbotActionRequest { Action = CertbotAction.RenewAll };

        await _sut.ExecuteActionAsync(1, request, ct: TestContext.Current.CancellationToken);

        await _repoMock.Received(1).AddTaskAsync(Arg.Is<ServerTask>(t =>
            t.ServerId == 1 && t.Executor == ExecutorType.Operation &&
            t.Operation == OperationKind.CertbotRenewAll && t.Status == TaskExecutionStatus.Pending),
            Arg.Any<CancellationToken>());
        await _auditMock.Received(1).LogAsync("CertbotRenewAll", "Certbot", 1, "RenewAll", Arg.Any<CancellationToken>());
        await _taskService.Received(1).NotifyTaskQueuedAsync(Arg.Any<ServerTask>(), ct: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteActionAsync_Renew_WithValidName_CreatesTask()
    {
        var request = new CertbotActionRequest { Action = CertbotAction.Renew, CertificateName = "example.com" };
        await _sut.ExecuteActionAsync(1, request, ct: TestContext.Current.CancellationToken);
        await _repoMock.Received(1).AddTaskAsync(Arg.Is<ServerTask>(t =>
            t.Executor == ExecutorType.Operation && t.Operation == OperationKind.CertbotRenew &&
            t.Command == "example.com"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteActionAsync_Renew_NullName_ThrowsBadRequest()
    {
        var request = new CertbotActionRequest { Action = CertbotAction.Renew, CertificateName = null };
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.ExecuteActionAsync(1, request, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExecuteActionAsync_Delete_WithValidName_CreatesTask()
    {
        var request = new CertbotActionRequest { Action = CertbotAction.Delete, CertificateName = "example.com" };
        await _sut.ExecuteActionAsync(1, request, ct: TestContext.Current.CancellationToken);
        await _repoMock.Received(1).AddTaskAsync(Arg.Is<ServerTask>(t =>
            t.Executor == ExecutorType.Operation && t.Operation == OperationKind.CertbotDelete &&
            t.Command == "example.com"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteActionAsync_Delete_NullName_ThrowsBadRequest()
    {
        var request = new CertbotActionRequest { Action = CertbotAction.Delete, CertificateName = null };
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.ExecuteActionAsync(1, request, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExecuteActionAsync_RevokeAndDelete_WithValidName_CreatesTask()
    {
        var request = new CertbotActionRequest { Action = CertbotAction.RevokeAndDelete, CertificateName = "example.com" };
        await _sut.ExecuteActionAsync(1, request, ct: TestContext.Current.CancellationToken);
        await _repoMock.Received(1).AddTaskAsync(Arg.Is<ServerTask>(t =>
            t.Executor == ExecutorType.Operation && t.Operation == OperationKind.CertbotRevoke &&
            t.Command == "example.com"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteActionAsync_RevokeAndDelete_NullName_ThrowsBadRequest()
    {
        var request = new CertbotActionRequest { Action = CertbotAction.RevokeAndDelete, CertificateName = null };
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.ExecuteActionAsync(1, request, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateCertificateAsync_ValidDomains_CreatesTask()
    {
        var request = new CertbotCreateRequest { Domains = "example.com,www.example.com", Email = "admin@example.com" };
        await _sut.CreateCertificateAsync(1, request, ct: TestContext.Current.CancellationToken);

        await _repoMock.Received(1).AddTaskAsync(Arg.Is<ServerTask>(t =>
            t.ServerId == 1 && t.TimeoutSeconds == 180 &&
            t.Executor == ExecutorType.Operation && t.Operation == OperationKind.CertbotObtain &&
            t.Command == "example.com"), Arg.Any<CancellationToken>());
        await _auditMock.Received(1).LogAsync("CertbotCreate", "Certbot", 1, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateCertificateAsync_InvalidDomains_ThrowsBadRequest()
    {
        var request = new CertbotCreateRequest { Domains = "not a valid; domain list!", Email = "admin@example.com" };
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.CreateCertificateAsync(1, request, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task R19_ExecuteActionAsync_RenewalCheck_WithACertificateName_TestsThatCertificateOnly()
    {
        await _sut.ExecuteActionAsync(1, new CertbotActionRequest { Action = CertbotAction.RenewalCheck, CertificateName = "random.example.com" },
            ct: TestContext.Current.CancellationToken);

        await _repoMock.Received(1).AddTaskAsync(Arg.Is<ServerTask>(t =>
            t.Operation == OperationKind.CertbotRenewalCheck && t.Command == "random.example.com" &&
            t.TimeoutSeconds == CertbotService.RenewalCheckTimeoutSeconds), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("--force-renewal")]
    [InlineData("a,b")]
    [InlineData("../x")]
    public async Task R19_ExecuteActionAsync_RenewalCheck_WithAnInvalidName_ThrowsBadRequest(string name)
    {
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.ExecuteActionAsync(1,
            new CertbotActionRequest { Action = CertbotAction.RenewalCheck, CertificateName = name }, ct: TestContext.Current.CancellationToken));
        await _repoMock.DidNotReceive().AddTaskAsync(Arg.Any<ServerTask>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task R19_RenewalCheckFailed_QueuesOneTaskNamingOnlyTheFailedCertificatesThatStillExist()
    {
        _repoMock.GetRenewalCheckFailedNamesAsync(1, Arg.Any<CancellationToken>()).Returns(["random.example.com", "gone.example.com", "demo.example.com"]);
        _repoMock.GetCertificatesAsync(1, Arg.Any<CancellationToken>()).Returns(
        [
            new CertbotCertificate { Name = "random.example.com", Domains = "[]" },
            new CertbotCertificate { Name = "demo.example.com", Domains = "[]" },
            new CertbotCertificate { Name = "fine.example.com", Domains = "[]" }
        ]);

        await _sut.ExecuteActionAsync(1, new CertbotActionRequest { Action = CertbotAction.RenewalCheckFailed }, ct: TestContext.Current.CancellationToken);

        await _repoMock.Received(1).AddTaskAsync(Arg.Is<ServerTask>(t =>
            t.ServerId == 1 && t.Executor == ExecutorType.Operation && t.Operation == OperationKind.CertbotRenewalCheck &&
            t.Command == "demo.example.com,random.example.com" && t.TimeoutSeconds == CertbotService.RenewalCheckTimeoutSeconds),
            Arg.Any<CancellationToken>());
        await _auditMock.Received(1).LogAsync("CertbotRenewalCheckFailed", "Certbot", 1, "demo.example.com, random.example.com", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task R19_RenewalCheckFailed_WithNothingFailed_IsRefusedAndQueuesNothing()
    {
        _repoMock.GetRenewalCheckFailedNamesAsync(1, Arg.Any<CancellationToken>()).Returns([]);

        await Assert.ThrowsAsync<BadRequestException>(() => _sut.ExecuteActionAsync(1,
            new CertbotActionRequest { Action = CertbotAction.RenewalCheckFailed }, ct: TestContext.Current.CancellationToken));
        await _repoMock.DidNotReceive().AddTaskAsync(Arg.Any<ServerTask>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task R19_RenewalCheckFailed_WhenEveryFailureIsAGoneCertificate_IsRefused()
    {
        _repoMock.GetRenewalCheckFailedNamesAsync(1, Arg.Any<CancellationToken>()).Returns(["gone.example.com"]);
        _repoMock.GetCertificatesAsync(1, Arg.Any<CancellationToken>()).Returns([new CertbotCertificate { Name = "fine.example.com", Domains = "[]" }]);

        await Assert.ThrowsAsync<BadRequestException>(() => _sut.ExecuteActionAsync(1,
            new CertbotActionRequest { Action = CertbotAction.RenewalCheckFailed }, ct: TestContext.Current.CancellationToken));
        await _repoMock.DidNotReceive().AddTaskAsync(Arg.Any<ServerTask>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task R19_RenewalCheckFailed_AboveTheNameLimit_IsRefusedWithAWayOut()
    {
        var names = Enumerable.Range(0, Aetheus.Shared.Components.Pipelines.CertbotRenewalCheckTarget.MaxNames + 1).Select(i => $"c{i}.example.com").ToList();
        _repoMock.GetRenewalCheckFailedNamesAsync(1, Arg.Any<CancellationToken>()).Returns(names);
        _repoMock.GetCertificatesAsync(1, Arg.Any<CancellationToken>()).Returns(names.Select(n => new CertbotCertificate { Name = n, Domains = "[]" }).ToList());

        var error = await Assert.ThrowsAsync<BadRequestException>(() => _sut.ExecuteActionAsync(1,
            new CertbotActionRequest { Action = CertbotAction.RenewalCheckFailed }, ct: TestContext.Current.CancellationToken));
        Assert.Contains("one by one", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(CertbotHelperVersion.TargetedRenewalCheck - 1)]
    public async Task R19_RenewalCheck_WithACertificateName_OnAnOlderHelper_IsRefusedAndQueuesNothing(int helperVersion)
    {
        _repoMock.GetHelperVersionAsync(1, Arg.Any<CancellationToken>()).Returns(helperVersion);

        var error = await Assert.ThrowsAsync<BadRequestException>(() => _sut.ExecuteActionAsync(1,
            new CertbotActionRequest { Action = CertbotAction.RenewalCheck, CertificateName = "random.example.com" }, ct: TestContext.Current.CancellationToken));

        Assert.Contains("Update the agent", error.Message, StringComparison.Ordinal);
        await _repoMock.DidNotReceive().AddTaskAsync(Arg.Any<ServerTask>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task R19_RenewalCheckFailed_OnAnOlderHelper_IsRefusedAndQueuesNothing()
    {
        _repoMock.GetHelperVersionAsync(1, Arg.Any<CancellationToken>()).Returns(CertbotHelperVersion.TargetedRenewalCheck - 1);
        _repoMock.GetRenewalCheckFailedNamesAsync(1, Arg.Any<CancellationToken>()).Returns(["random.example.com"]);
        _repoMock.GetCertificatesAsync(1, Arg.Any<CancellationToken>()).Returns([new CertbotCertificate { Name = "random.example.com", Domains = "[]" }]);

        var error = await Assert.ThrowsAsync<BadRequestException>(() => _sut.ExecuteActionAsync(1,
            new CertbotActionRequest { Action = CertbotAction.RenewalCheckFailed }, ct: TestContext.Current.CancellationToken));

        Assert.Contains("Update the agent", error.Message, StringComparison.Ordinal);
        await _repoMock.DidNotReceive().AddTaskAsync(Arg.Any<ServerTask>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task R19_RenewalCheck_OfEveryCertificate_StillWorksOnAnOlderHelper()
    {
        _repoMock.GetHelperVersionAsync(1, Arg.Any<CancellationToken>()).Returns(0);

        await _sut.ExecuteActionAsync(1, new CertbotActionRequest { Action = CertbotAction.RenewalCheck }, ct: TestContext.Current.CancellationToken);

        await _repoMock.Received(1).AddTaskAsync(Arg.Is<ServerTask>(t => t.Operation == OperationKind.CertbotRenewalCheck && t.Command == "-"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task R12_EveryCertbotTaskName_StartsWithTheSharedPrefixThePageFollows()
    {
        _repoMock.GetRenewalCheckFailedNamesAsync(1, Arg.Any<CancellationToken>()).Returns(["a.example.com"]);
        _repoMock.GetCertificatesAsync(1, Arg.Any<CancellationToken>()).Returns([new CertbotCertificate { Name = "a.example.com", Domains = "[]" }]);
        foreach (var request in new CertbotActionRequest[]
        {
            new() { Action = CertbotAction.RenewAll },
            new() { Action = CertbotAction.RenewalCheck, CertificateName = "a.example.com" },
            new() { Action = CertbotAction.RenewalCheckFailed },
            new() { Action = CertbotAction.Renew, CertificateName = "a.example.com" }
        })
            await _sut.ExecuteActionAsync(1, request, ct: TestContext.Current.CancellationToken);
        await _sut.CreateCertificateAsync(1, new CertbotCreateRequest { Domains = "example.com", Email = "admin@example.com" }, ct: TestContext.Current.CancellationToken);

        var names = _repoMock.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(ICertbotRepository.AddTaskAsync))
            .Select(call => ((ServerTask)call.GetArguments()[0]!).Name).ToList();
        Assert.Equal(5, names.Count);
        Assert.All(names, name => Assert.StartsWith(ServerTaskNames.CertbotPrefix + " ", name, StringComparison.Ordinal));
        Assert.Contains(names, name => name == "Certbot RenewalCheck - a.example.com");
    }

    [Fact]
    public async Task GetCertificatesAsync_EmptyList_ReturnsEmpty()
    {
        _repoMock.GetCertificatesAsync(1, Arg.Any<CancellationToken>())
            .Returns([]);

        var result = await _sut.GetCertificatesAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.Empty(result);
    }
}
