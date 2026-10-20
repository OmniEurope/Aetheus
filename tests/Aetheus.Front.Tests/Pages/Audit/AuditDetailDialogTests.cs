// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Components.Audit;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Audit;

public class AuditDetailDialogTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public AuditDetailDialogTests() => _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);

    private static AuditLogDto Sample(int id = 1) => new()
    {
        Id = id,
        Username = "admin",
        Action = "Updated",
        EntityType = "Server",
        EntityId = 42,
        Details = "{\"before\":1,\"after\":2}",
        Timestamp = new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc)
    };

    [Fact]
    public void ValidEntry_ShowsIntactBadge()
    {
        _handler.SetJsonResponse("api/audit/1/verify",
            new AuditChainVerificationResult { IsValid = true, TotalEntries = 7 });

        var cut = Render<AuditDetailDialog>(p => p.Add(c => c.Log, Sample()));

        cut.WaitForState(() => cut.Markup.Contains("AuditChainIntact"));
        Assert.Contains("AuditChainIntact", cut.Markup);
        Assert.Contains("admin", cut.Markup);   // actor
        Assert.Contains("Server", cut.Markup);  // target
    }

    [Fact]
    public void TamperedEntry_ShowsBreakBadge_NotOptimisticGreen()
    {
        _handler.SetJsonResponse("api/audit/1/verify",
            new AuditChainVerificationResult { IsValid = false, TotalEntries = 3, FirstInvalidId = 3, ErrorMessage = "Hash mismatch at entry 3" });

        var cut = Render<AuditDetailDialog>(p => p.Add(c => c.Log, Sample()));

        cut.WaitForState(() => cut.Markup.Contains("AuditChainBroken"));
        Assert.Contains("AuditChainBroken", cut.Markup);
        Assert.DoesNotContain("AuditChainIntact", cut.Markup);
    }

    [Fact]
    public void VerificationUnavailable_ShowsUnavailable_NotGreen()
    {
        // Server error on the verify endpoint must degrade to an honest "unavailable", never a green.
        _handler.SetResponse("api/audit/1/verify", HttpStatusCode.InternalServerError);

        var cut = Render<AuditDetailDialog>(p => p.Add(c => c.Log, Sample()));

        cut.WaitForState(() => cut.Markup.Contains("AuditIntegrityUnavailable"));
        Assert.Contains("AuditIntegrityUnavailable", cut.Markup);
        Assert.DoesNotContain("AuditChainIntact", cut.Markup);
    }
}
