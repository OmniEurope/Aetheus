// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.AiTasks;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Ai;

/// <summary>Stored proposed patches remain readable and downloadable, but cannot be applied through
/// the API (ADR-023).</summary>
public sealed class AiResultDialogTests : BunitContext
{
    public AiResultDialogTests()
    {
        BunitTestHelper.RegisterServices(this);
    }

    [Fact]
    public void AHistoricalPatch_IsReadableAndDownloadable_ButCannotBeApplied()
    {
        var result = new AiRunResultDto
        {
            Id = 7,
            ProfileName = "reviewer",
            ReportMarkdown = "report",
            DiffPatch = "--- a/x\n+++ b/x\n",
            ProjectId = 3,
            Succeeded = true
        };
        var cut = Render<AiResultDialog>(p => p.Add(x => x.InitialResult, result));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role='tab']")));
        cut.FindAll("[role='tab']").Single(b => b.TextContent.Contains("Diff", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Contains("DownloadPatch", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("ApplyToBranch", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplyOnProposedBranch", cut.Markup, StringComparison.Ordinal);
    }
}
