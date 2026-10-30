// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.VariableLibraries;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class VariableLibrariesControllerTests
{
    private readonly IVariableLibraryService _serviceMock = Substitute.For<IVariableLibraryService>();
    private readonly IResourceAuthorizationService _authzMock = Substitute.For<IResourceAuthorizationService>();
    private readonly VariableLibrariesController _sut;

    public VariableLibrariesControllerTests()
    {
        // ADR-059: the caller's organization lets it give a library to the project (the denial has its own test).
        _authzMock.CanOwnAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<ResourceType>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _sut = new VariableLibrariesController(_serviceMock, _authzMock);
        _sut.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, "1")], "test"))
            }
        };
    }

    [Fact]
    public async Task GetLibraries_ReturnsOk()
    {
        _authzMock.GetAccessibleResourceIdsAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.VariableLibrary, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(new List<int> { 1 });
        _serviceMock.GetLibrariesAsync(null, null, null, Arg.Any<PaginationRequest?>(), Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<VariableLibraryDto> { Items = [new VariableLibraryDto { Id = 1, Name = "Lib" }], TotalCount = 1 });

        var result = await _sut.GetLibraries(null, null, null, new PaginationRequest(), TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetLibrary_Authorized_ReturnsOk()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.VariableLibrary, 1, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.GetLibraryDetailAsync(1, Arg.Any<CancellationToken>())
            .Returns(new VariableLibraryDetailDto { Id = 1, Name = "Lib" });

        var result = await _sut.GetLibrary(1, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetLibrary_Forbidden_ReturnsForbid()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.VariableLibrary, 1, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.GetLibrary(1, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task GetLibraryNames_ReturnsOk()
    {
        _serviceMock.GetLibraryNamesAsync(null, Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns(["Lib1", "Lib2"]);

        var result = await _sut.GetLibraryNames(null, TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(2, ((List<string>)ok.Value!).Count);
    }

    [Fact]
    public async Task GetSuggestionKeys_UsesAccessibleIdsAndReturnsPage()
    {
        _authzMock.GetAccessibleResourceIdsAsync(
                Arg.Any<ClaimsPrincipal>(), ResourceType.VariableLibrary, Permission.Read,
                Arg.Any<CancellationToken>())
            .Returns([4]);
        _serviceMock.GetSuggestionKeysAsync(
                7, Arg.Any<PaginationRequest>(), Arg.Is<List<int>>(ids => ids.SequenceEqual(new[] { 4 })),
                Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<string> { Items = ["KEY"], TotalCount = 1 });

        var result = await _sut.GetSuggestionKeys(7, new PaginationRequest(), TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(["KEY"], Assert.IsType<PaginatedResult<string>>(ok.Value).Items);
    }

    [Fact]
    public async Task CreateLibrary_Authorized_ReturnsCreated()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.VariableLibrary, null, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.CreateLibraryAsync(Arg.Any<CreateVariableLibraryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new VariableLibraryDto { Id = 1, Name = "New" });

        var result = await _sut.CreateLibrary(new CreateVariableLibraryRequest { Name = "New" }, TestContext.Current.CancellationToken);

        Assert.IsType<CreatedAtActionResult>(result.Result);
    }

    // R-46: a Write role in organization A never creates a library inside a project of organization B.
    [Fact]
    public async Task CreateLibrary_InAProjectItsOrganizationDoesNotAllow_IsForbidden()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.VariableLibrary, null, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(true);
        _authzMock.CanOwnAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.VariableLibrary, 42, null, null, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.CreateLibrary(
            new CreateVariableLibraryRequest { Name = "New", ProjectId = 42 }, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
        await _serviceMock.DidNotReceive().CreateLibraryAsync(Arg.Any<CreateVariableLibraryRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateLibrary_Forbidden_ReturnsForbid()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.VariableLibrary, null, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.CreateLibrary(new CreateVariableLibraryRequest { Name = "X" }, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task UpdateLibrary_Authorized_ReturnsOk()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.VariableLibrary, 1, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.UpdateLibraryAsync(1, Arg.Any<UpdateVariableLibraryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new VariableLibraryDto { Id = 1, Name = "Updated" });

        var result = await _sut.UpdateLibrary(1, new UpdateVariableLibraryRequest { Name = "Updated" }, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task DeleteLibrary_Authorized_ReturnsNoContent()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.VariableLibrary, 1, Permission.Admin, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.DeleteLibraryAsync(1, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _sut.DeleteLibrary(1, TestContext.Current.CancellationToken);

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task DeleteLibrary_Forbidden_ReturnsForbid()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.VariableLibrary, 1, Permission.Admin, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.DeleteLibrary(1, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task CreateEntry_Authorized_ReturnsCreated()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.VariableLibrary, 1, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.CreateEntryAsync(1, Arg.Any<CreateVariableEntryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new VariableEntryDto { Id = 1, Key = "KEY" });

        var result = await _sut.CreateEntry(1, new CreateVariableEntryRequest { Key = "KEY", Value = "val" }, TestContext.Current.CancellationToken);

        Assert.IsType<CreatedResult>(result.Result);
    }

    [Fact]
    public async Task UpdateEntry_Authorized_ReturnsOk()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.VariableLibrary, 1, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.UpdateEntryAsync(1, 2, Arg.Any<UpdateVariableEntryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new VariableEntryDto { Id = 2, Key = "Updated" });

        var result = await _sut.UpdateEntry(1, 2, new UpdateVariableEntryRequest { Key = "Updated", Value = "v2" }, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task DeleteEntry_Authorized_ReturnsNoContent()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.VariableLibrary, 1, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.DeleteEntryAsync(1, 2, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _sut.DeleteEntry(1, 2, TestContext.Current.CancellationToken);

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task GetEntryVersions_Authorized_ReturnsOk()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.VariableLibrary, 1, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.GetEntryVersionsAsync(
                1, 2, Arg.Any<PaginationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<VariableEntryVersionDto>
            {
                Items = [new VariableEntryVersionDto { Version = 1 }],
                TotalCount = 1
            });

        var result = await _sut.GetEntryVersions(1, 2, new PaginationRequest(), TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task ExportEntries_Authorized_ReturnsOk()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.VariableLibrary, 1, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.ExportEntriesAsync(1, Arg.Any<CancellationToken>())
            .Returns([new VariableEntryDto { Id = 1, Key = "K" }]);

        var result = await _sut.ExportEntries(1, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task ImportEntries_Authorized_ReturnsOk()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.VariableLibrary, 1, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.ImportEntriesAsync(1, Arg.Any<List<CreateVariableEntryRequest>>(), Arg.Any<CancellationToken>())
            .Returns(3);

        var result = await _sut.ImportEntries(1, [new CreateVariableEntryRequest { Key = "K", Value = "V" }], TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var importResult = Assert.IsType<ImportResultDto>(ok.Value);
        Assert.Equal(3, importResult.ImportedCount);
    }
}

