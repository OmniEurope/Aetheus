// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Environments;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using NSubstitute;
using Environment = Aetheus.Back.Data.Entities.Environment;

namespace Aetheus.Back.Tests;

public class EnvironmentServiceTests
{
    private readonly IEnvironmentRepository _repo = Substitute.For<IEnvironmentRepository>();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly Aetheus.Back.Services.DomainEvents.IDomainEventDispatcher _events = Substitute.For<Aetheus.Back.Services.DomainEvents.IDomainEventDispatcher>();
    private readonly EnvironmentService _sut;

    public EnvironmentServiceTests()
    {
        var encryption = Substitute.For<IEncryptionService>();
        encryption.EncryptValue(Arg.Any<string>()).Returns(call => $"enc({call.Arg<string>()})");
        _sut = new EnvironmentService(_repo,
            _audit, Substitute.For<IEntityChangeNotifier>(), _events, encryption);
    }

    [Fact]
    public async Task GetEnvironmentsAsync_ReturnsPaginatedResult()
    {
        var env = new Environment { Id = 1, Name = "prod", Servers = [] };
        _repo.GetEnvironmentsPagedAsync(null, null, 1, 10, Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns((new List<Environment> { env }, 1));

        var result = await _sut.GetEnvironmentsAsync(null, new PaginationRequest { Page = 1, PageSize = 10 }, ct: TestContext.Current.CancellationToken);

        Assert.Single(result.Items);
        Assert.Equal("prod", result.Items[0].Name);
        Assert.Equal(1, result.TotalCount);
    }

    [Fact]
    public async Task GetEnvironmentAsync_Found_ReturnsDto()
    {
        _repo.GetEnvironmentWithServersAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Environment
            {
                Id = 1,
                Name = "staging",
                Servers = [new EnvironmentServer { ServerId = 10, Server = new Server { Id = 10, Name = "web-01", Status = ServerStatus.Online } }]
            });

        var result = await _sut.GetEnvironmentAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("staging", result.Name);
        Assert.Single(result.Servers);
        Assert.Equal("web-01", result.Servers[0].ServerName);
    }

