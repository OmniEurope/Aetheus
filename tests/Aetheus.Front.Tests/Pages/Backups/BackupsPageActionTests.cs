// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Aetheus.Front.Tests.TestDoubles;
using AngleSharp.Dom;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using BackupsPage = Aetheus.Front.Components.AppBackups.Backups;

namespace Aetheus.Front.Tests.Pages.Backups;

/// <summary>
/// The write side of the backups page, driven through its real buttons and form: create, edit,
/// delete behind its confirmation, run-now, and what each of those does when the backend refuses.
/// <see cref="BackupsPageTests"/> covers the read-only rendering.
/// </summary>
public sealed class BackupsPageActionTests : BunitContext
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly BunitTestHelper.TestHandler _handler;
    private readonly ImmediateDialogService _dialog;

    public BackupsPageActionTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        _dialog = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        _handler.SetJsonResponse(HttpMethod.Get, "api/projects", new PaginatedResult<ProjectDto>
        {
            Items = [new ProjectDto { Id = 1, Name = "aetheus" }],
            TotalCount = 1,
            Page = 1,
            PageSize = 100
        });
        _handler.SetJsonResponse(HttpMethod.Get, "api/servers", new PaginatedResult<ServerDto>
        {
            Items = [new ServerDto { Id = 2, Name = "vps-1" }],
            TotalCount = 1,
            Page = 1,
            PageSize = 100
        });
    }

    private static BackupPolicyDto Policy(int id = 1, string name = "nightly-db") => new()
    {
        Id = id,
        Name = name,
        Enabled = true,
        ProjectId = 1,
        ServerId = 2,
        DbEngine = BackupDbEngine.Postgres,
        DbHost = "127.0.0.1",
        DbPort = 5432,
        DbName = "app",
        DbUser = "app",
        HasPassword = true,
        FilePaths = ["/srv/app/uploads", "/etc/app.conf"],
        ScheduleCron = "0 3 * * *",
        RetentionCount = 14,
        RestoreCheckCron = "0 5 * * 0"
    };

    private void WirePolicies(params BackupPolicyDto[] policies) =>
        _handler.SetPaginatedJsonResponse(HttpMethod.Get, "api/backups", policies);

    /// <summary>R1-4: a policy row keeps one visible action (Run now); Runs, Edit and Delete are in its menu.</summary>
    [Fact]
    public void ARowShowsRunNowAsItsOnlyAction_AndKeepsTheOthersInTheMenu()
    {
        _handler.SetPaginatedJsonResponse(HttpMethod.Get, "api/backups", new[]
        {
            new BackupPolicyDto { Id = 1, Name = "nightly-db", Enabled = true, ProjectId = 1, ServerId = 2, ScheduleCron = "0 3 * * *", RetentionCount = 14 }
        });
        var cut = Render<BackupsPage>();
        cut.WaitForAssertion(() => Assert.Contains("nightly-db", cut.Markup, StringComparison.Ordinal));

        Assert.Single(cut.FindAll("button[title='BackupRunNow']"));
        Assert.Empty(cut.FindAll("button[title='BackupRuns'], button[title='Edit'], button[title='Delete']"));
        foreach (var label in new[] { "BackupRuns", "Edit", "Delete" })
            Assert.NotNull(cut.FindMenuItem(label));
    }

    // R1-4: Run now is the row's visible action, the others are entries of its "More actions" menu.
    private static IElement Button(IRenderedComponent<BackupsPage> cut, string text) =>
        cut.FindAll("button").FirstOrDefault(button => button.Names().Contains(text, StringComparison.Ordinal))
        ?? cut.FindMenuItem(text)
        ?? throw new InvalidOperationException($"No button or menu entry named {text}");

    private static IElement FormInput(IRenderedComponent<BackupsPage> cut, string name) =>
        name switch
        {
            "Name" => cut.Find("input#oe-pages-backups-backups-1"),
            "ScheduleCron" => cut.Find("input#oe-pages-backups-backups-11"),
            _ => cut.Find($"input[name='{name}']")
        };

    /// <summary>
    /// The page renders several forms (each grid filter is one), so the policy panel is
    /// located from its own Name field rather than by taking whichever form comes first.
    /// </summary>
    private static IElement PolicyForm(IRenderedComponent<BackupsPage> cut) =>
        FormInput(cut, "Name").Closest("form")!;

    /// <summary>
    /// Project and server are required by the form's own validation, and a dropdown cannot be
    /// driven from raw markup, so the two owner pickers are set through their bound value. Without
    /// this the template form refuses to submit, which is the correct production behaviour.
    /// </summary>
    private static async Task PickOwnersAsync(
        IRenderedComponent<BackupsPage> cut, int? projectId, int serverId)
    {
        var dropdowns = cut.FindComponents<OmniDropDown<int>>();
        if (projectId is { } project)
        {
            await cut.InvokeAsync(() => dropdowns[0].Instance.ValueChanged.InvokeAsync(project));
            await cut.InvokeAsync(() => dropdowns[1].Instance.ValueChanged.InvokeAsync(serverId));
            return;
        }
        await cut.InvokeAsync(() => dropdowns[0].Instance.ValueChanged.InvokeAsync(serverId));
    }

    private T LastBody<T>(string method, string urlContains)
    {
        var body = _handler.RequestDetails
            .Last(request => request.Method == method
                && request.Url.Contains(urlContains, StringComparison.Ordinal))
            .Body;
        return JsonSerializer.Deserialize<T>(body!, Json)!;
    }

    private bool Sent(string method, string urlContains) =>
        _handler.Requests.Any(request => request.Method == method
            && request.Url.Contains(urlContains, StringComparison.Ordinal));

    // ---------- create ----------

    [Fact]
    public async Task CreatingAPolicySendsTheFormWithItsFilePathsSplitPerLine()
    {
        WirePolicies();
        _handler.SetJsonResponse(HttpMethod.Post, "api/backups", Policy());
        var cut = Render<BackupsPage>();

        Button(cut, "Create").Click();
        FormInput(cut, "Name").Input("nightly-db");
        FormInput(cut, "ScheduleCron").Input("0 4 * * *");
        await PickOwnersAsync(cut, 1, 2);
        cut.Find("textarea").Input("/srv/app/uploads\n\n  /etc/app.conf  \n");
        PolicyForm(cut).Submit();

        cut.WaitForAssertion(() => Assert.True(Sent("POST", "api/backups")));
        var request = LastBody<CreateBackupPolicyRequest>("POST", "api/backups");
        Assert.Equal("nightly-db", request.Name);
        Assert.Equal("0 4 * * *", request.ScheduleCron);
        Assert.Equal(["/srv/app/uploads", "/etc/app.conf"], request.FilePaths);
    }

    [Fact]
    public async Task CreatingAPolicyInsideAProjectPreselectsThatProject()
    {
        WirePolicies();
        _handler.SetJsonResponse(HttpMethod.Post, "api/backups", Policy());
        var cut = Render<BackupsPage>(parameters => parameters.Add(page => page.ProjectId, 7));

        Button(cut, "Create").Click();
        FormInput(cut, "Name").Input("scoped");
        await PickOwnersAsync(cut, null, 2);
        PolicyForm(cut).Submit();

        cut.WaitForAssertion(() => Assert.True(Sent("POST", "api/backups")));
        Assert.Equal(7, LastBody<CreateBackupPolicyRequest>("POST", "api/backups").ProjectId);
    }

    [Fact]
    public async Task ARefusedCreateKeepsTheFormOpenSoTheOperatorDoesNotLoseTheirInput()
    {
        WirePolicies();
        _handler.SetJsonResponse(
            HttpMethod.Post, "api/backups", (BackupPolicyDto?)null, HttpStatusCode.BadRequest);
        var cut = Render<BackupsPage>();

        Button(cut, "Create").Click();
        FormInput(cut, "Name").Input("nightly-db");
        await PickOwnersAsync(cut, 1, 2);
        PolicyForm(cut).Submit();

        cut.WaitForAssertion(() => Assert.True(Sent("POST", "api/backups")));
        Assert.NotNull(PolicyForm(cut));
        Assert.Equal("nightly-db", FormInput(cut, "Name").GetAttribute("value"));
    }

    [Fact]
    public void CancellingTheFormClosesItWithoutCallingTheBackend()
    {
        WirePolicies();
        var cut = Render<BackupsPage>();

        Button(cut, "Create").Click();
        Assert.NotNull(PolicyForm(cut));
        Button(cut, "Cancel").Click();

        Assert.Empty(cut.FindAll("input#oe-pages-backups-backups-1"));
        Assert.False(Sent("POST", "api/backups"));
    }

    // ---------- edit ----------

    [Fact]
    public void EditingAPolicyPrefillsTheFormAndSendsAnUpdateForThatId()
    {
        WirePolicies(Policy());
        _handler.SetJsonResponse(HttpMethod.Put, "api/backups/1", Policy());
        var cut = Render<BackupsPage>();

        Button(cut, "Edit").Click();

        Assert.Equal("nightly-db", FormInput(cut, "Name").GetAttribute("value"));
        Assert.Equal("0 3 * * *", FormInput(cut, "ScheduleCron").GetAttribute("value"));
        Assert.Contains("/srv/app/uploads", cut.Find("textarea").GetAttribute("value"), StringComparison.Ordinal);

        PolicyForm(cut).Submit();

        cut.WaitForAssertion(() => Assert.True(Sent("PUT", "api/backups/1")));
        var request = LastBody<UpdateBackupPolicyRequest>("PUT", "api/backups/1");
        Assert.Equal("nightly-db", request.Name);
        Assert.Equal(14, request.RetentionCount);
        Assert.Equal(["/srv/app/uploads", "/etc/app.conf"], request.FilePaths);
    }

    [Fact]
    public void EditingAPolicyNeverResendsTheStoredPassword()
    {
        WirePolicies(Policy());
        _handler.SetJsonResponse(HttpMethod.Put, "api/backups/1", Policy());
        var cut = Render<BackupsPage>();

        Button(cut, "Edit").Click();
        PolicyForm(cut).Submit();

        cut.WaitForAssertion(() => Assert.True(Sent("PUT", "api/backups/1")));
        Assert.Null(LastBody<UpdateBackupPolicyRequest>("PUT", "api/backups/1").DbPassword);
    }

    [Fact]
    public void ARefusedUpdateKeepsTheFormOpen()
    {
        WirePolicies(Policy());
        _handler.SetJsonResponse(
            HttpMethod.Put, "api/backups/1", (BackupPolicyDto?)null, HttpStatusCode.Conflict);
        var cut = Render<BackupsPage>();

        Button(cut, "Edit").Click();
        PolicyForm(cut).Submit();

        cut.WaitForAssertion(() => Assert.True(Sent("PUT", "api/backups/1")));
        Assert.NotNull(PolicyForm(cut));
    }

    // ---------- delete ----------

    [Fact]
    public void DeletingAPolicyAsksForConfirmationAndDoesNothingWhenItIsDeclined()
    {
        WirePolicies(Policy());
        _dialog.ConfirmResult = false;
        var cut = Render<BackupsPage>();

        Button(cut, "Delete").Click();

        Assert.Equal("BackupDeleteConfirm", _dialog.LastConfirmMessage);
        Assert.False(Sent("DELETE", "api/backups/1"));
    }

    [Fact]
    public void ConfirmingTheDeleteCallsTheEndpointForThatPolicy()
    {
        WirePolicies(Policy());
        _handler.SetResponse(HttpMethod.Delete, "api/backups/1", HttpStatusCode.NoContent);
        _dialog.ConfirmResult = true;
        var cut = Render<BackupsPage>();

        Button(cut, "Delete").Click();

        cut.WaitForAssertion(() => Assert.True(Sent("DELETE", "api/backups/1")));
    }

    [Fact]
    public void ARefusedDeleteLeavesTheOpenRunHistoryInPlace()
    {
        WirePolicies(Policy());
        _handler.SetPaginatedJsonResponse(HttpMethod.Get, "api/backups/1/runs", Array.Empty<BackupRunDto>());
        _handler.SetResponse(HttpMethod.Delete, "api/backups/1", HttpStatusCode.Conflict);
        _dialog.ConfirmResult = true;
        var cut = Render<BackupsPage>();

        Button(cut, "BackupRuns").Click();
        cut.WaitForAssertion(() => Assert.Contains("BackupRuns", cut.Markup));
        Button(cut, "Delete").Click();

        cut.WaitForAssertion(() => Assert.True(Sent("DELETE", "api/backups/1")));
        Assert.Contains("BackupRuns", cut.Markup);
    }

    // ---------- run now and run history ----------

    [Fact]
    public void RunningAPolicyNowPostsToItsRunEndpoint()
    {
        WirePolicies(Policy());
        _handler.SetResponse(HttpMethod.Post, "api/backups/1/run", HttpStatusCode.Accepted);
        var cut = Render<BackupsPage>();

        Button(cut, "BackupRunNow").Click();

        cut.WaitForAssertion(() => Assert.True(Sent("POST", "api/backups/1/run")));
    }

    [Fact]
    public void ARefusedRunStillOnlyCallsTheRunEndpointOnce()
    {
        WirePolicies(Policy());
        _handler.SetResponse(HttpMethod.Post, "api/backups/1/run", HttpStatusCode.ServiceUnavailable);
        var cut = Render<BackupsPage>();

        Button(cut, "BackupRunNow").Click();

        cut.WaitForAssertion(
            () => Assert.Single(
                _handler.Requests,
                request => request.Method == "POST"
                    && request.Url.Contains("api/backups/1/run", StringComparison.Ordinal)));
    }

    [Fact]
    public void OpeningTheRunHistoryLoadsTheRunsOfThatPolicyOnly()
    {
        WirePolicies(Policy(), Policy(2, "weekly-files"));
        _handler.SetPaginatedJsonResponse(HttpMethod.Get, "api/backups/2/runs", new[]
        {
            new BackupRunDto
            {
                Id = 9,
                BackupPolicyId = 2,
                Status = BackupRunStatus.Succeeded,
                RestoreCheckStatus = RestoreCheckStatus.Verified,
                StartedAt = new DateTime(2026, 8, 1, 3, 0, 0, DateTimeKind.Utc)
            }
        });
        var cut = Render<BackupsPage>();

        // R1-4: Runs is an entry of each row's menu; open the second row's.
        cut.FindAll(".omni-overflow-menu__trigger")[1].Click();
        cut.FindAll(".omni-menu__item")
            .Single(entry => entry.Names().Contains("BackupRuns", StringComparison.Ordinal))
            .Click();

        cut.WaitForAssertion(
            () => Assert.True(Sent("GET", "api/backups/2/runs")));
        Assert.False(Sent("GET", "api/backups/1/runs"));
    }

    [Fact]
    public void ARunHistoryThatFailsToLoadRendersEmptyInsteadOfCrashing()
    {
        WirePolicies(Policy());
        _handler.SetResponse(HttpMethod.Get, "api/backups/1/runs", HttpStatusCode.InternalServerError);
        var cut = Render<BackupsPage>();

        Button(cut, "BackupRuns").Click();

        cut.WaitForAssertion(() => Assert.True(Sent("GET", "api/backups/1/runs")));
        Assert.Contains("BackupRuns", cut.Markup);
    }

    // ---------- listing resilience ----------

    [Fact]
    public void APolicyListThatFailsToLoadRendersTheEmptyStateInsteadOfCrashing()
    {
        _handler.SetResponse(HttpMethod.Get, "api/backups", HttpStatusCode.InternalServerError);
        var cut = Render<BackupsPage>();

        cut.WaitForAssertion(() => Assert.Contains("BackupEmptyTitle", cut.Markup));
    }

    [Fact]
    public void AnOptionLoadFailureStillRendersThePage()
    {
        WirePolicies(Policy());
        _handler.SetResponse(HttpMethod.Get, "api/projects", HttpStatusCode.InternalServerError);
        _handler.SetResponse(HttpMethod.Get, "api/servers", HttpStatusCode.InternalServerError);

        var cut = Render<BackupsPage>();

        cut.WaitForAssertion(() => Assert.Contains("nightly-db", cut.Markup));
    }
}
