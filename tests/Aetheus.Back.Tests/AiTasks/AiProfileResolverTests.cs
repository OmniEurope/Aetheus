// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AiTasks;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Tests.AiTasks;

/// <summary>
/// The profile a pipeline <c>ai</c> step runs with: its own named profile, else the pipeline's assigned
/// one, else the organization's most preferred usable one; and the sentence that says which and why.
/// </summary>
public sealed class AiProfileResolverTests
{
    private static AiRunnerProfile Profile(int id, string name, int priority, bool usable = true) => new()
    {
        Id = id,
        OrganizationId = 1,
        Name = name,
        Priority = priority,
        Provider = "anthropic",
        Model = usable ? "claude-sonnet" : string.Empty,
        ApiKeyEncrypted = usable ? "sk-key" : string.Empty
    };

    [Fact]
    public void TheStepsOwnProfile_WinsOverThePipelineProfileAndPriority()
    {
        var profiles = new[] { Profile(1, "Claude", 10), Profile(2, "claude-sonnet", 50), Profile(3, "Other", 5) };

        var selection = AiProfileResolver.Resolve("claude-sonnet", pipelineProfileId: 3, profiles);

        Assert.Equal(2, selection.Profile!.Id);
        Assert.False(selection.IsFallback);
        Assert.Equal("Using AI runner profile 'claude-sonnet' named by the step.", selection.Reason);
    }

    [Theory]
    [InlineData("CLAUDE-SONNET")]
    [InlineData("  claude-sonnet  ")]
    public void TheStepName_IsMatchedIgnoringCaseAndSurroundingSpaces(string requested)
    {
        var selection = AiProfileResolver.Resolve(requested, null, [Profile(1, "Claude-Sonnet", 10)]);

        Assert.Equal(1, selection.Profile!.Id);
        Assert.False(selection.IsFallback);
    }

    [Fact]
    public void AnExactNameMatch_BeatsAnotherCaseOfTheSameName()
    {
        var profiles = new[] { Profile(1, "claude", 10), Profile(2, "Claude", 20) };

        Assert.Equal(2, AiProfileResolver.Resolve("Claude", null, profiles).Profile!.Id);
    }

    [Fact]
    public void AMissingStepName_FallsBackOnThePipelineProfile_AndSaysSo()
    {
        var profiles = new[] { Profile(1, "Claude", 10), Profile(2, "Pipeline one", 90) };

        var selection = AiProfileResolver.Resolve("claude-sonnet", pipelineProfileId: 2, profiles);

        Assert.Equal(2, selection.Profile!.Id);
        Assert.True(selection.IsFallback);
        Assert.Equal(
            "Profile 'claude-sonnet' named by the step was not found in the organization. "
            + "Using the pipeline's AI runner profile 'Pipeline one'.",
            selection.Reason);
    }

    [Fact]
    public void AStepWithoutAName_UsesThePipelineProfile()
    {
        var profiles = new[] { Profile(1, "Claude", 10), Profile(2, "Pipeline one", 90) };

        var selection = AiProfileResolver.Resolve(null, pipelineProfileId: 2, profiles);

        Assert.Equal(2, selection.Profile!.Id);
        Assert.True(selection.IsFallback);
        Assert.Equal("Using the pipeline's AI runner profile 'Pipeline one'.", selection.Reason);
    }

    [Fact]
    public void WithoutNameOrPipelineProfile_TheLowestPriorityUsableProfileIsUsed()
    {
        var profiles = new[]
        {
            Profile(1, "Broken but preferred", 1, usable: false),
            Profile(2, "Second", 30),
            Profile(3, "First", 20)
        };

        var selection = AiProfileResolver.Resolve(" ", null, profiles);

        Assert.Equal(3, selection.Profile!.Id);
        Assert.True(selection.IsFallback);
        Assert.Contains("'First' (priority 20", selection.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ATieOnPriority_GoesToTheLowestId()
    {
        var profiles = new[] { Profile(7, "Later", 10), Profile(4, "Earlier", 10) };

        Assert.Equal(4, AiProfileResolver.Resolve(null, null, profiles).Profile!.Id);
    }

    [Fact]
    public void AnUnusableNamedProfile_FallsThrough_AndTheReasonSaysWhy()
    {
        var profiles = new[] { Profile(1, "claude-sonnet", 10, usable: false), Profile(2, "Backup", 20) };

        var selection = AiProfileResolver.Resolve("claude-sonnet", null, profiles);

        Assert.Equal(2, selection.Profile!.Id);
        Assert.Contains("Profile 'claude-sonnet' named by the step cannot be used", selection.Reason, StringComparison.Ordinal);
        Assert.Contains("Using AI runner profile 'Backup'", selection.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnusablePipelineProfile_FallsThroughToThePriorityOrder()
    {
        var profiles = new[] { Profile(1, "Broken", 5, usable: false), Profile(2, "Backup", 20) };

        var selection = AiProfileResolver.Resolve(null, pipelineProfileId: 1, profiles);

        Assert.Equal(2, selection.Profile!.Id);
        Assert.Contains("The pipeline's profile 'Broken' cannot be used", selection.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void APipelineProfileOutsideTheGivenOrganizationProfiles_IsNeverUsed()
    {
        // The caller passes only the pipeline's own organization: an id belonging to another organization
        // is simply not among the candidates, whatever its name or priority.
        var ownProfiles = new[] { Profile(1, "Mine", 50) };

        var selection = AiProfileResolver.Resolve("Foreign", pipelineProfileId: 99, ownProfiles);

        Assert.Equal(1, selection.Profile!.Id);
        Assert.Contains("not found in the organization", selection.Reason, StringComparison.Ordinal);
        Assert.Contains("is not in the pipeline's organization", selection.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void WhenNoProfileCanRun_TheProblemListsEveryReason()
    {
        var profiles = new[] { Profile(1, "Keyless", 10, usable: false), Profile(2, "Empty", 20, usable: false) };

        var selection = AiProfileResolver.Resolve("claude-sonnet", null, profiles);

        Assert.Null(selection.Profile);
        Assert.StartsWith("No AI runner profile can run this step.", selection.Problem, StringComparison.Ordinal);
        Assert.Contains("'claude-sonnet' named by the step was not found", selection.Problem, StringComparison.Ordinal);
        Assert.Contains("'Keyless' must be reconfigured", selection.Problem, StringComparison.Ordinal);
        Assert.Contains("'Empty' must be reconfigured", selection.Problem, StringComparison.Ordinal);
        Assert.Equal(selection.Problem, selection.Reason);
    }

    [Fact]
    public void AnOrganizationWithoutAnyProfile_SaysSo()
    {
        var selection = AiProfileResolver.Resolve("claude-sonnet", null, []);

        Assert.Null(selection.Profile);
        Assert.Contains("The pipeline's organization has no AI runner profile.", selection.Problem, StringComparison.Ordinal);
    }
}
