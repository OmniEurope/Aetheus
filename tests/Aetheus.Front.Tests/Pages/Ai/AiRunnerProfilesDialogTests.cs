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

    [Fact]
    public async Task ASavedNewProfile_ReloadsTheListAndItsFilters()
    {
        var before = await CreateAsync(true);

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
