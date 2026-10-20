// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.ServiceConnections;
using Bunit;

namespace Aetheus.Front.Tests.Pages.ServiceConnections;

public class ServiceConnectionsTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public ServiceConnectionsTests() => _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);

    private void SeedList() => _handler.SetJsonResponse("api/service-connections", new PaginatedResult<ServiceConnectionDto>
    {
        Items = [new ServiceConnectionDto { Id = 1, Name = "GitHub prod", Type = ServiceConnectionType.GitHub, Url = "https://github.com" }],
        TotalCount = 1
    });

    [Fact]
    public void Renders_List_WithConnection()
    {
        SeedList();

        var cut = Render<Aetheus.Front.Components.ServiceConnections.ServiceConnections>();

        cut.WaitForState(() => cut.Markup.Contains("GitHub prod"));
        Assert.Contains("GitHub prod", cut.Markup);
        Assert.Contains("GitHub", cut.Markup); // type badge
    }

    [Fact]
    public void TestButton_ValidResult_ShowsValidBadge()
    {
        SeedList();
        _handler.SetJsonResponse("api/service-connections/1/test",
            new ServiceConnectionTestResultDto { Status = ServiceConnectionTestStatus.Valid, Message = "ok" });

        var cut = Render<Aetheus.Front.Components.ServiceConnections.ServiceConnections>();
        cut.WaitForState(() => cut.Markup.Contains("GitHub prod"));

        var testButton = cut.FindMenuItem("TestConnection")!;
        testButton.Click();

        // The localized valid status (Enum key) renders in a badge - never fabricated.
        cut.WaitForState(() => cut.Markup.Contains("Enum_ServiceConnectionTestStatus_Valid"));
        Assert.Contains("Enum_ServiceConnectionTestStatus_Valid", cut.Markup);
    }

    [Fact]
    public void TestButton_UnsupportedResult_ShowsNotTestableBadge_NotGreen()
    {
        SeedList();
        _handler.SetJsonResponse("api/service-connections/1/test",
            new ServiceConnectionTestResultDto { Status = ServiceConnectionTestStatus.Unsupported });

        var cut = Render<Aetheus.Front.Components.ServiceConnections.ServiceConnections>();
        cut.WaitForState(() => cut.Markup.Contains("GitHub prod"));

        cut.FindMenuItem("TestConnection")!.Click();

        cut.WaitForState(() => cut.Markup.Contains("Enum_ServiceConnectionTestStatus_Unsupported"));
        Assert.DoesNotContain("Enum_ServiceConnectionTestStatus_Valid", cut.Markup);
    }
}
