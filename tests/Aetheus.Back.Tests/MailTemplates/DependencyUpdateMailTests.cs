// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Components.Notifications.MailTemplates;
using Aetheus.Shared.Components.Notifications;

namespace Aetheus.Back.Tests;

/// <summary>R-30: the two mails of an automatic dependency update go through the same notification template as every other event, in
/// English and in French, with the project in the subject, the outcome worded and a link to the project page.</summary>
public sealed class DependencyUpdateMailTests
{
    private const string Origin = "https://aetheus.example.org";

    private static NotificationEmail Email(string eventType, string outcome) => new("reader@example.org", eventType, eventType,
        $$"""{"ProjectId":3,"ProjectName":"Shop","PackageName":"Newtonsoft.Json","FromVersion":"13.0.1","TargetVersion":"13.0.3","Advisory":"GHSA-xxxx","Branch":"aetheus/deps/Newtonsoft.Json-13.0.3","Outcome":"{{outcome}}"}""");

    [Theory]
    [InlineData(NotificationEventTypes.DependencyUpdateReady, "PullRequestOpened", null, "Aetheus - Shop - Pull request", "Dependency update ready to merge", "Pull request opened on the project repository")]
    [InlineData(NotificationEventTypes.DependencyUpdateReady, "PullRequestOpened", "\"fr-FR\"", "Aetheus - Shop - Pull request", "Mise à jour de dépendance prête à fusionner", "Pull request ouverte sur le dépôt du projet")]
    [InlineData(NotificationEventTypes.DependencyUpdateReady, "PullRequestToOpenOnOrigin", null, "Aetheus - Shop - Pull request", "Dependency update ready to merge", "open the merge request on its origin")]
    [InlineData(NotificationEventTypes.DependencyUpdateClosed, "BranchDeleted", null, "Aetheus - Shop - Update", "Dependency update branch retired", "Branch deleted from the project repository")]
    [InlineData(NotificationEventTypes.DependencyUpdateClosed, "BranchKeptMirror", "\"fr-FR\"", "Aetheus - Shop - Mise à jour", "Branche de mise à jour de dépendance retirée", "supprimer la branche sur son origine demande votre accord")]
    public void TheMail_IsWordedInTheRecipientsLanguage_WithTheOutcomeAndTheProject(
        string eventType, string outcome, string? languageJson, string subject, string title, string outcomeText)
    {
        var message = NotificationMailKit.Build(Email(eventType, outcome), Origin, languageJson);

        Assert.Equal(subject, message.Subject);
        Assert.Contains(title, message.TextBody, StringComparison.Ordinal);
        Assert.Contains(outcomeText, message.TextBody, StringComparison.Ordinal);
        Assert.Contains("Newtonsoft.Json", message.TextBody, StringComparison.Ordinal);
        Assert.Contains("href=\"" + Origin + "/projects/3/overview\"", message.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain(outcome, message.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void TheProjectIdentifier_IsNotShownAsARow()
    {
        var message = NotificationMailKit.Build(Email(NotificationEventTypes.DependencyUpdateReady, "PullRequestOpened"), Origin);

        Assert.DoesNotContain("Project ID", message.TextBody, StringComparison.Ordinal);
    }
}
