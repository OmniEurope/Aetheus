// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Servers;

public class ServerCronSectionRenderTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly BunitTestHelper.TestHandler _handler;

    public ServerCronSectionRenderTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    private static ServerDetailDto MakeServer(bool installed = true, params CronJobDto[] jobs) => new()
    {
        Id = 12,
        Name = "cron-test",
        Hostname = "10.0.0.12",
        Type = ServerType.Normal,
        Status = ServerStatus.Online,
        Tags = [],
        Services = [],
        Cron = new CronDataDto
        {
            IsInstalled = installed,
            Jobs = jobs.ToList()
        }
    };

    private IRenderedComponent<ServerCronSection> RenderSection(ServerDetailDto? server = null)
    {
        var s = server ?? MakeServer();
        return Render<ServerCronSection>(p => p
            .Add(x => x.Server, s)
            .Add(x => x.ServerId, s.Id));
    }

    [Fact]
    public void NotInstalled_RendersHeader()
    {
        var cut = RenderSection(MakeServer(installed: false));
        Assert.Contains("Cron", cut.Markup);
        Assert.Contains("NotInstalled", cut.Markup);
    }

    [Fact]
    public void Installed_NoJobs_RendersEmptyState()
    {
        var cut = RenderSection();
        Assert.Contains("Cron", cut.Markup);
        Assert.Contains("Installed", cut.Markup);
    }

    [Fact]
    public void Installed_WithJobs_RendersEachJob()
    {
        var job = new CronJobDto
        {
            Id = "job1",
            User = "root",
            Schedule = "*/5 * * * *",
            Command = "/usr/bin/backup.sh",
            Source = "/etc/crontab"
        };
        var cut = RenderSection(MakeServer(true, job));
        Assert.Contains("/usr/bin/backup.sh", cut.Markup);
        Assert.Contains("*/5 * * * *", cut.Markup);
    }

    [Fact]
    public void FilteredJobs_Search_FiltersByCommand()
    {
        var jobs = new[]
        {
            new CronJobDto { Id = "1", User = "root", Schedule = "0 0 * * *", Command = "/usr/bin/backup.sh" },
            new CronJobDto { Id = "2", User = "app", Schedule = "0 */2 * * *", Command = "/usr/bin/sync.sh" }
        };
        var cut = RenderSection(MakeServer(true, jobs));

        typeof(ServerCronSection).GetField("_jobSearch", Priv)!
            .SetValue(cut.Instance, "sync");

        var filteredProp = typeof(ServerCronSection).GetProperty("FilteredJobs", Priv)!;
        var filtered = (List<CronJobDto>)filteredProp.GetValue(cut.Instance)!;

        Assert.Single(filtered);
        Assert.Equal("/usr/bin/sync.sh", filtered[0].Command);
    }

    // Hardened rule #2/#3: never await OpenAsync before closing; assert only that
    // OmniDialogService.OnOpen fires, then close immediately. The dialog content lives in a
    // separate host, NOT in cut.Markup - never WaitForState on it.
    [Fact]
    public async Task OpenCreateDialog_OpensDialog()
    {
        var cut = RenderSection();
        var dialog = Services.GetRequiredService<OmniDialogService>();
        var opened = false;
        dialog.OnOpen += (_, _, _, _) => opened = true;

        var method = typeof(ServerCronSection).GetMethod("OpenCreateDialog", Priv)!;
        var task = cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [])!);
        await cut.InvokeAsync(() => dialog.Close(null));
        await task;

        Assert.True(opened);
    }

    [Fact]
    public async Task OpenEditDialog_OpensDialog()
    {
        var cut = RenderSection();
        var dialog = Services.GetRequiredService<OmniDialogService>();
        var opened = false;
        dialog.OnOpen += (_, _, _, _) => opened = true;

        var job = new CronJobDto { Id = "job1", User = "root", Schedule = "0 2 * * *", Command = "/bin/ls" };
        var method = typeof(ServerCronSection).GetMethod("OpenEditDialog", Priv)!;
        var task = cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [job])!);
        await cut.InvokeAsync(() => dialog.Close(null));
        await task;

        Assert.True(opened);
    }

    [Fact]
    public void ValidateCronExpression_WithValidSchedule_ReturnsIsValidTrue()
    {
        var result = CronSchedulePreview.ValidateCronExpression("*/5 * * * *");
        Assert.True(result.IsValid);
    }
}
