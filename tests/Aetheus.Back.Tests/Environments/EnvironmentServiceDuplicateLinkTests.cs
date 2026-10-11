// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Environments;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Environment = Aetheus.Back.Data.Entities.Environment;

namespace Aetheus.Back.Tests;

public class EnvironmentServiceDuplicateLinkTests
{
    private readonly IEnvironmentRepository _repo = Substitute.For<IEnvironmentRepository>();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly IEntityChangeNotifier _notifier = Substitute.For<IEntityChangeNotifier>();
    private readonly Aetheus.Back.Services.DomainEvents.IDomainEventDispatcher _events =
        Substitute.For<Aetheus.Back.Services.DomainEvents.IDomainEventDispatcher>();
    private readonly EnvironmentService _sut;

    public EnvironmentServiceDuplicateLinkTests()
    {
        var encryption = Substitute.For<IEncryptionService>();
        encryption.EncryptValue(Arg.Any<string>()).Returns(call => $"enc({call.Arg<string>()})");
        _sut = new EnvironmentService(_repo, _audit, _notifier, _events, encryption);
    }

    private static Environment EmptyEnv(string name, int projectId = 1, int id = 0) => new()
    {
        Id = id,
        Name = name,
        ProjectId = projectId,
        Description = "d",
        Servers = [],
        Checks = [],
        LinkedProjectServers = [],
        Libraries = [],
        Vaults = [],
        Pipelines = []
    };

    // --- DuplicateEnvironmentAsync ---

