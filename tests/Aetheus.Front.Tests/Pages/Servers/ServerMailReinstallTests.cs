// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Tests.TestDoubles;
using AngleSharp.Dom;
using Bunit;
using Bunit.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>Covers mail reinstallation, single-submit setup and loading states that defer
/// configuration conclusions.</summary>
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

    private IRenderedComponent<ContainerFragment>? _host;

    /// <summary>The Réinstaller button is drawn in the page header.</summary>
    private IElement? HeaderButton(string label) =>
        _host!.FindAll("[data-outlet=\"pipelines-header-actions\"] button").FirstOrDefault(b => b.TextContent.Contains(label, StringComparison.Ordinal));

    private static MailDataDto Stack(bool installed = true, bool managed = true, int helperVersion = 3) => new()
    {
        IsInstalled = installed,
        IsManagedByAetheus = managed,
        HelperVersion = helperVersion,
        Hostname = "mx.sonytumen.com"
    };

    private IRenderedComponent<ServerMailReinstall> RenderReinstall(MailDataDto mail, bool canManage = true)
    {
        _host = this.RenderWithOutlets<ServerMailReinstall>(new Dictionary<string, object?>
        {
            [nameof(ServerMailReinstall.Server)] = ServerTestData.MakeMailServer(),
            [nameof(ServerMailReinstall.ServerId)] = 10,
            [nameof(ServerMailReinstall.Mail)] = mail,
            [nameof(ServerMailReinstall.CanManage)] = canManage
        }, PipelinesList.HeaderActionsSection);
        return _host.FindComponent<ServerMailReinstall>();
    }

    [Fact]
    public void R1_12_Reinstall_IsInThePageHeader_WithTheMailActionsMenuOutletBesideIt()
    {
        var cut = RenderReinstall(Stack());

        Assert.Empty(cut.FindAll(".mail-reinstall-button"));
        var header = _host!.Find("[data-outlet=\"pipelines-header-actions\"]");
        Assert.Contains("omni-button--danger", header.QuerySelector(".mail-reinstall-button")!.ClassName, StringComparison.Ordinal);
    }

    [Fact]
    public void R1_12_OnTheMailPage_TheHeaderHoldsReinstall_ThenTheMailActionsMenu()
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
        _host = this.RenderWithOutlets<ServerMailSection>(new Dictionary<string, object?>
        {
            [nameof(ServerMailSection.Server)] = server,
            [nameof(ServerMailSection.ServerId)] = 10
        }, PipelinesList.HeaderActionsSection);

        _host.WaitForAssertion(() => Assert.Single(_host.FindAll("[data-outlet=\"pipelines-header-actions\"] .mail-actions-menu")));
        var header = _host.Find("[data-outlet=\"pipelines-header-actions\"]");
        var reinstall = header.QuerySelector(".mail-reinstall-button")!;
        Assert.Contains("omni-button--danger", reinstall.ClassName, StringComparison.Ordinal);
        // Réinstaller first, the "..." menu right after it.
        Assert.Contains("mail-actions-menu", reinstall.NextElementSibling!.ClassName, StringComparison.Ordinal);
        // The card keeps no copy of either.
        Assert.Empty(_host.FindAll(".mail-status .mail-reinstall-button, .mail-status .mail-actions-menu"));
    }

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

        var button = HeaderButton("MailReinstall");
        Assert.NotNull(button);
        Assert.False(button!.HasAttribute("disabled"));
        Assert.Contains("omni-button--danger", button.ClassName, StringComparison.Ordinal);
    }

    [Fact]
    public void NoStackOnTheHost_OffersNothing()
    {
        var cut = RenderReinstall(Stack(installed: false, managed: false));

        Assert.Null(HeaderButton("MailReinstall"));
    }

    [Fact]
    public void AHelperWithoutPurge_DisablesReinstall()
    {
        var cut = RenderReinstall(Stack(helperVersion: 2));

        Assert.True(HeaderButton("MailReinstall")!.HasAttribute("disabled"));
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

        HeaderButton("MailReinstall")!.Click();
        // A second click finds the button busy, or already replaced by the banner.
        HeaderButton("MailReinstall")?.Click();

        cut.WaitForAssertion(() => Assert.Equal(1, Posts("mail/purge")));
        Assert.Equal(1, _dialog.OpenCount);
        Assert.Equal("sonytumen.com", (string?)_dialog.LastParameters![nameof(MailReinstallDialog.Word)]);
        Assert.Contains("\"confirmation\":\"sonytumen.com\"", _handler.RequestDetails.Last(r => r.Url.EndsWith("mail/purge", StringComparison.Ordinal)).Body, StringComparison.OrdinalIgnoreCase);
        // While the stack is erased, one banner and no button.
        Assert.Null(HeaderButton("MailReinstall"));
        Assert.Contains("MailReinstallRunning", cut.Find(".mail-reinstall-running").TextContent, StringComparison.Ordinal);
        Assert.Single(cut.FindAll(".mail-reinstall-running .mail-reinstall-running__indicator"));

        var section = cut.Instance;
        await cut.InvokeAsync(() => section.HandleTaskCompletedAsync(
            new TaskCompletedNotification { TaskId = 77, ServerId = 10, Status = TaskExecutionStatus.Failed }));

        cut.WaitForAssertion(() =>
        {
            var outcome = cut.Find(".mail-reinstall-outcome");
            Assert.Contains("MailReinstallFailed", outcome.TextContent, StringComparison.Ordinal);
        });
        // The test localizer returns the bare key, so the formatted line is read from the outcome itself.
        Assert.Equal(
            new ServerMailReinstall.ReinstallOutcome(false, TaskExecutionStatus.Failed, "Failed to disable unit: Access denied"),
            section.Outcome);
        Assert.Null(HeaderButton("MailReinstall")!.GetAttribute("aria-busy"));
        Assert.Empty(cut.FindAll(".mail-reinstall-running"));
    }

    [Fact]
    public async Task ASucceededPurge_StaysOnThePageOnceTheStackIsGone()
    {
        _handler.SetJsonResponse("api/servers/10/mail/domains", new PaginatedResult<MailDomainDto>());
        _handler.SetJsonResponse(HttpMethod.Post, "api/servers/10/mail/purge", new MailTaskQueuedDto { TaskId = 78 });
        _dialog.OpenResult = "mx.sonytumen.com";
        var cut = RenderReinstall(Stack());

        HeaderButton("MailReinstall")!.Click();
        cut.WaitForAssertion(() => Assert.Equal(1, Posts("mail/purge")));
        // No domain known: the host name is the word to type.
        Assert.Equal("mx.sonytumen.com", (string?)_dialog.LastParameters![nameof(MailReinstallDialog.Word)]);

        await cut.InvokeAsync(() => cut.Instance.HandleTaskCompletedAsync(
            new TaskCompletedNotification { TaskId = 78, ServerId = 10, Status = TaskExecutionStatus.Success }));
        // The next heartbeat reports no stack: the button goes, the outcome stays.
        cut.Render(p => p.Add(x => x.Mail, Stack(installed: false, managed: false)));

        Assert.Empty(_host!.FindAll("button"));
        Assert.Contains("MailReinstallSucceeded", cut.Find(".mail-reinstall-outcome").TextContent, StringComparison.Ordinal);
    }

    // One setup per submission.

    // No configuration conclusion while loading.

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

        cut.WaitForAssertion(() => Assert.Contains("MailInstalledNotConfigured", cut.Markup, StringComparison.Ordinal));
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

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".mail-counts-loading")));
        Assert.DoesNotContain("MailInstalledNotConfigured", cut.Markup, StringComparison.Ordinal);
    }
}
