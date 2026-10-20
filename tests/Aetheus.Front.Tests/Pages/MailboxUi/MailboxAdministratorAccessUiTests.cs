// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.MailboxUi;

/// <summary>
/// Recette R1-41 / R3-50: the unlock dialog offers the opening without password only when the backend says
/// so (administrator, access prepared); the mailbox shows an administrator opening. Recette R3-49: the Accounts
/// tab's menu holds "Client mail", "Purger" (confirmed) and, for an administrator only, the preparation of
/// the administrator access. Recette R1-41: the "Messagerie" page lists the mailboxes and a row opens one.
/// </summary>
public sealed class MailboxAdministratorAccessUiTests : BunitContext
{
    private const string Base = "api/servers/7/mail/accounts/3/mailbox";
    private BunitTestHelper.TestHandler? _registered;
    private ImmediateDialogService? _immediate;

    // Services are registered on first use: an administrator test registers them as an administrator.
    private BunitTestHelper.TestHandler _handler => _registered ??= Register(isAdmin: false);
    private ImmediateDialogService _dialog
    {
        get
        {
            _ = _handler;
            return _immediate!;
        }
    }

    private void AsAdministrator() => _registered = Register(isAdmin: true);

    private BunitTestHelper.TestHandler Register(bool isAdmin)
    {
        var handler = BunitTestHelper.RegisterServices(this, isAdmin: isAdmin);
        BunitTestHelper.UseImmediateDialogs(this);
        _immediate = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        return handler;
    }

    private IRenderedComponent<MailboxUnlockDialog> RenderUnlock(bool canOpenAsAdministrator)
    {
        _ = _handler;
        return Render<MailboxUnlockDialog>(parameters => parameters
            .Add(dialog => dialog.ServerId, 7).Add(dialog => dialog.AccountId, 3)
            .Add(dialog => dialog.CanOpenAsAdministrator, canOpenAsAdministrator));
    }

