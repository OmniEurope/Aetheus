// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>The launch dialog returns the chosen branch, the filled parameters and the run-to-end choice; a
/// missing required parameter keeps it open with the reason; another branch re-reads its own parameters,
/// and a failed re-read keeps the previous fields.</summary>
public sealed class PipelineLaunchDialogTests : BunitContext
{
    private readonly ImmediateDialogService _dialogs;

    public PipelineLaunchDialogTests()
    {
        BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        _dialogs = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
    }

    private static List<GitLightBranchDto> Branches() =>
    [
        new() { Name = "develop" },
        new() { Name = "main", IsDefault = true },
        new() { Name = "release" },
    ];

    private static Task SubmitAsync(IRenderedComponent<PipelineLaunchDialog> cut) => cut.Find("form").SubmitAsync();

    private PipelineLaunchChoice Choice() => Assert.IsType<PipelineLaunchChoice>(_dialogs.LastCloseResult);

    [Fact]
    public async Task Submitting_ReturnsTheDefaultBranchAndTheFilledParameters()
    {
        var cut = Render<PipelineLaunchDialog>(parameters => parameters
            .Add(dialog => dialog.Branches, Branches())
            .Add(dialog => dialog.Parameters, [new PipelineRunParameterDto { Name = "region", Default = "eu" }]));

        await SubmitAsync(cut);

        Assert.Equal(1, _dialogs.CloseCount);
        Assert.Equal("main", Choice().SourceBranch);
        Assert.Equal("eu", Choice().Parameters!["region"]);
        Assert.False(Choice().RunToEnd);
    }

    [Fact]
    public async Task AMissingRequiredParameter_KeepsTheDialogOpenWithTheReason()
    {
        var cut = Render<PipelineLaunchDialog>(parameters => parameters
            .Add(dialog => dialog.Parameters, [new PipelineRunParameterDto { Name = "version", DisplayName = "Version", Required = true }]));

        await SubmitAsync(cut);

        Assert.Equal(0, _dialogs.CloseCount);
        Assert.Contains("RunParameterRequired", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunToEnd_IsReturnedWhenChosen_AndNoParametersMeansNone()
    {
        var cut = Render<PipelineLaunchDialog>(parameters => parameters
            .Add(dialog => dialog.SourceBranch, "release")
            .Add(dialog => dialog.Branches, Branches())
            .Add(dialog => dialog.RunToEndAllowed, true));
        await cut.InvokeAsync(() => cut.FindComponent<OmniSwitch<bool>>().Instance.ValueChanged.InvokeAsync(true));

        await SubmitAsync(cut);

        Assert.Equal("release", Choice().SourceBranch);
        Assert.Null(Choice().Parameters);
        Assert.True(Choice().RunToEnd);
    }

    [Fact]
    public async Task AnotherBranch_ReadsItsOwnParameters_AndTheSameBranchReadsNothing()
    {
        var reads = new List<string?>();
        var cut = Render<PipelineLaunchDialog>(parameters => parameters
            .Add(dialog => dialog.Branches, Branches())
            .Add(dialog => dialog.Parameters, [new PipelineRunParameterDto { Name = "region", Default = "eu" }])
            .Add(dialog => dialog.ReloadParameters, branch =>
            {
                reads.Add(branch);
                return Task.FromResult<List<PipelineRunParameterDto>?>(
                    [new PipelineRunParameterDto { Name = "channel", Default = "beta" }]);
            }));
        var branch = cut.FindComponent<OmniDropDown<string>>();

        await cut.InvokeAsync(() => branch.Instance.ValueChanged.InvokeAsync("main"));
        await cut.InvokeAsync(() => branch.Instance.ValueChanged.InvokeAsync("release"));
        await SubmitAsync(cut);

        Assert.Equal(["release"], reads);
        Assert.Equal("release", Choice().SourceBranch);
        Assert.Equal("beta", Choice().Parameters!["channel"]);
        Assert.False(Choice().Parameters!.ContainsKey("region"));
    }

    [Fact]
    public async Task AFailedReRead_KeepsThePreviousFields()
    {
        var cut = Render<PipelineLaunchDialog>(parameters => parameters
            .Add(dialog => dialog.Branches, Branches())
            .Add(dialog => dialog.Parameters, [new PipelineRunParameterDto { Name = "region", Default = "eu" }])
            .Add(dialog => dialog.ReloadParameters, _ => Task.FromResult<List<PipelineRunParameterDto>?>(null)));

        await cut.InvokeAsync(() => cut.FindComponent<OmniDropDown<string>>().Instance.ValueChanged.InvokeAsync("develop"));
        await SubmitAsync(cut);

        Assert.Equal("develop", Choice().SourceBranch);
        Assert.Equal("eu", Choice().Parameters!["region"]);
    }

    [Fact]
    public async Task WithoutReRead_AnotherBranchOnlyChangesTheBranch()
    {
        var cut = Render<PipelineLaunchDialog>(parameters => parameters
            .Add(dialog => dialog.Branches, Branches())
            .Add(dialog => dialog.Parameters, [new PipelineRunParameterDto { Name = "region", Default = "eu" }]));

        await cut.InvokeAsync(() => cut.FindComponent<OmniDropDown<string>>().Instance.ValueChanged.InvokeAsync("develop"));
        await SubmitAsync(cut);

        Assert.Equal("develop", Choice().SourceBranch);
        Assert.Equal("eu", Choice().Parameters!["region"]);
    }
}
