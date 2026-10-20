// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.Mail;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Aetheus.Back.Tests;

/// <summary>Require server Write for operations and Read for reads, rejecting unauthorized calls
/// before service execution.</summary>
public sealed class MailStackControllerTests
{
    private readonly IMailOperationsService _operations = Substitute.For<IMailOperationsService>();
    private readonly IMailDiagnosticsService _diagnostics = Substitute.For<IMailDiagnosticsService>();
    private readonly IMailStackPurgeService _purge = Substitute.For<IMailStackPurgeService>();
    private readonly IMailMasterAccessService _masterAccess = Substitute.For<IMailMasterAccessService>();
    private readonly IMailAccountPurgeService _accountPurge = Substitute.For<IMailAccountPurgeService>();
    private readonly IResourceAuthorizationService _authz = Substitute.For<IResourceAuthorizationService>();
    private readonly MailStackController _sut;

    public MailStackControllerTests()
    {
        _sut = new MailStackController(_operations, _diagnostics, _purge, _masterAccess, _accountPurge, _authz)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "1")], "test"))
                }
            }
        };
    }

    private void Grant(Permission permission, bool granted) =>
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, 3, permission, Arg.Any<CancellationToken>())
            .Returns(granted);

    [Fact]
    public async Task WriteOperations_AreForbiddenWithoutWrite_AndNeverReachTheService()
    {
        Grant(Permission.Read, true);
        Grant(Permission.Write, false);
        var ct = TestContext.Current.CancellationToken;

        Assert.IsType<ForbidResult>((await _sut.RequestCertificate(3, new RequestMailCertificateRequest { Email = "a@b.co" }, ct)).Result);
        Assert.IsType<ForbidResult>((await _sut.InstallCertificate(3, ct)).Result);
        Assert.IsType<ForbidResult>((await _sut.InstallSpamFilter(3, ct)).Result);
        Assert.IsType<ForbidResult>((await _sut.UpdateSpamFilter(3, new UpdateSpamFilterRequest(), ct)).Result);
        Assert.IsType<ForbidResult>((await _sut.LearnSpam(3, new LearnSpamRequest { RawMessage = "x" }, ct)).Result);
        Assert.IsType<ForbidResult>((await _sut.SendTest(3, new MailTestDeliveryRequest(), ct)).Result);
        Assert.IsType<ForbidResult>((await _sut.DeleteQueuedMessage(3, "4F2A1B3C9D", ct)).Result);
        Assert.IsType<ForbidResult>((await _sut.ProbeOutboundSmtp(3, ct)).Result);

        Assert.Empty(_operations.ReceivedCalls());
    }

    [Fact]
    public async Task MasterAccessAndAccountPurge_AreForbiddenWithoutWrite_AndNeverReachTheirService()
    {
        Grant(Permission.Write, false);
        var ct = TestContext.Current.CancellationToken;

        Assert.IsType<ForbidResult>((await _sut.PrepareMasterAccess(3, ct)).Result);
        Assert.IsType<ForbidResult>((await _sut.PurgeMissingAccounts(3, ct)).Result);

        Assert.Empty(_masterAccess.ReceivedCalls());
        Assert.Empty(_accountPurge.ReceivedCalls());
    }

    [Fact]
    public void MasterAccess_IsReservedToAdministrators()
    {
        // Recette R1-41: on top of the class-level [Authorize], the action demands the Admin role.
        var roles = typeof(MailStackController).GetMethod(nameof(MailStackController.PrepareMasterAccess))!
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), inherit: true)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()
            .Select(attribute => attribute.Roles);
        Assert.Contains("Admin", roles);
    }

    [Fact]
    public async Task MasterAccessAndAccountPurge_WithWrite_ReachTheirService()
    {
        Grant(Permission.Write, true);
        var ct = TestContext.Current.CancellationToken;
        _masterAccess.PrepareAsync(3, Arg.Any<CancellationToken>()).Returns(new MailTaskQueuedDto { TaskId = 41 });
        _accountPurge.PurgeMissingAsync(3, Arg.Any<CancellationToken>()).Returns(new MailAccountPurgeResultDto { Removed = 2 });

        var prepared = Assert.IsType<OkObjectResult>((await _sut.PrepareMasterAccess(3, ct)).Result);
        var purged = Assert.IsType<OkObjectResult>((await _sut.PurgeMissingAccounts(3, ct)).Result);

        Assert.Equal(41, Assert.IsType<MailTaskQueuedDto>(prepared.Value).TaskId);
        Assert.Equal(2, Assert.IsType<MailAccountPurgeResultDto>(purged.Value).Removed);
    }

    [Fact]
    public async Task Reads_AreForbiddenWithoutRead()
    {
        Grant(Permission.Read, false);
        var ct = TestContext.Current.CancellationToken;

        Assert.IsType<ForbidResult>((await _sut.GetCertificate(3, ct)).Result);
        Assert.IsType<ForbidResult>((await _sut.GetSpamFilter(3, ct)).Result);
        Assert.IsType<ForbidResult>((await _sut.GetDiagnostics(3, ct)).Result);
        Assert.IsType<ForbidResult>((await _sut.PreviewMx(3, "sonytumen.com", ct)).Result);
        Assert.IsType<ForbidResult>((await _sut.CheckPrerequisites(3, "mx.example.com", ct)).Result);
        Assert.IsType<ForbidResult>((await _sut.VerifyDns(3, 1, ct)).Result);
        Assert.IsType<ForbidResult>((await _sut.RefreshQueue(3, ct)).Result);
        Assert.IsType<ForbidResult>((await _sut.IngestQuota(3, 9, ct)).Result);

        Assert.Empty(_diagnostics.ReceivedCalls());
        Assert.Empty(_operations.ReceivedCalls());
    }

    [Fact]
    public async Task Purge_IsForbiddenWithoutWrite_AndNeverReachesTheService()
    {
        // "Réinstaller la messagerie" erases mailboxes: same permission as the setup, server Write.
        Grant(Permission.Read, true);
        Grant(Permission.Write, false);

        var result = await _sut.Purge(3, new MailPurgeRequest { Confirmation = "example.com" }, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
        Assert.Empty(_purge.ReceivedCalls());
    }

    [Fact]
    public async Task Purge_WithWrite_ReturnsTheQueuedTaskId()
    {
        Grant(Permission.Write, true);
        _purge.PurgeAsync(3, Arg.Is<MailPurgeRequest>(r => r.Confirmation == "example.com"), Arg.Any<CancellationToken>())
            .Returns(new MailTaskQueuedDto { TaskId = 91 });

        var result = await _sut.Purge(3, new MailPurgeRequest { Confirmation = "example.com" }, TestContext.Current.CancellationToken);

        Assert.Equal(91, Assert.IsType<MailTaskQueuedDto>(Assert.IsType<OkObjectResult>(result.Result).Value).TaskId);
    }

    [Fact]
    public async Task QueuedOperations_ReturnTheTaskId()
    {
        Grant(Permission.Write, true);
        _operations.SendTestAsync(3, Arg.Any<MailTestDeliveryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new MailTaskQueuedDto { TaskId = 77 });

        var result = await _sut.SendTest(3, new MailTestDeliveryRequest { From = "a@b.co", To = "c@d.co" }, TestContext.Current.CancellationToken);

        Assert.Equal(77, Assert.IsType<MailTaskQueuedDto>(Assert.IsType<OkObjectResult>(result.Result).Value).TaskId);
    }
}
