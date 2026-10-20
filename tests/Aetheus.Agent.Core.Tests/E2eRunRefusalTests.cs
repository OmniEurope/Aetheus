// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Functions;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The three groups of <see cref="E2eRun.Refusal"/> on their own: the run shape (label, workers), the
/// flags and the filter, and the expected commit, each accepting every valid value and the whole
/// refusal keeping the script's order when several inputs are wrong.
/// </summary>
public sealed class E2eRunRefusalTests
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";

    private static E2eRun Run(
        string label = "qa-42-current", string workers = "1", string preserve = "false",
        string previous = "false", string filter = "", string commit = Commit) =>
        new(label, "front", "back", "password", "image", commit, "project", preserve, previous, filter, workers, "results");

    [Theory]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("3")]
    [InlineData("4")]
    public void ShapeRefusal_AcceptsOneToFourWorkers(string workers) =>
        Assert.Null(Run(workers: workers).ShapeRefusal());

    [Theory]
    [InlineData("qa 42", "1", "Invalid E2E run label.")]
    [InlineData("qa-42", "0", "AETHEUS_E2E_WORKERS must be 1 to 4.")]
    [InlineData("qa-42", "", "AETHEUS_E2E_WORKERS must be 1 to 4.")]
    public void ShapeRefusal_NamesTheFirstWrongInput(string label, string workers, string reason) =>
        Assert.Equal(reason, Run(label: label, workers: workers).ShapeRefusal());

    [Theory]
    [InlineData("true", "true", "")]
    [InlineData("false", "true", E2eRun.RollbackRelevantFilter)]
    [InlineData("true", "false", E2eRun.RollbackRelevantFilter)]
    public void FlagRefusal_AcceptsBothBooleansAndTheOneFilter(string preserve, string previous, string filter) =>
        Assert.Null(Run(preserve: preserve, previous: previous, filter: filter).FlagRefusal());

    [Theory]
    [InlineData("True", "false", "", "Preserve-database flag must be true or false.")]
    [InlineData("false", "yes", "", "E2E_PREVIOUS_PAYLOAD_ONLY must be true or false.")]
    [InlineData("false", "false", "Category=Smoke", "E2E_TEST_FILTER must be empty or Category=RollbackRelevant.")]
    public void FlagRefusal_NamesTheFirstWrongInput(string preserve, string previous, string filter, string reason) =>
        Assert.Equal(reason, Run(preserve: preserve, previous: previous, filter: filter).FlagRefusal());

    [Theory]
    [InlineData(Commit, null)]
    [InlineData("0123456789ABCDEF0123456789ABCDEF01234567", null)]
    [InlineData("0123456789abcdef0123456789abcdef0123456", "Expected E2E source commit must contain 40 hexadecimal characters.")]
    [InlineData("0123456789abcdef0123456789abcdef0123456g", "Expected E2E source commit is not hexadecimal.")]
    public void CommitRefusal_RequiresFortyHexadecimalCharacters(string commit, string? reason) =>
        Assert.Equal(reason, Run(commit: commit).CommitRefusal());

    [Fact]
    public void Refusal_KeepsTheScriptOrderAcrossGroups()
    {
        Assert.Null(Run().Refusal());
        Assert.Equal("Invalid E2E run label.", Run(label: "a b", preserve: "x", commit: "z").Refusal());
        Assert.Equal("Preserve-database flag must be true or false.", Run(preserve: "x", commit: "z").Refusal());
        Assert.Equal("Expected E2E source commit must contain 40 hexadecimal characters.", Run(commit: "z").Refusal());
    }
}
