// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http.Json;
using Aetheus.Back.Components.AgentUpdate;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class AgentEnrollmentCompatibilityIntegrationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task RegisterCurrentAgent_ImmediatelyExposesProtocolAndRequiredCapabilities()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        using var client = factory.CreateClient();
        var adminToken = await IntegrationAuth.LoginAsAdminAsync(
            client,
            TestContext.Current.CancellationToken);
        IntegrationAuth.SetBearer(client, adminToken);
        var tokenResponse = await client.PostAsJsonAsync(
            "/api/auth/registration-tokens",
            new CreateRegistrationTokenRequest { ExpirationHours = 1 },
            TestContext.Current.CancellationToken);
        tokenResponse.EnsureSuccessStatusCode();
        var registrationToken = await tokenResponse.Content.ReadFromJsonAsync<RegistrationTokenDto>(
            IntegrationJsonOptions.Default,
            TestContext.Current.CancellationToken);
        Assert.NotNull(registrationToken);

        // Reads the current agent version from the compatibility catalogue so enrollment assertions
        // do not depend on a fixed release number.
        var currentAgentVersion = factory.Services
            .GetRequiredService<IAgentReleaseCatalog>().Current.SoftwareVersion;

        IntegrationAuth.SetBearer(client, null);
        var registerResponse = await client.PostAsJsonAsync(
            "/api/auth/register",
            new ServerRegistrationRequest
            {
                RegistrationToken = registrationToken.Token,
                Hostname = $"compatibility-{Guid.NewGuid():N}",
                OsDescription = "Ubuntu 24.04",
                AgentVersion = currentAgentVersion,
                AgentProtocolVersion = AgentProtocol.CurrentVersion,
                AgentCapabilities =
                [
                    AgentCapabilities.SelfUpdate,
                    AgentCapabilities.PipelineBuild,
                    AgentCapabilities.ShellExecution
                ],
                PipelineRunnerAvailable = true
            },
            TestContext.Current.CancellationToken);
        registerResponse.EnsureSuccessStatusCode();
        var registration = await registerResponse.Content.ReadFromJsonAsync<ServerRegistrationResponse>(
            IntegrationJsonOptions.Default,
            TestContext.Current.CancellationToken);
        Assert.NotNull(registration);

        IntegrationAuth.SetBearer(client, adminToken);
        var server = await client.GetFromJsonAsync<ServerDetailDto>(
            $"/api/servers/{registration.ServerId}",
            IntegrationJsonOptions.Default,
            TestContext.Current.CancellationToken);

        Assert.NotNull(server);
        Assert.Equal(AgentProtocol.CurrentVersion, server.AgentProtocolVersion);
        Assert.Contains(AgentCapabilities.SelfUpdate, server.AgentCapabilities);
        Assert.Contains(AgentCapabilities.PipelineBuild, server.AgentCapabilities);
        Assert.Equal(AgentCompatibilityStatus.UpToDate, server.AgentCompatibility!.Status);
        Assert.Equal(AgentCompatibilityReason.Current, server.AgentCompatibility.Reason);
        Assert.DoesNotContain(AgentCapabilities.SelfUpdate, server.AgentCompatibility!.MissingCapabilities);
        Assert.DoesNotContain(AgentCapabilities.PipelineBuild, server.AgentCompatibility.MissingCapabilities);
    }
}
