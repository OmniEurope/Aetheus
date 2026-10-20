// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class AuditControllerTests
{
    private readonly IAuditService _serviceMock = Substitute.For<IAuditService>();
    private readonly IAuditChainService _chainMock = Substitute.For<IAuditChainService>();
    private readonly AuditController _sut;

    public AuditControllerTests()
    {
        _sut = new AuditController(_serviceMock, _chainMock);
    }

    [Fact]
    public async Task GetAuditLogs_ReturnsPaginatedResult()
    {
        var paged = new PaginatedResult<AuditLogDto>
        {
            Items = [new() { Id = 1, Username = "admin", Action = "Created", EntityType = "Server", Timestamp = DateTime.UtcNow }],
            TotalCount = 1,
            Page = 1,
            PageSize = 50
        };
        _serviceMock.GetLogsPagedAsync(1, 50, null, null, null, null, null, null, TestContext.Current.CancellationToken).Returns(paged);

        var result = await _sut.GetAuditLogs(ct: TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var paginated = Assert.IsType<PaginatedResult<AuditLogDto>>(ok.Value);
        Assert.Equal(1, paginated.TotalCount);
        Assert.Single(paginated.Items);
    }

    [Fact]
    public async Task GetAuditLogs_DelegatesPageSizeToService()
    {
        _serviceMock
            .GetLogsPagedAsync(1, 200, null, null, null, null, null, null, TestContext.Current.CancellationToken)
            .Returns(new PaginatedResult<AuditLogDto> { Page = 1, PageSize = 200 });

        await _sut.GetAuditLogs(pageSize: 999, ct: TestContext.Current.CancellationToken);

        await _serviceMock.Received(1).GetLogsPagedAsync(1, 200, null, null, null, null, null, null, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GetAuditLogs_PassesAllFilters()
    {
        var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        _serviceMock
            .GetLogsPagedAsync(2, 25, "q", "Created", "Server", null, from, to, TestContext.Current.CancellationToken)
            .Returns(new PaginatedResult<AuditLogDto> { TotalCount = 3, Page = 2, PageSize = 25 });

        await _sut.GetAuditLogs(page: 2, pageSize: 25, search: "q", action: "Created", entityType: "Server", dateFrom: from, dateTo: to, ct: TestContext.Current.CancellationToken);

        await _serviceMock.Received(1).GetLogsPagedAsync(2, 25, "q", "Created", "Server", null, from, to, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GetAuditLogs_PassesTheColumnFilters_ThroughToTheService()
    {
        // The grid's header filters travel beside the typed parameters.
        List<GridFilter> filters = [new() { Field = "Timestamp", Operator = GridFilterOperator.GreaterThanOrEqual, Value = "2026-09-01T08:00:00Z" }];

        await _sut.GetAuditLogs(ct: TestContext.Current.CancellationToken, filters: filters);

        await _serviceMock.Received(1).GetLogsPagedAsync(1, 50, null, null, null, null, null, null, TestContext.Current.CancellationToken,
            null, true, Arg.Is<IReadOnlyList<GridFilter>?>(received => received != null && received.Single().Field == "Timestamp"));
    }

    [Fact]
    public async Task GetActions_ReturnsDistinctActions()
    {
        _serviceMock.GetDistinctActionsAsync(TestContext.Current.CancellationToken).Returns(["Created", "Updated", "Deleted"]);

        var result = await _sut.GetActions(ct: TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var actions = Assert.IsType<List<string>>(ok.Value);
        Assert.Equal(3, actions.Count);
    }

    [Fact]
    public async Task GetEntityTypes_ReturnsDistinctTypes()
    {
        _serviceMock.GetDistinctEntityTypesAsync(TestContext.Current.CancellationToken).Returns(["Server", "Pipeline"]);

        var result = await _sut.GetEntityTypes(ct: TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var types = Assert.IsType<List<string>>(ok.Value);
        Assert.Equal(2, types.Count);
    }

    [Fact]
    public async Task VerifyEntry_Found_ReturnsResult()
    {
        _chainMock.VerifyUpToEntryAsync(5, TestContext.Current.CancellationToken)
            .Returns(new AuditChainVerificationResult { IsValid = true, TotalEntries = 5 });

        var result = await _sut.VerifyEntry(5, ct: TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var verification = Assert.IsType<AuditChainVerificationResult>(ok.Value);
        Assert.True(verification.IsValid);
    }

    [Fact]
    public async Task VerifyEntry_UnknownId_ReturnsNotFound()
    {
        _chainMock.VerifyUpToEntryAsync(99, TestContext.Current.CancellationToken).Returns((AuditChainVerificationResult?)null);

        var result = await _sut.VerifyEntry(99, ct: TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result.Result);
    }
}
