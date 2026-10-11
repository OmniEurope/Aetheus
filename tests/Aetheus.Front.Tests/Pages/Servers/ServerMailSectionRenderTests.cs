// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Servers;

public class ServerMailSectionRenderTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;

    public ServerMailSectionRenderTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
    }

    private static ServerDetailDto MakeMailServer(bool installed = true) =>
        ServerTestData.MakeMailServer(installed, postfixRunning: installed, dovecotRunning: installed);

    private void StubMailApi(int serverId = 10)
    {
        _handler.SetJsonResponse($"api/servers/{serverId}/mail/domains", new PaginatedResult<MailDomainDto>
        {
            Items = [new() { Id = 1, Name = "example.com", IsActive = true, DkimSelector = "default", HasSpf = true, HasDkim = true }],
            TotalCount = 1
        });
        _handler.SetJsonResponse($"api/servers/{serverId}/mail/accounts", new PaginatedResult<MailAccountDto>
        {
            Items = [new() { Id = 1, Email = "admin@example.com", IsActive = true, QuotaMb = 1024, Domain = "example.com" }],
            TotalCount = 1
        });
        _handler.SetJsonResponse($"api/servers/{serverId}/mail/aliases", new PaginatedResult<MailAliasDto>());
        _handler.SetJsonResponse($"api/servers/{serverId}/mail/action", true);
        _handler.SetJsonResponse($"api/servers/{serverId}/mail/setup", true);
        _handler.SetJsonResponse($"api/servers/{serverId}/mail/logs", true);
        _handler.SetJsonResponse($"api/servers/{serverId}/mail/dns", new MailDnsRecordsDto());
        _handler.SetJsonResponse($"api/servers/{serverId}/mail/diagnostics", new MailDiagnosticsDto());
    }

    private IRenderedComponent<ServerMailSection> RenderSection(ServerDetailDto? server = null, int serverId = 10)
    {
        StubMailApi(serverId);
        var s = server ?? MakeMailServer();
        return Render<ServerMailSection>(p => p
            .Add(x => x.Server, s)
            .Add(x => x.ServerId, serverId));
    }

    // ── Render ────────────────────────────────────────────────────────────────

    [Fact]
    public void Renders_WithDomains_ShowsDomainNames()
    {
        var cut = RenderSection();
        cut.WaitForState(() => cut.Markup.Contains("example.com"), TimeSpan.FromSeconds(2));
        Assert.Contains("example.com", cut.Markup);
    }

    [Fact]
    public void DnsCheckDialog_ShowsObservedMxWithoutProviderWarning()
    {
        var cut = Render<MailDnsCheckDialog>(p => p.Add(x => x.Check, new MailDnsCheckDto
        {
            Domain = "example.com",
            Records = [new MailDnsRecordCheckDto
            {
                Kind = MailDnsRecordKind.Mx,
                Observed = ["mail.example.com", "mx1.ovh.net"],
                Verdict = MailCheckVerdict.Ok
            }]
        }));

        Assert.Contains("mail.example.com", cut.Markup);
        Assert.Contains("mx1.ovh.net", cut.Markup);
        Assert.DoesNotContain("MailOvhRoutingNotice", cut.Markup);
    }

    // ── LoadDataAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task PagedLoaders_PopulateDomainsAndAccounts()
    {
        var cut = RenderSection();
        var domainLoader = typeof(ServerMailSection).GetMethod("LoadDomainsAsync", Priv)!;
        var accountLoader = typeof(ServerMailSection).GetMethod("LoadAccountsAsync", Priv)!;
        var args = new GridLoadArgs { Skip = 0, Top = 25 };

        await cut.InvokeAsync(async () => await (Task)domainLoader.Invoke(cut.Instance, [args])!);
        await cut.InvokeAsync(async () => await (Task)accountLoader.Invoke(cut.Instance, [args])!);

        var domains = (List<MailDomainDto>)typeof(ServerMailSection)
            .GetField("_domains", Priv)!
            .GetValue(cut.Instance)!;
        Assert.NotEmpty(domains);

        var accounts = (List<MailAccountDto>)typeof(ServerMailSection)
            .GetField("_accounts", Priv)!
            .GetValue(cut.Instance)!;
        Assert.NotEmpty(accounts);
    }

    // ── ExecuteActionAsync ────────────────────────────────────────────────────

    // ── Mail dialog model validation ─────────────────────────────────────────

    [Fact]
    public void MailDialogModel_ValidDomain_HasNoValidationError()
    {
        var model = new MailDialogModel { Mode = MailDialogMode.AddDomain, Domain = "example.com", L = new BunitTestHelper.StubLocalizer() };
        var errors = new List<ValidationResult>();

        Assert.True(Validator.TryValidateObject(model, new ValidationContext(model), errors, true));
        Assert.Empty(errors);
    }

    [Fact]
    public void MailDialogModel_InvalidDomain_HasDomainValidationError()
    {
        var model = new MailDialogModel { Mode = MailDialogMode.AddDomain, Domain = "not_a_domain!", L = new BunitTestHelper.StubLocalizer() };
        var errors = new List<ValidationResult>();

        Assert.False(Validator.TryValidateObject(model, new ValidationContext(model), errors, true));
        Assert.Contains(errors, error => error.MemberNames.Contains(nameof(MailDialogModel.Domain)));
    }

    [Fact]
    public void MailDialogModel_EmptyDomain_IsRequired()
    {
        var model = new MailDialogModel { Mode = MailDialogMode.AddDomain, Domain = string.Empty, L = new BunitTestHelper.StubLocalizer() };
        var errors = new List<ValidationResult>();

        Assert.False(Validator.TryValidateObject(model, new ValidationContext(model), errors, true));
        Assert.Contains(errors, error => error.MemberNames.Contains(nameof(MailDialogModel.Domain)));
    }

    // ── ValidateEmailField ────────────────────────────────────────────────────

    [Fact]
    public void MailDialogModel_ValidAccount_HasNoValidationError()
    {
        var model = new MailDialogModel { Mode = MailDialogMode.AddAccount, Email = "user@example.com", Password = "strong-secret", L = new BunitTestHelper.StubLocalizer() };
        var errors = new List<ValidationResult>();

        Assert.True(Validator.TryValidateObject(model, new ValidationContext(model), errors, true));
        Assert.Empty(errors);
    }

    [Fact]
    public void MailDialogModel_InvalidEmail_HasEmailValidationError()
    {
        var model = new MailDialogModel { Mode = MailDialogMode.AddAccount, Email = "not-an-email", Password = "strong-secret", L = new BunitTestHelper.StubLocalizer() };
        var errors = new List<ValidationResult>();

        Assert.False(Validator.TryValidateObject(model, new ValidationContext(model), errors, true));
        Assert.Contains(errors, error => error.MemberNames.Contains(nameof(MailDialogModel.Email)));
    }

    [Fact]
    public void MailDialogModel_ShortPassword_HasPasswordValidationError()
    {
        var model = new MailDialogModel
        {
            Mode = MailDialogMode.AddAccount,
            Email = "user@example.com",
            Password = new string('x', PasswordPolicy.MinimumLength - 1),
            L = new BunitTestHelper.StubLocalizer()
        };

        var errors = model.Validate(new ValidationContext(model)).ToList();

        Assert.Contains(errors, error => error.MemberNames.Contains(nameof(MailDialogModel.Password)));
    }

    // ── ShowConfirm / ConfirmCancelled ────────────────────────────────────────

    [Fact]
    public async Task ShowConfirm_UsesNativeDialog()
    {
        var cut = RenderSection();
        var method = typeof(ServerMailSection).GetMethod("ShowConfirm", Priv)!;

        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, ["Delete Domain", "Are you sure?", (Func<Task>)(() => Task.CompletedTask)])!);
        var dialog = (Aetheus.Front.Tests.TestDoubles.ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();

        Assert.Equal("Delete Domain", dialog.LastTitle);
        Assert.Equal("Are you sure?", dialog.LastConfirmMessage);
    }

    [Fact]
    public async Task ShowConfirm_Cancelled_DoesNotRunAction()
    {
        var cut = RenderSection();
        var executed = false;
        var method = typeof(ServerMailSection).GetMethod("ShowConfirm", Priv)!;
        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, ["Delete", "Sure?", (Func<Task>)(() => { executed = true; return Task.CompletedTask; })])!);

        Assert.False(executed);
    }

    // ── Static helpers ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(500, "B", "500")]
    [InlineData(1024, "KB", "1")]
    [InlineData(1024 * 1024, "MB", "1")]
    public void FormatBytes_ReturnsHumanReadable(long bytes, string expectedUnit, string expectedNumber)
    {
        var result = ServerMailQueueTab.FormatBytes(bytes);
        Assert.Contains(expectedUnit, result);
        Assert.Contains(expectedNumber, result);
    }

    // ── HandleTaskCompleted ───────────────────────────────────────────────────

    [Fact]
    public void HandleTaskCompleted_DoesNotThrow()
    {
        var cut = RenderSection();
        var ex = Record.Exception(() => cut.Instance.HandleTaskCompleted(new TaskCompletedNotification { TaskId = 1, ServerId = 10 }));
        Assert.Null(ex);
    }

    // ── ShowDkimRotation ──────────────────────────────────────────────────────

    [Fact]
    public async Task ShowDkimRotation_OpensDialogWithDkimFields()
    {
        var cut = RenderSection();
        var method = typeof(ServerMailSection).GetMethod("ShowDkimRotation", Priv)!;
        var domain = new MailDomainDto { Id = 1, Name = "example.com", DkimSelector = "default" };

        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [domain])!);

        var dom = (string)typeof(ServerMailSection).GetField("_dkimRotationDomain", Priv)!.GetValue(cut.Instance)!;
        var dialog = (Aetheus.Front.Tests.TestDoubles.ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();

        Assert.Equal("example.com", dom);
        Assert.Equal(typeof(MailOperationDialog), dialog.LastComponent);
        Assert.Equal(MailDialogMode.RotateDkim, dialog.LastParameters!["Mode"]);
        var model = Assert.IsType<MailDialogModel>(dialog.LastParameters["Model"]);
        Assert.Equal("default", model.CurrentSelector);
    }
}
