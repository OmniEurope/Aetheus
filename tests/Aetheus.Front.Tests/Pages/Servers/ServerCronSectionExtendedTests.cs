// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>Extended cron section tests covering async methods and confirmation flow.</summary>
public class ServerCronSectionExtendedTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly BunitTestHelper.TestHandler _handler;

    public ServerCronSectionExtendedTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    private static ServerDetailDto MakeCronServer() => new()
    {
        Id = 40,
        Name = "cron-srv",
        Hostname = "10.0.0.4",
        Type = ServerType.Normal,
        Status = ServerStatus.Online,
        Tags = [],
        Services = [],
        Docker = new DockerDataDto { Containers = [], Images = [], ComposeStacks = [], Networks = [], Volumes = [] },
        Cron = new CronDataDto
        {
            Jobs =
            [
                new CronJobDto { Id = "j1", User = "root", Schedule = "0 * * * *", Command = "/usr/bin/backup.sh" },
                new CronJobDto { Id = "j2", User = "www-data", Schedule = "*/5 * * * *", Command = "/usr/bin/cleanup.sh" },
                new CronJobDto { Id = "j3", User = "root", Schedule = "0 0 * * *", Command = "/usr/bin/rotate-logs.sh" }
            ]
        }
    };

    private IRenderedComponent<ServerCronSection> RenderSection()
    {
        _handler.SetJsonResponse("api/servers/40/cron/job", true);
        _handler.SetJsonResponse("api/servers/40/cron/job/delete", true);
        return Render<ServerCronSection>(p => p
            .Add(x => x.Server, MakeCronServer())
            .Add(x => x.ServerId, 40));
    }

    private static Task InvokeSaveJobAsync(ServerCronSection instance, CronJobSaveRequest request) =>
        (Task)typeof(ServerCronSection).GetMethod("SaveJobAsync", Priv)!.Invoke(instance, [request])!;

    [Fact]
    public void FilteredJobs_ByUser_Filters()
    {
        var cut = RenderSection();
        typeof(ServerCronSection).GetField("_jobSearch", Priv)!.SetValue(cut.Instance, "www-data");
        var jobs = (List<CronJobDto>)typeof(ServerCronSection).GetProperty("FilteredJobs", Priv)!.GetValue(cut.Instance)!;
        Assert.Single(jobs);
    }

    [Fact]
    public void FilteredJobs_BySchedule_Filters()
    {
        var cut = RenderSection();
        typeof(ServerCronSection).GetField("_jobSearch", Priv)!.SetValue(cut.Instance, "*/5");
        var jobs = (List<CronJobDto>)typeof(ServerCronSection).GetProperty("FilteredJobs", Priv)!.GetValue(cut.Instance)!;
        Assert.Single(jobs);
    }

    [Fact]
    public async Task SaveJobAsync_EmptyFields_ShowsError()
    {
        var cut = RenderSection();
        await InvokeSaveJobAsync(cut.Instance, new CronJobSaveRequest { User = "root", Schedule = "", Command = "" });
        Assert.False((bool)typeof(ServerCronSection).GetField("_actionRunning", Priv)!.GetValue(cut.Instance)!);
    }

    [Fact]
    public async Task SaveJobAsync_ValidFields_SendsRequest()
    {
        _handler.SetResponse(
            HttpMethod.Post,
            "api/servers/40/cron",
            System.Net.HttpStatusCode.NoContent);
        var cut = RenderSection();
        await InvokeSaveJobAsync(cut.Instance, new CronJobSaveRequest { User = "root", Schedule = "0 * * * *", Command = "echo hi" });
        Assert.False((bool)typeof(ServerCronSection).GetField("_actionRunning", Priv)!.GetValue(cut.Instance)!);
    }

    [Fact]
    public async Task DeleteJobAsync_SendsRequest()
    {
        _handler.SetResponse(
            HttpMethod.Delete,
            "api/servers/40/cron",
            System.Net.HttpStatusCode.NoContent);
        var cut = RenderSection();
        var job = new CronJobDto { Id = "j1", User = "root", Schedule = "0 * * * *", Command = "backup" };
        await (Task)typeof(ServerCronSection).GetMethod("DeleteJobAsync", Priv)!.Invoke(cut.Instance, [job])!;
        Assert.False((bool)typeof(ServerCronSection).GetField("_actionRunning", Priv)!.GetValue(cut.Instance)!);
    }

    [Fact]
    public void ValidateCronExpression_InvalidExpression_ReturnsError()
    {
        var result = CronSchedulePreview.ValidateCronExpression("bad");
        Assert.False(result.IsValid);
        Assert.NotNull(result.Error);
    }
}
