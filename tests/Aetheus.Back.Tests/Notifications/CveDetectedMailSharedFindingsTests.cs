// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Components.Notifications.MailTemplates;
using Aetheus.Back.Services.Email;
using Aetheus.Shared.Components.Analysis;
using Aetheus.Shared.Components.Notifications;

namespace Aetheus.Back.Tests;

/// <summary>R-8 and R-22: the "vulnerabilities still open" mail tells what several projects share once, and the host system
/// vulnerabilities of a server in a section of that server (never in a project's block); its counts are of distinct
/// vulnerabilities. Payload built by the production builder, narrowed by the production narrowing, composed by the
/// production composer and template; only the sender is a double.</summary>
public sealed class CveDetectedMailSharedFindingsTests
{
    private const string Origin = "https://aetheus.example.org";

    /// <summary>A link to this path of the application, whatever attributes the layout adds to the anchor.</summary>
    private static string Link(string path, string label) =>
        "href=\"" + System.Text.RegularExpressions.Regex.Escape(Origin + path) + "\"[^>]*>" + System.Text.RegularExpressions.Regex.Escape(label) + "</a>";
    private const string Server = "vps2577917";
    private const int ServerId = 41;

    private static CveWatchAnnouncedHostFinding Host(long id, int projectId, string projectName, string rule, AnalysisSeverity severity, bool exploited, string? fix) =>
        new(id, 3, projectId, projectName, ServerId, Server, rule, "linux", "6.8.0-146.146", fix, severity, exploited, true);

    private static CveWatchAnnouncedMatch Package(long id, int projectId, string projectName, string rule, string package, AnalysisSeverity severity) =>
        new(id, 3, projectId, projectName, rule, package, "1.0.0", "2.0.0", severity, false, false);

    /// <summary>Aetheus (1) and Wordmorrow (2) share the host system of one server, and each has one package finding of its own.</summary>
    private static (List<CveWatchAnnouncedMatch> Matches, List<CveWatchAnnouncedHostFinding> Hosts) TwoProjects(int sharedHostFindings = 3)
    {
        List<CveWatchAnnouncedHostFinding> hosts = [];
        for (var index = 0; index < sharedHostFindings; index++)
        {
            var (rule, severity, exploited, fix) = index switch
            {
                0 => ("CVE-2023-20585", AnalysisSeverity.High, true, "6.8.0-147.147"),
                1 => ("CVE-2021-3773", AnalysisSeverity.Critical, false, null),
                _ => ($"CVE-2099-{index:0000}", AnalysisSeverity.High, false, "6.8.0-147.147")
            };
            hosts.Add(Host(index * 2L + 1, 1, "Aetheus", rule, severity, exploited, fix));
            hosts.Add(Host(index * 2L + 2, 2, "Wordmorrow", rule, severity, exploited, fix));
        }
        return ([Package(100, 1, "Aetheus", "CVE-2099-7001", "liba", AnalysisSeverity.High),
            Package(101, 2, "Wordmorrow", "CVE-2099-7002", "libb", AnalysisSeverity.Critical)], hosts);
    }

    private static string Payload(object built, IReadOnlySet<int>? followed = null)
    {
        var json = JsonSerializer.Serialize(built);
        return followed is null ? json : ProjectDigestPayload.Narrow(json, followed)!;
    }

    private static EmailMessage Mail(string payload, string language = "\"en\"") =>
        NotificationMailKit.Build(new NotificationEmail("reader@example.org", "analysis.cve.detected in 2 projects",
            NotificationEventTypes.CveDetected, payload), Origin, language);

    private static string TextOf(string payload, string language = "\"en\"") => Mail(payload, language).TextBody!;

    private static int Occurrences(string text, string value) => text.Split(value).Length - 1;

    [Fact]
    public void TheSharedHostFindings_AreToldOnce_UnderTheirServer_AndNoProjectBlockHoldsThem()
    {
        var (matches, hosts) = TwoProjects();

        var message = Mail(Payload(CveWatchAnnouncementPayload.Build(matches, hosts, digest: true)));
        var text = message.TextBody!;

        // One section for the server, counting its 3 distinct vulnerabilities once although 2 projects reach it; no CVE listed.
        Assert.Equal(1, Occurrences(text, $"Server {Server}"));
        Assert.DoesNotContain("CVE-2023-20585", text);
        Assert.DoesNotContain("CVE-2099-7001", text);
        Assert.DoesNotContain("Common to", text);
        Assert.Matches(Link($"/servers/{ServerId}/quality", $"More information"), message.HtmlBody);
        // Each project keeps only its own package finding: 1, 1 and the server's 3, never 4 (host counted in a project).
        Assert.Equal(2, Occurrences(text, "Distinct vulnerabilities: 1"));
        Assert.Equal(1, Occurrences(text, "Distinct vulnerabilities: 3"));
        Assert.DoesNotContain("Distinct vulnerabilities: 4", text);
        Assert.True(text.IndexOf($"Server {Server}", StringComparison.Ordinal) > text.LastIndexOf("Distinct vulnerabilities: 1", StringComparison.Ordinal),
            "the server section comes after the project blocks");
    }

