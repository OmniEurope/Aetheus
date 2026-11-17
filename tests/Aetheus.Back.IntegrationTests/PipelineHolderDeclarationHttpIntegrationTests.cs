// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;
using Aetheus.Back.Components.VariableLibraries;
using Aetheus.Back.Components.Vaults;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// R-24 through the real HTTP pipeline, JWT included, on real PostgreSQL: a pipeline reads a key that a vault of its
/// project holds but the pipeline does not declare; the readiness says so; <c>POST api/pipelines/{id}/declared-holders</c>
/// adds the vault to the definition (text edit, comments kept, committed as an update); the readiness re-read no longer
/// lists the key; a user without Write is refused and nothing is written. What is not real: the project has no Git
/// repository, so the definition is saved to the database only (the commit to Git is the update path proved by
/// <c>PipelineScheduleWriteHttpIntegrationTests</c>).
/// </summary>
public sealed class PipelineHolderDeclarationHttpIntegrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Key = "R24_DB_PASSWORD";
    private const string Vault = "r24-proof-vault";

    private const string Yaml = """
        # the deploy of this project
        name: r24-proof
        trigger: manual # by hand
        stages:
          - name: Deploy
            steps:
              - name: Run
                shell: echo $(R24_DB_PASSWORD)
        """;

    private static readonly IntegrationUsers.Grant[] Writer =
    [
        new(ResourceType.Pipeline, null, Permission.Write),
        new(ResourceType.Pipeline, null, Permission.Read),
        new(ResourceType.Project, null, Permission.Write),
        new(ResourceType.Vault, null, Permission.Read),
        new(ResourceType.VariableLibrary, null, Permission.Read)
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Scene(int OrganizationId, int ProjectId, int PipelineId, int VaultId);

    private static async Task<Scene> SeedAsync(AetheusWebApplicationFactory factory, string yaml = Yaml)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var organization = new Organization { Name = $"r24-{Guid.NewGuid():N}", Slug = $"r{Guid.NewGuid():N}"[..20] };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(Ct);
        var project = new Project { Name = $"r24-{Guid.NewGuid():N}", OrganizationId = organization.Id };
        db.Projects.Add(project);
        await db.SaveChangesAsync(Ct);
        var pipeline = new Pipeline
        {
            ProjectId = project.Id,
            Name = "r24-proof",
            TriggerType = PipelineTriggerType.Manual,
            CreatedByUsername = "owner",
            YamlDefinition = yaml
        };
        db.Pipelines.Add(pipeline);
        await db.SaveChangesAsync(Ct);
        var vaults = scope.ServiceProvider.GetRequiredService<IVaultService>();
        var vault = await vaults.CreateVaultAsync(new CreateVaultRequest { Name = Vault, ProjectId = project.Id }, Ct);
        await vaults.CreateSecretAsync(vault.Id, new CreateVaultSecretRequest { Key = Key, Value = "r24-secret-value" }, Ct);
        return new Scene(organization.Id, project.Id, pipeline.Id, vault.Id);
    }

    private static Task<HttpResponseMessage> DeclareAsync(
        HttpClient client, int pipelineId, string name = Vault,
        PipelineSetupReadinessContainerKind kind = PipelineSetupReadinessContainerKind.Vault) =>
        client.PostAsJsonAsync($"/api/pipelines/{pipelineId}/declared-holders",
            new DeclarePipelineHolderRequest { Kind = kind, Name = name }, IntegrationJsonOptions.Default, Ct);

    private static async Task<PipelineSetupReadinessDto> ReadinessAsync(HttpClient client, int projectId) =>
        await client.GetFromJsonAsync<PipelineSetupReadinessDto>(
            $"/api/pipelines/setup/project-readiness?projectId={projectId}", IntegrationJsonOptions.Default, Ct)
        ?? throw new InvalidOperationException("The readiness came back empty.");

    private static async Task<string> StoredYamlAsync(AetheusWebApplicationFactory factory, int id)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Pipelines.AsNoTracking()
            .Where(row => row.Id == id).Select(row => row.YamlDefinition).SingleAsync(Ct);
    }

    private static async Task<List<string>> AuditActionsAsync(AetheusWebApplicationFactory factory, int id)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().AuditLogs.AsNoTracking()
            .Where(entry => entry.EntityType == "Pipeline" && entry.EntityId == id).Select(entry => entry.Action).ToListAsync(Ct);
    }

    [Fact]
    public async Task ADeclaredVault_MakesTheKeyDefined_TheReadinessDropsIt_AndTheTextEditKeepsTheComments()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var scene = await SeedAsync(factory);
        var writer = await IntegrationUsers.CreateAsync(factory.Services, "r24-writer", Writer, scene.OrganizationId);
        using var client = await IntegrationUsers.LoginClientAsync(factory, writer.Username);

        var before = await ReadinessAsync(client, scene.ProjectId);
        var undefined = Assert.Single(before.Checks, check => check.Kind == PipelineSetupReadinessKind.UndefinedVariables);
        var row = Assert.Single(undefined.UndefinedVariables);
        Assert.Equal((Key, scene.VaultId, Vault, PipelineSetupReadinessContainerKind.Vault, scene.PipelineId),
            (row.Name, row.ExistingHolderId, row.ExistingHolderName, row.ExistingHolderKind, row.PipelineId));

        using var response = await DeclareAsync(client, scene.PipelineId);

        Assert.True(response.IsSuccessStatusCode, $"POST gave {response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
        // The inserted lines follow the document's own line ending; the raw literal above has the checkout's (CRLF on
        // a Windows working copy, LF elsewhere), so the expectation is built with it.
        var nl = Yaml.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var expected = Yaml.Replace("trigger: manual # by hand", $"vaults:{nl}  - r24-proof-vault{nl}trigger: manual # by hand", StringComparison.Ordinal);
        Assert.Equal(expected, (await response.Content.ReadFromJsonAsync<PipelineDto>(IntegrationJsonOptions.Default, Ct))!.YamlDefinition);
        Assert.Equal(expected, await StoredYamlAsync(factory, scene.PipelineId));
        Assert.Contains("HolderDeclared", await AuditActionsAsync(factory, scene.PipelineId));
        Assert.Contains("Updated", await AuditActionsAsync(factory, scene.PipelineId));

        var after = await ReadinessAsync(client, scene.ProjectId);
        Assert.DoesNotContain(after.Checks, check => check.Kind == PipelineSetupReadinessKind.UndefinedVariables);

        // Declared twice changes nothing and still answers.
        using var again = await DeclareAsync(client, scene.PipelineId);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(expected, await StoredYamlAsync(factory, scene.PipelineId));
    }

    [Fact]
    public async Task AUserWithoutWrite_IsRefused_AndNothingIsWritten()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var scene = await SeedAsync(factory);
        var reader = await IntegrationUsers.CreateAsync(factory.Services, "r24-reader",
        [
            new(ResourceType.Pipeline, null, Permission.Read),
            new(ResourceType.Project, null, Permission.Read),
            new(ResourceType.Vault, null, Permission.Read)
        ], scene.OrganizationId);
        var noProjectWrite = await IntegrationUsers.CreateAsync(factory.Services, "r24-nopw",
        [
            new(ResourceType.Pipeline, null, Permission.Write),
            new(ResourceType.Pipeline, null, Permission.Read),
            new(ResourceType.Project, null, Permission.Read),
            new(ResourceType.Vault, null, Permission.Read)
        ], scene.OrganizationId);
        var noVaultRead = await IntegrationUsers.CreateAsync(factory.Services, "r24-novr",
        [
            new(ResourceType.Pipeline, null, Permission.Write),
            new(ResourceType.Pipeline, null, Permission.Read),
            new(ResourceType.Project, null, Permission.Write)
        ], scene.OrganizationId);
        var writer = await IntegrationUsers.CreateAsync(factory.Services, "r24-ok", Writer, scene.OrganizationId);

        foreach (var username in new[] { reader.Username, noProjectWrite.Username, noVaultRead.Username })
        {
            using var client = await IntegrationUsers.LoginClientAsync(factory, username);
            using var response = await DeclareAsync(client, scene.PipelineId);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{username} got {response.StatusCode}");
        }

        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await DeclareAsync(anonymous, scene.PipelineId)).StatusCode);
        Assert.Equal(Yaml, await StoredYamlAsync(factory, scene.PipelineId));
        Assert.DoesNotContain("HolderDeclared", await AuditActionsAsync(factory, scene.PipelineId));

        // The positive control: the same request passes with the rights of the update route and Write on the project.
        using var allowed = await IntegrationUsers.LoginClientAsync(factory, writer.Username);
        Assert.Equal(HttpStatusCode.OK, (await DeclareAsync(allowed, scene.PipelineId)).StatusCode);
        Assert.Contains("r24-proof-vault", await StoredYamlAsync(factory, scene.PipelineId), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHolderTheProjectDoesNotHold_OrAYamlItCannotKeep_IsA400WithItsReason_AndNothingIsWritten()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var scene = await SeedAsync(factory);
        var odd = await SeedAsync(factory, Yaml.Replace("trigger: manual", "vaults: single", StringComparison.Ordinal));
        var writer = await IntegrationUsers.CreateAsync(factory.Services, "r24-w400", Writer, scene.OrganizationId);
        var oddWriter = await IntegrationUsers.CreateAsync(factory.Services, "r24-w400b", Writer, odd.OrganizationId);
        using var client = await IntegrationUsers.LoginClientAsync(factory, writer.Username);
        using var oddClient = await IntegrationUsers.LoginClientAsync(factory, oddWriter.Username);

        using var missing = await DeclareAsync(client, scene.PipelineId, "someone-elses-vault");
        using var wrongKind = await DeclareAsync(client, scene.PipelineId, Vault, PipelineSetupReadinessContainerKind.Library);
        using var unkeepable = await DeclareAsync(oddClient, odd.PipelineId);

        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains("someone-elses-vault", await missing.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, wrongKind.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unkeepable.StatusCode);
        Assert.Contains("vaults", await unkeepable.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal(Yaml, await StoredYamlAsync(factory, scene.PipelineId));
        Assert.Equal(Yaml.Replace("trigger: manual", "vaults: single", StringComparison.Ordinal), await StoredYamlAsync(factory, odd.PipelineId));
    }

    [Fact]
    public async Task AVaultOfAnotherOrganization_CannotBeDeclared()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var mine = await SeedAsync(factory);
        var theirs = await SeedAsync(factory);
        var writer = await IntegrationUsers.CreateAsync(factory.Services, "r24-cross", Writer, mine.OrganizationId);
        using var client = await IntegrationUsers.LoginClientAsync(factory, writer.Username);

        // Their pipeline: no right in their organization. The vault of theirs, by the same name, is no vault of my project.
        using var onTheirPipeline = await DeclareAsync(client, theirs.PipelineId);
        Assert.Equal(HttpStatusCode.Forbidden, onTheirPipeline.StatusCode);
        Assert.Equal(Yaml, await StoredYamlAsync(factory, theirs.PipelineId));
    }
}