    [Fact]
    public void Without_the_backend_offer_the_dialog_only_asks_for_the_password()
    {
        var cut = RenderUnlock(canOpenAsAdministrator: false);

        Assert.Empty(cut.FindAll(".mailbox-admin-open-button"));
        Assert.Contains("omni-button--primary", cut.Find("button[type=submit]").ClassName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_administrator_opens_without_password_and_the_dialog_closes_with_the_access()
    {
        _handler.SetJsonResponse(HttpMethod.Post, Base + "/open-as-administrator",
            new MailboxAccessDto { AccountId = 3, IsUnlocked = true, OpenedAsAdministrator = true });
        var cut = RenderUnlock(canOpenAsAdministrator: true);
        BunitTestHelper.Interactive(this);

        await cut.Find(".mailbox-admin-open-button").ClickAsync(new());

        Assert.Contains(_handler.Requests, request => request.Method == "POST" && request.Url.EndsWith(Base + "/open-as-administrator", StringComparison.Ordinal));
        Assert.DoesNotContain(_handler.Requests, request => request.Url.EndsWith("/unlock", StringComparison.Ordinal));
        Assert.True(Assert.IsType<MailboxAccessDto>(_dialog.LastCloseResult).OpenedAsAdministrator);
        // The administrator opening is the dialog's one blue action; typing the password stays possible, in grey.
        Assert.Contains("omni-button--secondary", cut.Find("button[type=submit]").ClassName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_administrator_opening_is_said_and_the_dialog_stays_open()
    {
        _handler.SetResponse(HttpMethod.Post, Base + "/open-as-administrator", HttpStatusCode.BadRequest);
        var cut = RenderUnlock(canOpenAsAdministrator: true);
        BunitTestHelper.Interactive(this);

        await cut.Find(".mailbox-admin-open-button").ClickAsync(new());

        Assert.Equal(0, _dialog.CloseCount);
        Assert.Contains("MailboxAdminOpenFailed", cut.Find(".mailbox-admin-open-error").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void A_mailbox_opened_through_the_administrator_access_says_so()
    {
        _handler.SetJsonResponse(HttpMethod.Get, "api/servers/7/mail/client-settings", new MailClientSettingsDto { Host = "mail.example.org" });
        _handler.SetJsonResponse(HttpMethod.Get, Base + "/folders", new List<MailboxFolderDto>());
        _handler.SetJsonResponse(HttpMethod.Get, Base + "/messages?", new PaginatedResult<MailboxMessageSummaryDto>());
        _handler.SetJsonResponse(HttpMethod.Get, Base, new MailboxAccessDto
        {
            AccountId = 3,
            Email = "me@example.org",
            IsUnlocked = true,
            UnlockedUntil = DateTime.Now.AddMinutes(30),
            OpenedAsAdministrator = true
        });

        var cut = Render<MailboxView>(parameters => parameters.Add(view => view.ServerId, 7).Add(view => view.AccountId, 3));

        cut.WaitForAssertion(() => Assert.Contains("MailboxOpenedAsAdministrator", cut.Find(".mailbox-admin-badge").TextContent, StringComparison.Ordinal));
    }

    private IRenderedComponent<ServerMailAccountsMenu> RenderMenu(bool canManage = true)
    {
        _ = _handler;
        return Render<ServerMailAccountsMenu>(parameters => parameters
            .Add(menu => menu.ServerId, 7).Add(menu => menu.CanManage, canManage));
    }

    [Fact]
    public void The_accounts_menu_hides_the_administrator_access_from_a_non_administrator()
    {
        var cut = RenderMenu();

        Assert.NotNull(cut.FindMenuItem("MailClientHelpButton"));
        Assert.NotNull(cut.FindMenuItem("MailAccountsPurge"));
        Assert.Null(cut.FindMenuItem("MailMasterAccessPrepare"));
    }

    [Fact]
    public void An_administrator_prepares_the_administrator_access_from_the_accounts_menu()
    {
        AsAdministrator();
        _handler.SetJsonResponse(HttpMethod.Post, "api/servers/7/mail/master-access", new MailTaskQueuedDto { TaskId = 12 });
        var cut = RenderMenu();

        cut.FindMenuItem("MailMasterAccessPrepare")!.Click();

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests,
            request => request.Method == "POST" && request.Url.EndsWith("api/servers/7/mail/master-access", StringComparison.Ordinal)));
    }

    [Fact]
    public void The_preparation_waits_for_a_reachable_agent()
    {
        AsAdministrator();
        var cut = RenderMenu(canManage: false);

        Assert.True(cut.FindMenuItem("MailMasterAccessPrepare")!.HasAttribute("disabled")
            || cut.FindMenuItem("MailMasterAccessPrepare")!.GetAttribute("aria-disabled") == "true");
    }

    [Fact]
    public void Purge_asks_first_and_sends_nothing_when_declined()
    {
        _dialog.ConfirmResult = false;
        var cut = RenderMenu();

        cut.FindMenuItem("MailAccountsPurge")!.Click();

        cut.WaitForAssertion(() => Assert.NotNull(_dialog.LastConfirmOptions));
        Assert.True(_dialog.LastConfirmOptions!.Destructive);
        Assert.Equal("MailAccountsPurgeVerb", _dialog.LastConfirmOptions.OkButtonText);
        Assert.DoesNotContain(_handler.Requests, request => request.Url.Contains("purge-missing", StringComparison.Ordinal));
    }

    [Fact]
    public void Purge_confirmed_deletes_the_missing_accounts_and_reloads_the_grid()
    {
        _dialog.ConfirmResult = true;
        _handler.SetJsonResponse(HttpMethod.Post, "api/servers/7/mail/accounts/purge-missing", new MailAccountPurgeResultDto { Removed = 2 });
        var reloaded = 0;
        var cut = Render<ServerMailAccountsMenu>(parameters => parameters
            .Add(menu => menu.ServerId, 7).Add(menu => menu.CanManage, true)
            .Add(menu => menu.OnAccountsChanged, () => reloaded++));

        cut.FindMenuItem("MailAccountsPurge")!.Click();

        cut.WaitForAssertion(() => Assert.Equal(1, reloaded));
        Assert.Contains(_handler.Requests, request => request.Method == "POST" && request.Url.EndsWith("accounts/purge-missing", StringComparison.Ordinal));
    }

    [Fact]
    public void The_mailbox_directory_lists_every_visible_mailbox_and_a_row_opens_it()
    {
        _handler.SetJsonResponse("api/mail/mailboxes", new PaginatedResult<MailboxDirectoryEntryDto>
        {
            Items = [new MailboxDirectoryEntryDto { ServerId = 5, ServerName = "mx-1", AccountId = 2, Email = "admin@example.org", Domain = "example.org" }],
            TotalCount = 1,
            Page = 1,
            PageSize = 25
        });
        var nav = Services.GetRequiredService<BunitNavigationManager>();

        var cut = Render<MailboxDirectory>();

        cut.WaitForAssertion(() => Assert.Contains("admin@example.org", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("mx-1", cut.Markup, StringComparison.Ordinal);
        cut.Find("button[aria-label=MailboxOpen]").Click();
        Assert.EndsWith("/servers/5/mail/accounts/2/mailbox", nav.Uri, StringComparison.Ordinal);
    }
}
