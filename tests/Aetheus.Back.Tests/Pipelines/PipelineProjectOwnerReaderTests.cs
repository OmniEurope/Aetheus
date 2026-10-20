// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>The project-owner port (AUD-07, suite) answers what the pipeline ownership resolver reads
/// (its queries' edge cases are covered by <c>PipelineServerResolverTests</c> and
/// <c>ProjectOwnerResolutionTests</c>), and the IPipelineRepository facade, which delegates to it,
/// answers the same.</summary>
public sealed class PipelineProjectOwnerReaderTests : IDisposable
{
    private readonly AppDbContext _db = new(
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheFacade_AnswersExactlyWhatThePortAnswers()
    {
        _db.Users.AddRange(
            new User { Id = 1, Username = "owner", PasswordHash = "x", IsActive = true },
            new User { Id = 2, Username = "disabled", PasswordHash = "x", IsActive = false });
        _db.OrganizationMembers.Add(new OrganizationMember { OrganizationId = 7, UserId = 1, Role = OrganizationRole.Owner });
        _db.Projects.Add(new Project { Id = 10, Name = "app", OrganizationId = 7 });
        await _db.SaveChangesAsync(Ct);
        _db.ChangeTracker.Clear();
        IPipelineProjectOwnerReader port = new PipelineProjectOwnerRepository(_db);
        IPipelineProjectOwnerReader facade = new PipelineRepository(
            _db, TimeProvider.System, NullLogger<PipelineRepository>.Instance,
            new PipelineTaskLifecycleRepository(_db, TimeProvider.System), new PipelineRunLineageRepository(_db));

        Assert.Equal("owner", await port.GetProjectOwnerUsernameAsync(10, Ct));
        Assert.Equal(await port.GetProjectOwnerUsernameAsync(10, Ct), await facade.GetProjectOwnerUsernameAsync(10, Ct));
        Assert.Equal(await port.GetProjectOwnerUsernameAsync(404, Ct), await facade.GetProjectOwnerUsernameAsync(404, Ct));
        Assert.True(await port.IsActiveUsernameAsync("owner", Ct));
        foreach (var username in new[] { "owner", "disabled", "absent" })
            Assert.Equal(await port.IsActiveUsernameAsync(username, Ct), await facade.IsActiveUsernameAsync(username, Ct));
    }

    public void Dispose() => _db.Dispose();
}
