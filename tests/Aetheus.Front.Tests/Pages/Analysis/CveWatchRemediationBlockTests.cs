// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Components.Analysis;
using Aetheus.Shared.Components.VulnerabilityWatch;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Analysis;

/// <summary>R-14: the "What to do" block of the finding page of a CVE watch match. The API is a double: these tests prove what the block asks
/// for and how it draws the answer (the recommendation, the one main action, the command and reason of a 422), not what the server decides.</summary>
public sealed class CveWatchRemediationBlockTests : BunitContext
{
    private const string ByFinding = "api/analysis/cve-watch/by-finding/301";
    private const string Remediation = "api/analysis/cve-watch/77/remediation";

    private readonly BunitTestHelper.TestHandler _handler;

    public CveWatchRemediationBlockTests() => _handler = BunitTestHelper.RegisterServices(this);

    private static CveWatchMatchDto OpenMatch(CveRecommendationKind kind = CveRecommendationKind.BumpDependency, DateTime? closedAt = null) => new()
    {
        MatchId = 77,
        ProjectId = 1,
        ProjectName = "Aetheus",
        AdvisoryId = "GHSA-aaaa-bbbb-cccc",
        PackageName = "Contoso.Session.Core",
        Version = "4.1.0",
        FixedVersion = "4.1.3",
        Status = VulnerabilityMatchStatus.Affected,
        FindingId = 301,
        ClosedAt = closedAt,
        Recommendation = new CveWatchRecommendationDto
        {
            Kind = kind,
            TargetVersion = "4.1.3",
            Files = ["src/App/App.csproj"],
            VerifiedAgainstRegistry = true
        }
    };

    private void RegisterMatch(CveWatchMatchDto match, CveRemediationDto? latest = null)
    {
        _handler.SetJsonResponse(ByFinding, match);
        _handler.SetJsonResponse(HttpMethod.Get, Remediation, latest ?? new CveRemediationDto { MatchId = match.MatchId });
    }

    private IRenderedComponent<CveWatchRemediationBlock> RenderBlock(bool canWrite = true) =>
        Render<CveWatchRemediationBlock>(parameters => parameters.Add(block => block.FindingId, 301).Add(block => block.CanWrite, canWrite));

