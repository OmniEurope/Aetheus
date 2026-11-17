// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.VariableLibraries;
using Aetheus.Back.Components.Vaults;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Tests.Repositories;

/// <summary>R-25: finding which library or vault already holds a key reads key names only, and only through the project
/// that asks (multi-tenant): a vault of another project, or of another project's environment, is never seen.</summary>
public sealed class KeyHolderLookupTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly AppDbContext _db;
    private readonly VaultRepository _vaults;
    private readonly VariableLibraryRepository _libraries;
    private readonly int _mine;
    private readonly int _other;
    private readonly int _otherEnvironment;

    public KeyHolderLookupTests()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        _vaults = new VaultRepository(_db, TimeProvider.System);
        _libraries = new VariableLibraryRepository(_db);
        var mine = new Project { Name = "mine" };
        var other = new Project { Name = "other" };
        _db.Projects.AddRange(mine, other);
        _db.SaveChanges();
        _mine = mine.Id;
        _other = other.Id;
        var environment = new Aetheus.Back.Data.Entities.Environment { Name = "prod", ProjectId = _other };
        _db.Environments.Add(environment);
        _db.SaveChanges();
        _otherEnvironment = environment.Id;
    }

    public void Dispose() => _db.Dispose();

    private static VaultSecret Secret(string key) => new() { Key = key, EncryptedValue = "cipher" };

    [Fact]
    public async Task Vaults_OnlyTheAskingProjectsOwnAreSeen_AndTheProjectsOwnComeFirst()
    {
        _db.Vaults.AddRange(
            new Vault { Name = "global", Secrets = [Secret("JWT_KEY")] },
            new Vault { Name = "w1.prod", ProjectId = _mine, Secrets = [Secret("JWT_KEY"), Secret("DB_PASSWORD"), Secret("UNRELATED")] },
            new Vault { Name = "other.prod", ProjectId = _other, Secrets = [Secret("JWT_KEY")] },
            new Vault { Name = "other.env", EnvironmentId = _otherEnvironment, Secrets = [Secret("JWT_KEY")] });
        await _db.SaveChangesAsync(Ct);

        var found = await _vaults.FindKeyHoldersWithCrossAccessAsync(["JWT_KEY", "DB_PASSWORD"], _mine, Ct);

        Assert.Equal(
            ["global:JWT_KEY", "w1.prod:DB_PASSWORD", "w1.prod:JWT_KEY"],
            found.Select(holder => $"{holder.Name}:{holder.Key}").Order(StringComparer.Ordinal));
        // The project's own vault comes first, the shared one after it.
        Assert.Equal(["w1.prod", "w1.prod", "global"], found.Select(holder => holder.Name));
    }

    [Fact]
    public async Task Libraries_OnlyTheAskingProjectsOwnAreSeen()
    {
        _db.VariableLibraries.AddRange(
            new VariableLibrary { Name = "w1.share", ProjectId = _mine, Entries = [new VariableLibraryEntry { Key = "API_HOST", Value = "v" }] },
            new VariableLibrary { Name = "other.share", ProjectId = _other, Entries = [new VariableLibraryEntry { Key = "API_HOST", Value = "v" }] },
            new VariableLibrary { Name = "other.env", EnvironmentId = _otherEnvironment, Entries = [new VariableLibraryEntry { Key = "API_HOST", Value = "v" }] });
        await _db.SaveChangesAsync(Ct);

        var found = await _libraries.FindKeyHoldersWithCrossAccessAsync(["API_HOST"], _mine, Ct);

        var holder = Assert.Single(found);
        Assert.Equal(("w1.share", "API_HOST"), (holder.Name, holder.Key));
    }

    [Fact]
    public async Task AKeyNobodyHolds_ReturnsNothing()
    {
        _db.Vaults.Add(new Vault { Name = "w1.prod", ProjectId = _mine, Secrets = [Secret("OTHER_KEY")] });
        await _db.SaveChangesAsync(Ct);

        Assert.Empty(await _vaults.FindKeyHoldersWithCrossAccessAsync(["JWT_KEY"], _mine, Ct));
        Assert.Empty(await _libraries.FindKeyHoldersWithCrossAccessAsync(["JWT_KEY"], _mine, Ct));
    }
}