    [Fact]
    public void TheHeader_CountsDistinctVulnerabilities_NotOncePerProject()
    {
        var (matches, hosts) = TwoProjects();

        var text = TextOf(Payload(CveWatchAnnouncementPayload.Build(matches, hosts, digest: true)));

        // 3 host vulnerabilities reached by 2 projects are 3, not 6; 2 package findings, one per project; 1 exploited; 3 new (the host ones).
        Assert.Matches(@"Projects affected\s*2", text);
        Assert.Matches(@"Vulnerabilities\s*2", text);
        Assert.Matches(@"Host system vulnerabilities\s*3", text);
        Assert.DoesNotContain("Common to several projects", text);
        Assert.Matches(@"Exploited in the wild\s*1", text);
        Assert.Matches(@"New since the last mail\s*3", text);
    }

    [Fact]
    public void AServerWithMoreVulnerabilitiesThanTheMailLists_StillSaysHowManyThereAre()
    {
        var (matches, hosts) = TwoProjects(sharedHostFindings: 13);

        var text = TextOf(Payload(CveWatchAnnouncementPayload.Build(matches, hosts, digest: true)));

        Assert.Contains("Distinct vulnerabilities: 13", text);
        Assert.DoesNotContain("CVE-2099-0002", text);
        Assert.Matches(@"Host system vulnerabilities\s*13", text);
    }

    [Fact]
    public void ARecipientWhoFollowsOnlyOneProject_ReadsTheServerOfThatProject_AndNotTheOthers()
    {
        var (matches, hosts) = TwoProjects();

        var text = TextOf(Payload(CveWatchAnnouncementPayload.Build(matches, hosts, digest: true), new HashSet<int> { 2 }));

        Assert.DoesNotContain("Common to", text);
        Assert.Matches(@"Projects affected\s*1", text);
        Assert.Matches(@"Host system vulnerabilities\s*3", text);
        Assert.Contains($"Server {Server}", text);
    }

    [Fact]
    public void ARecipientWhoFollowsNoProjectOfTheServer_IsNotToldTheServer()
    {
        var (matches, hosts) = TwoProjects();
        hosts.AddRange(hosts.Select(finding => finding with { ProjectId = 3, ProjectName = "Third", ServerId = 99, ServerName = "other-server" }).ToList());

        var text = TextOf(Payload(CveWatchAnnouncementPayload.Build(matches, hosts, digest: true), new HashSet<int> { 1 }));

        Assert.Contains($"Server {Server}", text);
        Assert.DoesNotContain("other-server", text);
    }

    [Fact]
    public void TheFrenchMail_NamesTheServerSectionAndItsCount()
    {
        var (matches, hosts) = TwoProjects();

        var message = Mail(Payload(CveWatchAnnouncementPayload.Build(matches, hosts, digest: true)), "\"fr\"");

        Assert.Contains($"Serveur {Server}", message.TextBody);
        Assert.Contains("Vulnérabilités distinctes : 3", message.TextBody);
        Assert.Matches(Link($"/servers/{ServerId}/quality", $"Voir plus d'informations"), message.HtmlBody);
        Assert.Matches(@"Vulnérabilités du système hôte\s*3", message.TextBody);
    }

    [Fact]
    public void AProjectPackageFinding_IsSharedByCveAndPackage_WhateverTheVersionOfEachProject_AndLinksTheWatch()
    {
        List<CveWatchAnnouncedMatch> matches =
        [
            new(1, 3, 1, "Aetheus", "CVE-2021-23337", "lodash", "4.17.20", "4.17.21", AnalysisSeverity.High, true, true),
            new(2, 3, 2, "Wordmorrow", "CVE-2021-23337", "lodash", "4.17.19", "4.17.21", AnalysisSeverity.High, true, true)
        ];

        var message = Mail(Payload(CveWatchAnnouncementPayload.Build(matches, [], digest: true)));

        Assert.Matches(@"Common to Aetheus \(\S+\), Wordmorrow \(", message.TextBody);
        Assert.Matches(@"Vulnerabilities\s*1", message.TextBody);
        Assert.Contains("Distinct vulnerabilities: 1", message.TextBody);
        Assert.DoesNotContain("CVE-2021-23337", message.TextBody);
        Assert.Matches(Link($"/analysis?tab=cve-watch", $"More information"), message.HtmlBody);
    }
}
