// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.Mail;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class MailControllerTests
{
    private readonly IMailService _serviceMock = Substitute.For<IMailService>();
    private readonly IResourceAuthorizationService _authzMock = Substitute.For<IResourceAuthorizationService>();
    private readonly MailController _sut;

    public MailControllerTests()
    {
        _sut = new MailController(_serviceMock, _authzMock);
        _sut.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, "1")], "test"))
            }
        };
    }

    private void AllowRead(int sid) =>
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, sid, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(true);

    private void AllowWrite(int sid) =>
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, sid, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(true);

    private void DenyRead(int sid) =>
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, sid, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(false);

    private void DenyWrite(int sid) =>
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, sid, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(false);

    [Fact]
    public async Task GetState_Authorized_ReturnsOk()
    {
        AllowRead(1);
        _serviceMock.GetStateAsync(1, Arg.Any<CancellationToken>()).Returns(new MailDataDto());
        var result = await _sut.GetState(1, TestContext.Current.CancellationToken);
        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetState_Forbidden_ReturnsForbid()
    {
        DenyRead(1);
        var result = await _sut.GetState(1, TestContext.Current.CancellationToken);
        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task GetDomains_Authorized_ReturnsOk()
    {
        AllowRead(1);
        _serviceMock.GetDomainsAsync(1, Arg.Any<PaginationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<MailDomainDto>());
        var result = await _sut.GetDomains(1, new PaginationRequest(), TestContext.Current.CancellationToken);
        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetDomains_Forbidden_ReturnsForbid()
    {
        DenyRead(1);
        var result = await _sut.GetDomains(1, new PaginationRequest(), TestContext.Current.CancellationToken);
        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task GetDomain_Authorized_ReturnsOk()
    {
        AllowRead(1);
        _serviceMock.GetDomainAsync(1, 10, Arg.Any<CancellationToken>()).Returns(new MailDomainDto { Id = 10 });
        var result = await _sut.GetDomain(1, 10, TestContext.Current.CancellationToken);
        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task CreateDomain_Authorized_ReturnsCreated()
    {
        AllowWrite(1);
        _serviceMock.CreateDomainAsync(1, Arg.Any<CreateMailDomainRequest>(), Arg.Any<CancellationToken>())
            .Returns(new MailDomainDto { Id = 10 });
        var result = await _sut.CreateDomain(1, new CreateMailDomainRequest { Name = "test.com" }, TestContext.Current.CancellationToken);
        Assert.IsType<CreatedAtActionResult>(result.Result);
    }

    [Fact]
    public async Task CreateDomain_Forbidden_ReturnsForbid()
    {
        DenyWrite(1);
        var result = await _sut.CreateDomain(1, new CreateMailDomainRequest { Name = "t.com" }, TestContext.Current.CancellationToken);
        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task UpdateDomain_Authorized_ReturnsOk()
    {
        AllowWrite(1);
        _serviceMock.UpdateDomainAsync(1, 10, Arg.Any<UpdateMailDomainRequest>(), Arg.Any<CancellationToken>())
            .Returns(new MailDomainDto { Id = 10 });
        var result = await _sut.UpdateDomain(1, 10, new UpdateMailDomainRequest(), TestContext.Current.CancellationToken);
        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task DeleteDomain_Authorized_ReturnsNoContent()
    {
        AllowWrite(1);
        _serviceMock.DeleteDomainAsync(1, 10, Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var result = await _sut.DeleteDomain(1, 10, TestContext.Current.CancellationToken);
        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task DeleteDomain_Forbidden_ReturnsForbid()
    {
        DenyWrite(1);
        var result = await _sut.DeleteDomain(1, 10, TestContext.Current.CancellationToken);
        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task GetAccounts_Authorized_ReturnsOk()
    {
        AllowRead(1);
        _serviceMock.GetAccountsAsync(1, Arg.Any<PaginationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<MailAccountDto>());
        var result = await _sut.GetAccounts(1, new PaginationRequest(), TestContext.Current.CancellationToken);
        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task CreateAccount_Authorized_ReturnsCreated()
    {
        AllowWrite(1);
        _serviceMock.CreateAccountAsync(1, Arg.Any<CreateMailAccountRequest>(), Arg.Any<CancellationToken>())
            .Returns(new MailAccountDto { Id = 10 });
        var result = await _sut.CreateAccount(1, new CreateMailAccountRequest(), TestContext.Current.CancellationToken);
        Assert.IsType<CreatedResult>(result.Result);
    }

    [Fact]
    public async Task UpdateAccount_Authorized_ReturnsOk()
    {
        AllowWrite(1);
        _serviceMock.UpdateAccountAsync(1, 10, Arg.Any<UpdateMailAccountRequest>(), Arg.Any<CancellationToken>())
            .Returns(new MailAccountDto { Id = 10 });
        var result = await _sut.UpdateAccount(1, 10, new UpdateMailAccountRequest(), TestContext.Current.CancellationToken);
        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task DeleteAccount_Authorized_ReturnsNoContent()
    {
        AllowWrite(1);
        _serviceMock.DeleteAccountAsync(1, 10, Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var result = await _sut.DeleteAccount(1, 10, TestContext.Current.CancellationToken);
        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task ExecuteAction_Authorized_ReturnsOk()
    {
        AllowWrite(1);
        _serviceMock.ExecuteActionAsync(1, Arg.Any<MailActionRequest>(), Arg.Any<CancellationToken>()).Returns(new MailTaskQueuedDto { TaskId = 5 });
        var result = await _sut.ExecuteAction(1, new MailActionRequest(), TestContext.Current.CancellationToken);
        Assert.Equal(5, Assert.IsType<MailTaskQueuedDto>(Assert.IsType<OkObjectResult>(result.Result).Value).TaskId);
    }

    [Fact]
    public async Task ExecuteAction_Forbidden_ReturnsForbid()
    {
        DenyWrite(1);
        var result = await _sut.ExecuteAction(1, new MailActionRequest(), TestContext.Current.CancellationToken);
        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task GetLogs_Authorized_ReturnsOk()
    {
        AllowRead(1);
        _serviceMock.GetLogsAsync(1, Arg.Any<MailLogRequest>(), Arg.Any<CancellationToken>()).Returns(new MailTaskQueuedDto { TaskId = 6 });
        var result = await _sut.GetLogs(1, new MailLogRequest(), TestContext.Current.CancellationToken);
        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task Setup_Authorized_ReturnsOk()
    {
        AllowWrite(1);
        _serviceMock.SetupAsync(1, Arg.Any<MailSetupRequest>(), Arg.Any<CancellationToken>()).Returns(new MailTaskQueuedDto { TaskId = 23257 });
        var result = await _sut.Setup(1, new MailSetupRequest(), TestContext.Current.CancellationToken);
        // The page follows this task and keeps "Configurer" busy until it completes.
        Assert.Equal(23257, Assert.IsType<MailTaskQueuedDto>(Assert.IsType<OkObjectResult>(result.Result).Value).TaskId);
    }

    [Fact]
    public async Task GetDnsRecords_Authorized_ReturnsOk()
    {
        AllowRead(1);
        _serviceMock.GetDnsRecordsAsync(1, 10, Arg.Any<CancellationToken>()).Returns(new MailDnsRecordsDto());
        var result = await _sut.GetDnsRecords(1, 10, TestContext.Current.CancellationToken);
        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetAliases_Authorized_ReturnsOk()
    {
        AllowRead(1);
        _serviceMock.GetAliasesAsync(1, Arg.Any<PaginationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<MailAliasDto>());
        var result = await _sut.GetAliases(1, new PaginationRequest(), TestContext.Current.CancellationToken);
        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task CreateAlias_Authorized_ReturnsCreated()
    {
        AllowWrite(1);
        _serviceMock.CreateAliasAsync(1, 10, Arg.Any<CreateMailAliasRequest>(), Arg.Any<CancellationToken>())
            .Returns(new MailAliasDto());
        var result = await _sut.CreateAlias(1, 10, new CreateMailAliasRequest(), TestContext.Current.CancellationToken);
        Assert.IsType<CreatedResult>(result.Result);
    }

    [Fact]
    public async Task UpdateAlias_Authorized_ReturnsOk()
    {
        AllowWrite(1);
        _serviceMock.UpdateAliasAsync(1, 5, Arg.Any<UpdateMailAliasRequest>(), Arg.Any<CancellationToken>())
            .Returns(new MailAliasDto());
        var result = await _sut.UpdateAlias(1, 5, new UpdateMailAliasRequest(), TestContext.Current.CancellationToken);
        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task DeleteAlias_Authorized_ReturnsNoContent()
    {
        AllowWrite(1);
        _serviceMock.DeleteAliasAsync(1, 5, Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var result = await _sut.DeleteAlias(1, 5, TestContext.Current.CancellationToken);
        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task RotateDkimKey_Authorized_ReturnsOk()
    {
        AllowWrite(1);
        _serviceMock.RotateDkimKeyAsync(1, 10, Arg.Any<DkimRotationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new DkimRotationResultDto());
        var result = await _sut.RotateDkimKey(1, 10, new DkimRotationRequest(), TestContext.Current.CancellationToken);
        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task RotateDkimKey_Forbidden_ReturnsForbid()
    {
        DenyWrite(1);
        var result = await _sut.RotateDkimKey(1, 10, new DkimRotationRequest(), TestContext.Current.CancellationToken);
        Assert.IsType<ForbidResult>(result.Result);
    }
}
