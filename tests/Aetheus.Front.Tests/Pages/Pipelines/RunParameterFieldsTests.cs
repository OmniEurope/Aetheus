// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Pipelines;

public sealed class RunParameterFieldsTests : BunitContext
{
    public RunParameterFieldsTests() => BunitTestHelper.RegisterServices(this);

    private static List<PipelineRunParameterDto> CandidateParameter() =>
        [new() { Name = "candidateVersion", DisplayName = "Candidate", Type = "string", Required = true }];

    private static List<ReleaseDto> TwoReleases() =>
        [
            new() { Id = 1, Version = "1.1.482", Status = ReleaseStatus.Published },
            new() { Id = 2, Version = "1.1.481", Status = ReleaseStatus.Promoted }
        ];

    /// <summary>
    /// The point of the release list: clicking a release fills the required version field, then the
    /// user still confirms it - the value goes through the same collection and validation as a typed one.
    /// </summary>
    [Fact]
    public void ClickingARelease_FillsTheRequiredVersionField()
    {
        var cut = Render<RunParameterFields>(p => p
            .Add(x => x.Parameters, CandidateParameter())
            .Add(x => x.AvailableReleases, TwoReleases()));

        // Picking a release row fills the version field.
        cut.WaitForAssertion(() => Assert.Contains("1.1.482", cut.Markup));
        cut.FindAll("tr[data-omni-row-index]").First(row => row.TextContent.Contains("1.1.482")).Click();

        Assert.True(cut.Instance.TryCollect(out var values, out var error));
        Assert.Null(error);
        Assert.Equal("1.1.482", values["candidateVersion"]);
    }

    /// <summary>
    /// With several required-without-default text fields there is no unambiguous target, so the list
    /// stays informational: no fill buttons, and an empty submit still fails on the required check.
    /// </summary>
    [Fact]
    public void AmbiguousTarget_ShowsTheListWithoutFillButtons()
    {
        var parameters = new List<PipelineRunParameterDto>
        {
            new() { Name = "candidateVersion", DisplayName = "Candidate", Type = "string", Required = true },
            new() { Name = "reason", DisplayName = "Reason", Type = "string", Required = true }
        };
        var cut = Render<RunParameterFields>(p => p
            .Add(x => x.Parameters, parameters)
            .Add(x => x.AvailableReleases, TwoReleases()));

        cut.WaitForAssertion(() => Assert.Contains("1.1.482", cut.Markup));
        // Neither a row click nor a card click may guess between the two fields.
        cut.FindAll("tr[data-omni-row-index]").First(row => row.TextContent.Contains("1.1.482")).Click();
        cut.FindAll("[data-testid='release-picker-card']").First(card => card.TextContent.Contains("1.1.482")).Click();
        Assert.False(cut.Instance.TryCollect(out var values, out var error));
        Assert.NotNull(error);
        Assert.Empty(values);
        Assert.DoesNotContain("1.1.482", cut.FindAll("input").Select(input => input.GetAttribute("value")));
    }

    /// <summary>R3-13: a parameter's description sits behind the "?" of its OE field (Help), named by the
    /// control's aria-describedby, and nowhere else as text.</summary>
    [Fact]
    public void Description_IsBehindTheFieldQuestionMark_NotACaptionUnderTheField()
    {
        const string description = "Release to deploy, e.g. c-384f63...";
        var parameters = new List<PipelineRunParameterDto>
        {
            new() { Name = "candidateVersion", DisplayName = "Candidate", Type = "string", Required = true, Description = description },
            new() { Name = "dryRun", DisplayName = "Dry run", Type = "boolean", Description = "Plan only" },
            new() { Name = "plain", DisplayName = "Plain", Type = "string" }
        };
        var cut = Render<RunParameterFields>(p => p.Add(x => x.Parameters, parameters));

        var descriptionId = RunParameterFields.DescriptionId("candidateVersion", description)!;
        Assert.Equal(description, cut.Find($"#{descriptionId}").TextContent.Trim());
        Assert.Equal(descriptionId, cut.Find("input#candidateVersion").GetAttribute("aria-describedby"));
        Assert.Equal("Plan only", cut.Find($"#{RunParameterFields.DescriptionId("dryRun", "Plan only")}").TextContent.Trim());
        Assert.Empty(cut.FindAll($"#{RunParameterFields.FieldId("plain")}-help-content"));
        Assert.Null(cut.Find("input#plain").GetAttribute("aria-describedby"));
        Assert.Single(cut.FindAll("*"), element => element.Children.Length == 0
            && element.TextContent.Contains(description, StringComparison.Ordinal));
    }

