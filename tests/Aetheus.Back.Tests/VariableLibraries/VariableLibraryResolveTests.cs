// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.VariableLibraries;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class VariableLibraryResolveTests
{
    private readonly IVariableLibraryRepository _repo = Substitute.For<IVariableLibraryRepository>();
    private readonly VariableLibraryService _sut;

    public VariableLibraryResolveTests() =>
        _sut = new VariableLibraryService(_repo, Substitute.For<IDbTransactionScope>(),
            Substitute.For<IAuditService>(), Substitute.For<IEntityChangeNotifier>(),
            TimeProvider.System, Substitute.For<IMemoryCache>());

    private static VariableLibrary Lib(int? projectId, int? envId, int? projectServerId, string key, string value, int id = 0) =>
        new()
        {
            Id = id,
            ProjectId = projectId,
            EnvironmentId = envId,
            ProjectServerId = projectServerId,
            Entries = [new VariableLibraryEntry { Key = key, Value = value }]
        };

    [Fact]
    public async Task ResolveWithCrossAccess_AppliesScopePrecedence()
    {
        // Same key at every scope: project-server scope must win (applied last).
        _repo.FindByNamesWithCrossAccessAsync(Arg.Any<List<string>>(), 1, Arg.Any<CancellationToken>()).Returns(
        [
            Lib(null, null, null, "ENV", "global"),
            Lib(1, null, null, "ENV", "project"),
            Lib(null, 2, null, "ENV", "environment"),
            Lib(null, null, 3, "ENV", "projectserver")
        ]);

        var result = await _sut.ResolveLibrariesWithCrossAccessAsync(["any"], 1, ct: TestContext.Current.CancellationToken);

        Assert.Equal("projectserver", result["ENV"]);
    }

    [Fact]
    public async Task ResolveWithCrossAccess_DistinctKeys_AllPresent_CaseInsensitive()
    {
        _repo.FindByNamesWithCrossAccessAsync(Arg.Any<List<string>>(), 1, Arg.Any<CancellationToken>()).Returns(
        [
            Lib(null, null, null, "A", "1"),
            Lib(1, null, null, "B", "2")
        ]);

        var result = await _sut.ResolveLibrariesWithCrossAccessAsync(["any"], 1, ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        Assert.Equal("1", result["a"]); // OrdinalIgnoreCase comparer
    }

    /// <summary>R2-27: the summary links an empty variable to the library whose value a run reads, which is the
    /// one the scope precedence picks, not another library holding the same key.</summary>
    [Fact]
    public async Task EntryOwners_NameTheLibraryWhoseValueWins_PerKey()
    {
        _repo.FindByNamesWithCrossAccessAsync(Arg.Any<List<string>>(), 1, Arg.Any<CancellationToken>()).Returns(
        [
            Lib(null, null, null, "API_HOST", "", id: 10),
            Lib(1, null, null, "API_HOST", "", id: 11),
            Lib(1, null, null, "API_PORT", "8080", id: 11),
            Lib(null, null, null, "ONLY_GLOBAL", "", id: 10)
        ]);

        var owners = await _sut.ResolveEntryOwnersWithCrossAccessAsync(["any"], 1, TestContext.Current.CancellationToken);

        Assert.Equal(11, owners["api_host"]);
        Assert.Equal(11, owners["API_PORT"]);
        Assert.Equal(10, owners["ONLY_GLOBAL"]);
    }
}
