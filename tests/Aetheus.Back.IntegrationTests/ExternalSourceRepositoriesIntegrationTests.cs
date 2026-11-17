// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Git;
using Aetheus.Back.Data.Entities;
using Aetheus.Shared.Components.Git;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// Recette R-69 (OE2) on PostgreSQL: the rows an empty internal repository and an external repository leave in a project, read
/// back through the real repository, the real mapper and the reading the production analysis and the project overview share
/// (<see cref="ProjectSourceRepositories"/>). Real: the schema, the queries, the includes that decide
/// "additional source". The refusal to attach beside an empty internal repository is covered by <c>ExternalRepoServiceTests</c>.
/// </summary>
public sealed class ExternalSourceRepositoriesIntegrationTests(PostgresFixture fixture) : RelationalTestBase(fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<int> SeedProjectAsync(string name)
    {
        await using var db = NewContext();
        var organization = new Organization { Name = $"Org {name}", Slug = $"org-{name}", Description = "d" };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(Ct);
        var project = new Project { Name = name, Description = "d", OrganizationId = organization.Id };
        db.Projects.Add(project);
        await db.SaveChangesAsync(Ct);
        return project.Id;
    }

    private async Task AddInternalRepositoryAsync(int projectId, bool withCode)
    {
        await using var db = NewContext();
        var connection = new GitConnection
        {
            ProjectId = projectId,
            ProviderType = GitProviderType.AetheusGit,
            OwnerOrGroup = projectId.ToString(),
            RepositoryName = "oe2"
        };
        db.GitConnections.Add(connection);
        await db.SaveChangesAsync(Ct);
        db.GitInternalRepos.Add(new GitInternalRepo
        {
            ProjectId = projectId,
            Name = "oe2",
            Slug = "oe2",
            GitConnectionId = connection.Id,
            IsEmpty = !withCode,
            LastPushAt = withCode ? DateTime.UtcNow : null
        });
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>The external repository and its mirror holding code; <paramref name="asProjectSource"/> is what attaching it on
    /// a project without a repository does.</summary>
    private async Task AddExternalMirrorAsync(int projectId, bool asProjectSource)
    {
        await using var db = NewContext();
        var connection = new GitConnection
        {
            ProjectId = projectId,
            ProviderType = GitProviderType.GitHub,
            OwnerOrGroup = "omnieurope",
            RepositoryName = "OmniEurope.Blazor",
            MirrorStatus = GitMirrorStatus.Ready
        };
        db.GitConnections.Add(connection);
        await db.SaveChangesAsync(Ct);
        db.GitInternalRepos.Add(new GitInternalRepo
        {
            ProjectId = projectId,
            Name = "OmniEurope.Blazor",
            Slug = "omnieurope-blazor",
            GitConnectionId = connection.Id,
            IsEmpty = false,
            LastPushAt = DateTime.UtcNow
        });
        if (asProjectSource)
        {
            var project = await db.Projects.FindAsync([projectId], Ct);
            project!.GitConnectionId = connection.Id;
        }
        await db.SaveChangesAsync(Ct);
    }

    private async Task<ProjectSourceRepositories> ReadSourceAsync(int projectId)
    {
        await using var db = NewContext();
        var rows = await new GitLightRepository(db).GetByProjectAsync(projectId, Ct);
        return ProjectSourceRepositories.Of(rows.Select(row => GitLightMapper.MapRepoToDto(row, "https://example/git")));
    }

    [Fact]
    public async Task AnExternalRepositoryBesideAnEmptyInternalOne_LeavesTheEmptyOneAsTheSource_ThatTheReadingNames()
    {
        await ResetAndMigrateAsync();
        var projectId = await SeedProjectAsync("OE2");
        await AddInternalRepositoryAsync(projectId, withCode: false);
        await AddExternalMirrorAsync(projectId, asProjectSource: false);

        var source = await ReadSourceAsync(projectId);

        Assert.Null(source.Readable);
        Assert.True(source.ExternalBesideEmpty);
        Assert.True(source.ExternalSource);
        Assert.Equal(["oe2"], source.Own.Select(repository => repository.Slug));
    }

    [Fact]
    public async Task AnExternalRepositoryThatIsTheProjectSource_IsTheOneRead_WithItsCode()
    {
        await ResetAndMigrateAsync();
        var projectId = await SeedProjectAsync("OE2b");
        await AddExternalMirrorAsync(projectId, asProjectSource: true);

        var source = await ReadSourceAsync(projectId);

        Assert.Equal("omnieurope-blazor", source.Readable?.Slug);
        Assert.True(source.OwnIsExternal);
        Assert.False(source.ExternalBesideEmpty);
    }
}
