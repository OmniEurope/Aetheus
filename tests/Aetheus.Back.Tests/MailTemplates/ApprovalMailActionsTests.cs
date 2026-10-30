// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Notifications.MailTemplates;

namespace Aetheus.Back.Tests;

/// <summary>
/// Recette R-24: an approval request mail opens on its Approve, Reject and Comment buttons, plain links to the
/// approval's page (no signature, no token), Approve in Primary blue, Reject in Danger red, Comment in the
/// neutral Secondary grey. The page acts; the link itself never does.
/// </summary>
public sealed class ApprovalMailActionsTests
{
    private const string Origin = "https://aetheus.example.org";

    private static NotificationEmail ApprovalRequest(string payload = """{"PipelineRunId":2698,"PipelineId":41,"PipelineName":"deploy-prod","BuildNumber":12,"ProjectId":3,"StageName":"Deploy","EnvironmentName":"prod","ApprovalId":55}""") =>
        new("reader@example.org", "pipeline.approval-requested in project Shop", "pipeline.approval-requested", payload);

    private static string Href(string action) =>
        $"href=\"{Origin}/pipelines/runs/2698/approvals/55?action={action}\"";

    [Fact]
    public void TheThreeButtons_OpenTheMail_AboveTheExplanation_InFrench()
    {
        var html = NotificationMailKit.Build(ApprovalRequest(), Origin, languageJson: "\"fr-FR\"").HtmlBody!;

        var approve = html.IndexOf(Href("approve"), StringComparison.Ordinal);
        var reject = html.IndexOf(Href("reject"), StringComparison.Ordinal);
        var comment = html.IndexOf(Href("comment"), StringComparison.Ordinal);
        var intro = html.IndexOf("Un pipeline attend une approbation.", StringComparison.Ordinal);
        Assert.True(approve > 0 && reject > approve && comment > reject, "The three links are missing or out of order.");
        Assert.True(intro > comment, "The buttons do not come before the explanation.");
        Assert.Contains(">Approuver</a>", html, StringComparison.Ordinal);
        Assert.Contains(">Rejeter</a>", html, StringComparison.Ordinal);
        Assert.Contains(">Commenter</a>", html, StringComparison.Ordinal);
        // The approval's own identifier only feeds the links: it is no detail row.
        Assert.DoesNotContain("ApprovalId", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TheButtons_AreColoured_PrimaryDangerSecondary()
    {
        var block = ApprovalMailActions.Block("pipeline.approval-requested", 2698, 55, Origin,
            Aetheus.Back.Services.Email.MailBlockColours.Light, key => key);

        Assert.StartsWith("<table><tr><td style=\"padding:10px 18px;background-color:#1565c0;color:#ffffff\">", block, StringComparison.Ordinal);
        Assert.Contains("background-color:#c62828;color:#ffffff\"><a href=\"" + Origin + "/pipelines/runs/2698/approvals/55?action=reject\"", block, StringComparison.Ordinal);
        Assert.Contains("background-color:#eef0f3;color:#404b5a\"><a href=\"" + Origin + "/pipelines/runs/2698/approvals/55?action=comment\"", block, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLinks_CarryNoSignatureOrToken()
    {
        var html = NotificationMailKit.Build(ApprovalRequest(), Origin).HtmlBody!;

        foreach (var action in new[] { "approve", "reject", "comment" })
            Assert.Contains(Href(action), html, StringComparison.Ordinal);
        Assert.DoesNotContain("token=", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("signature=", html, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("pipeline.failed", """{"PipelineRunId":2698,"ApprovalId":55}""", Origin)]
    [InlineData("pipeline.approval-requested", """{"PipelineRunId":2698}""", Origin)]
    [InlineData("pipeline.approval-requested", """{"PipelineRunId":2698,"ApprovalId":55}""", null)]
    public void NoButtons_WithoutAnApprovalToOpen(string eventType, string payload, string? origin)
    {
        var html = NotificationMailKit.Build(new NotificationEmail("reader@example.org", eventType, eventType, payload), origin).HtmlBody!;

        Assert.DoesNotContain("/approvals/", html, StringComparison.Ordinal);
        Assert.DoesNotContain("?action=", html, StringComparison.Ordinal);
    }
}
