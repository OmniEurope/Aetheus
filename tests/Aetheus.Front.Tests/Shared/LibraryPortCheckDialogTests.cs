// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http;
using Aetheus.Front.Components.VariableLibraries;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;

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
        // A port taken by another owner makes the dialog read the library's entries to offer the move.
        _handler.SetPaginatedJsonResponse(HttpMethod.Get, "api/variable-libraries/100/entries", Array.Empty<VariableEntryDto>());
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

    private const string PreviewUrl = "api/variable-libraries/100/ports/free-preview";
    private const string ReallocateUrl = "api/variable-libraries/100/ports/reallocate";

    private ImmediateDialogService Dialog => (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();

    private IRenderedComponent<LibraryPortCheckDialog> CheckWithAConflict()
    {
        BunitTestHelper.UseImmediateDialogs(this);
        _handler.SetPaginatedJsonResponse(HttpMethod.Get, "api/variable-libraries/100/entries",
        [
            new VariableEntryDto { Id = 1, Key = "PORT_FRONT_BLUE", Value = "10000" },
            new VariableEntryDto { Id = 2, Key = "PORT_BACK_BLUE", Value = "10001" }
        ]);
        _handler.SetJsonResponse(HttpMethod.Post, CheckUrl, new PortCheckResultDto
        {
            ServerId = 7,
            ServerName = "vps2577917",
            Entries =
            [
                new PortCheckEntryDto { Port = 10000, IsFree = false, OwnerProjectId = 99, ContextProjectId = 4 },
                new PortCheckEntryDto { Port = 10001, IsFree = false, OwnerProjectId = 4, ContextProjectId = 4 }
            ]
        });
        _handler.SetJsonResponse(HttpMethod.Post, PreviewUrl, new List<int> { 10006 });
        _handler.SetJsonResponse(HttpMethod.Post, ReallocateUrl, new List<VariableEntryDto> { new() { Id = 1, Key = "PORT_FRONT_BLUE", Value = "10006" } });
        var cut = Check();
        cut.WaitForAssertion(() => Assert.Contains("PortReallocate", cut.Markup, StringComparison.Ordinal));
        return cut;
    }

    private bool Posted(string url) => _handler.RequestsSnapshot().Any(request => request.Method == "POST"
        && request.Url.EndsWith(url, StringComparison.Ordinal));

    [Fact]
    public void WithoutAnyConflict_SaysSoAndOffersNoReallocation()
    {
        _handler.SetJsonResponse(HttpMethod.Post, CheckUrl, new PortCheckResultDto
        {
            ServerId = 7,
            ServerName = "vps2577917",
            Entries = [new PortCheckEntryDto { Port = 10000, IsFree = false, OwnerProjectId = 4, ContextProjectId = 4 }]
        });

        var cut = Check();

        cut.WaitForAssertion(() => Assert.Contains("PortCheckNoConflict", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("PortReallocate", cut.Markup, StringComparison.Ordinal);
        Assert.False(Posted(ReallocateUrl));
    }

    [Fact]
    public void AConflict_ListsEachChangeBeforeWritingAndWritesNothingWhenCancelled()
    {
        var cut = CheckWithAConflict();
        Dialog.ConfirmResult = false;

        cut.FindAll("button").First(b => b.TextContent.Contains("PortReallocate", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.NotNull(Dialog.LastConfirmMessage));
        Assert.Contains("PORT_FRONT_BLUE : 10000 → 10006", Dialog.LastConfirmMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("PORT_BACK_BLUE", Dialog.LastConfirmMessage, StringComparison.Ordinal);
        Assert.False(Posted(ReallocateUrl));
        Assert.Equal(0, Dialog.CloseCount);
    }

    [Fact]
    public void AConflict_WhenConfirmed_WritesTheNewPortsAndClosesTheDialog()
    {
        var cut = CheckWithAConflict();
        Dialog.ConfirmResult = true;

        cut.FindAll("button").First(b => b.TextContent.Contains("PortReallocate", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.True(Posted(ReallocateUrl)));
        var post = Assert.Single(_handler.RequestDetails, request => request.Method == "POST"
            && request.Url.EndsWith(ReallocateUrl, StringComparison.Ordinal));
        Assert.Contains("\"PORT_FRONT_BLUE\"", post.Body, StringComparison.Ordinal);
        cut.WaitForAssertion(() => Assert.Equal(true, Dialog.LastCloseResult));
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
