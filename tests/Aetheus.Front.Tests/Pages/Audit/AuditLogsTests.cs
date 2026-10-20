// SPDX-License-Identifier: EUPL-1.2
using Bunit;

namespace Aetheus.Front.Tests.Pages;

public class AuditLogsTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public AuditLogsTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
    }

    [Fact]
    public void Renders_AuditLogsPage_ForAdmin()
    {
        _handler.SetJsonResponse("api/audit/actions", new List<string> { "Created", "Updated", "Deleted" });
        _handler.SetJsonResponse("api/audit/entity-types", new List<string> { "Server", "Pipeline" });
        _handler.SetJsonResponse("api/audit", new PaginatedResult<AuditLogDto> { Items = [], TotalCount = 0 });

        var cut = Render<AuditLogs>();
        // Admin sees the audit-logs page header rendered.
        Assert.Contains("AuditLogs", cut.Markup);
    }

    [Fact]
    public void Renders_AuditLogsPage_WithData()
    {
        _handler.SetJsonResponse("api/audit/actions", new List<string> { "Created", "Updated", "Deleted" });
        _handler.SetJsonResponse("api/audit/entity-types", new List<string> { "Server", "Pipeline" });
        _handler.SetJsonResponse("api/audit", new PaginatedResult<AuditLogDto>
        {
            Items =
            [
                new AuditLogDto
                {
                    Id = 1,
                    Action = "Created",
                    EntityType = "Server",
                    Username = "admin",
                    Timestamp = DateTime.UtcNow
                }
            ],
            TotalCount = 1
        });

        var cut = Render<AuditLogs>();
        // The seeded audit row renders its username and entity type in the grid.
        cut.WaitForState(() => cut.Markup.Contains("admin"));
        Assert.Contains("admin", cut.Markup);
        Assert.Contains("Server", cut.Markup);
    }
}
