// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Tests.Shared;

/// <summary> After a service action the page re-reads the server, but the stored service list only
/// changes when the agent's next heartbeat is stored. That read must neither replace a newer list
/// the live heartbeat already brought nor pass an unchanged list off as the result of the action.
/// </summary>
public class ServerDetailLoaderRefreshTests
{
    private static ServiceInfoDto Service(string name, bool installed) =>
        new() { Name = name, Type = ServiceType.Systemd, Status = installed ? "enabled" : "Not installed", IsInstalled = installed, IsManageable = true };

    private static ServerDetailDto Detail(params ServiceInfoDto[] services) =>
        new() { Id = 5, Name = "vps", Services = [.. services] };

    [Fact]
    public void AStaleReadAfterALiveHeartbeat_KeepsTheHeartbeatsServices()
    {
        // The heartbeat already moved dovecot to "Available"; the read still has it installed.
        var live = Detail(Service("dovecot", installed: false));
        var stale = Detail(Service("dovecot", installed: true));

        var merged = ServerDetailLoader.KeepNewerServices(live, stale, heartbeatDuringFetch: true);

        Assert.Same(live.Services, merged.Services);
        Assert.False(Assert.Single(merged.Services).IsInstalled);
    }

    [Fact]
    public void AnUnchangedList_KeepsItsInstance_SoTheActionIsNotTakenAsApplied()
    {
        var current = Detail(Service("dovecot", installed: true));
        var sameContent = Detail(Service("dovecot", installed: true));

        var merged = ServerDetailLoader.KeepNewerServices(current, sameContent, heartbeatDuringFetch: false);

        Assert.Same(current.Services, merged.Services);
    }

    [Fact]
    public void AChangedListWithoutAHeartbeatMeanwhile_IsTaken()
    {
        var current = Detail(Service("dovecot", installed: true));
        var fetched = Detail(Service("dovecot", installed: false));

        var merged = ServerDetailLoader.KeepNewerServices(current, fetched, heartbeatDuringFetch: false);

        Assert.Same(fetched.Services, merged.Services);
    }
}
