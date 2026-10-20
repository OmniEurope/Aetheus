// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Shared;

namespace Aetheus.Back.Tests.Servers;

/// <summary>Preserve the reported installed flag so unavailable services appear in the correct
/// section.</summary>
public sealed class ServerServicesWellKnownMappingTests
{
    [Fact]
    public void SupplementWithWellKnown_KeepsTheAgentsNotInstalledFlag()
    {
        var result = ServerDataMapper.SupplementWithWellKnown(
        [
            new ServiceInfoDto { Name = "dovecot", Type = ServiceType.Systemd, Status = "Not installed", IsInstalled = false, IsManageable = true },
        ]);

        var dovecot = Assert.Single(result, s => s.Name == "dovecot");
        Assert.False(dovecot.IsInstalled);
        Assert.True(dovecot.IsManageable);
    }

    [Fact]
    public void SupplementWithWellKnown_KeepsAReportedServiceInstalled()
    {
        var result = ServerDataMapper.SupplementWithWellKnown(
        [
            new ServiceInfoDto { Name = "postfix", Type = ServiceType.Systemd, Status = "running", IsRunning = true, IsInstalled = true },
        ]);

        var postfix = Assert.Single(result, s => s.Name == "postfix");
        Assert.True(postfix.IsInstalled);
        // A well-known service is manageable even when the agent did not say so.
        Assert.True(postfix.IsManageable);
    }

    [Fact]
    public void SupplementWithWellKnown_AnUnreportedWellKnownService_IsAvailableNotInstalled()
    {
        // The new agent stops reporting a purged dovecot: the catalogue then offers it under "Available".
        var result = ServerDataMapper.SupplementWithWellKnown(
        [
            new ServiceInfoDto { Name = "nginx", Type = ServiceType.Systemd, Status = "running", IsRunning = true },
        ]);

        var dovecot = Assert.Single(result, s => s.Name == "dovecot");
        Assert.False(dovecot.IsInstalled);
        Assert.Equal(ServerDataMapper.WellKnownManageableServices.Count, result.Count);
    }
}
