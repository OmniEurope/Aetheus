// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// R-113: a warning stored on a run is a problem (code, parameters, English sentence) in one string, so the front
/// says it in the user's language at display time. These tests pin the stored form the front and the back read.
/// </summary>
public sealed class StoredProblemTests
{
    private static ProblemDto Problem(string code, params (string Name, string Value)[] parameters) =>
        new(code, parameters.ToDictionary(parameter => parameter.Name, parameter => parameter.Value));

    [Fact]
    public void AStoredProblem_ReadsBackItsCodeParametersAndEnglish()
    {
        var stored = StoredProblem.Compose(
            Problem("VariableLibraryNotFound", ("library", "prod-vars")), "Variable library 'prod-vars' not found.");

        Assert.True(StoredProblem.TryRead(stored, out var content));
        Assert.Equal("VariableLibraryNotFound", content.Problem.Code);
        Assert.Equal("prod-vars", content.Problem.Parameters["library"]);
        Assert.Equal("Variable library 'prod-vars' not found.", content.English);
        Assert.Empty(content.Inner);
    }

    [Fact]
    public void TheInnerProblems_AreKeptInOrder()
    {
        var stored = StoredProblem.Compose(
            Problem("StepFailedDeploy", ("step", "Deploy")), "Deploy step 'Deploy': refused",
            [Problem("RequiredVaultMissing", ("vault", "a")), Problem("RequiredLibraryMissing", ("library", "b"))]);

        Assert.True(StoredProblem.TryRead(stored, out var content));
        Assert.Equal(["RequiredVaultMissing", "RequiredLibraryMissing"], content.Inner.Select(problem => problem.Code));
    }

    [Fact]
    public void TheEnglishSentence_StaysReadableInsideTheStoredString()
    {
        var stored = StoredProblem.Compose(Problem("X", ("name", "o'neil")), "Stage 'Build' <failed> & stopped");

        Assert.Contains("Stage 'Build' <failed> & stopped", stored, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Stage 'Build': no online pipeline runner available.")]
    [InlineData("problem:not json")]
    [InlineData("problem:{\"parameters\":{}}")]
    public void AnythingThatIsNotAStoredProblem_IsNotRead_AndKeepsItsOwnText(string? text)
    {
        Assert.False(StoredProblem.TryRead(text, out _));
        Assert.Null(StoredProblem.CodeOf(text));
        Assert.Equal(text ?? string.Empty, text is null ? string.Empty : StoredProblem.English(text));
    }

    [Fact]
    public void ToStored_OfAPipelineProblem_CarriesItsCodeAndEnglish()
    {
        var problem = PipelineProblem.Of(
            PipelineProblemCodes.ChainedPipelineGone, "pipeline 7 no longer exists", ("pipeline", 7));

        var stored = problem.ToStored();

        Assert.Equal("ChainedPipelineGone", StoredProblem.CodeOf(stored));
        Assert.Equal("7", StoredProblem.ParameterOf(stored, "pipeline"));
        Assert.Equal("pipeline 7 no longer exists", StoredProblem.English(stored));
    }

    [Fact]
    public void SeveralProblems_AreStoredTogether_WithEachOneInside()
    {
        var first = PipelineProblem.Of(PipelineProblemCodes.RequiredVaultMissing, "Vault 'a' missing.", ("vault", "a"));
        var second = PipelineProblem.Of(PipelineProblemCodes.RequiredLibraryMissing, "Library 'b' missing.", ("library", "b"));

        var stored = PipelineProblem.StoredTogether([first, second]);

        Assert.NotNull(stored);
        Assert.Equal("ProblemsTogether", StoredProblem.CodeOf(stored));
        Assert.Equal("Vault 'a' missing. Library 'b' missing.", StoredProblem.English(stored));
        Assert.True(StoredProblem.TryRead(stored, out var content));
        Assert.Equal(["RequiredVaultMissing", "RequiredLibraryMissing"], content.Inner.Select(problem => problem.Code));
        Assert.Null(PipelineProblem.StoredTogether([]));
        Assert.Equal("RequiredVaultMissing", StoredProblem.CodeOf(PipelineProblem.StoredTogether([first])));
    }
}
