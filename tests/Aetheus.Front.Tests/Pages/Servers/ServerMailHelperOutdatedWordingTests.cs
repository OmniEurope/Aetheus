// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>The outdated-helper warning names the installed version first and the expected one second: it
/// once said "deploy version 5" whatever the expected version was.</summary>
public sealed class ServerMailHelperOutdatedWordingTests : BunitContext
{
    private readonly CultureInfo _previousUiCulture = CultureInfo.CurrentUICulture;

    public ServerMailHelperOutdatedWordingTests()
    {
        var handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        handler.SetJsonResponse("api/servers/10/mail/diagnostics", new MailDiagnosticsDto
        {
            CheckedAt = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc),
            Items = []
        });
        Services.AddSingleton(MailOperationDialogValidationTests.RealLocalizer());
    }

    protected override void Dispose(bool disposing)
    {
        CultureInfo.CurrentUICulture = _previousUiCulture;
        base.Dispose(disposing);
    }

    [Theory]
    [InlineData("en-US", "outdated (version 5)", "deploy version ")]
    [InlineData("fr-FR", "obsolètes (version 5)", "déployer la version ")]
    public void Warning_NamesTheInstalledVersion_ThenTheExpectedOne(string culture, string installed, string expected)
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);

        var cut = this.RenderWithOutlets<ServerMailStatusList>(new Dictionary<string, object?>
        {
            [nameof(ServerMailStatusList.ServerId)] = 10,
            [nameof(ServerMailStatusList.Mail)] = new MailDataDto
            {
                IsInstalled = true,
                IsManagedByAetheus = true,
                Hostname = "mail.example.com",
                HelperVersion = 5
            },
            [nameof(ServerMailStatusList.CanManage)] = true,
            [nameof(ServerMailStatusList.DefaultSender)] = "admin@example.com"
        }, ServerMailReinstall.HeaderMenuSection);

        var text = cut.Find(".mail-helper-outdated").TextContent;
        Assert.Contains(installed, text, StringComparison.Ordinal);
        Assert.Contains($"{expected}{ServerMailStatusList.CurrentHelperVersion}.", text, StringComparison.Ordinal);
    }
}
