// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Notifications.MailTemplates;
using Aetheus.Shared.Components.Notifications;

namespace Aetheus.Back.Tests;

/// <summary>The mail of <c>analysis.cve.detected</c> (PLAN-004, recette R-26, R-27, R-14 and R-22): one mail for all the
/// projects a recipient follows, worded in the recipient's language, its button at the top and leading to the analysis
/// concerned, with no technical identifier shown. A block says how many distinct vulnerabilities it holds and links to the
/// page that lists them, never the list; the vulnerabilities of a server's host system sit in that server's section. The
/// mail is composed for real by the production composer and template; only the sender is a double (see
/// <see cref="NotificationMailKit"/>), so nothing here proves an SMTP delivery.</summary>
public sealed class CveDetectedMailTests
{
    private const string Origin = "https://aetheus.example.org";

    /// <summary>A link to this path of the application, whatever attributes the layout adds to the anchor.</summary>
    private static string Link(string path, string label) =>
        "href=\"" + System.Text.RegularExpressions.Regex.Escape(Origin + path) + "\"[^>]*>" + System.Text.RegularExpressions.Regex.Escape(label) + "</a>";

    private static NotificationEmail Mail(string payload) => new("reader@example.org",
        "analysis.cve.detected in 1 project", NotificationEventTypes.CveDetected, payload);

    /// <summary>The mail of a new severe finding, for the one project the recipient follows.</summary>
    private static NotificationEmail Immediate => Mail(
        """
        {"Digest":false,"Projects":[{"ProjectId":7,"OrganizationId":3,"ProjectName":"Alpha","Severity":"Critical","VulnerabilityCount":12,"ExploitedCount":1,"NewCount":12,
         "Vulnerabilities":[{"RuleId":"CVE-2021-23337","PackageName":"lodash","Version":"4.17.20","FixedVersion":"4.17.21","Severity":"High","Exploited":true,"IsNew":true},
         {"RuleId":"CVE-2024-0001","PackageName":"left-pad","Version":"1.0.0","FixedVersion":null,"Severity":"Critical","Exploited":false,"IsNew":true}]}]}
        """);

    /// <summary>The nightly mail: two projects, 2 and 1 vulnerabilities, one of them new.</summary>
    private static NotificationEmail Nightly => Mail(
        """
        {"Digest":true,"Projects":[
         {"ProjectId":7,"OrganizationId":3,"ProjectName":"Alpha","Severity":"High","VulnerabilityCount":2,"ExploitedCount":0,"NewCount":1,
          "Vulnerabilities":[{"RuleId":"CVE-2099-0001","PackageName":"liba","Version":"1.0.0","FixedVersion":"2.0.0","Severity":"High","Exploited":false,"IsNew":true},
          {"RuleId":"CVE-2099-0002","PackageName":"libb","Version":"1.0.0","FixedVersion":"2.0.0","Severity":"High","Exploited":false,"IsNew":false}]},
         {"ProjectId":9,"OrganizationId":3,"ProjectName":"Beta","Severity":"Critical","VulnerabilityCount":1,"ExploitedCount":0,"NewCount":0,
          "Vulnerabilities":[{"RuleId":"CVE-2099-0003","PackageName":"libc","Version":"3.0.0","FixedVersion":null,"Severity":"Critical","Exploited":false,"IsNew":false}]}]}
        """);

    [Fact]
    public void TheEvent_IsInTheCatalogue_OnByDefault_AndCarriesAProject()
    {
        var descriptor = NotificationEventTypes.Find(NotificationEventTypes.CveDetected);

        Assert.NotNull(descriptor);
        Assert.True(descriptor.DefaultEnabled);
        Assert.True(descriptor.CarriesProjectId);
        Assert.True(descriptor.SentByEmail);
    }

