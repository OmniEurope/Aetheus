// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Tests.TestDoubles;
using AngleSharp.Dom;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>
/// "Réinstaller la messagerie" (recette R2-083 / R2-088), the single setup launch (R2-085) and the page
/// that concludes nothing while it loads (R2-089).
/// </summary>
public sealed class ServerMailReinstallTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;
    private readonly ImmediateDialogService _dialog;

    public ServerMailReinstallTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        _dialog = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
    }

    private static IElement? Button<T>(IRenderedComponent<T> cut, string label) where T : Microsoft.AspNetCore.Components.IComponent =>
        cut.FindAll("button").FirstOrDefault(b => b.TextContent.Contains(label, StringComparison.Ordinal));

    private static MailDataDto Stack(bool installed = true, bool managed = true, int helperVersion = 3) => new()
    {
        IsInstalled = installed,
        IsManagedByAetheus = managed,
        HelperVersion = helperVersion,
        Hostname = "mx.sonytumen.com"
    };

    private IRenderedComponent<ServerMailReinstall> RenderReinstall(MailDataDto mail, bool canManage = true) =>
        Render<ServerMailReinstall>(p => p
            .Add(x => x.ServerId, 10)
            .Add(x => x.Mail, mail)
            .Add(x => x.CanManage, canManage));

    private int Posts(string path) =>
        _handler.Requests.Count(r => r.Method == "POST" && r.Url.EndsWith(path, StringComparison.Ordinal));

    // ── Typed confirmation ───────────────────────────────────────────────────

    [Fact]
    public void TheRedButtonStaysDisabledUntilTheDomainIsTyped()
    {
        var cut = Render<MailReinstallDialog>(p => p.Add(x => x.Word, "sonytumen.com"));
        IElement Confirm() => cut.FindAll("button").Single(b => b.TextContent.Contains("MailReinstallConfirm", StringComparison.Ordinal));

        Assert.True(Confirm().HasAttribute("disabled"));
        Assert.Contains("omni-button--danger", Confirm().ClassName, StringComparison.Ordinal);

        cut.Find("#mail-reinstall-confirmation").Input("sonytumen");
        Assert.True(Confirm().HasAttribute("disabled"));

        cut.Find("#mail-reinstall-confirmation").Input("SonyTumen.com");
        Assert.False(Confirm().HasAttribute("disabled"));

        cut.Find("form").Submit();
        cut.Find("form").Submit();
        Assert.Equal(1, _dialog.CloseCount);
        Assert.Equal("SonyTumen.com", _dialog.LastCloseResult);
    }

    [Fact]
    public void AWrongWordSubmittedWithEnter_ClosesNothing()
    {
        var cut = Render<MailReinstallDialog>(p => p.Add(x => x.Word, "sonytumen.com"));

        cut.Find("#mail-reinstall-confirmation").Input("other.test");
        cut.Find("form").Submit();

        Assert.Equal(0, _dialog.CloseCount);
    }

    [Fact]
    public void TheDialogSaysWhatIsErased_AndOffersRevenir()
    {
        var cut = Render<MailReinstallDialog>(p => p.Add(x => x.Word, "sonytumen.com"));

        foreach (var key in new[] { "MailReinstallErasesConfiguration", "MailReinstallErasesMailboxes", "MailReinstallErasesDkim", "MailReinstallAfter" })
            Assert.Contains(key, cut.Markup, StringComparison.Ordinal);
        Assert.Contains(cut.FindAll("button"), b => b.TextContent.Contains("GoBack", StringComparison.Ordinal));
    }

    // ── The page action ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(true, false)]  // configured but adopted
    [InlineData(true, true)]   // provisioned by Aetheus, Postfix stopped (tasks 23257 and 23258)
    public void AStackOnTheHost_OffersReinstall(bool installed, bool managed)
    {
        var cut = RenderReinstall(Stack(installed, managed));

        var button = Button(cut, "MailReinstall");
        Assert.NotNull(button);
        Assert.False(button!.HasAttribute("disabled"));
        Assert.Contains("omni-button--danger", button.ClassName, StringComparison.Ordinal);
    }

    [Fact]
    public void NoStackOnTheHost_OffersNothing()
    {
        var cut = RenderReinstall(Stack(installed: false, managed: false));

        Assert.Null(Button(cut, "MailReinstall"));
    }

    [Fact]
    public void AHelperWithoutPurge_DisablesReinstall()
    {
        var cut = RenderReinstall(Stack(helperVersion: 2));

        Assert.True(Button(cut, "MailReinstall")!.HasAttribute("disabled"));
    }

    [Fact]
    public async Task OneConfirmedReinstall_PostsOnce_StaysBusy_ThenShowsTheFailureWithItsLastLogLine()
    {
        _handler.SetJsonResponse("api/servers/10/mail/domains", new PaginatedResult<MailDomainDto>
        {
            Items = [new() { Id = 1, Name = "sonytumen.com" }],
            TotalCount = 1
        });
        _handler.SetJsonResponse(HttpMethod.Post, "api/servers/10/mail/purge", new MailTaskQueuedDto { TaskId = 77 });
        _handler.SetJsonResponse("api/logs/task/77", new List<TaskLogDto>
        {
            new() { Id = 1, TaskId = 77, Message = "Reading package lists..." },
            new() { Id = 2, TaskId = 77, Message = "Failed to disable unit: Access denied" }
        });
        _dialog.OpenResult = "sonytumen.com";
        var cut = RenderReinstall(Stack());

        Button(cut, "MailReinstall")!.Click();
        Button(cut, "MailReinstall")!.Click();

        cut.WaitForAssertion(() => Assert.Equal(1, Posts("mail/purge")), TimeSpan.FromSeconds(3));
        Assert.Equal(1, _dialog.OpenCount);
        Assert.Equal("sonytumen.com", (string?)_dialog.LastParameters![nameof(MailReinstallDialog.Word)]);
        Assert.Contains("\"confirmation\":\"sonytumen.com\"", _handler.RequestDetails.Last(r => r.Url.EndsWith("mail/purge", StringComparison.Ordinal)).Body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("true", Button(cut, "MailReinstall")!.GetAttribute("aria-busy"));
        Assert.Contains("MailReinstallRunning", cut.Markup, StringComparison.Ordinal);

        var section = cut.Instance;
        await cut.InvokeAsync(() => section.HandleTaskCompletedAsync(
            new TaskCompletedNotification { TaskId = 77, ServerId = 10, Status = TaskExecutionStatus.Failed }));

        cut.WaitForAssertion(() =>
        {
            var outcome = cut.Find(".mail-reinstall-outcome");
            Assert.Contains("MailReinstallFailed", outcome.TextContent, StringComparison.Ordinal);
        }, TimeSpan.FromSeconds(3));
        // The test localizer returns the bare key, so the formatted line is read from the outcome itself.
        Assert.Equal(
            new ServerMailReinstall.ReinstallOutcome(false, TaskExecutionStatus.Failed, "Failed to disable unit: Access denied"),
            section.Outcome);
        Assert.Null(Button(cut, "MailReinstall")!.GetAttribute("aria-busy"));
    }

    [Fact]
    public async Task ASucceededPurge_StaysOnThePageOnceTheStackIsGone()
    {
        _handler.SetJsonResponse("api/servers/10/mail/domains", new PaginatedResult<MailDomainDto>());
        _handler.SetJsonResponse(HttpMethod.Post, "api/servers/10/mail/purge", new MailTaskQueuedDto { TaskId = 78 });
        _dialog.OpenResult = "mx.sonytumen.com";
        var cut = Render<ServerMailReinstall>(p => p.Add(x => x.ServerId, 10).Add(x => x.Mail, Stack()).Add(x => x.CanManage, true));

        cut.FindAll("button").First(b => b.TextContent.Contains("MailReinstall", StringComparison.Ordinal)).Click();
        cut.WaitForAssertion(() => Assert.Equal(1, Posts("mail/purge")), TimeSpan.FromSeconds(3));
        // No domain known: the host name is the word to type.
        Assert.Equal("mx.sonytumen.com", (string?)_dialog.LastParameters![nameof(MailReinstallDialog.Word)]);

        await cut.InvokeAsync(() => cut.Instance.HandleTaskCompletedAsync(
            new TaskCompletedNotification { TaskId = 78, ServerId = 10, Status = TaskExecutionStatus.Success }));
        // The next heartbeat reports no stack: the button goes, the outcome stays.
        cut.Render(p => p.Add(x => x.Mail, Stack(installed: false, managed: false)));

        Assert.Empty(cut.FindAll("button"));
        Assert.Contains("MailReinstallSucceeded", cut.Find(".mail-reinstall-outcome").TextContent, StringComparison.Ordinal);
    }

    // ── R2-085: one click, one setup ─────────────────────────────────────────

    // ── R2-089: no conclusion while loading ──────────────────────────────────

    [Fact]
    public void WhileTheCountsLoad_TheNotConfiguredWarningIsNotShown()
    {
        var domains = new TaskCompletionSource<PaginatedResult<MailDomainDto>>();
        var accounts = new TaskCompletionSource<PaginatedResult<MailAccountDto>>();
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "api/servers/10/mail/domains", _ => domains.Task);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "api/servers/10/mail/accounts", _ => accounts.Task);
        _handler.SetJsonResponse("api/servers/10/mail/aliases", new PaginatedResult<MailAliasDto>());
        _handler.SetJsonResponse("api/servers/10/mail/diagnostics", new MailDiagnosticsDto());
        var server = ServerTestData.MakeMailServer() with { MailSetupAvailable = true };
        var cut = Render<ServerMailSection>(p => p.Add(x => x.Server, server).Add(x => x.ServerId, 10));

        Assert.DoesNotContain("MailInstalledNotConfigured", cut.Markup, StringComparison.Ordinal);
        Assert.NotEmpty(cut.FindAll(".mail-counts-loading"));

        domains.SetResult(new PaginatedResult<MailDomainDto>());
        accounts.SetResult(new PaginatedResult<MailAccountDto>());

        cut.WaitForAssertion(() => Assert.Contains("MailInstalledNotConfigured", cut.Markup, StringComparison.Ordinal), TimeSpan.FromSeconds(3));
        Assert.Empty(cut.FindAll(".mail-counts-loading"));
    }

    [Fact]
    public void OnceTheCountsAreRead_AConfiguredStackShowsNoWarning()
    {
        _handler.SetJsonResponse("api/servers/10/mail/domains", new PaginatedResult<MailDomainDto>
        {
            Items = [new() { Id = 1, Name = "sonytumen.com" }],
            TotalCount = 1
        });
        _handler.SetJsonResponse("api/servers/10/mail/accounts", new PaginatedResult<MailAccountDto>());
        _handler.SetJsonResponse("api/servers/10/mail/aliases", new PaginatedResult<MailAliasDto>());
        _handler.SetJsonResponse("api/servers/10/mail/diagnostics", new MailDiagnosticsDto());
        var server = ServerTestData.MakeMailServer() with { MailSetupAvailable = true };
        var cut = Render<ServerMailSection>(p => p.Add(x => x.Server, server).Add(x => x.ServerId, 10));

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".mail-counts-loading")), TimeSpan.FromSeconds(3));
        Assert.DoesNotContain("MailInstalledNotConfigured", cut.Markup, StringComparison.Ordinal);
    }
}
