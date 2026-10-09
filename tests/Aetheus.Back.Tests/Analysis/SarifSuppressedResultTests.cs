// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis;

namespace Aetheus.Back.Tests.Analysis;

/// <summary>
/// A suppressed SARIF result is one somebody already decided about, in the source, with a written
/// justification. Reporting it again turns every accepted exception back into an open finding at the
/// next run, and a gate that keeps raising what has already been answered is a gate people stop
/// reading.
///
/// This matters concretely here: `SEC004` flags a secret-shaped property on a wire contract, and six
/// of them in this repository are deliberate disclosures carrying a `SuppressMessage` with its
/// reason. Publishing the Roslyn SARIF without honouring suppressions would file all six as fresh
/// security findings on every build.
/// </summary>
public sealed class SarifSuppressedResultTests
{
    private static string Report(string resultBody) => $$"""
        {
          "version": "2.1.0",
          "runs": [{
            "tool": { "driver": { "name": "Roslyn", "rules": [{ "id": "SEC004" }] } },
            "results": [{{resultBody}}]
          }]
        }
        """;

    private const string Location = """
        "locations": [{ "physicalLocation": {
          "artifactLocation": { "uri": "file:///workspace/src/Dtos.cs" },
          "region": { "startLine": 12 }
        }}]
        """;

    private static string Result(string? suppressions) => $$"""
        {
          "ruleId": "SEC004",
          "message": { "text": "returns a secret-shaped property" },
          {{Location}}{{(suppressions is null ? "" : "," + suppressions)}}
        }
        """;

    [Fact]
    public void AResultSuppressedInSourceIsNotReportedAgain()
    {
        var sarif = Report(Result("""
            "suppressions": [{
              "kind": "inSource",
              "justification": "Enrolment response; the token is the agent's only credential."
            }]
            """));

        Assert.Empty(SarifAnalysisParser.Parse(sarif, AnalysisCategory.Sast));
    }

    [Fact]
    public void AnAcceptedSuppressionIsHonoured()
    {
        var sarif = Report(Result("""
            "suppressions": [{ "kind": "external", "status": "accepted" }]
            """));

        Assert.Empty(SarifAnalysisParser.Parse(sarif, AnalysisCategory.Sast));
    }

    [Fact]
    public void AnUnsuppressedResultIsStillReported()
    {
        Assert.Single(SarifAnalysisParser.Parse(Report(Result(null)), AnalysisCategory.Sast));
    }

    [Fact]
    public void AnEmptySuppressionsArrayMeansTheToolFoundNoneAndIsNotASuppression()
    {
        // SARIF 2.1.0 distinguishes the two: an empty array is the tool saying it looked and found
        // nothing, which is the opposite of a suppression. Reading it as one would silently drop
        // every finding produced by tools that always emit the field.
        var sarif = Report(Result("""
            "suppressions": []
            """));

        Assert.Single(SarifAnalysisParser.Parse(sarif, AnalysisCategory.Sast));
    }

    [Theory]
    [InlineData("rejected")]
    [InlineData("underReview")]
    public void ASuppressionThatWasNotAcceptedLeavesTheFindingOpen(string status)
    {
        // A rejected or still-reviewed suppression is an open question; hiding the finding would
        // answer it on the tool's behalf.
        var sarif = Report(Result($$"""
            "suppressions": [{ "kind": "external", "status": "{{status}}" }]
            """));

        Assert.Single(SarifAnalysisParser.Parse(sarif, AnalysisCategory.Sast));
    }

    [Fact]
    public void OnlyTheSuppressedResultsAreDroppedFromAMixedReport()
    {
        var sarif = $$"""
            {
              "version": "2.1.0",
              "runs": [{
                "tool": { "driver": { "name": "Roslyn", "rules": [{ "id": "SEC004" }] } },
                "results": [
                  {
                    "ruleId": "SEC004",
                    "message": { "text": "justified disclosure" },
                    {{Location}},
                    "suppressions": [{ "kind": "inSource", "justification": "documented" }]
                  },
                  {
                    "ruleId": "SEC004",
                    "message": { "text": "new disclosure nobody reviewed" },
                    {{Location}}
                  }
                ]
              }]
            }
            """;

        var finding = Assert.Single(SarifAnalysisParser.Parse(sarif, AnalysisCategory.Sast));

        Assert.Contains("nobody reviewed", finding.Message, StringComparison.Ordinal);
    }
}