    [Theory]
    [InlineData("\"en\"", "Serious vulnerabilities detected", "Distinct vulnerabilities: 12", "More information")]
    [InlineData("\"fr\"", "Vulnérabilités graves détectées", "Vulnérabilités distinctes : 12", "Voir plus d'informations")]
    public void TheMail_SaysHowManyDistinctVulnerabilities_WithoutListingThem_InTheRecipientsLanguage(
        string languageJson, string title, string count, string more)
    {
        var message = NotificationMailKit.Build(Immediate, Origin, languageJson: languageJson);

        Assert.EndsWith(languageJson == "\"fr\"" ? " - Vulnérabilité" : " - Vulnerability", message.Subject, StringComparison.Ordinal);
        Assert.Contains(title, message.TextBody);
        Assert.Contains(count, message.TextBody);
        Assert.Contains(more, message.TextBody);
        Assert.DoesNotContain("CVE-2021-23337", message.TextBody);
        Assert.DoesNotContain("CVE-2024-0001", message.TextBody);
        Assert.DoesNotContain("lodash", message.TextBody);
        Assert.DoesNotContain("OrganizationId", message.TextBody);
        Assert.DoesNotContain("\"RuleId\"", message.TextBody);
        Assert.DoesNotContain("[new]", message.TextBody);
        Assert.DoesNotContain("[nouveau]", message.TextBody);
    }

    [Fact]
    public void TheMailOfOneProject_OpensThatProjectsFindings_NotTheQualityOfEveryProject()
    {
        var message = NotificationMailKit.Build(Immediate, Origin);

        // R-27: the button is the analysis concerned; the project's own findings, not /analysis (the portfolio).
        Assert.Contains($"href=\"{Origin}/projects/7/quality?tab=findings\"", message.HtmlBody);
        Assert.Contains("Review analysis", message.TextBody);
        Assert.DoesNotContain($"href=\"{Origin}/analysis\"", message.HtmlBody);
        // R-14: the block links "More information" to the same page.
        Assert.Matches(Link($"/projects/7/quality?tab=findings", $"More information"), message.HtmlBody);
    }

    [Fact]
    public void TheNightlyMailOfSeveralProjects_IsOneMail_NamingEachProjectWithItsOwnLink_AndCountingTheNewOnes()
    {
        var message = NotificationMailKit.Build(Nightly, Origin);

        Assert.Equal("Aetheus - Vulnerability", message.Subject);
        // One mail names both projects, each opening its own findings, with its count and its "More information" link.
        Assert.Contains($"href=\"{Origin}/projects/7/quality?tab=findings\"", message.HtmlBody);
        Assert.Contains("Alpha</a>", message.HtmlBody);
        Assert.Contains($"href=\"{Origin}/projects/9/quality?tab=findings\"", message.HtmlBody);
        Assert.Contains("Beta</a>", message.HtmlBody);
        Assert.Matches(Link($"/projects/7/quality?tab=findings", $"More information"), message.HtmlBody);
        Assert.Matches(Link($"/projects/9/quality?tab=findings", $"More information"), message.HtmlBody);
        Assert.Contains("Distinct vulnerabilities: 2", message.TextBody);
        Assert.Contains("Distinct vulnerabilities: 1", message.TextBody);
        // The nightly mail does not list the vulnerabilities one by one either.
        Assert.DoesNotContain("CVE-2099-0001", message.TextBody);
        Assert.DoesNotContain("CVE-2099-0003", message.TextBody);
        // Totals over the projects the recipient follows, and the share that is new.
        Assert.Contains("Projects affected", message.TextBody);
        Assert.Contains("New since the last mail", message.TextBody);
        // The button of a mail about several projects opens the CVE watch (a tab of the quality page, recette R-15).
        Assert.Contains($"href=\"{Origin}/analysis?tab=cve-watch\"", message.HtmlBody);
        Assert.Contains("Open the vulnerability watch", message.TextBody);
    }

    [Fact]
    public void TheNightlyMail_IsWordedInFrench_ForAFrenchSpeakingRecipient()
    {
        var message = NotificationMailKit.Build(Nightly, Origin, languageJson: "\"fr\"");

        Assert.Equal("Aetheus - Vulnérabilité", message.Subject);
        Assert.Contains("Vulnérabilités distinctes : 2", message.TextBody);
        Assert.Contains("Voir plus d'informations", message.TextBody);
        Assert.Contains("Ouvrir la veille de vulnérabilités", message.TextBody);
        Assert.Contains("Projets touchés", message.TextBody);
    }

    [Fact]
    public void TheMoreInformationLink_IsLeftOut_WhenTheMailHasNoAddress()
    {
        var message = NotificationMailKit.Build(Immediate, frontOrigin: null);

        Assert.Contains("Distinct vulnerabilities: 12", message.TextBody);
        Assert.DoesNotContain("More information", message.HtmlBody);
    }

