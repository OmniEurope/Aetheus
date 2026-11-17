// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.AiTasks;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages.Ai;

/// <summary>A profile is created or edited in its dialog; only a saved dialog reloads the list and its
/// filter values, a dismissed one leaves them alone.</summary>
public sealed class AiRunnerProfilesDialogTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public AiRunnerProfilesDialogTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
        BunitTestHelper.UseImmediateDialogs(this);
        _handler.SetJsonResponse("api/ai/profile-options", new List<AiRunnerProfileDto>());
        _handler.SetJsonResponse("api/ai/profiles", new PaginatedResult<AiRunnerProfileDto>
        {
            Items = [new AiRunnerProfileDto { Id = 6, Name = "reviewer", Model = "m" }],
            TotalCount = 1,
            Page = 1,
            PageSize = 25
        });
    }

    private ImmediateDialogService Dialog => (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();

    private int OptionReads() => _handler.Requests.Count(request => request.Url.Contains("api/ai/profile-options", StringComparison.Ordinal));

    private async Task<int> CreateAsync(object? dialogAnswer)
    {
        Dialog.OpenResult = dialogAnswer;
        var cut = Render<AiRunnerProfiles>();
        cut.WaitForAssertion(() => Assert.Equal(1, OptionReads()));
        var before = OptionReads();
        await cut.FindAll("button").First(button => button.TextContent.Contains("Create", StringComparison.Ordinal)).ClickAsync(new());
        return before;
    }

    private static AngleSharp.Dom.IElement FormField(IRenderedComponent<AiRunnerProfileDialog> cut, string label) =>
        cut.FindAll(".rz-form-field, .omni-form-field")
            .First(field => field.QuerySelector("label")?.TextContent.Trim().TrimEnd('*').Trim() == label)
            .QuerySelector("input")!;

    private static AiRunnerProfileDto Profile(int id, string name) => new() { Id = id, Name = name, Model = "m" };

    [Fact]
    public async Task ASavedNewProfile_ShowsAtOnce_BeforeTheReadBehindItEnds()
    {
        var gate = new TaskCompletionSource<PaginatedResult<AiRunnerProfileDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dialog.OpenResult = Profile(8, "added-profile");
        var cut = Render<AiRunnerProfiles>();
        cut.WaitForAssertion(() => Assert.Contains("reviewer", cut.Markup, StringComparison.Ordinal));
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "api/ai/profiles", _ => gate.Task);

        await cut.FindAll("button").First(button => button.TextContent.Contains("Create", StringComparison.Ordinal)).ClickAsync(new());

        // The read behind the row is held: the row came from the answer of the dialog alone, with no blocking bar.
        cut.WaitForAssertion(() => Assert.Contains("added-profile", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("reviewer", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("thead > tr.omni-data-grid__progress .omni-loading-bar"));
        gate.SetResult(new PaginatedResult<AiRunnerProfileDto> { Items = [Profile(6, "reviewer"), Profile(8, "added-profile")], TotalCount = 2, Page = 1, PageSize = 25 });
    }

    [Fact]
    public async Task ADeletedProfile_LeavesAtOnce_BeforeTheReadBehindItEnds()
    {
        var cut = Render<AiRunnerProfiles>();
        cut.WaitForAssertion(() => Assert.Contains("reviewer", cut.Markup, StringComparison.Ordinal));
        Dialog.ConfirmResult = true;
        _handler.SetResponse(HttpMethod.Delete, "api/ai/profiles/6", System.Net.HttpStatusCode.NoContent);
        var gate = new TaskCompletionSource<PaginatedResult<AiRunnerProfileDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "api/ai/profiles", _ => gate.Task);

        await cut.Find("button[title='Delete']").ClickAsync(new());

        cut.WaitForAssertion(() => Assert.DoesNotContain("reviewer", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("thead > tr.omni-data-grid__progress .omni-loading-bar"));
        gate.SetResult(new PaginatedResult<AiRunnerProfileDto> { Items = [], TotalCount = 0, Page = 1, PageSize = 25 });
    }

    [Fact]
    public void TheProfileDialog_ClosesWithTheSavedProfile_NotWithABare_True()
    {
        _handler.SetJsonResponse(HttpMethod.Post, "api/ai/profiles", Profile(9, "from-dialog"));
        var cut = Render<AiRunnerProfileDialog>();
        cut.WaitForAssertion(() => cut.Find("form"));
        FormField(cut, "Name").Input("from-dialog");
        FormField(cut, "AiApiKey").Input("sk-ant-typed");

        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Equal(9, Assert.IsType<AiRunnerProfileDto>(Dialog.LastCloseResult).Id));
    }

    [Fact]
    public async Task ASavedNewProfile_ReloadsTheListAndItsFilters()
    {
        var before = await CreateAsync(Profile(8, "added-profile"));

        Assert.Equal(typeof(AiRunnerProfileDialog), Dialog.LastComponent);
        Assert.Null(Dialog.LastParameters!["ProfileId"]);
        Assert.Equal("Create", Dialog.LastTitle);
        Assert.True(OptionReads() > before);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task ADismissedDialog_ReloadsNothing(object? answer)
    {
        var before = await CreateAsync(answer);

        Assert.Equal(typeof(AiRunnerProfileDialog), Dialog.LastComponent);
        Assert.Equal(before, OptionReads());
    }

    [Fact]
    public async Task EditingARow_OpensTheDialogOnThatProfile()
    {
        Dialog.OpenResult = null;
        var cut = Render<AiRunnerProfiles>();
        cut.WaitForAssertion(() => Assert.Contains("reviewer", cut.Markup, StringComparison.Ordinal));

        await cut.FindAll("button").First(button => button.GetAttribute("title") == "Edit").ClickAsync(new());

        Assert.Equal(6, Dialog.LastParameters!["ProfileId"]);
        Assert.Equal("Edit", Dialog.LastTitle);
    }
}
