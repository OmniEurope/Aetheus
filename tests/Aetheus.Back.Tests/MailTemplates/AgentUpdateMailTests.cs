// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Components.Notifications.MailTemplates;

namespace Aetheus.Back.Tests;

/// <summary>Follow-up of recette R-55: a failed agent update is worded in the recipient's language from its failure
/// code; only what the host itself reported is shown, and then as the host's raw output.</summary>
public sealed class AgentUpdateMailTests
{
    private const string Origin = "https://aetheus.example.org";

    private static string Html(string payload, string languageJson) => NotificationMailKit.Build(
        new NotificationEmail("reader@example.org", "agent-update.failed", "agent-update.failed", payload), Origin, languageJson)
        .HtmlBody ?? string.Empty;

    private static string Payload(string code, string diagnostic) =>
        $$"""{"ServerId":5,"ServerName":"vps","FromVersion":"1.0.0","ToVersion":"1.1.0","FailureCode":"{{code}}","FailureDiagnostic":"{{diagnostic}}"}""";

    [Theory]
    [InlineData("wrong-version", "Expected 1.1.0, received 1.0.0.", "sans passer")]
    [InlineData("unsupported-protocol", "Received unsupported protocol 9.", "protocole")]
    [InlineData("missing-capabilities", "Missing expected capabilities: a, b.", "capacités attendues")]
    [InlineData("confirmation-timeout", "No confirming heartbeat arrived before the deadline.", "signal de vie")]
    [InlineData("update-task-timeout", "The update task did not reach handoff before its execution deadline.", "passage de relais")]
    public void ACodedFailure_IsWordedInFrench_WithoutTheEnglishDiagnostic(string code, string diagnostic, string expectedFrench)
    {
        var html = Html(Payload(code, diagnostic), "\"fr-FR\"");

        Assert.Contains("Cause de l", html, StringComparison.Ordinal);
        Assert.Contains(expectedFrench, html, StringComparison.Ordinal);
        Assert.DoesNotContain(diagnostic, html, StringComparison.Ordinal);
        Assert.DoesNotContain(code, html, StringComparison.Ordinal);
        Assert.DoesNotContain("Sortie de l", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ACodedFailure_IsWordedInEnglish_ForAnEnglishRecipient()
    {
        var html = Html(Payload("wrong-version", "Expected 1.1.0, received 1.0.0."), "\"en-GB\"");

        Assert.Contains("Cause of the failure", html, StringComparison.Ordinal);
        Assert.Contains("without moving to the expected version", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Expected 1.1.0, received", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"fr-FR\"", "Sortie de l")]
    [InlineData("\"en-GB\"", "Host output:")]
    public void TheHostsOwnMessage_IsLabelledAsRawOutput_InAMonospaceBlock(string languageJson, string label)
    {
        var html = Html(Payload("agent-update-failed", "curl: (6) Could not resolve host"), languageJson);

        Assert.Contains(label, html, StringComparison.Ordinal);
        Assert.Matches("<pre[^>]*monospace[^>]*>curl: \\(6\\) Could not resolve host</pre>", html);
        Assert.DoesNotContain("agent-update-failed", html, StringComparison.Ordinal);
    }

    private static string HtmlWithProjectSubject(string languageJson) => NotificationMailKit.Build(
        new NotificationEmail("reader@example.org", "agent-update.failed in project Shop", "agent-update.failed",
            """{"AgentUpdateRequestId":42,"ServerId":5,"ServerName":"vps","ProjectId":7,"FromVersion":"1.0.0","ToVersion":"1.1.0","FailureCode":"wrong-version"}"""),
        Origin, languageJson).HtmlBody ?? string.Empty;

    [Theory]
    [InlineData("\"fr-FR\"", "Identifiant de la demande", "Identifiant du projet", "Identifiant du serveur")]
    [InlineData("\"en-GB\"", "Update request ID", "Project ID", "Server ID")]
    public void TheRawRequestProjectAndServerIdentifiers_AreNotShownAsRows_AndTheNamesAre(
        string languageJson, string requestLabel, string projectIdLabel, string serverIdLabel)
    {
        var html = HtmlWithProjectSubject(languageJson);

        Assert.DoesNotContain(requestLabel, html, StringComparison.Ordinal);
        Assert.DoesNotContain(projectIdLabel, html, StringComparison.Ordinal);
        Assert.DoesNotContain(serverIdLabel, html, StringComparison.Ordinal);
        Assert.Contains("vps", html, StringComparison.Ordinal);
        Assert.Contains("Shop", html, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownCode_KeepsItsCode_AndShowsTheDiagnosticAsHostOutput()
    {
        var html = Html(Payload("something-new", "boom"), "\"fr-FR\"");

        Assert.Contains("something-new", html, StringComparison.Ordinal);
        Assert.Matches("<pre[^>]*>boom</pre>", html);
    }
}