    /// <summary>R-22: a project with its own dependency finding and the host system of one server, attached to it.</summary>
    private static NotificationEmail ServerNightly => Mail(
        """
        {"Digest":true,"Projects":[{"ProjectId":7,"OrganizationId":3,"ProjectName":"Alpha","Severity":"High","VulnerabilityCount":1,"ExploitedCount":0,"NewCount":0,
         "Vulnerabilities":[{"RuleId":"CVE-2099-0001","PackageName":"liba","Version":"1.0.0","FixedVersion":null,"Severity":"High","Exploited":false,"IsNew":false}]}],
         "Shared":[],
         "Servers":[{"ServerId":5,"ServerName":"srv-<1>","ProjectIds":[7],"Severity":"Critical","VulnerabilityCount":3,"ExploitedCount":0,"NewCount":2}]}
        """);

    [Theory]
    [InlineData("\"en\"", "Server srv-&lt;1&gt;", "Host system vulnerabilities", "Distinct vulnerabilities: ", "More information")]
    [InlineData("\"fr\"", "Serveur srv-&lt;1&gt;", "Vulnérabilités du système hôte", "Vulnérabilités distinctes : ", "Voir plus d'informations")]
    public void TheHostVulnerabilities_AreToldUnderTheirServer_WithItsQualityPage_AndNeverInTheProjectsBlock(
        string languageJson, string serverLabel, string summary, string count, string more)
    {
        var message = NotificationMailKit.Build(ServerNightly, Origin, languageJson: languageJson);
        var html = message.HtmlBody!;

        var server = html.IndexOf(serverLabel, StringComparison.Ordinal);
        Assert.True(server >= 0, "the server has its own section, its name encoded");
        Assert.Contains(summary, message.TextBody);
        // The project block holds its own 1, the server section its 3, after the project block.
        var projectCount = html.IndexOf(count + "1", StringComparison.Ordinal);
        var serverCount = html.IndexOf(count + "3", StringComparison.Ordinal);
        Assert.True(projectCount >= 0 && projectCount < server, "the project block keeps only its dependency vulnerabilities");
        Assert.True(serverCount > server, "the host vulnerabilities are under the server");
        Assert.DoesNotContain(count + "4", html);
        Assert.Matches(Link($"/servers/5/quality", $"{more}"), html);
        Assert.Matches(Link($"/projects/7/quality?tab=findings", $"{more}"), html);
        Assert.DoesNotContain("srv-<1>", html);
    }

    [Fact]
    public void AProjectWithOnlyHostNews_HasNoBlockOfItsOwn_TheServerSectionTellsIt()
    {
        var message = NotificationMailKit.Build(Mail(
            """
            {"Digest":true,"Projects":[{"ProjectId":7,"OrganizationId":3,"ProjectName":"Alpha","Severity":null,"VulnerabilityCount":0,"ExploitedCount":0,"NewCount":0,"Vulnerabilities":[]}],
             "Shared":[],
             "Servers":[{"ServerId":5,"ServerName":"srv-1","ProjectIds":[7],"Severity":"Critical","VulnerabilityCount":12,"ExploitedCount":0,"NewCount":2}]}
            """), Origin);

        Assert.Contains("Distinct vulnerabilities: 12", message.TextBody);
        Assert.Equal(1, message.TextBody!.Split("Distinct vulnerabilities:").Length - 1);
        Assert.Contains($"href=\"{Origin}/servers/5/quality\"", message.HtmlBody);
        Assert.DoesNotContain("Alpha</a>", message.HtmlBody);
        Assert.Matches(@"Host system vulnerabilities\s*12", message.TextBody);
    }

    [Theory]
    [InlineData("\"en\"", "Review analysis")]
    [InlineData("\"fr\"", "Examiner l'analyse")]
    public void TheButton_IsAtTheTop_BeforeTheIntroAndTheDetails_InEveryLanguage(string languageJson, string label)
    {
        var message = NotificationMailKit.Build(Immediate, Origin, languageJson: languageJson);

        var button = message.HtmlBody!.IndexOf($"href=\"{Origin}/projects/7/quality?tab=findings\"", StringComparison.Ordinal);
        var details = message.HtmlBody.IndexOf(languageJson == "\"en\"" ? "Distinct vulnerabilities" : "Vulnérabilités distinctes", StringComparison.Ordinal);
        Assert.True(button >= 0, "the mail has its button");
        Assert.True(details > button, "the button comes before the details");
        Assert.Contains(label, message.TextBody);
        // The button is the first link of the mail (the inbox link at the bottom comes after).
        Assert.Equal(button, message.HtmlBody.IndexOf("href=\"", StringComparison.Ordinal));
    }
}
