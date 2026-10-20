// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Auth;
using Aetheus.Back.Components.Environments;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Data;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using static Aetheus.Back.Tests.Servers.RetiredServerScenario;

namespace Aetheus.Back.Tests.Servers;

/// <summary>Re-enrollment preserves server identity, links and administrator settings, preferring
/// machine identity within the token organisation and falling back to hostname.</summary>
public sealed class ServerRevivalEnrollmentTests
{
    private readonly RetiredServerScenario _scenario = new();

    [Fact]
    public async Task ReinstallWithTheSameMachineId_RevivesTheSameServerWithItsLinks_EvenUnderANewHostname()
    {
        await _scenario.SeedAsync();
        await _scenario.RetireAsync();
        var token = await _scenario.AddRegistrationTokenAsync();

        var response = await _scenario.EnrollAsync(Request(token, "vps2577917-reinstalled", MachineHash));

        Assert.NotNull(response);
        Assert.Equal(ServerId, response.ServerId);
        var server = await _scenario.LoadIncludingRetiredAsync(ServerId);
        Assert.Null(server.DeletedAt);
        Assert.Equal(ServerStatus.Online, server.Status);
        Assert.Equal("Ubuntu 24.04.3 LTS", server.OsDescription);
        Assert.Equal(Hostname, server.Hostname);
        Assert.Equal("[\"prod\",\"web\"]", server.Tags);
        Assert.True(server.RequireContainerIsolation);

        await using var db = _scenario.NewContext();
        Assert.Equal(2, await db.Servers.IgnoreQueryFilters([ServerQueryFilters.ExcludeRetired]).CountAsync(Ct));
        var environment = await new EnvironmentRepository(db).GetEnvironmentWithServersAsync(EnvironmentId, Ct);
        Assert.Equal(ServerId, Assert.Single(environment!.Servers).ServerId);
        Assert.Equal(ServerId, (await new PipelineServerResolver(db).FindOnlineServerInPoolAsync(PoolName, ct: Ct))?.Id);
        Assert.Single(await db.ServerPortReservations.Where(reservation => reservation.ServerId == ServerId).ToListAsync(Ct));
        var auth = new AuthRepository(db, _scenario.Clock);
        Assert.Equal(ServerId, await auth.ValidateServerTokenHashAsync(AuthTokenHelper.HashToken(SigningKey, response.BearerToken), Ct));
        Assert.Null(await auth.ValidateServerTokenHashAsync(OldTokenHash, Ct));
        await _scenario.Audit.Received(1).LogAsync("Revived", "Server", ServerId,
            Arg.Is<string?>(detail => detail != null && detail.Contains("machine-id", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OlderAgentWithoutMachineId_RevivesTheRetiredServerByHostname()
    {
        await _scenario.SeedAsync();
        await _scenario.RetireAsync();
        var token = await _scenario.AddRegistrationTokenAsync();

        var response = await _scenario.EnrollAsync(Request(token, Hostname, machineIdHash: null));

        Assert.Equal(ServerId, response?.ServerId);
        var server = await _scenario.LoadIncludingRetiredAsync(ServerId);
        Assert.Null(server.DeletedAt);
        Assert.Equal(MachineHash, server.MachineIdHash);
        await _scenario.Audit.Received(1).LogAsync("Revived", "Server", ServerId,
            Arg.Is<string?>(detail => detail != null && detail.Contains("hostname", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetiredHostnameOwnedByAnotherOrganization_IsRefusedAndStaysRetired()
    {
        await _scenario.SeedAsync();
        await _scenario.RetireAsync();
        var token = await _scenario.AddRegistrationTokenAsync(OtherOrgId);

        var response = await _scenario.EnrollAsync(Request(token, Hostname, machineIdHash: null));

        Assert.Null(response);
        Assert.NotNull((await _scenario.LoadIncludingRetiredAsync(ServerId)).DeletedAt);
        await using var db = _scenario.NewContext();
        Assert.Equal(2, await db.Servers.IgnoreQueryFilters([ServerQueryFilters.ExcludeRetired]).CountAsync(Ct));
    }

    [Fact]
    public async Task MachineIdRecordedInAnotherOrganization_IsNotAMatch()
    {
        await _scenario.SeedAsync();
        await _scenario.RetireAsync();
        var token = await _scenario.AddRegistrationTokenAsync(OtherOrgId);

        var response = await _scenario.EnrollAsync(Request(token, "fresh-host", MachineHash));

        Assert.NotNull(response);
        Assert.NotEqual(ServerId, response.ServerId);
        Assert.Equal(OtherOrgId, (await _scenario.LoadIncludingRetiredAsync(response.ServerId)).OrganizationId);
        Assert.NotNull((await _scenario.LoadIncludingRetiredAsync(ServerId)).DeletedAt);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ReenrollingAnActiveServer_KeepsTheAdminsPipelineRunnerChoice(bool adminChoice, bool agentReportsRunner)
    {
        await _scenario.SeedAsync();
        await using (var seed = _scenario.NewContext())
        {
            var server = await seed.Servers.SingleAsync(item => item.Id == ServerId, Ct);
            server.PipelineRunnerEnabled = adminChoice;
            await seed.SaveChangesAsync(Ct);
        }
        var token = await _scenario.AddRegistrationTokenAsync();

        var response = await _scenario.EnrollAsync(Request(token, Hostname, MachineHash, agentReportsRunner));

        Assert.Equal(ServerId, response?.ServerId);
        Assert.Equal(adminChoice, (await _scenario.LoadIncludingRetiredAsync(ServerId)).PipelineRunnerEnabled);
    }

    [Fact]
    public async Task RevivingARetiredServer_KeepsTheAdminsPipelineRunnerChoice()
    {
        await _scenario.SeedAsync();
        await _scenario.RetireAsync();
        var token = await _scenario.AddRegistrationTokenAsync();

        await _scenario.EnrollAsync(Request(token, Hostname, MachineHash, pipelineRunnerAvailable: false));

        Assert.True((await _scenario.LoadIncludingRetiredAsync(ServerId)).PipelineRunnerEnabled);
    }

    [Fact]
    public async Task BrandNewMachine_StillGetsTheRunnerGateFromItsReport_AndRecordsItsIdentity()
    {
        await _scenario.SeedAsync();
        var token = await _scenario.AddRegistrationTokenAsync();
        var otherMachine = new string('b', 64);

        var response = await _scenario.EnrollAsync(Request(token, "brand-new", otherMachine, pipelineRunnerAvailable: false));

        Assert.NotNull(response);
        var server = await _scenario.LoadIncludingRetiredAsync(response.ServerId);
        Assert.NotEqual(ServerId, server.Id);
        Assert.False(server.PipelineRunnerEnabled);
        Assert.Equal(otherMachine, server.MachineIdHash);
    }
}
