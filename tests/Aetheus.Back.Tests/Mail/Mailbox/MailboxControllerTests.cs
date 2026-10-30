// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.Mail.Mailbox;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Aetheus.Back.Tests;

/// <summary>The mailbox endpoints follow the mail page's rights and key the password to the
/// caller.</summary>
public sealed class MailboxControllerTests
{
    private readonly IMailboxService _service = Substitute.For<IMailboxService>();
    private readonly IMailboxAdministratorAccess _administratorAccess = Substitute.For<IMailboxAdministratorAccess>();
    private readonly IResourceAuthorizationService _authz = Substitute.For<IResourceAuthorizationService>();
    private readonly MailboxController _sut;

    public MailboxControllerTests()
    {
        _sut = new MailboxController(_service, _administratorAccess, _authz);
        SignIn("1");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void SignIn(string? userId)
    {
        Claim[] claims = userId is null ? [] : [new Claim(ClaimTypes.NameIdentifier, userId)];
        _sut.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) }
        };
    }

    // R-11: reading a mailbox is server.mailbox.read and changing it server.mailbox.manage, the keys the
    // Read and Write levels of the server gave before; the level itself still gates the client settings.
    private void Allow(Permission permission)
    {
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, 7, permission, Arg.Any<CancellationToken>())
            .Returns(true);
        var key = permission == Permission.Read ? PermissionKeys.ServerMailboxRead : PermissionKeys.ServerMailboxManage;
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), key, 7, Arg.Any<CancellationToken>()).Returns(true);
    }

    [Fact]
    public void The_controller_requires_authentication()
    {
        Assert.NotNull(typeof(MailboxController).GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true).SingleOrDefault());
    }

    [Fact]
    public async Task Reading_without_read_permission_is_forbidden_and_reaches_no_service()
    {
        Assert.IsType<ForbidResult>((await _sut.GetAccess(7, 3, Ct)).Result);
        Assert.IsType<ForbidResult>((await _sut.GetFolders(7, 3, Ct)).Result);
        Assert.IsType<ForbidResult>((await _sut.GetMessages(7, 3, "INBOX", ct: Ct)).Result);
        Assert.IsType<ForbidResult>((await _sut.GetMessage(7, 3, 5, "INBOX", ct: Ct)).Result);
        Assert.IsType<ForbidResult>(await _sut.GetAttachment(7, 3, 5, 0, "INBOX", Ct));
        Assert.IsType<ForbidResult>((await _sut.Unlock(7, 3, new MailboxUnlockRequest { Password = "x" }, Ct)).Result);
        Assert.IsType<ForbidResult>((await _sut.GetClientSettings(7, Ct)).Result);

        Assert.Empty(_service.ReceivedCalls());
    }

    [Fact]
    public async Task Changing_the_mailbox_needs_write_permission()
    {
        Allow(Permission.Read);

        Assert.IsType<ForbidResult>((await _sut.Send(7, 3, new MailboxSendRequest(), Ct)).Result);
        Assert.IsType<ForbidResult>((await _sut.Delete(7, 3, 5, "INBOX", Ct)).Result);
        Assert.IsType<ForbidResult>(await _sut.SetRead(7, 3, 5, "INBOX", new MailboxReadStateRequest(), Ct));
        Assert.IsType<ForbidResult>((await _sut.CreateFolder(7, 3, new MailboxCreateFolderRequest { Name = "x" }, Ct)).Result);

        Assert.Empty(_service.ReceivedCalls());
    }

    [Fact]
    public async Task A_caller_without_identifier_is_forbidden_even_with_rights()
    {
        Allow(Permission.Read);
        SignIn(null);

        Assert.IsType<ForbidResult>((await _sut.GetFolders(7, 3, Ct)).Result);
        Assert.Empty(_service.ReceivedCalls());
    }

    [Fact]
    public async Task Unlock_passes_the_password_under_the_caller_key()
    {
        Allow(Permission.Read);
        _service.UnlockAsync(Arg.Any<MailboxCredentialKey>(), "pw", Arg.Any<CancellationToken>())
            .Returns(new MailboxAccessDto { IsUnlocked = true });

        var result = await _sut.Unlock(7, 3, new MailboxUnlockRequest { Password = "pw" }, Ct);

        Assert.IsType<OkObjectResult>(result.Result);
        await _service.Received(1).UnlockAsync(new MailboxCredentialKey("test:1", 7, 3), "pw", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Two_users_get_two_keys()
    {
        Allow(Permission.Read);
        await _sut.Lock(7, 3, Ct);
        SignIn("2");
        await _sut.Lock(7, 3, Ct);

        _service.Received(1).Lock(new MailboxCredentialKey("test:1", 7, 3));
        _service.Received(1).Lock(new MailboxCredentialKey("test:2", 7, 3));
    }

    [Fact]
    public async Task Send_with_write_permission_reaches_the_service()
    {
        Allow(Permission.Write);
        var request = new MailboxSendRequest { To = "a@example.com" };
        _service.SendAsync(Arg.Any<MailboxCredentialKey>(), request, Arg.Any<CancellationToken>())
            .Returns(new MailboxSendResultDto { SavedToSent = true });

        var result = await _sut.Send(7, 3, request, Ct);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task An_attachment_is_always_served_as_a_download()
    {
        Allow(Permission.Read);
        _service.GetAttachmentAsync(Arg.Any<MailboxCredentialKey>(), "INBOX", 5u, 0, Arg.Any<CancellationToken>())
            .Returns(new MailboxAttachmentContent("page.html", "text/html", [1]));

        var file = Assert.IsType<FileContentResult>(await _sut.GetAttachment(7, 3, 5, 0, "INBOX", Ct));

        Assert.Equal("application/octet-stream", file.ContentType);
        Assert.Equal("page.html", file.FileDownloadName);
    }

    // --- Recette R1-41 / R3-50: opening without the password, administrators only ---

    private void SignInAs(string userId, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        _sut.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) }
        };
    }

    [Fact]
    public async Task A_non_administrator_cannot_open_without_password_even_with_server_rights()
    {
        Allow(Permission.Read);
        Allow(Permission.Write);

        Assert.IsType<ForbidResult>((await _sut.OpenAsAdministrator(7, 3, Ct)).Result);

        Assert.Empty(_administratorAccess.ReceivedCalls());
    }

    [Fact]
    public async Task An_administrator_without_read_on_the_server_is_forbidden()
    {
        SignInAs("1", "Admin");

        Assert.IsType<ForbidResult>((await _sut.OpenAsAdministrator(7, 3, Ct)).Result);

        Assert.Empty(_administratorAccess.ReceivedCalls());
    }

    [Fact]
    public async Task An_administrator_opens_under_their_own_key()
    {
        SignInAs("1", "Admin");
        Allow(Permission.Read);
        _administratorAccess.OpenAsync(Arg.Any<MailboxCredentialKey>(), true, Arg.Any<CancellationToken>())
            .Returns(new MailboxAccessDto { IsUnlocked = true, OpenedAsAdministrator = true });

        var result = await _sut.OpenAsAdministrator(7, 3, Ct);

        Assert.True(Assert.IsType<MailboxAccessDto>(Assert.IsType<OkObjectResult>(result.Result).Value).OpenedAsAdministrator);
        await _administratorAccess.Received(1).OpenAsync(new MailboxCredentialKey("test:1", 7, 3), true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Only_an_administrator_is_offered_the_opening_without_password()
    {
        Allow(Permission.Read);
        _service.GetAccessAsync(Arg.Any<MailboxCredentialKey>(), Arg.Any<CancellationToken>()).Returns(new MailboxAccessDto());
        _administratorAccess.IsPreparedAsync(7, Arg.Any<CancellationToken>()).Returns(true);

        var asUser = Assert.IsType<MailboxAccessDto>(Assert.IsType<OkObjectResult>((await _sut.GetAccess(7, 3, Ct)).Result).Value);
        SignInAs("1", "Admin");
        var asAdmin = Assert.IsType<MailboxAccessDto>(Assert.IsType<OkObjectResult>((await _sut.GetAccess(7, 3, Ct)).Result).Value);

        Assert.False(asUser.CanOpenAsAdministrator);
        Assert.True(asAdmin.CanOpenAsAdministrator);
    }

    [Fact]
    public async Task The_mailbox_directory_lists_only_the_servers_the_reader_can_read()
    {
        var directory = new MailboxDirectoryController(_service, _authz) { ControllerContext = _sut.ControllerContext };
        _authz.GetAccessibleResourceIdsAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(new List<int> { 7 });
        var request = new PaginationRequest();
        _service.ListDirectoryAsync(Arg.Any<IReadOnlyCollection<int>?>(), request, Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<MailboxDirectoryEntryDto>());

        Assert.IsType<OkObjectResult>((await directory.List(request, Ct)).Result);

        await _service.Received(1).ListDirectoryAsync(
            Arg.Is<IReadOnlyCollection<int>?>(ids => ids != null && ids.SequenceEqual(new[] { 7 })), request, Arg.Any<CancellationToken>());
    }
}
