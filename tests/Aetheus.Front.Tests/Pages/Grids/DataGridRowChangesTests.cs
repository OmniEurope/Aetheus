// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Components.Settings;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using AlertsPage = Aetheus.Front.Components.Alerts.Alerts;
using PipelineTemplatesPage = Aetheus.Front.Components.Pipelines.PipelineTemplates;

namespace Aetheus.Front.Tests.Pages.Grids;

/// <summary>R-10 on the pages whose API answers the whole list (the grid asks LoadData, the page cuts the window): a
/// deleted or created row changes the table from the action's answer, while the read that confirms it is still
/// running, with no loader; a refused action keeps the rows.</summary>
public sealed class DataGridRowChangesTests : BunitContext
{
    private const string Bar = "thead > tr.omni-data-grid__progress .omni-loading-bar";
    private readonly BunitTestHelper.TestHandler _handler;
    private readonly ImmediateDialogService _dialog;

    public DataGridRowChangesTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
        BunitTestHelper.UseImmediateDialogs(this);
        _dialog = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        _dialog.ConfirmResult = true;
    }

    private static AetheusDataGrid<T> GridOf<T>(IRenderedComponent<Microsoft.AspNetCore.Components.IComponent> cut)
        where T : notnull => cut.FindComponent<AetheusDataGrid<T>>().Instance;

    private TaskCompletionSource<T> HoldTheNextRead<T>(string urlContains)
    {
        var gate = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, urlContains, _ => gate.Task);
        return gate;
    }

    private static AlertRuleDto Rule(int id, string name) => new()
    {
        Id = id,
        Name = name,
        Metric = MetricType.Cpu,
        Operator = ComparisonOperator.GreaterThan,
        Threshold = 90,
        SustainedSeconds = 60,
        IsEnabled = true,
        CreatedAt = DateTime.UtcNow
    };

    [Fact]
    public async Task ADeletedAlertRule_LeavesTheGridAtOnce_AndARefusedDeletionKeepsIt()
    {
        _handler.SetJsonResponse("api/alerts", new List<AlertRuleDto> { Rule(1, "High CPU"), Rule(2, "Low Disk") });
        var cut = Render<AlertsPage>();
        cut.WaitForAssertion(() => Assert.Contains("Low Disk", cut.Markup, StringComparison.Ordinal));
        var grid = GridOf<AlertRuleDto>(cut);

        _handler.SetResponse(HttpMethod.Delete, "api/alerts/2", HttpStatusCode.Conflict);
        await cut.FindAll("button[title='Delete']")[1].ClickAsync(new());
        Assert.Contains("Low Disk", cut.Markup, StringComparison.Ordinal);

        _handler.SetResponse(HttpMethod.Delete, "api/alerts/2", HttpStatusCode.NoContent);
        var gate = HoldTheNextRead<List<AlertRuleDto>>("api/alerts");
        await cut.FindAll("button[title='Delete']")[1].ClickAsync(new());

        cut.WaitForAssertion(() => Assert.DoesNotContain("Low Disk", cut.Markup, StringComparison.Ordinal));
        Assert.False(grid.BackgroundRefresh.IsCompleted);
        Assert.Contains("High CPU", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(Bar));

        gate.SetResult([Rule(1, "High CPU")]);
        await cut.InvokeAsync(() => grid.BackgroundRefresh);
        Assert.DoesNotContain("Low Disk", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("High CPU", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADeletedPipelineTemplate_LeavesTheGridAtOnce_AndARefusedDeletionKeepsIt()
    {
        PipelineTemplateSummaryDto Template(int id, string name) => new() { Id = id, Name = name, Version = 1, CanEdit = true };
        _handler.SetJsonResponse("api/pipelines/templates", new List<PipelineTemplateSummaryDto> { Template(1, "Build"), Template(2, "Deploy") });
        var cut = Render<PipelineTemplatesPage>();
        cut.WaitForAssertion(() => Assert.Contains("Deploy", cut.Markup, StringComparison.Ordinal));
        var grid = GridOf<PipelineTemplateSummaryDto>(cut);

        _handler.SetResponse(HttpMethod.Delete, "api/pipelines/templates/2", HttpStatusCode.Conflict);
        await cut.FindAll("button[title='Delete']")[1].ClickAsync(new());
        Assert.Contains("Deploy", cut.Markup, StringComparison.Ordinal);

        _handler.SetResponse(HttpMethod.Delete, "api/pipelines/templates/2", HttpStatusCode.NoContent);
        var gate = HoldTheNextRead<List<PipelineTemplateSummaryDto>>("api/pipelines/templates");
        await cut.FindAll("button[title='Delete']")[1].ClickAsync(new());

        cut.WaitForAssertion(() => Assert.DoesNotContain("Deploy", cut.Markup, StringComparison.Ordinal));
        Assert.False(grid.BackgroundRefresh.IsCompleted);
        Assert.Contains("Build", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(Bar));

        gate.SetResult([Template(1, "Build")]);
        await cut.InvokeAsync(() => grid.BackgroundRefresh);
        Assert.DoesNotContain("Deploy", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Build", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADeletedSecret_LeavesTheGridAtOnce_BeforeTheReadEnds()
    {
        _handler.SetJsonResponse("api/settings", new List<AppSettingDto> { new() { Key = "SiteName", Value = "Aetheus" } });
        _handler.SetJsonResponse("api/settings/secrets", new List<SecretDto> { new() { Id = 1, Key = "API_KEY" }, new() { Id = 2, Key = "DB_KEY" } });
        _handler.SetJsonResponse("api/auth/registration-tokens", new List<RegistrationTokenDto>());
        Services.GetRequiredService<NavigationManager>().NavigateTo("/admin/settings?tab=secrets");
        var cut = Render<AdminSettings>();
        cut.WaitForAssertion(() => Assert.Contains("DB_KEY", cut.Markup, StringComparison.Ordinal));
        var grid = GridOf<SecretDto>(cut);

        var gate = HoldTheNextRead<List<SecretDto>>("api/settings/secrets");
        _handler.SetResponse(HttpMethod.Delete, "api/settings/secrets/2", HttpStatusCode.NoContent);
        await cut.FindAll("button[title='DeleteSecret']")[1].ClickAsync(new());

        cut.WaitForAssertion(() => Assert.DoesNotContain("DB_KEY", cut.Markup, StringComparison.Ordinal));
        Assert.False(grid.BackgroundRefresh.IsCompleted);
        Assert.Contains("API_KEY", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(Bar));

        gate.SetResult([new SecretDto { Id = 1, Key = "API_KEY" }]);
        await cut.InvokeAsync(() => grid.BackgroundRefresh);
        Assert.DoesNotContain("DB_KEY", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("API_KEY", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ALinkToTheSecretsTab_ShowsTheSecrets_WhenTheListAnswersAfterTheGridAsked()
    {
        _handler.SetJsonResponse("api/settings", new List<AppSettingDto> { new() { Key = "SiteName", Value = "Aetheus" } });
        _handler.SetJsonResponse("api/auth/registration-tokens", new List<RegistrationTokenDto>());
        var gate = HoldTheNextRead<List<SecretDto>>("api/settings/secrets");
        Services.GetRequiredService<NavigationManager>().NavigateTo("/admin/settings?tab=secrets");
        var cut = Render<AdminSettings>();

        gate.SetResult([new SecretDto { Id = 1, Key = "API_KEY" }]);

        cut.WaitForAssertion(() => Assert.Contains("API_KEY", cut.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AGeneratedToken_ShowsAtOnce_BeforeTheReadEnds()
    {
        _handler.SetJsonResponse("api/settings", new List<AppSettingDto> { new() { Key = "SiteName", Value = "Aetheus" } });
        _handler.SetJsonResponse("api/settings/secrets", new List<SecretDto>());
        _handler.SetJsonResponse("api/auth/registration-tokens", new List<RegistrationTokenDto>());
        Services.GetRequiredService<NavigationManager>().NavigateTo("/admin/settings?tab=tokens");
        var cut = Render<AdminSettings>();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("code.token-code")));
        var grid = GridOf<RegistrationTokenDto>(cut);

        var created = new RegistrationTokenDto { Id = 9, Token = "tok-9", ExpiresAt = DateTime.UtcNow.AddHours(24) };
        var gate = HoldTheNextRead<List<RegistrationTokenDto>>("api/auth/registration-tokens");
        _handler.SetJsonResponse(HttpMethod.Post, "api/auth/registration-tokens", created);
        await cut.Find("button[title='GenerateToken']").ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("code.token-code")));
        Assert.False(grid.BackgroundRefresh.IsCompleted);
        Assert.Empty(cut.FindAll(Bar));

        gate.SetResult([created]);
        await cut.InvokeAsync(() => grid.BackgroundRefresh);
        Assert.Single(cut.FindAll("code.token-code"));
    }
}
