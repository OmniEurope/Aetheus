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

    private IRenderedComponent<LibraryPortAllocateDialog> RenderWith(PortAllocationTargetsDto targets)
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetJsonResponse(HttpMethod.Get, "/ports/servers", targets);
        _handler.SetJsonResponse(
            HttpMethod.Post, "/ports/allocate",
            new List<VariableEntryDto> { new() { Key = "PORT_FRONT", Value = "10000" } });
        // Read the library before allocation to identify pre-existing entries.
        _handler.SetJsonResponse(HttpMethod.Get, "/entries", new PaginatedResult<VariableEntryDto>());

        return Render<LibraryPortAllocateDialog>(parameters => parameters.Add(c => c.LibraryId, 100));
    }

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
    public void UsableLibrary_PrefillsTheBlueGreenPreset()
    {
        var cut = RenderWith(Targets());

        Assert.Contains("PORT_FRONT_BLUE", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("PORT_BACK_GREEN", cut.Markup, StringComparison.Ordinal);
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
        // Empty before the call, the four keys after it: this call wrote them.
        var reads = 0;
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "/entries", _ =>
            Task.FromResult(reads++ == 0 ? new PaginatedResult<VariableEntryDto>() : FourBlueGreenEntries()));

        var cut = Render<LibraryPortAllocateDialog>(parameters => parameters.Add(c => c.LibraryId, 100));
        cut.FindAll("button").First(button => button.TextContent.Contains("PortAllocate", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() =>
            Assert.Contains("PORT_FRONT_BLUE = 10000", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("An unexpected error occurred", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Allocate_FailedOverKeysThatAlreadyExisted_KeepsTheError()
    {
        // Allocating keys the library already holds breaks the unique index: 500, nothing written,
        // and the keys are there afterwards because they were there before. That is not a success.
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetJsonResponse(HttpMethod.Get, "/ports/servers", Targets());
        _handler.SetJsonResponse(
            HttpMethod.Post, "/ports/allocate",
            new ApiError { Message = "An unexpected error occurred." },
            HttpStatusCode.InternalServerError);
        _handler.SetJsonResponse(HttpMethod.Get, "/entries", FourBlueGreenEntries());

        var cut = Render<LibraryPortAllocateDialog>(parameters => parameters.Add(c => c.LibraryId, 100));
        cut.FindAll("button").First(button => button.TextContent.Contains("PortAllocate", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() =>
            Assert.Contains("An unexpected error occurred", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("PORT_FRONT_BLUE = 10000", cut.Markup, StringComparison.Ordinal);
    }

    private static PaginatedResult<VariableEntryDto> FourBlueGreenEntries() => new()
    {
        Items =
        [
            new VariableEntryDto { Key = "PORT_FRONT_BLUE", Value = "10000" },
            new VariableEntryDto { Key = "PORT_BACK_BLUE", Value = "10001" },
            new VariableEntryDto { Key = "PORT_FRONT_GREEN", Value = "10002" },
            new VariableEntryDto { Key = "PORT_BACK_GREEN", Value = "10003" }
        ],
        TotalCount = 4
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
