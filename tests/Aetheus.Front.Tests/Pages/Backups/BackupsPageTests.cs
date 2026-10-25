// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;
using Aetheus.Front.Components.AppBackups;
using Bunit;
using BackupsPage = Aetheus.Front.Components.AppBackups.Backups;

namespace Aetheus.Front.Tests.Pages.Backups;

public class BackupsPageTests : BunitContext
{
    private static BackupPolicyDto Policy(int id, string name, bool enabled) => new()
    {
        Id = id,
        Name = name,
        Enabled = enabled,
        ProjectId = 1,
        ServerId = 1,
        DbEngine = BackupDbEngine.Postgres,
        ScheduleCron = "0 3 * * *",
        RetentionCount = 7
    };

    private BunitTestHelper.TestHandler Wire(params BackupPolicyDto[] policies)
    {
        var handler = BunitTestHelper.RegisterServices(this);
        handler.SetPaginatedJsonResponse(HttpMethod.Get, "api/backups", policies);
        handler.SetJsonResponse(HttpMethod.Get, "api/projects", new PaginatedResult<ProjectDto> { Items = [] });
        handler.SetJsonResponse(HttpMethod.Get, "api/servers", new PaginatedResult<ServerDto> { Items = [] });
        return handler;
    }

    [Fact]
    public void RendersPolicies_WithEnabledBadge()
    {
        Wire(Policy(1, "nightly-db", enabled: true), Policy(2, "weekly-files", enabled: false));

        var cut = Render<BackupsPage>();

        Assert.Contains("nightly-db", cut.Markup);
        Assert.Contains("weekly-files", cut.Markup);
        Assert.Contains("BackupEnabledYes", cut.Markup);
        Assert.Contains("BackupEnabledNo", cut.Markup);
    }

    [Fact]
    public void EmptyList_ShowsEmptyState()
    {
        Wire();

        var cut = Render<BackupsPage>();

        Assert.Contains("BackupEmptyTitle", cut.Markup);
    }

    [Fact]
    public void ProjectScope_FiltersPoliciesAndFixesTheProjectContext()
    {
        var handler = Wire(Policy(1, "project-nightly", enabled: true));

        var cut = Render<BackupsPage>(parameters => parameters
            .Add(component => component.ProjectId, 7));

        cut.WaitForAssertion(() => Assert.Contains(handler.Requests, request =>
            request.Method == "GET"
            && request.Url.Contains("api/backups", StringComparison.Ordinal)
            && request.Url.Contains("projectId=7", StringComparison.Ordinal)));
        cut.FindAll("button").First(button => button.TextContent.Contains("Create")).Click();

        Assert.DoesNotContain(">Project<", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void NewPolicy_OpensTheForm()
    {
        Wire();
        var cut = Render<BackupsPage>();

        cut.FindAll("button").First(b => b.TextContent.Contains("Create")).Click();

        // A form-only field label appears once the create panel is open.
        Assert.Contains("BackupScheduleCron", cut.Markup);
    }

    [Fact]
    public void RunHistory_ShowsRestoreCheckStates_NoFake()
    {
        var handler = Wire(Policy(1, "nightly-db", enabled: true));
        handler.SetPaginatedJsonResponse(HttpMethod.Get, "api/backups/1/runs", new List<BackupRunDto>
        {
            new() { Id = 1, BackupPolicyId = 1, Status = BackupRunStatus.Succeeded, RestoreCheckStatus = RestoreCheckStatus.Verified },
            new() { Id = 2, BackupPolicyId = 1, Status = BackupRunStatus.Succeeded, RestoreCheckStatus = RestoreCheckStatus.Failed },
            new() { Id = 3, BackupPolicyId = 1, Status = BackupRunStatus.Succeeded, RestoreCheckStatus = RestoreCheckStatus.Unverified }
        });

        var cut = Render<BackupsPage>();
        cut.FindMenuItem("BackupRuns")!.Click();

        Assert.Contains("BackupRestoreVerified", cut.Markup);
        Assert.Contains("BackupRestoreFailed", cut.Markup);
        Assert.Contains("BackupRestoreUnverified", cut.Markup);
    }
}