    /// <summary>R3-6: a required parameter carries OE's required mark on its label, never a star written
    /// into the label text; an optional one carries none.</summary>
    [Fact]
    public void ARequiredParameter_CarriesTheOeRequiredMark_WithoutAHandWrittenStar()
    {
        var parameters = new List<PipelineRunParameterDto>
        {
            new() { Name = "target", DisplayName = "Target", Type = "string", Required = true },
            new() { Name = "confirm", DisplayName = "Confirm", Type = "boolean", Required = true },
            new() { Name = "plain", DisplayName = "Plain", Type = "string" }
        };
        var cut = Render<RunParameterFields>(p => p.Add(x => x.Parameters, parameters));

        Assert.Contains("omni-label--required", cut.Find("label[for='target']").ClassList);
        Assert.Contains("omni-label--required", cut.Find("label[for='confirm']").ClassList);
        Assert.DoesNotContain("omni-label--required", cut.Find("label[for='plain']").ClassList);
        // The only star is the mark OE draws (aria-hidden), none in the label text itself.
        var target = cut.Find("label[for='target']");
        Assert.Equal("*", target.QuerySelector(".omni-label__required")!.TextContent);
        Assert.Equal(1, target.TextContent.Count(character => character == '*'));
        Assert.DoesNotContain('*', cut.Find("label[for='plain']").TextContent);
        // The switch has no text of its own: the field label names it.
        Assert.Equal("confirm-label", cut.Find("#confirm[role='switch']").GetAttribute("aria-labelledby"));
    }
    [Fact]
    public void NoReleases_RendersNoReleaseBlock()
    {
        var cut = Render<RunParameterFields>(p => p.Add(x => x.Parameters, CandidateParameter()));

        Assert.DoesNotContain("1.1.", cut.Markup);
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Contains("-"));
    }

    [Fact]
    public void TryCollect_ReadsEachParameterTypeFromItsPrefillOrDefault()
    {
        var parameters = new List<PipelineRunParameterDto>
        {
            new() { Name = "dryRun", DisplayName = "Dry run", Type = "boolean", Default = "true" },
            new() { Name = "force", DisplayName = "Force", Type = "Boolean" },
            new() { Name = "count", DisplayName = "Count", Type = "number", Default = "3" },
            new() { Name = "ratio", DisplayName = "Ratio", Type = "number" },
            new() { Name = "env", DisplayName = "Env", Type = "choice", AllowedValues = ["dev", "prod"], Default = "dev" },
            new() { Name = "note", DisplayName = "Note", Type = "string" },
            new() { Name = "label", DisplayName = "Label" }
        };
        var prefill = new Dictionary<string, string> { ["count"] = "12.5", ["label"] = "nightly" };
        var cut = Render<RunParameterFields>(p => p
            .Add(x => x.Parameters, parameters)
            .Add(x => x.Prefill, prefill));

        Assert.True(cut.Instance.TryCollect(out var values, out var error));

        Assert.Null(error);
        Assert.Equal("true", values["dryRun"]);
        Assert.Equal("12.5", values["count"]);
        Assert.Equal("dev", values["env"]);
        Assert.Equal("nightly", values["label"]);
        Assert.False(values.ContainsKey("force"));
        Assert.False(values.ContainsKey("ratio"));
        Assert.False(values.ContainsKey("note"));
    }

    [Theory]
    [InlineData("boolean")]
    [InlineData("number")]
    [InlineData("choice")]
    [InlineData("string")]
    public void TryCollect_ARequiredParameterLeftEmpty_Fails(string type)
    {
        var parameters = new List<PipelineRunParameterDto>
        {
            new() { Name = "target", DisplayName = "Target", Type = type, Required = true, AllowedValues = ["a", "b"] }
        };
        var cut = Render<RunParameterFields>(p => p.Add(x => x.Parameters, parameters));

        Assert.False(cut.Instance.TryCollect(out var values, out var error));

        Assert.Empty(values);
        Assert.Contains("RunParameterRequired", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryCollect_ARequiredBooleanAnsweredFalse_IsKept()
    {
        var parameters = new List<PipelineRunParameterDto>
        {
            new() { Name = "confirm", DisplayName = "Confirm", Type = "boolean", Required = true, Default = "false" }
        };
        var cut = Render<RunParameterFields>(p => p.Add(x => x.Parameters, parameters));

        Assert.True(cut.Instance.TryCollect(out var values, out _));

        Assert.Equal("false", values["confirm"]);
    }

    /// <summary>French interfaces prefer supplied French labels and help, falling back to default
    /// text.</summary>
    [Theory]
    [InlineData("fr-FR", "Cible", "Où cela part")]
    [InlineData("en-US", "Target", "Where it goes")]
    public void TheLabelAndHelp_FollowTheInterfaceLanguage(string culture, string label, string help)
    {
        var previous = System.Globalization.CultureInfo.CurrentUICulture;
        System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo(culture);
        try
        {
            var cut = Render<RunParameterFields>(p => p.Add(x => x.Parameters,
            [
                new PipelineRunParameterDto
                {
                    Name = "target", DisplayName = "Target", DisplayNameFr = "Cible", Required = true,
                    Description = "Where it goes", DescriptionFr = "Où cela part"
                },
                new PipelineRunParameterDto { Name = "plain", DisplayName = "Plain" }
            ]));

            Assert.Contains(label, cut.Markup, StringComparison.Ordinal);
            Assert.Contains(help, cut.Markup, StringComparison.Ordinal);
            Assert.Contains("Plain", cut.Markup, StringComparison.Ordinal);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = previous;
        }
    }
}
