// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http;
using Bunit;

namespace Aetheus.Front.Tests.Shared;

/// <summary>The allocation dialog must prevent invalid requests and preserve the backend's refusal
/// reason.</summary>
public class LibraryPortAllocateDialogTests : BunitContext
{
    private BunitTestHelper.TestHandler? _handler;

    private IRenderedComponent<LibraryPortAllocateDialog> RenderWith(
        PortAllocationTargetsDto targets, params VariableEntryDto[] existing)
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetJsonResponse(HttpMethod.Get, "/ports/servers", targets);
        _handler.SetJsonResponse(
            HttpMethod.Post, "/ports/allocate",
            new List<VariableEntryDto> { new() { Key = "PORT_FRONT", Value = "10000" } });
        // Read the library before allocation to identify pre-existing entries.
        _handler.SetJsonResponse(
            HttpMethod.Get, "/entries", new PaginatedResult<VariableEntryDto> { Items = [.. existing], TotalCount = existing.Length });

        return Render<LibraryPortAllocateDialog>(parameters => parameters.Add(c => c.LibraryId, 100));
    }

    private static string KeysText(IRenderedComponent<LibraryPortAllocateDialog> cut) =>
        cut.Find("textarea#alloc-keys").GetAttribute("value") ?? cut.Find("textarea#alloc-keys").TextContent;

    private static bool AllocateDisabled(IRenderedComponent<LibraryPortAllocateDialog> cut) =>
        cut.FindAll("button").First(button => button.TextContent.Contains("PortAllocate", StringComparison.Ordinal))
            .HasAttribute("disabled");

    private static PortAllocationTargetsDto Targets(
        PortAllocationBlocker blocker = PortAllocationBlocker.None, int? preselected = 7) => new()
        {
            Servers = [new PortAllocationTargetDto { ServerId = 7, ServerName = "vps2577917" }],
            PreselectedServerId = preselected,
            ProjectId = 4,
            Blocker = blocker
        };

    [Fact]
    public void BlockedLibrary_ShowsTheReasonAndNoForm()
    {
        var cut = RenderWith(Targets(blocker: PortAllocationBlocker.NoProject));

        Assert.Contains("PortAllocateNoProject", cut.Markup, StringComparison.Ordinal);
        // Offering a form that always fails would be worse than saying so up front.
        Assert.DoesNotContain("PortAllocateKeys", cut.Markup);
    }

    [Fact]
    public void LibraryWithoutPortKeys_StartsOnTheSimplePairAndSaysTheyWillBeCreated()
    {
        // R-86: the blue-green keys were offered on a library whose Compose reads PORT_FRONT and PORT_BACK.
        var cut = RenderWith(Targets());

        var keys = KeysText(cut);
        Assert.Contains("PORT_FRONT", keys, StringComparison.Ordinal);
        Assert.Contains("PORT_BACK", keys, StringComparison.Ordinal);
        Assert.DoesNotContain("BLUE", keys, StringComparison.Ordinal);
        Assert.Contains("PortAllocateKeysWillBeCreated", cut.Markup, StringComparison.Ordinal);
        Assert.False(AllocateDisabled(cut));
    }

    [Fact]
    public void LibraryWithEmptyPortKeys_StartsOnThemAndSaysTheyWillBeFilled()
    {
        // R-85 / R-86: PORT_FRONT and PORT_BACK exist and are empty: allocation fills them.
        var cut = RenderWith(
            Targets(),
            new VariableEntryDto { Id = 1, Key = "PORT_FRONT", Value = string.Empty },
            new VariableEntryDto { Id = 2, Key = "PORT_BACK", Value = string.Empty },
            new VariableEntryDto { Id = 3, Key = "DB_HOST", Value = string.Empty });

        var keys = KeysText(cut);
        Assert.Contains("PORT_FRONT", keys, StringComparison.Ordinal);
        Assert.Contains("PORT_BACK", keys, StringComparison.Ordinal);
        Assert.DoesNotContain("DB_HOST", keys, StringComparison.Ordinal);
        Assert.Contains("PortAllocateKeysWillBeFilled", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("PortAllocateKeysWillBeCreated", cut.Markup, StringComparison.Ordinal);
        Assert.False(AllocateDisabled(cut));
    }

    [Fact]
    public void AKeyThatAlreadyHoldsAValue_IsReportedAndBlocksTheButton()
    {
        var cut = RenderWith(
            Targets(),
            new VariableEntryDto { Id = 1, Key = "PORT_FRONT", Value = "10031" });
        cut.Find("textarea#alloc-keys").Input("PORT_FRONT\nPORT_BACK");

        Assert.Contains("PortAllocateKeysHaveValue", cut.Markup, StringComparison.Ordinal);
        Assert.True(AllocateDisabled(cut));
    }

    [Fact]
    public void InitialKeys_ReplaceTheDefaultList()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetJsonResponse(HttpMethod.Get, "/ports/servers", Targets());
        _handler.SetJsonResponse(
            HttpMethod.Get, "/entries",
            new PaginatedResult<VariableEntryDto>
            {
                Items = [new VariableEntryDto { Id = 1, Key = "PORT_FRONT", Value = string.Empty }],
                TotalCount = 1
            });

        var cut = Render<LibraryPortAllocateDialog>(parameters => parameters
            .Add(c => c.LibraryId, 100)
            .Add(c => c.InitialKeys, ["PORT_FRONT"]));

        Assert.Equal("PORT_FRONT", KeysText(cut).Trim());
        Assert.Contains("PortAllocateKeysWillBeFilled", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ALoneServerWithoutPreselection_IsChosenAndTheButtonEnabled()
    {
        // R-88: the select showed the only server as if chosen while the value stayed null.
        var cut = RenderWith(Targets(preselected: null));

        Assert.False(AllocateDisabled(cut));
    }

    [Fact]
    public void SeveralServersWithoutPreselection_ShowAnEmptyOptionAndKeepTheButtonDisabled()
    {
        var targets = Targets(preselected: null);
        targets.Servers.Add(new PortAllocationTargetDto { ServerId = 8, ServerName = "vps-other" });

        var cut = RenderWith(targets);

        Assert.True(AllocateDisabled(cut));
        var options = cut.Find("#alloc-server").QuerySelectorAll("option");
        Assert.Equal(3, options.Length);
        Assert.Equal(string.Empty, options[0].GetAttribute("value") ?? string.Empty);
    }

    [Fact]
    public void Allocate_ShowsTheCreatedEntriesWithTheirPorts()
    {
        var cut = RenderWith(Targets());

        cut.FindAll("button").First(button => button.TextContent.Contains("PortAllocate", StringComparison.Ordinal)).Click();

        Assert.Contains("PORT_FRONT = 10000", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Allocate_Refused_KeepsTheApiReason()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetJsonResponse(HttpMethod.Get, "/ports/servers", Targets());
        _handler.SetJsonResponse(
            HttpMethod.Post, "/ports/allocate",
            new ApiError { Message = "The allocation window 10000-10001 has only 2 free port(s) left, and 4 were requested." },
            HttpStatusCode.Conflict);
        // After refusal, re-read the library; absence of written entries confirms the refusal.
        _handler.SetJsonResponse(HttpMethod.Get, "/entries", new PaginatedResult<VariableEntryDto>());

        var cut = Render<LibraryPortAllocateDialog>(parameters => parameters.Add(c => c.LibraryId, 100));
        cut.FindAll("button").First(button => button.TextContent.Contains("PortAllocate", StringComparison.Ordinal)).Click();

        Assert.Contains("only 2 free port(s)", cut.Markup, StringComparison.Ordinal);
    }


    [Fact]
    public void Allocate_FailedAfterWriting_ReportsTheEntriesInsteadOfAnError()
    {
        // A failed response can follow persisted allocation, so the subsequent library read must
        // detect existing ports and prevent duplicate allocation.
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetJsonResponse(HttpMethod.Get, "/ports/servers", Targets());
        _handler.SetJsonResponse(
            HttpMethod.Post, "/ports/allocate",
            new ApiError { Message = "An unexpected error occurred." },
            HttpStatusCode.InternalServerError);
        // Empty when the dialog opens and just before the call, the two default keys after it: this call
        // wrote them.
        var reads = 0;
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "/entries", _ =>
            Task.FromResult(reads++ <= 1 ? new PaginatedResult<VariableEntryDto>() : TwoPortEntries("10000", "10001")));

        var cut = Render<LibraryPortAllocateDialog>(parameters => parameters.Add(c => c.LibraryId, 100));
        cut.FindAll("button").First(button => button.TextContent.Contains("PortAllocate", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() =>
            Assert.Contains("PORT_FRONT = 10000", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("An unexpected error occurred", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Allocate_FailedOverKeysThatAlreadyExisted_KeepsTheError()
    {
        // The keys are there afterwards because they were there before (empty, so the dialog offers
        // them): a 500 with nothing written is not a success.
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetJsonResponse(HttpMethod.Get, "/ports/servers", Targets());
        _handler.SetJsonResponse(
            HttpMethod.Post, "/ports/allocate",
            new ApiError { Message = "An unexpected error occurred." },
            HttpStatusCode.InternalServerError);
        _handler.SetJsonResponse(HttpMethod.Get, "/entries", TwoPortEntries(string.Empty, string.Empty));

        var cut = Render<LibraryPortAllocateDialog>(parameters => parameters.Add(c => c.LibraryId, 100));
        cut.FindAll("button").First(button => button.TextContent.Contains("PortAllocate", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() =>
            Assert.Contains("An unexpected error occurred", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("PORT_FRONT = 10000", cut.Markup, StringComparison.Ordinal);
    }

    private static PaginatedResult<VariableEntryDto> TwoPortEntries(string front, string back) => new()
    {
        Items =
        [
            new VariableEntryDto { Key = "PORT_FRONT", Value = front },
            new VariableEntryDto { Key = "PORT_BACK", Value = back }
        ],
        TotalCount = 2
    };

    [Theory]
    [InlineData("PORT_A\nPORT_B", 2)]
    [InlineData("PORT_A, PORT_B; PORT_C", 3)]
    [InlineData("PORT_A\n\n  PORT_A  ", 1)]
    [InlineData("   ", 0)]
    public void ParseKeys_ReadsOneKeyPerLineAndDropsDuplicates(string input, int expected)
        => Assert.Equal(expected, LibraryPortAllocateDialog.ParseKeys(input).Count);

    [Fact]
    public void ParseKeys_StopsAtTheAllocationCap()
    {
        var many = string.Join("\n", Enumerable.Range(1, PortRegistryLimits.MaxPortsPerAllocation + 5)
            .Select(index => $"PORT_{index}"));

        Assert.Equal(PortRegistryLimits.MaxPortsPerAllocation, LibraryPortAllocateDialog.ParseKeys(many).Count);
    }
}
