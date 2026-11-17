// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Components.Notifications.MailTemplates;

namespace Aetheus.Back.Tests;

/// <summary>R-30 follow-up: the mail of a refused automated launch says the reason in the recipient's language
/// from the problem codes of the payload, and keeps the English reason for a payload that has none.</summary>
public sealed class LaunchRefusedMailReasonTests
{
    private const string Origin = "https://aetheus.example.org";

    private const string English = "1 scheduled occurrence(s) from 2026-11-05 02:00 to 2026-11-05 02:00 UTC were missed.";

    private const string SingleCode =
        "[{\"Code\":\"ScheduleOccurrenceMissed\",\"Parameters\":{\"count\":\"1\",\"first\":\"2026-11-05 02:00\",\"last\":\"2026-11-05 02:00\",\"limit\":\"6\"}}]";

    private const string UnknownCode = "[{\"Code\":\"SomeFutureCode\",\"Parameters\":{}}]";

    private static string Payload(string? reasonProblems) =>
        "{\"PipelineId\":4,\"PipelineName\":\"nightly\",\"ProjectId\":10,\"TriggerSource\":\"Scheduler\",\"Reason\":\"" + English + "\""
        + (reasonProblems is null ? string.Empty : ",\"ReasonProblems\":" + reasonProblems) + "}";

    private static string Html(string payload, string languageJson) =>
        System.Net.WebUtility.HtmlDecode(NotificationMailKit.Build(
            new NotificationEmail("reader@example.org", "pipeline.launch-refused", "pipeline.launch-refused", payload), Origin, languageJson)
            .HtmlBody ?? string.Empty);

    [Fact]
    public void AFrenchRecipient_ReadsTheReasonInFrench()
    {
        var html = Html(Payload(SingleCode), "\"fr-FR\"");

        Assert.Contains("L'occurrence planifiée du 2026-11-05 02:00 UTC a été manquée au-delà de la limite de rattrapage de 6 h", html, StringComparison.Ordinal);
        Assert.DoesNotContain(English, html, StringComparison.Ordinal);
        Assert.DoesNotContain("ReasonProblems", html, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEnglishRecipient_ReadsTheReasonInEnglishFromTheCode()
    {
        var html = Html(Payload(SingleCode), "\"en\"");

        Assert.Contains("The scheduled occurrence of 2026-11-05 02:00 UTC was missed for longer than the catch-up limit of 6 h", html, StringComparison.Ordinal);
        Assert.DoesNotContain("ReasonProblems", html, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOldPayloadWithoutCodes_KeepsItsEnglishReason_ForAFrenchRecipient()
    {
        var html = Html(Payload(null), "\"fr-FR\"");

        Assert.Contains(English, html, StringComparison.Ordinal);
    }

    [Fact]
    public void ACodeTheMailHasNoWordFor_KeepsTheEnglishReason()
    {
        var html = Html(Payload(UnknownCode), "\"fr-FR\"");

        Assert.Contains(English, html, StringComparison.Ordinal);
        Assert.DoesNotContain("SomeFutureCode", html, StringComparison.Ordinal);
    }
}
