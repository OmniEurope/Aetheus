// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary> A heartbeat that replaces a server section's list marks the rows the grid did not hold
/// (bold for a few seconds), and only those; the first list and a list without new rows mark
/// nothing. </summary>
public class ServerLiveGridRowsTests : BunitContext
{
    private const string NewRowClass = "omni-data-grid__row--new";

    public ServerLiveGridRowsTests() => BunitTestHelper.RegisterServices(this);

    private static List<DockerVolumeDto> Volumes(params string[] names) =>
        [.. names.Select(name => new DockerVolumeDto { Name = name, Driver = "local", Mountpoint = $"/data/{name}" })];

    [Fact]
    public void FirstList_MarksNothing()
    {
        var cut = Render<DockerVolumesTab>(p => p.Add(x => x.Volumes, Volumes("pgdata", "logs")));

        Assert.Empty(cut.FindAll($"tr.{NewRowClass}"));
    }

    [Fact]
    public void HeartbeatWithANewVolume_MarksOnlyThatVolume()
    {
        var cut = Render<DockerVolumesTab>(p => p.Add(x => x.Volumes, Volumes("pgdata")));

        cut.Render(p => p.Add(x => x.Volumes, Volumes("pgdata", "cache")));

        cut.WaitForAssertion(() =>
        {
            var marked = cut.FindAll($"tr.{NewRowClass}");
            Assert.Contains("cache", Assert.Single(marked).TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void HeartbeatWithTheSameVolumes_MarksNothing()
    {
        var cut = Render<DockerVolumesTab>(p => p.Add(x => x.Volumes, Volumes("pgdata", "logs")));

        cut.Render(p => p.Add(x => x.Volumes, Volumes("pgdata", "logs")));

        Assert.Empty(cut.FindAll($"tr.{NewRowClass}"));
    }

    private static ServerDetailDto CronServer(int id, params string[] jobIds) => new()
    {
        Id = id,
        Name = $"cron-{id}",
        Hostname = "10.0.0.12",
        Type = ServerType.Normal,
        Status = ServerStatus.Online,
        Tags = [],
        Services = [],
        Cron = new CronDataDto
        {
            IsInstalled = true,
            Jobs = [.. jobIds.Select(jobId => new CronJobDto { Id = jobId, User = "root", Schedule = "0 * * * *", Command = $"run {jobId}", Source = "crontab" })]
        }
    };

    [Fact]
    public void CronHeartbeatWithANewJob_MarksOnlyThatJob()
    {
        var cut = Render<ServerCronSection>(p => p.Add(x => x.Server, CronServer(12, "a")).Add(x => x.ServerId, 12));

        cut.Render(p => p.Add(x => x.Server, CronServer(12, "a", "b")).Add(x => x.ServerId, 12));

        cut.WaitForAssertion(() =>
            Assert.Contains("run b", Assert.Single(cut.FindAll($"tr.{NewRowClass}")).TextContent, StringComparison.Ordinal));
    }

    /// <summary>Another server shown on the same section is its first list, not a list of new rows.</summary>
    [Fact]
    public void AnotherServer_MarksNothing()
    {
        var cut = Render<ServerCronSection>(p => p.Add(x => x.Server, CronServer(12, "a")).Add(x => x.ServerId, 12));

        cut.Render(p => p.Add(x => x.Server, CronServer(13, "x", "y")).Add(x => x.ServerId, 13));

        Assert.Empty(cut.FindAll($"tr.{NewRowClass}"));
    }
}