    [Fact]
    public async Task Duplicate_SourceNotFound_ReturnsNull()
    {
        _repo.GetEnvironmentForDuplicationAsync(9, Arg.Any<CancellationToken>()).Returns((Environment?)null);

        Assert.Null(await _sut.DuplicateEnvironmentAsync(9, null, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Duplicate_NoNameClash_UsesCopySuffixAndPersists()
    {
        _repo.GetEnvironmentForDuplicationAsync(1, Arg.Any<CancellationToken>()).Returns(EmptyEnv("prod"));
        _repo.NameExistsInProjectAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(false);
        _repo.GetEnvironmentWithServersAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(EmptyEnv("prod (copy)"));

        var result = await _sut.DuplicateEnvironmentAsync(1, null, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("prod (copy)", result.Name);
        await _repo.Received(1).AddEnvironmentAsync(Arg.Is<Environment>(e => e.Name == "prod (copy)"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task R1_23_Duplicate_DoesNotCopyTheSourceDescription()
    {
        var source = EmptyEnv("prod");
        source.Description = "Production - auto-deploy Aetheus";
        _repo.GetEnvironmentForDuplicationAsync(1, Arg.Any<CancellationToken>()).Returns(source);
        _repo.NameExistsInProjectAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(false);
        _repo.GetEnvironmentWithServersAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(EmptyEnv("prod (copy)"));

        await _sut.DuplicateEnvironmentAsync(1, null, ct: TestContext.Current.CancellationToken);

        await _repo.Received(1).AddEnvironmentAsync(
            Arg.Is<Environment>(e => e.Description == string.Empty), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Duplicate_NameClash_FallsBackToNumberedSuffix()
    {
        _repo.GetEnvironmentForDuplicationAsync(1, Arg.Any<CancellationToken>()).Returns(EmptyEnv("prod"));
        // "prod (copy)" exists, "prod (2)" free
        _repo.NameExistsInProjectAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(true, false);
        _repo.GetEnvironmentWithServersAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(EmptyEnv("prod (2)"));

        var result = await _sut.DuplicateEnvironmentAsync(1, null, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        await _repo.Received(1).AddEnvironmentAsync(Arg.Is<Environment>(e => e.Name == "prod (2)"), Arg.Any<CancellationToken>());
    }

    // --- LinkProjectServerAsync ---

    [Fact]
    public async Task Link_EnvironmentNotFound_ReturnsFalse()
    {
        _repo.FindEnvironmentAsync(9, Arg.Any<CancellationToken>()).Returns((Environment?)null);

        Assert.False(await _sut.LinkProjectServerAsync(9, 5, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Link_AlreadyLinked_ReturnsTrueWithoutAdding()
    {
        _repo.FindEnvironmentAsync(1, Arg.Any<CancellationToken>()).Returns(EmptyEnv("prod", id: 1));
        _repo.LinkExistsAsync(1, 5, Arg.Any<CancellationToken>()).Returns(true);

        Assert.True(await _sut.LinkProjectServerAsync(1, 5, ct: TestContext.Current.CancellationToken));
        await _repo.DidNotReceive().AddLinkAsync(Arg.Any<EnvironmentProjectServer>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Link_New_AddsLinkAuditsAndReturnsTrue()
    {
        _repo.FindEnvironmentAsync(1, Arg.Any<CancellationToken>()).Returns(EmptyEnv("prod", id: 1));
        _repo.LinkExistsAsync(1, 5, Arg.Any<CancellationToken>()).Returns(false);

        Assert.True(await _sut.LinkProjectServerAsync(1, 5, ct: TestContext.Current.CancellationToken));
        await _repo.Received(1).AddLinkAsync(Arg.Is<EnvironmentProjectServer>(l => l.ProjectServerId == 5), Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync("LinkedProjectServer", "Environment", 1, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unlink_NotLinked_ReturnsFalse()
    {
        _repo.LinkExistsAsync(1, 5, Arg.Any<CancellationToken>()).Returns(false);

        Assert.False(await _sut.UnlinkProjectServerAsync(1, 5, ct: TestContext.Current.CancellationToken));
        await _repo.DidNotReceive().RemoveLinkAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unlink_Linked_RemovesAuditsAndReturnsTrue()
    {
        _repo.LinkExistsAsync(1, 5, Arg.Any<CancellationToken>()).Returns(true);

        Assert.True(await _sut.UnlinkProjectServerAsync(1, 5, ct: TestContext.Current.CancellationToken));
        await _repo.Received(1).RemoveLinkAsync(1, 5, Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync("UnlinkedProjectServer", "Environment", 1, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task R1_29_Duplicate_CopiesNamesNotValues_AndWritesThePipelinesToTheProject()
    {
        var source = EmptyEnv("prod", projectId: 4);
        source.Libraries = [new VariableLibrary { Name = "runtime", Description = "prod hosts",
            Entries = [new VariableLibraryEntry { Key = "HOST", Value = "prod.example.org" }] }];
        source.Vaults = [new Vault { Name = "deploy",
            Secrets = [new VaultSecret { Key = "DB_PASSWORD", EncryptedValue = "source-cipher" }] }];
        source.Pipelines = [new Pipeline { Name = "release", YamlDefinition = "name: release" }];
        Environment? added = null;
        _repo.GetEnvironmentForDuplicationAsync(1, Arg.Any<CancellationToken>()).Returns(source);
        _repo.NameExistsInProjectAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(false);
        _repo.When(repository => repository.AddEnvironmentAsync(Arg.Any<Environment>(), Arg.Any<CancellationToken>()))
            .Do(call => added = call.Arg<Environment>());
        _repo.GetEnvironmentWithServersAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(_ => added!);

        await _sut.DuplicateEnvironmentAsync(1, 9, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(added);
        var entry = Assert.Single(Assert.Single(added.Libraries).Entries);
        Assert.Equal(("HOST", string.Empty), (entry.Key, entry.Value));
        Assert.DoesNotContain("prod hosts", added.Libraries[0].Description, StringComparison.Ordinal);
        var secret = Assert.Single(Assert.Single(added.Vaults).Secrets);
        Assert.Equal("DB_PASSWORD", secret.Key);
        Assert.NotEqual("source-cipher", secret.EncryptedValue);
        Assert.Equal("enc()", secret.EncryptedValue);
        _events.Received(1).Publish(Arg.Is<EnvironmentLinkedToProjectEvent>(e =>
            e.EnvironmentName == "prod (copy)" && e.ProjectId == 9));
    }
}