    [Fact]
    public async Task GetEnvironmentAsync_NotFound_ReturnsNull()
    {
        _repo.GetEnvironmentWithServersAsync(99, Arg.Any<CancellationToken>())
            .Returns((Environment?)null);

        var result = await _sut.GetEnvironmentAsync(99, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task CreateEnvironmentAsync_CreatesAndReturnsDto()
    {
        _repo.AddEnvironmentAsync(Arg.Any<Environment>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _repo.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _repo.GetEnvironmentWithServersAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new Environment { Id = 1, Name = "dev", Servers = [] });

        var result = await _sut.CreateEnvironmentAsync(new CreateEnvironmentRequest
        {
            Name = "dev",
            ServerIds = []
        }, ct: TestContext.Current.CancellationToken);

        Assert.Equal("dev", result.Name);
        await _repo.Received(1).AddEnvironmentAsync(Arg.Any<Environment>(), Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync("Created", "Environment", Arg.Any<int>(), "dev", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateEnvironmentAsync_WithSource_UsesOverridesAndDeepCopiesConfiguration()
    {
        var source = new Environment
        {
            Id = 77,
            Name = "source",
            Description = "source description",
            ProjectId = 8,
            Servers = [new EnvironmentServer { ServerId = 4 }],
            Checks =
            [
                new EnvironmentCheck
                {
                    Id = 1,
                    Name = "readiness",
                    Type = EnvironmentCheckType.StatusCheck,
                    Configuration = "{}",
                    TimeoutSeconds = 30
                }
            ],
            LinkedProjectServers = [new EnvironmentProjectServer { ProjectServerId = 12 }],
            Libraries =
            [
                new VariableLibrary
                {
                    Id = 2,
                    Name = "runtime",
                    Entries = [new VariableLibraryEntry { Id = 3, Key = "REGION", Value = "eu" }]
                }
            ],
            Vaults =
            [
                new Vault
                {
                    Id = 4,
                    Name = "deploy",
                    Secrets = [new VaultSecret { Id = 5, Key = "TOKEN", EncryptedValue = "cipher" }]
                }
            ],
            Pipelines =
            [
                new Pipeline
                {
                    Id = 6,
                    Name = "release",
                    YamlDefinition = "steps: []",
                    TriggerType = PipelineTriggerType.Manual,
                    CreatedByUsername = "owner"
                }
            ]
        };
        Environment? added = null;
        _repo.GetEnvironmentForDuplicationAsync(77, Arg.Any<CancellationToken>()).Returns(source);
        _repo.When(repository => repository.AddEnvironmentAsync(
                Arg.Any<Environment>(), Arg.Any<CancellationToken>()))
            .Do(call => added = call.Arg<Environment>());
        _repo.GetEnvironmentWithServersAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => added!);

        var result = await _sut.CreateEnvironmentAsync(new CreateEnvironmentRequest
        {
            Name = "target",
            Description = "edited before creation",
            ProjectId = 3,
            SourceEnvironmentId = 77,
            ServerIds = [9]
        }, ct: TestContext.Current.CancellationToken);

        Assert.Equal("target", result.Name);
        Assert.NotNull(added);
        Assert.Equal(3, added.ProjectId);
        Assert.Equal("edited before creation", added.Description);
        Assert.Equal(9, Assert.Single(added.Servers).ServerId);
        Assert.NotSame(source.Checks[0], Assert.Single(added.Checks));
        Assert.Equal("readiness", added.Checks[0].Name);
        Assert.Equal(12, Assert.Single(added.LinkedProjectServers).ProjectServerId);
        // The structure is copied (names), never the values.
        Assert.NotSame(source.Libraries[0], Assert.Single(added.Libraries));
        var entry = Assert.Single(added.Libraries[0].Entries);
        Assert.Equal("REGION", entry.Key);
        Assert.Equal(string.Empty, entry.Value);
        Assert.Contains("Fill them in", added.Libraries[0].Description, StringComparison.Ordinal);
        Assert.NotSame(source.Vaults[0], Assert.Single(added.Vaults));
        var secret = Assert.Single(added.Vaults[0].Secrets);
        Assert.Equal("TOKEN", secret.Key);
        Assert.Equal("enc()", secret.EncryptedValue);
        Assert.Contains("Fill them in", added.Vaults[0].Description, StringComparison.Ordinal);
        Assert.NotSame(source.Pipelines[0], Assert.Single(added.Pipelines));
        Assert.Equal("steps: []", added.Pipelines[0].YamlDefinition);
        // The copied pipelines are written into the project's repository, as on update.
        _events.Received(1).Publish(Arg.Is<EnvironmentLinkedToProjectEvent>(e =>
            e.EnvironmentName == "target" && e.ProjectId == 3));
    }

    [Fact]
    public async Task R1_29_CreateEnvironmentAsync_WithoutCopy_PublishesNothing()
    {
        Environment? added = null;
        _repo.When(repository => repository.AddEnvironmentAsync(
                Arg.Any<Environment>(), Arg.Any<CancellationToken>()))
            .Do(call => added = call.Arg<Environment>());
        _repo.GetEnvironmentWithServersAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => added!);

        await _sut.CreateEnvironmentAsync(new CreateEnvironmentRequest
        {
            Name = "fresh",
            ProjectId = 3,
            ServerIds = []
        }, ct: TestContext.Current.CancellationToken);

        _events.DidNotReceive().Publish(Arg.Any<EnvironmentLinkedToProjectEvent>());
    }

    [Fact]
    public async Task UpdateEnvironmentAsync_NotFound_ReturnsNull()
    {
        _repo.FindEnvironmentAsync(99, Arg.Any<CancellationToken>())
            .Returns((Environment?)null);

        var result = await _sut.UpdateEnvironmentAsync(99, new UpdateEnvironmentRequest { Name = "x", ServerIds = [] }, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task UpdateEnvironmentAsync_LinkedToAProject_PublishesTheEvent_PipelinesCopiesOn()
    {
        // Environments publishes a background copy request handled by
        // EnvironmentPipelinesCopyHandler, keeping it independent of the Pipelines Git service.
        var env = new Environment { Id = 1, Name = "staging", Servers = [] };
        _repo.FindEnvironmentAsync(1, Arg.Any<CancellationToken>()).Returns(env);
        _repo.GetEnvironmentWithServersAsync(1, Arg.Any<CancellationToken>()).Returns(env);

        await _sut.UpdateEnvironmentAsync(1, new UpdateEnvironmentRequest { Name = "staging", ProjectId = 4, ServerIds = [] }, ct: TestContext.Current.CancellationToken);

        _events.Received(1).Publish(Arg.Is<EnvironmentLinkedToProjectEvent>(e =>
            e.EnvironmentId == 1 && e.EnvironmentName == "staging" && e.ProjectId == 4));
    }

    [Fact]
    public async Task UpdateEnvironmentAsync_NotLinked_PublishesNothing()
    {
        var env = new Environment { Id = 1, Name = "staging", Servers = [] };
        _repo.FindEnvironmentAsync(1, Arg.Any<CancellationToken>()).Returns(env);
        _repo.GetEnvironmentWithServersAsync(1, Arg.Any<CancellationToken>()).Returns(env);

        await _sut.UpdateEnvironmentAsync(1, new UpdateEnvironmentRequest { Name = "staging", ServerIds = [] }, ct: TestContext.Current.CancellationToken);

        _events.DidNotReceive().Publish(Arg.Any<EnvironmentLinkedToProjectEvent>());
    }

    [Fact]
    public async Task UpdateEnvironmentAsync_Found_UpdatesAndReturns()
    {
        var env = new Environment { Id = 1, Name = "old", Servers = [] };
        _repo.FindEnvironmentAsync(1, Arg.Any<CancellationToken>())
            .Returns(env);
        _repo.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _repo.GetEnvironmentWithServersAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Environment { Id = 1, Name = "new", Servers = [] });

        var result = await _sut.UpdateEnvironmentAsync(1, new UpdateEnvironmentRequest { Name = "new", ServerIds = [] }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        await _audit.Received(1).LogAsync("Updated", "Environment", 1, "new", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteEnvironmentAsync_NotFound_ReturnsFalse()
    {
        _repo.FindEnvironmentAsync(99, Arg.Any<CancellationToken>())
            .Returns((Environment?)null);

        var result = await _sut.DeleteEnvironmentAsync(99, ct: TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    [Fact]
    public async Task DeleteEnvironmentAsync_Found_DeletesAndReturnsTrue()
    {
        _repo.FindEnvironmentAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Environment { Id = 1, Name = "prod" });
        _repo.RemoveEnvironmentAsync(Arg.Any<Environment>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var result = await _sut.DeleteEnvironmentAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.True(result);
        await _repo.Received(1).RemoveEnvironmentAsync(Arg.Any<Environment>(), Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync("Deleted", "Environment", 1, "prod", Arg.Any<CancellationToken>());
    }
}
