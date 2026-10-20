// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Audit;

/// <summary>The audit page's chain check: an intact chain is reported green with its entry count, a broken one
/// red with the first bad entry, and a failed or empty answer as an honest failure, never a green.</summary>
public sealed class AuditChainVerificationTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public AuditChainVerificationTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
        _handler.SetJsonResponse("api/audit/actions", new List<string>());
        _handler.SetJsonResponse("api/audit/entity-types", new List<string>());
        _handler.SetJsonResponse("api/audit", new PaginatedResult<AuditLogDto> { Items = [], TotalCount = 0 });
    }

    private async Task<IRenderedComponent<AuditLogs>> VerifyAsync()
    {
        var cut = Render<AuditLogs>();
        await cut.FindAll("button").First(button => button.TextContent.Contains("AuditVerifyChain", StringComparison.Ordinal)).ClickAsync(new());
        return cut;
    }

    [Fact]
    public async Task AnIntactChain_IsReportedGreenWithItsEntryCount()
    {
        _handler.SetJsonResponse("api/audit/verify-chain", new AuditChainVerificationResult { IsValid = true, TotalEntries = 42 });

        var cut = await VerifyAsync();

        Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniSeverity.Success && toast.Detail == "AuditChainIntact");
        Assert.Contains("AuditChainIntactCount", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABrokenChain_IsReportedRed()
    {
        _handler.SetJsonResponse("api/audit/verify-chain", new AuditChainVerificationResult { IsValid = false, FirstInvalidId = 17 });

        var cut = await VerifyAsync();

        Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniSeverity.Danger && toast.Detail == "AuditChainBroken");
        Assert.DoesNotContain(Services.Toasts(), toast => toast.Severity == OmniSeverity.Success);
        Assert.Contains("AuditBreakAt", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedCheck_IsAnHonestFailure_AndTheBadgeSaysUnavailable()
    {
        _handler.SetResponse("api/audit/verify-chain", HttpStatusCode.InternalServerError);

        var cut = await VerifyAsync();

        Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniSeverity.Danger && toast.Detail == "OperationFailed");
        Assert.DoesNotContain(Services.Toasts(), toast => toast.Severity == OmniSeverity.Success);
        Assert.Contains("AuditIntegrityUnavailable", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyAnswer_IsAFailureToo()
    {
        _handler.SetRawResponse("api/audit/verify-chain", "null");

        await VerifyAsync();

        Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniSeverity.Danger && toast.Detail == "OperationFailed");
        Assert.DoesNotContain(Services.Toasts(), toast => toast.Severity == OmniSeverity.Success);
    }
}
