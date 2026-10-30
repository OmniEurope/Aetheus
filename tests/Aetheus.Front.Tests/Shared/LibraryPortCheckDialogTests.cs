// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http;
using Bunit;

namespace Aetheus.Front.Tests.Shared;

/// <summary>Checking a library's PORT_ values on a server: the verdict lists each port for that server, a
/// refusal shows the reason the API gave, and a refusal without one says the check could not load.</summary>
public sealed class LibraryPortCheckDialogTests : BunitContext
{
    private const string CheckUrl = "api/variable-libraries/100/ports/check";
    private readonly BunitTestHelper.TestHandler _handler;

    public LibraryPortCheckDialogTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetJsonResponse(HttpMethod.Get, "/ports/servers", new PortAllocationTargetsDto
        {
            Servers = [new PortAllocationTargetDto { ServerId = 7, ServerName = "vps2577917" }],
            PreselectedServerId = 7,
            ProjectId = 4
        });
    }

    private IRenderedComponent<LibraryPortCheckDialog> Check()
    {
        var cut = Render<LibraryPortCheckDialog>(parameters => parameters.Add(dialog => dialog.LibraryId, 100));
        cut.FindAll("button").First(button => button.TextContent.Contains("CheckPorts", StringComparison.Ordinal)).Click();
        return cut;
    }

    [Fact]
    public void ALoneServerWithoutPreselection_IsChosenAndTheButtonEnabled()
    {
        // R-88: the select showed the only server as if chosen while the value stayed null and the
        // button stayed disabled.
        _handler.SetJsonResponse(HttpMethod.Get, "/ports/servers", new PortAllocationTargetsDto
        {
            Servers = [new PortAllocationTargetDto { ServerId = 7, ServerName = "vps2577917" }],
            PreselectedServerId = null,
            ProjectId = 4
        });
        _handler.SetJsonResponse(HttpMethod.Post, CheckUrl, new PortCheckResultDto { ServerId = 7, ServerName = "vps2577917" });

        var cut = Render<LibraryPortCheckDialog>(parameters => parameters.Add(dialog => dialog.LibraryId, 100));
        var button = cut.FindAll("button").First(b => b.TextContent.Contains("CheckPorts", StringComparison.Ordinal));
        Assert.False(button.HasAttribute("disabled"));
        button.Click();

        cut.WaitForAssertion(() => Assert.Contains(_handler.RequestDetails, request => request.Method == "POST"
            && request.Url.EndsWith(CheckUrl, StringComparison.Ordinal)
            && request.Body!.Contains("\"serverId\":7", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void SeveralServersWithoutPreselection_ShowAnEmptyOptionAndKeepTheButtonDisabled()
    {
        _handler.SetJsonResponse(HttpMethod.Get, "/ports/servers", new PortAllocationTargetsDto
        {
            Servers =
            [
                new PortAllocationTargetDto { ServerId = 7, ServerName = "vps2577917" },
                new PortAllocationTargetDto { ServerId = 8, ServerName = "vps-other" }
            ],
            ProjectId = 4
        });

        var cut = Render<LibraryPortCheckDialog>(parameters => parameters.Add(dialog => dialog.LibraryId, 100));

        Assert.True(cut.FindAll("button").First(b => b.TextContent.Contains("CheckPorts", StringComparison.Ordinal)).HasAttribute("disabled"));
        var options = cut.Find("#lib-check-server").QuerySelectorAll("option");
        Assert.Equal(3, options.Length);
        Assert.Equal(string.Empty, options[0].GetAttribute("value") ?? string.Empty);
    }

    [Fact]
    public void TheVerdict_ListsEachPortForTheChosenServer()
    {
        _handler.SetJsonResponse(HttpMethod.Post, CheckUrl, new PortCheckResultDto
        {
            ServerId = 7,
            ServerName = "vps2577917",
            Entries = [new PortCheckEntryDto { Port = 10000, IsFree = true }, new PortCheckEntryDto { Port = 10001, IsFree = false, OwnerLabel = "shop" }]
        });

        var cut = Check();

        cut.WaitForAssertion(() => Assert.Contains("10001", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("10000", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(_handler.RequestDetails, request => request.Method == "POST" && request.Url.EndsWith(CheckUrl, StringComparison.Ordinal)
            && request.Body!.Contains("\"serverId\":7", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("LoadFailed", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusal_ShowsTheApisReason()
    {
        _handler.SetJsonResponse(HttpMethod.Post, CheckUrl, new ApiError { Message = "The library has no PORT_ entry." }, HttpStatusCode.BadRequest);

        var cut = Check();

        cut.WaitForAssertion(() => Assert.Contains("The library has no PORT_ entry.", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("LoadFailed", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusalWithoutReason_SaysTheCheckCouldNotLoad()
    {
        _handler.SetResponse(HttpMethod.Post, CheckUrl, HttpStatusCode.InternalServerError);

        var cut = Check();

        cut.WaitForAssertion(() => Assert.Contains("LoadFailed", cut.Markup, StringComparison.Ordinal));
    }
}
