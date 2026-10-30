// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.VariableLibraries;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Aetheus.Back.Tests;

public class VariableLibraryServiceTests
{
    private readonly IVariableLibraryRepository _repoMock = Substitute.For<IVariableLibraryRepository>();
    private readonly IDbTransactionScope _transactionMock = Substitute.For<IDbTransactionScope>();
    private readonly IAuditService _auditMock = Substitute.For<IAuditService>();
    private readonly VariableLibraryService _sut;

    public VariableLibraryServiceTests()
    {
        _sut = new VariableLibraryService(_repoMock, _transactionMock, _auditMock, Substitute.For<IEntityChangeNotifier>(), TimeProvider.System, Substitute.For<IMemoryCache>());
    }

    [Fact]
    public async Task GetLibrariesAsync_ReturnsMappedResult()
    {
        var libraries = new List<VariableLibrary>
        {
            new() { Id = 1, Name = "Globals", Description = "Global vars", Entries = [new() { Id = 1, Key = "K", Value = "V" }] }
        };
        _repoMock.GetLibrariesPagedAsync(null, null, null, null, 1, 10, Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns((libraries, 1));

        var result = await _sut.GetLibrariesAsync(null, request: new PaginationRequest { Page = 1, PageSize = 10 }, ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.TotalCount);
        Assert.Single(result.Items);
        Assert.Equal("Globals", result.Items[0].Name);
        Assert.Equal(1, result.Items[0].EntryCount);
    }

    [Fact]
    public async Task GetLibrariesAsync_EmptyList_ReturnsEmptyResult()
    {
        _repoMock.GetLibrariesPagedAsync(null, null, null, null, 1, 10, Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns((new List<VariableLibrary>(), 0));

        var result = await _sut.GetLibrariesAsync(null, request: new PaginationRequest { Page = 1, PageSize = 10 }, ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, result.TotalCount);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task GetLibrariesAsync_WithProjectId_PassesProjectId()
    {
        _repoMock.GetLibrariesPagedAsync(null, 5, null, null, 1, 10, Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns((new List<VariableLibrary>(), 0));

        await _sut.GetLibrariesAsync(5, request: new PaginationRequest { Page = 1, PageSize = 10 }, ct: TestContext.Current.CancellationToken);

        await _repoMock.Received(1).GetLibrariesPagedAsync(null, 5, null, null, 1, 10, Arg.Any<List<int>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetEntriesAsync_MapsRequestedServerPage()
    {
        _repoMock.FindLibraryAsync(1, Arg.Any<CancellationToken>())
            .Returns(new VariableLibrary { Id = 1, Name = "Lib" });
        _repoMock.GetEntriesPagedAsync(
                1, null, 2, 25, "Key", true, Arg.Any<CancellationToken>())
            .Returns((new List<VariableLibraryEntry>
            {
                new() { Id = 30, Key = "Z", Value = "V", Versions = [new()] }
            }, 30));

        var result = await _sut.GetEntriesAsync(1, new PaginationRequest
        {
            Page = 2,
            PageSize = 25,
            SortBy = "Key",
            SortDescending = true
        }, ct: TestContext.Current.CancellationToken);

        Assert.Equal(30, result.TotalCount);
        Assert.Equal(2, result.Page);
        Assert.Equal(1, result.Items[0].VersionCount);
    }

    [Fact]
    public async Task GetSuggestionKeysAsync_MapsRequestedPage()
    {
        _repoMock.GetSuggestionKeysPagedAsync(
                7, null, 2, 200, false, Arg.Is<List<int>>(ids => ids.SequenceEqual(new[] { 3 })),
                Arg.Any<CancellationToken>())
            .Returns((new List<string> { "ZETA" }, 201));

        var result = await _sut.GetSuggestionKeysAsync(7, new PaginationRequest
        {
            Page = 2,
            PageSize = 200
        }, [3], ct: TestContext.Current.CancellationToken);

        Assert.Equal(201, result.TotalCount);
        Assert.Equal(["ZETA"], result.Items);
    }

    [Fact]
    public async Task GetLibraryDetailAsync_Found_ReturnsDetailDto()
    {
        var library = new VariableLibrary
        {
            Id = 1,
            Name = "Lib1",
            Description = "Desc",
            ProjectId = 2,
            Project = new Project { Id = 2, Name = "MyProject" },
            Entries = [new() { Id = 10, Key = "KEY1", Value = "VAL1" }]
        };
        _repoMock.GetLibraryDetailAsync(1, Arg.Any<CancellationToken>())
            .Returns(library);
        _repoMock.GetEntryCountAsync(1, Arg.Any<CancellationToken>()).Returns(1);

        var result = await _sut.GetLibraryDetailAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("Lib1", result.Name);
        Assert.Equal("MyProject", result.ProjectName);
        Assert.Equal(1, result.EntryCount);
        Assert.Empty(result.Entries);
    }

    [Fact]
    public async Task GetLibraryDetailAsync_NotFound_ReturnsNull()
    {
        _repoMock.GetLibraryDetailAsync(99, Arg.Any<CancellationToken>())
            .Returns((VariableLibrary?)null);

        var result = await _sut.GetLibraryDetailAsync(99, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetLibraryNamesAsync_ReturnsList()
    {
        _repoMock.GetLibraryNamesAsync(null, Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns(["Globals", "Staging"]);

        var result = await _sut.GetLibraryNamesAsync(null, ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        Assert.Contains("Globals", result);
    }

    [Fact]
    public async Task CreateLibraryAsync_CallsRepo_ReturnsMappedDto()
    {
        _repoMock.AddLibraryAsync(Arg.Any<VariableLibrary>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var request = new CreateVariableLibraryRequest { Name = "NewLib", Description = "Desc", ProjectId = 3 };
        var result = await _sut.CreateLibraryAsync(request, ct: TestContext.Current.CancellationToken);

        Assert.Equal("NewLib", result.Name);
        Assert.Equal("Desc", result.Description);
        Assert.Equal(3, result.ProjectId);
        await _repoMock.Received(1).AddLibraryAsync(Arg.Is<VariableLibrary>(l => l.Name == "NewLib"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateLibraryAsync_Found_UpdatesAndReturns()
    {
        var rowVersion = Guid.NewGuid();
        var library = new VariableLibrary { Id = 1, Name = "Old", Description = "Old desc", RowVersion = rowVersion, Entries = [] };
        _repoMock.GetLibraryDetailAsync(1, Arg.Any<CancellationToken>())
            .Returns(library);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var request = new UpdateVariableLibraryRequest { Name = "Updated", Description = "New desc", ProjectId = 5, RowVersion = rowVersion };
        var result = await _sut.UpdateLibraryAsync(1, request, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("Updated", result.Name);
        Assert.Equal(5, result.ProjectId);
    }

    [Fact]
    public async Task UpdateLibraryAsync_NotFound_ReturnsNull()
    {
        _repoMock.GetLibraryDetailAsync(99, Arg.Any<CancellationToken>())
            .Returns((VariableLibrary?)null);

        var result = await _sut.UpdateLibraryAsync(99, new UpdateVariableLibraryRequest { Name = "X" }, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task UpdateLibraryAsync_ConcurrencyConflict_ThrowsConflictException()
    {
        var library = new VariableLibrary { Id = 1, Name = "Lib", Description = "Desc", Entries = [] };
        _repoMock.GetLibraryDetailAsync(1, Arg.Any<CancellationToken>())
            .Returns(library);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new DbUpdateConcurrencyException());

        await Assert.ThrowsAsync<ConflictException>(() =>
            _sut.UpdateLibraryAsync(1, new UpdateVariableLibraryRequest { Name = "Updated" }, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteLibraryAsync_Found_ReturnsTrue()
    {
        var library = new VariableLibrary { Id = 1, Name = "ToDelete" };
        _repoMock.FindLibraryAsync(1, Arg.Any<CancellationToken>())
            .Returns(library);
        _repoMock.RemoveLibraryAsync(library, Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var result = await _sut.DeleteLibraryAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.True(result);
        await _repoMock.Received(1).RemoveLibraryAsync(library, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteLibraryAsync_NotFound_ReturnsFalse()
    {
        _repoMock.FindLibraryAsync(99, Arg.Any<CancellationToken>())
            .Returns((VariableLibrary?)null);

        var result = await _sut.DeleteLibraryAsync(99, ct: TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    [Fact]
    public async Task CreateEntryAsync_LibraryFound_CreatesEntryAndVersion()
    {
        var library = new VariableLibrary { Id = 1, Name = "Lib" };
        _repoMock.FindLibraryAsync(1, Arg.Any<CancellationToken>())
            .Returns(library);
        _repoMock.AddEntryAsync(Arg.Any<VariableLibraryEntry>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _repoMock.GetNextVersionAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(1);
        _repoMock.AddEntryVersionAsync(Arg.Any<VariableLibraryEntryVersion>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var request = new CreateVariableEntryRequest { Key = "DB_HOST", Value = "localhost" };
        var result = await _sut.CreateEntryAsync(1, request, ct: TestContext.Current.CancellationToken);

        Assert.Equal("DB_HOST", result.Key);
        Assert.Equal("localhost", result.Value);
        await _repoMock.Received(1).AddEntryVersionAsync(
            Arg.Is<VariableLibraryEntryVersion>(v => v.ChangeType == ChangeType.Created),
            Arg.Any<CancellationToken>());
        await _transactionMock.Received(1).BeginTransactionAsync(Arg.Any<CancellationToken>());
        await _transactionMock.Received(1).CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateEntryAsync_LibraryNotFound_ThrowsNotFoundException()
    {
        _repoMock.FindLibraryAsync(99, Arg.Any<CancellationToken>())
            .Returns((VariableLibrary?)null);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            _sut.CreateEntryAsync(99, new CreateVariableEntryRequest { Key = "K", Value = "V" }, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateEntryAsync_KeyAlreadyInTheLibrary_IsRefusedWithItsReason()
    {
        _repoMock.FindLibraryAsync(1, Arg.Any<CancellationToken>()).Returns(new VariableLibrary { Id = 1, Name = "Lib" });
        _repoMock.EntryKeyExistsAsync(1, "DB_HOST", null, Arg.Any<CancellationToken>()).Returns(true);

        var refused = await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.CreateEntryAsync(1, new CreateVariableEntryRequest { Key = "DB_HOST", Value = "x" }, ct: TestContext.Current.CancellationToken));

        Assert.Contains("DB_HOST", refused.Message, StringComparison.Ordinal);
        await _repoMock.DidNotReceive().AddEntryAsync(Arg.Any<VariableLibraryEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateEntryAsync_RenamedToAnotherEntrysKey_IsRefused()
    {
        var entry = new VariableLibraryEntry { Id = 10, VariableLibraryId = 1, Key = "OLD", Value = "old" };
        _repoMock.FindEntryAsync(10, Arg.Any<CancellationToken>()).Returns(entry);
        _repoMock.EntryKeyExistsAsync(1, "TAKEN", 10, Arg.Any<CancellationToken>()).Returns(true);

        await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.UpdateEntryAsync(1, 10, new UpdateVariableEntryRequest { Key = "TAKEN", Value = "v" }, ct: TestContext.Current.CancellationToken));

        Assert.Equal("OLD", entry.Key);
        await _repoMock.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateEntryAsync_Found_UpdatesAndCreatesVersion()
    {
        var entry = new VariableLibraryEntry { Id = 10, VariableLibraryId = 1, Key = "OLD", Value = "old" };
        _repoMock.FindEntryAsync(10, Arg.Any<CancellationToken>())
            .Returns(entry);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _repoMock.GetNextVersionAsync(10, Arg.Any<CancellationToken>())
            .Returns(2);
        _repoMock.AddEntryVersionAsync(Arg.Any<VariableLibraryEntryVersion>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var result = await _sut.UpdateEntryAsync(1, 10, new UpdateVariableEntryRequest { Key = "NEW", Value = "new" }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("NEW", result.Key);
        await _repoMock.Received(1).AddEntryVersionAsync(
            Arg.Is<VariableLibraryEntryVersion>(v => v.ChangeType == ChangeType.Updated),
            Arg.Any<CancellationToken>());
        await _transactionMock.Received(1).BeginTransactionAsync(Arg.Any<CancellationToken>());
        await _transactionMock.Received(1).CommitAsync(Arg.Any<CancellationToken>());
    }

    // Seen on the local instance: an entry validated as it was gained an "Updated" version with the same value.
    [Fact]
    public async Task UpdateEntryAsync_NothingChanged_AddsNoVersion()
    {
        var entry = new VariableLibraryEntry { Id = 10, VariableLibraryId = 1, Key = "K", Value = "same" };
        _repoMock.FindEntryAsync(10, Arg.Any<CancellationToken>())
            .Returns(entry);

        var result = await _sut.UpdateEntryAsync(1, 10, new UpdateVariableEntryRequest { Key = "K", Value = "same" }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("same", result.Value);
        await _repoMock.DidNotReceive().AddEntryVersionAsync(Arg.Any<VariableLibraryEntryVersion>(), Arg.Any<CancellationToken>());
        await _repoMock.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateEntryAsync_NotFound_ReturnsNull()
    {
        _repoMock.FindEntryAsync(99, Arg.Any<CancellationToken>())
            .Returns((VariableLibraryEntry?)null);

        var result = await _sut.UpdateEntryAsync(1, 99, new UpdateVariableEntryRequest { Key = "K", Value = "V" }, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task UpdateEntryAsync_WrongLibraryId_ReturnsNull()
    {
        var entry = new VariableLibraryEntry { Id = 10, VariableLibraryId = 2, Key = "K", Value = "V" };
        _repoMock.FindEntryAsync(10, Arg.Any<CancellationToken>())
            .Returns(entry);

        var result = await _sut.UpdateEntryAsync(1, 10, new UpdateVariableEntryRequest { Key = "K", Value = "V" }, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task DeleteEntryAsync_Found_CreatesVersionAndRemoves()
    {
        var entry = new VariableLibraryEntry { Id = 10, VariableLibraryId = 1, Key = "K", Value = "V" };
        _repoMock.FindEntryAsync(10, Arg.Any<CancellationToken>())
            .Returns(entry);
        _repoMock.GetNextVersionAsync(10, Arg.Any<CancellationToken>())
            .Returns(3);
        _repoMock.AddEntryVersionAsync(Arg.Any<VariableLibraryEntryVersion>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _repoMock.RemoveEntryAsync(entry, Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var result = await _sut.DeleteEntryAsync(1, 10, ct: TestContext.Current.CancellationToken);

        Assert.True(result);
        await _repoMock.Received(1).AddEntryVersionAsync(
            Arg.Is<VariableLibraryEntryVersion>(v => v.ChangeType == ChangeType.Deleted),
            Arg.Any<CancellationToken>());
        await _repoMock.Received(1).RemoveEntryAsync(entry, Arg.Any<CancellationToken>());
        await _transactionMock.Received(1).BeginTransactionAsync(Arg.Any<CancellationToken>());
        await _transactionMock.Received(1).CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteEntryAsync_NotFound_ReturnsFalse()
    {
        _repoMock.FindEntryAsync(99, Arg.Any<CancellationToken>())
            .Returns((VariableLibraryEntry?)null);

        var result = await _sut.DeleteEntryAsync(1, 99, ct: TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    [Fact]
    public async Task DeleteEntryAsync_WrongLibraryId_ReturnsFalse()
    {
        var entry = new VariableLibraryEntry { Id = 10, VariableLibraryId = 2, Key = "K", Value = "V" };
        _repoMock.FindEntryAsync(10, Arg.Any<CancellationToken>())
            .Returns(entry);

        var result = await _sut.DeleteEntryAsync(1, 10, ct: TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    [Fact]
    public async Task GetEntryVersionsAsync_Found_ReturnsMappedVersions()
    {
        var entry = new VariableLibraryEntry { Id = 10, VariableLibraryId = 1, Key = "K", Value = "V" };
        _repoMock.FindEntryAsync(10, Arg.Any<CancellationToken>())
            .Returns(entry);
        _repoMock.GetEntryVersionsPagedAsync(
                10, null, 1, 25, null, false, Arg.Any<CancellationToken>())
            .Returns((new List<VariableLibraryEntryVersion>
            {
                new VariableLibraryEntryVersion { Version = 1, Key = "K", Value = "V1", ChangeType = ChangeType.Created },
                new VariableLibraryEntryVersion { Version = 2, Key = "K", Value = "V2", ChangeType = ChangeType.Updated }
            }, 2));

        var result = await _sut.GetEntryVersionsAsync(1, 10, new PaginationRequest(), ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.TotalCount);
        Assert.Equal(ChangeType.Created, result.Items[0].ChangeType);
        Assert.Equal(ChangeType.Updated, result.Items[1].ChangeType);
    }

    [Fact]
    public async Task GetEntryVersionsAsync_EntryNotFound_ThrowsNotFoundException()
    {
        _repoMock.FindEntryAsync(99, Arg.Any<CancellationToken>())
            .Returns((VariableLibraryEntry?)null);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            _sut.GetEntryVersionsAsync(1, 99, new PaginationRequest(), ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResolveLibrariesAsync_MergesGlobalAndProject()
    {
        var libraries = new List<VariableLibrary>
        {
            new()
            {
                Id = 1, Name = "Globals", ProjectId = null,
                Entries = [
                    new() { Key = "DB_HOST", Value = "global-db" },
                    new() { Key = "API_URL", Value = "global-api" }
                ]
            },
            new()
            {
                Id = 2, Name = "Globals", ProjectId = 5,
                Entries = [
                    new() { Key = "DB_HOST", Value = "project-db" }
                ]
            }
        };
        _repoMock.FindByNamesAsync(Arg.Any<List<string>>(), 5, Arg.Any<CancellationToken>())
            .Returns(libraries);

        var result = await _sut.ResolveLibrariesAsync(["Globals"], 5, ct: TestContext.Current.CancellationToken);

        Assert.Equal("project-db", result["DB_HOST"]); // Project overrides global
        Assert.Equal("global-api", result["API_URL"]); // Global kept
    }

    [Fact]
    public async Task ResolveLibrariesAsync_NoLibraries_ReturnsEmptyDictionary()
    {
        _repoMock.FindByNamesAsync(Arg.Any<List<string>>(), null, Arg.Any<CancellationToken>())
            .Returns([]);

        var result = await _sut.ResolveLibrariesAsync(["NonExistent"], null, ct: TestContext.Current.CancellationToken);

        Assert.Empty(result);
    }

    [Fact]
    public async Task ResolveLibrariesAsync_CaseInsensitiveKeys()
    {
        var libraries = new List<VariableLibrary>
        {
            new()
            {
                Id = 1, Name = "Lib", ProjectId = null,
                Entries = [new() { Key = "db_host", Value = "val1" }]
            },
            new()
            {
                Id = 2, Name = "Lib", ProjectId = 1,
                Entries = [new() { Key = "DB_HOST", Value = "val2" }]
            }
        };
        _repoMock.FindByNamesAsync(Arg.Any<List<string>>(), 1, Arg.Any<CancellationToken>())
            .Returns(libraries);

        var result = await _sut.ResolveLibrariesAsync(["Lib"], 1, ct: TestContext.Current.CancellationToken);

        Assert.Single(result); // Case-insensitive override
        Assert.Equal("val2", result["DB_HOST"]);
    }

    // --- ExportEntriesAsync ---

    [Fact]
    public async Task ExportEntriesAsync_ReturnsEntryList()
    {
        _repoMock.FindLibraryAsync(1, Arg.Any<CancellationToken>())
            .Returns(new VariableLibrary { Id = 1, Name = "Lib" });
        _repoMock.GetEntriesAsync(1, Arg.Any<CancellationToken>()).Returns(
        [
            new VariableLibraryEntry { Id = 1, Key = "K1", Value = "V1", Versions = [new()] },
            new VariableLibraryEntry { Id = 2, Key = "K2", Value = "V2", Versions = [] }
        ]);

        var result = await _sut.ExportEntriesAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        Assert.Equal("K1", result[0].Key);
        Assert.Equal(1, result[0].VersionCount);
    }

    [Fact]
    public async Task ExportEntriesAsync_NotFound_ThrowsNotFoundException()
    {
        _repoMock.FindLibraryAsync(99, Arg.Any<CancellationToken>())
            .Returns((VariableLibrary?)null);

        await Assert.ThrowsAsync<NotFoundException>(() => _sut.ExportEntriesAsync(99, ct: TestContext.Current.CancellationToken));
    }

    // --- ImportEntriesAsync ---

    [Fact]
    public async Task ImportEntriesAsync_CreatesEntriesAndVersions()
    {
        _repoMock.FindLibraryAsync(1, Arg.Any<CancellationToken>())
            .Returns(new VariableLibrary { Id = 1, Name = "Lib" });
        _repoMock.GetNextVersionAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(1);

        var entries = new List<CreateVariableEntryRequest>
        {
            new() { Key = "K1", Value = "V1" },
            new() { Key = "K2", Value = "V2" }
        };

        var count = await _sut.ImportEntriesAsync(1, entries, ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, count);
        await _repoMock.Received(1).AddEntriesRangeAsync(Arg.Is<List<VariableLibraryEntry>>(l => l.Count == 2), Arg.Any<CancellationToken>());
        await _repoMock.Received(1).AddEntryVersionsRangeAsync(Arg.Is<List<VariableLibraryEntryVersion>>(l => l.Count == 2), Arg.Any<CancellationToken>());
        await _transactionMock.Received(1).CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ImportEntriesAsync_NotFound_ThrowsNotFoundException()
    {
        _repoMock.FindLibraryAsync(99, Arg.Any<CancellationToken>())
            .Returns((VariableLibrary?)null);

        await Assert.ThrowsAsync<NotFoundException>(() => _sut.ImportEntriesAsync(99, [], ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExportEntriesAsync_Found_ReturnsEntries()
    {
        _repoMock.FindLibraryAsync(1, TestContext.Current.CancellationToken)
            .Returns(new VariableLibrary { Id = 1, Name = "lib" });
        _repoMock.GetEntriesAsync(1, TestContext.Current.CancellationToken).Returns(
        [
            new VariableLibraryEntry { Id = 1, Key = "K1", Value = "V1", Versions = [new VariableLibraryEntryVersion()] },
            new VariableLibraryEntry { Id = 2, Key = "K2", Value = "V2", Versions = [] }
        ]);

        var result = await _sut.ExportEntriesAsync(1, ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, result.Count);
        Assert.Equal("K1", result[0].Key);
        Assert.Equal(1, result[0].VersionCount);
    }

    [Fact]
    public async Task ExportEntriesAsync_NotFound_Throws()
    {
        _repoMock.FindLibraryAsync(99, TestContext.Current.CancellationToken)
            .Returns((VariableLibrary?)null);

        await Assert.ThrowsAsync<NotFoundException>(() => _sut.ExportEntriesAsync(99, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ImportEntriesAsync_Success_CreatesEntriesAndVersions()
    {
        _repoMock.FindLibraryAsync(1, TestContext.Current.CancellationToken)
            .Returns(new VariableLibrary { Id = 1, Name = "lib" });
        _transactionMock.BeginTransactionAsync(TestContext.Current.CancellationToken).Returns(Task.CompletedTask);
        _transactionMock.CommitAsync(TestContext.Current.CancellationToken).Returns(Task.CompletedTask);
        _repoMock.AddEntryAsync(Arg.Any<VariableLibraryEntry>(), TestContext.Current.CancellationToken).Returns(Task.CompletedTask);
        _repoMock.GetNextVersionAsync(Arg.Any<int>(), TestContext.Current.CancellationToken).Returns(1);
        _repoMock.AddEntryVersionAsync(Arg.Any<VariableLibraryEntryVersion>(), TestContext.Current.CancellationToken).Returns(Task.CompletedTask);

        var result = await _sut.ImportEntriesAsync(1, [new CreateVariableEntryRequest { Key = "K", Value = "V" }], ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, result);
        await _transactionMock.Received(1).CommitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ResolveLibrariesAsync_ProjectOverridesGlobalForSameKey()
    {
        _repoMock.FindByNamesAsync(Arg.Any<List<string>>(), 1, TestContext.Current.CancellationToken)
            .Returns([
                new VariableLibrary { Id = 1, Name = "vars", ProjectId = null, Entries = [new VariableLibraryEntry { Key = "A", Value = "global" }, new VariableLibraryEntry { Key = "B", Value = "globalB" }] },
                new VariableLibrary { Id = 2, Name = "vars", ProjectId = 1, Entries = [new VariableLibraryEntry { Key = "A", Value = "project" }] }
            ]);

        var result = await _sut.ResolveLibrariesAsync(["vars"], 1, ct: TestContext.Current.CancellationToken);

        Assert.Equal("project", result["A"]);
        Assert.Equal("globalB", result["B"]);
    }
}