    [Fact]
    public void TheRecommendation_IsShown_WithTheFilesToChange_AndTheMainActionIsOffered()
    {
        RegisterMatch(OpenMatch());

        var cut = RenderBlock();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("CveRemediationBump", cut.Find("[data-testid='cve-recommendation']").TextContent, StringComparison.Ordinal);
            Assert.Contains("CveRemediationFiles", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("CveRemediationPrepare", cut.Find("[data-testid='cve-remediation-prepare']").TextContent, StringComparison.Ordinal);
        });
        // One main action: nothing else is a button of the block apart from the menu trigger.
        Assert.Single(cut.FindAll("[data-testid='cve-remediation-prepare']"));
    }

    [Fact]
    public void TheSentences_CarryThePackage_TheVersions_TheFiles_AndTheBranch_OfTheRealWording()
    {
        // The default localizer of the suite answers keys; this one reads the application's own resource file, so the values placed in the sentences are proved.
        Services.AddSingleton<Microsoft.Extensions.Localization.IStringLocalizer<Aetheus.Front.Resources.AppStrings>>(Aetheus.Front.Tests.TestDoubles.ResxLocalizer.English());
        RegisterMatch(OpenMatch(), new CveRemediationDto { MatchId = 77, Status = VulnerabilityRemediationStatus.PullRequestOpen, Branch = "aetheus/cve-fix-77", PullRequestNumber = 12 });

        var cut = RenderBlock();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("Upgrade Contoso.Session.Core from 4.1.0 to 4.1.3.", cut.Find("[data-testid='cve-recommendation']").TextContent.Trim());
            Assert.Contains("Files to change: src/App/App.csproj", cut.Markup, StringComparison.Ordinal);
            var attempt = cut.Find("[data-testid='cve-remediation-attempt']").TextContent;
            Assert.Contains("Branch aetheus/cve-fix-77", attempt, StringComparison.Ordinal);
            Assert.Contains("Pull request #12", attempt, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void ThePrepareAction_PostsTheRemediation_ShowsTheAttempt_AndIsNoLongerOffered()
    {
        RegisterMatch(OpenMatch());
        _handler.SetJsonResponse(HttpMethod.Post, Remediation, new CveRemediationDto
        {
            MatchId = 77,
            Status = VulnerabilityRemediationStatus.Requested,
            Branch = "aetheus/cve-fix-77"
        }, HttpStatusCode.Created);
        var cut = RenderBlock();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid='cve-remediation-prepare']")));

        cut.Find("[data-testid='cve-remediation-prepare']").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains(_handler.RequestsSnapshot(), request => request.Method == "POST" && request.Url.EndsWith(Remediation, StringComparison.Ordinal));
            Assert.Contains("CveRemediationBranch", cut.Find("[data-testid='cve-remediation-attempt']").TextContent, StringComparison.Ordinal);
            Assert.Empty(cut.FindAll("[data-testid='cve-remediation-prepare']"));
            Assert.Empty(cut.FindAll("[data-testid='cve-remediation-fallback']"));
        });
        Assert.Contains(Services.Toasts(), toast => toast.Detail.Contains("CveRemediationStarted", StringComparison.Ordinal));
    }

    [Fact]
    public void A422_ShowsTheCommandAndTheReasonInPlace_NotAnErrorToast()
    {
        RegisterMatch(OpenMatch());
        _handler.SetJsonResponse(HttpMethod.Post, Remediation, new CveRemediationDto
        {
            MatchId = 77,
            Fallback = new CveRemediationFallbackDto
            {
                Reason = CveRemediationFallbackReason.ExternalMirror,
                Detail = "The repository mirrors github.com/acme/app.",
                Command = "dotnet add package Contoso.Session.Core --version 4.1.3",
                WorkingDirectory = "src/App"
            }
        }, HttpStatusCode.UnprocessableEntity);
        var cut = RenderBlock();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid='cve-remediation-prepare']")));

        cut.Find("[data-testid='cve-remediation-prepare']").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Enum_CveRemediationFallbackReason_ExternalMirror", cut.Find("[data-testid='cve-remediation-reason']").TextContent, StringComparison.Ordinal);
            Assert.Contains("dotnet add package Contoso.Session.Core --version 4.1.3", cut.Find("[data-testid='cve-remediation-command']").TextContent, StringComparison.Ordinal);
            Assert.Contains("CveRemediationFallbackDirectory", cut.Find("[data-testid='cve-remediation-fallback']").TextContent, StringComparison.Ordinal);
        });
        Assert.Empty(Services.Toasts());
    }

    [Fact]
    public void ARefusalWithNoDetail_SaysItsReasonOnce_AndNoCommandIsInvented()
    {
        // The real wording: the reason once, in the language of the page, then nothing that repeats it (R-48).
        Services.AddSingleton<Microsoft.Extensions.Localization.IStringLocalizer<Aetheus.Front.Resources.AppStrings>>(Aetheus.Front.Tests.TestDoubles.ResxLocalizer.English());
        RegisterMatch(OpenMatch(CveRecommendationKind.BumpRuntime));
        _handler.SetJsonResponse(HttpMethod.Post, Remediation, new CveRemediationDto
        {
            MatchId = 77,
            Fallback = new CveRemediationFallbackDto { Reason = CveRemediationFallbackReason.NoFileToChange, Detail = string.Empty }
        }, HttpStatusCode.UnprocessableEntity);
        var cut = RenderBlock();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid='cve-remediation-prepare']")));

        cut.Find("[data-testid='cve-remediation-prepare']").Click();

        cut.WaitForAssertion(() =>
        {
            const string reason = "The recommendation is not a change of a file.";
            var fallback = cut.Find("[data-testid='cve-remediation-fallback']").TextContent;
            Assert.Equal(reason, cut.Find("[data-testid='cve-remediation-reason']").TextContent.Trim());
            Assert.Equal(1, fallback.Split(reason).Length - 1);
            Assert.Empty(cut.FindAll("[data-testid='cve-remediation-command']"));
            Assert.DoesNotContain("dotnet add package", cut.Markup, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void ARuntimeWithItsFiles_NamesThem_AndARuntimeWithNone_SaysWhereToRaiseIt_NeverTheSbom()
    {
        Services.AddSingleton<Microsoft.Extensions.Localization.IStringLocalizer<Aetheus.Front.Resources.AppStrings>>(Aetheus.Front.Tests.TestDoubles.ResxLocalizer.English());
        var withFiles = OpenMatch(CveRecommendationKind.BumpRuntime);
        RegisterMatch(withFiles with { Recommendation = withFiles.Recommendation! with { Files = ["global.json", "deploy/docker/Dockerfile.back"] } });

        var cut = RenderBlock();

        cut.WaitForAssertion(() => Assert.Contains("Files to change: global.json, deploy/docker/Dockerfile.back", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("[data-testid='cve-runtime-source']"));
        Assert.DoesNotContain("sbom", cut.Markup, StringComparison.Ordinal);

        _handler.SetJsonResponse(ByFinding, withFiles with { Recommendation = withFiles.Recommendation! with { Files = [] } });
        var none = RenderBlock();

        none.WaitForAssertion(() =>
        {
            var advice = none.Find("[data-testid='cve-runtime-source']").TextContent;
            Assert.Contains("global.json", advice, StringComparison.Ordinal);
            Assert.Contains("base image", advice, StringComparison.Ordinal);
        });
        Assert.DoesNotContain("Files to change", none.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("sbom", none.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void AFallbackWithNoCommand_SaysSo_InsteadOfInventingOne()
    {
        RegisterMatch(OpenMatch());
        _handler.SetJsonResponse(HttpMethod.Post, Remediation, new CveRemediationDto
        {
            MatchId = 77,
            Fallback = new CveRemediationFallbackDto { Reason = CveRemediationFallbackReason.BranchProtected, Detail = "main is protected." }
        }, HttpStatusCode.UnprocessableEntity);
        var cut = RenderBlock();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid='cve-remediation-prepare']")));

        cut.Find("[data-testid='cve-remediation-prepare']").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("CveRemediationFallbackNoCommand", cut.Find("[data-testid='cve-remediation-fallback']").TextContent, StringComparison.Ordinal);
            Assert.Empty(cut.FindAll("[data-testid='cve-remediation-command']"));
        });
    }

    [Fact]
    public void AReadOnlyReader_SeesTheRecommendation_ButNoAction()
    {
        RegisterMatch(OpenMatch());

        var cut = RenderBlock(canWrite: false);

        cut.WaitForAssertion(() => Assert.Contains("CveRemediationBump", cut.Find("[data-testid='cve-recommendation']").TextContent, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("[data-testid='cve-remediation-prepare']"));
    }

    [Theory]
    [InlineData(CveRecommendationKind.NoFixYet)]
    [InlineData(CveRecommendationKind.EndOfLife)]
    [InlineData(CveRecommendationKind.OsUpdate)]
    public void ARecommendationThatIsNotAFileChange_OffersNoUpgrade(CveRecommendationKind kind)
    {
        RegisterMatch(OpenMatch(kind));

        var cut = RenderBlock();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid='cve-recommendation']")));
        Assert.Empty(cut.FindAll("[data-testid='cve-remediation-prepare']"));
    }

    [Fact]
    public void ARuntimeThatNoFileNames_ShowsWhereItComesFrom_AndOffersNoUpgradeTheServerWouldRefuse()
    {
        var match = OpenMatch(CveRecommendationKind.BumpRuntime);
        RegisterMatch(match with { Recommendation = match.Recommendation! with { Files = [] } });

        var cut = RenderBlock();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid='cve-runtime-source']")));
        Assert.Empty(cut.FindAll("[data-testid='cve-remediation-prepare']"));
    }

    [Fact]
    public void AnAttemptUnderWay_IsShown_AndNotRepeated()
    {
        RegisterMatch(OpenMatch(), new CveRemediationDto
        {
            MatchId = 77,
            Status = VulnerabilityRemediationStatus.PullRequestOpen,
            Branch = "aetheus/cve-fix-77",
            PullRequestNumber = 12
        });

        var cut = RenderBlock();

        cut.WaitForAssertion(() =>
        {
            var attempt = cut.Find("[data-testid='cve-remediation-attempt']").TextContent;
            Assert.Contains("Enum_VulnerabilityRemediationStatus_PullRequestOpen", attempt, StringComparison.Ordinal);
            Assert.Contains("CveRemediationPullRequest", attempt, StringComparison.Ordinal);
        });
        Assert.Empty(cut.FindAll("[data-testid='cve-remediation-prepare']"));
    }

    [Fact]
    public void AFindingWithNoMatch_OrAClosedOne_ShowsNothing()
    {
        _handler.SetResponse(ByFinding, HttpStatusCode.NotFound);
        var none = RenderBlock();
        none.WaitForAssertion(() => Assert.Contains(_handler.RequestsSnapshot(), request => request.Url.EndsWith(ByFinding, StringComparison.Ordinal)));
        Assert.Empty(none.FindAll("[data-testid='cve-remediation']"));
        Assert.Empty(Services.Toasts());

        _handler.SetJsonResponse(ByFinding, OpenMatch(closedAt: new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc)));
        var closed = Render<CveWatchRemediationBlock>(parameters => parameters.Add(block => block.FindingId, 301).Add(block => block.CanWrite, true));
        closed.WaitForAssertion(() => Assert.Contains(_handler.RequestsSnapshot(), request => request.Url.EndsWith(ByFinding, StringComparison.Ordinal)));
        Assert.Empty(closed.FindAll("[data-testid='cve-remediation']"));
    }

    [Fact]
    public void AFailedRead_SaysSoInPlace_WithARetry()
    {
        _handler.SetResponse(ByFinding, HttpStatusCode.InternalServerError);

        var cut = RenderBlock();

        // The failure is said where the block is, not turned into "nothing to do" nor into a global toast.
        cut.WaitForAssertion(() => Assert.Contains("LoadFailed", cut.Find("[data-testid='cve-remediation-failed']").TextContent, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("[data-testid='cve-remediation-prepare']"));
        Assert.Empty(Services.Toasts());
    }

    [Fact]
    public void TheVersionSource_ShowsTheOrigin_TheShortenedCommit_TheFoundVersion_AndTheFixedVersion()
    {
        RegisterMatch(OpenMatch() with { InventoryOrigin = ProjectInventoryOrigin.ReleaseCommit, InventoryRef = "0123456789abcdef0123456789abcdef01234567" });

        var cut = RenderBlock();

        cut.WaitForAssertion(() =>
        {
            var found = cut.Find("[data-testid='cve-found-in']").TextContent;
            Assert.Contains("4.1.0", found, StringComparison.Ordinal);
            Assert.Contains("Enum_ProjectInventoryOrigin_ReleaseCommit", found, StringComparison.Ordinal);
            Assert.Contains("0123456", found, StringComparison.Ordinal);
            Assert.DoesNotContain("0123456789abcdef0123456789abcdef01234567", found, StringComparison.Ordinal);
            Assert.Contains("4.1.3", cut.Find("[data-testid='cve-fixed-available']").TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void TheVersionSource_ShowsTheBranch_OfARepositoryInventory()
    {
        RegisterMatch(OpenMatch() with { InventoryOrigin = ProjectInventoryOrigin.Repository, InventoryRef = "develop" });

        var cut = RenderBlock();

        cut.WaitForAssertion(() =>
            Assert.Contains("Enum_ProjectInventoryOrigin_Repository - develop", cut.Find("[data-testid='cve-found-in']").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public void WithNoFixPublished_TheVersionSource_SaysNoFix_AndInventsNoOrigin()
    {
        RegisterMatch(OpenMatch() with { FixedVersion = null, Recommendation = null });

        var cut = RenderBlock();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("CveWatchNoFix", cut.Find("[data-testid='cve-fixed-available']").TextContent, StringComparison.Ordinal);
            var found = cut.Find("[data-testid='cve-found-in']").TextContent;
            Assert.DoesNotContain("Enum_ProjectInventoryOrigin", found, StringComparison.Ordinal);
            Assert.Contains("4.1.0", found, StringComparison.Ordinal);
        });
    }
}
