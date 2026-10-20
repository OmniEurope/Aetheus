// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Shared;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// Recette R3-67: <c>${{ project.slug }}</c> in a library or vault name is the project's slug, the
/// prefix the analytics vault already used (<c>&lt;slug&gt;.analytics</c>).
/// </summary>
public sealed class PipelineProjectResourceNamesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Aetheus", 1, "aetheus")]
    [InlineData("Mon Aetheus", 2, "mon-aetheus")]
    [InlineData("  Été / 2026  ", 3, "t-2026")]
    [InlineData("***", 4, "project-4")]
    public void Slug_IsTheLowerCaseHyphenatedName(string name, int id, string expected) =>
        Assert.Equal(expected, ProjectResourceNames.Slug(name, id));

    [Fact]
    public void Slug_IsBoundedTo90Characters() =>
        Assert.Equal(90, ProjectResourceNames.Slug(new string('a', 120), 1).Length);

    [Theory]
    [InlineData("${{ project.slug }}.share", "aetheus.share")]
    [InlineData("${{project.slug}}.prod", "aetheus.prod")]
    [InlineData("aetheus.share", "aetheus.share")]
    [InlineData("shared-${{ project.slug }}", "shared-aetheus")]
    public void Expand_ReplacesTheSlugExpression(string name, string expected) =>
        Assert.Equal(expected, PipelineProjectResourceNames.Expand(name, "aetheus"));

    [Fact]
    public void Expand_WithoutAProject_LeavesTheExpression_SoNoResourceCanMatchIt() =>
        Assert.Equal("${{ project.slug }}.share", PipelineProjectResourceNames.Expand("${{ project.slug }}.share", null));

    [Fact]
    public void Expand_CoversReadAndDeclaredLibrariesAndVaults()
    {
        var definition = new PipelineYamlDefinition
        {
            Name = "p",
            VariableLibraries = ["${{ project.slug }}.share", "common"],
            Vaults = ["${{ project.slug }}.qa"],
            Requires = new PipelineRequiresDefinition
            {
                Libraries = ["${{ project.slug }}.prod"],
                Vaults = ["${{ project.slug }}.package"],
                Environments = ["${{ project.slug }}-env"]
            }
        };

        var expanded = PipelineProjectResourceNames.Expand(definition, "wordmorrow");

        Assert.Equal(["wordmorrow.share", "common"], expanded.VariableLibraries);
        Assert.Equal(["wordmorrow.qa"], expanded.Vaults);
        Assert.Equal(["wordmorrow.prod"], expanded.Requires!.Libraries);
        Assert.Equal(["wordmorrow.package"], expanded.Requires.Vaults);
        // Only library and vault names are project-relative; an environment name is left as written.
        Assert.Equal(["${{ project.slug }}-env"], expanded.Requires.Environments);
    }

    [Fact]
    public void Expand_WithoutTheExpression_ReturnsTheSameDefinition()
    {
        var definition = new PipelineYamlDefinition { Name = "p", VariableLibraries = ["aetheus.share"] };

        Assert.Same(definition, PipelineProjectResourceNames.Expand(definition, "aetheus"));
    }

    [Fact]
    public async Task ExpandForProjectAsync_ReadsTheProjectName()
    {
        var repo = Substitute.For<IPipelineRepository>();
        repo.GetProjectNameAsync(5, Arg.Any<CancellationToken>()).Returns("Wordmorrow");
        var definition = new PipelineYamlDefinition { Name = "p", Vaults = ["${{ project.slug }}.package"] };

        var expanded = await PipelineProjectResourceNames.ExpandForProjectAsync(definition, repo, 5, Ct);

        Assert.Equal(["wordmorrow.package"], expanded.Vaults);
    }

    [Fact]
    public async Task ExpandForProjectAsync_DoesNotReadTheProject_WhenNoNameUsesTheExpression()
    {
        var repo = Substitute.For<IPipelineRepository>();
        var definition = new PipelineYamlDefinition { Name = "p", VariableLibraries = ["aetheus.share"] };

        await PipelineProjectResourceNames.ExpandForProjectAsync(definition, repo, 5, Ct);

        await repo.DidNotReceive().GetProjectNameAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("${{ project.slug }}.share", false)]
    [InlineData("app-${{ parameters.environment }}", false)]
    [InlineData("aetheus.share", false)]
    [InlineData("${{ project.name }}.share", true)]
    [InlineData("${{ variables.X }}", true)]
    public void HasUnsupportedExpression_AllowsOnlyTheSlugAndTemplateParameters(string name, bool expected) =>
        Assert.Equal(expected, PipelineProjectResourceNames.HasUnsupportedExpression(name));

    [Fact]
    public void Validator_RefusesALibraryNameWithAnExpressionNothingReplaces()
    {
        var errors = new List<string>();
        PipelineDefinitionValidator.ValidateDefinitionBasics(
            new PipelineYamlDefinition { Name = "p", VariableLibraries = ["${{ project.name }}.share"] }, errors, []);

        Assert.Contains(errors, error => error.Contains("accept only the ${{ project.slug }}", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_AcceptsTheSlugExpression()
    {
        var errors = new List<string>();
        PipelineDefinitionValidator.ValidateDefinitionBasics(
            new PipelineYamlDefinition { Name = "p", VariableLibraries = ["${{ project.slug }}.share"], Vaults = ["${{ project.slug }}.qa"] },
            errors, []);

        Assert.DoesNotContain(errors, error => error.Contains("accept only", StringComparison.Ordinal));
    }
}
