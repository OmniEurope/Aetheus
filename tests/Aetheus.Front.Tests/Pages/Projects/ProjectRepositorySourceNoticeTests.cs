// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Components.Projects.ProjectDetailSections;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Projects;

/// <summary>Recette R-4: the repository card says where the code is read from. An external address without a mirror is
/// said not to be read, and a writer creates the mirror from it; the server's refusal is shown, not a generic failure.</summary>
public sealed class ProjectRepositorySourceNoticeTests : BunitContext
{
    private const string External = "https://github.com/team/app.git";
    private readonly BunitTestHelper.TestHandler _handler;

    public ProjectRepositorySourceNoticeTests() => _handler = BunitTestHelper.RegisterServices(this);

    private IRenderedComponent<ProjectRepositorySourceNotice> RenderNotice(
        string? url, bool hasRepository = false, bool readsFromMirror = false, bool canWrite = true, Action? mirrored = null) =>
        Render<ProjectRepositorySourceNotice>(p => p
            .Add(c => c.ProjectId, 1)
            .Add(c => c.RepositoryUrl, url)
            .Add(c => c.HasRepository, hasRepository)
            .Add(c => c.ReadsFromMirror, readsFromMirror)
            .Add(c => c.CanWrite, canWrite)
            .Add(c => c.OnMirrored, () => mirrored?.Invoke()));

    [Fact]
    public void AnExternalAddressWithoutMirror_SaysItIsNotRead_AndOffersTheMirror()
    {
        var cut = RenderNotice(External);

        Assert.Contains("ExternalAddressNotMirroredTitle", cut.Markup);
        Assert.Contains("ExternalAddressNotMirroredHint", cut.Markup);
        Assert.Contains("CreateProjectMirror", cut.Find("#project-create-mirror").TextContent);
    }

    [Fact]
    public void AReader_IsToldButOffersNoMirrorCreation()
    {
        var cut = RenderNotice(External, canWrite: false);

        Assert.Contains("ExternalAddressNotMirroredHint", cut.Markup);
        Assert.Empty(cut.FindAll("#project-create-mirror"));
    }

    [Theory]
    [InlineData("https://aetheus.example/git/1/app.git", false)]
    [InlineData(External, true)]
    [InlineData(null, false)]
    public void AnAddressAetheusReads_ShowsNoWarning(string? url, bool hasRepository)
    {
        var cut = RenderNotice(url, hasRepository);

        Assert.DoesNotContain("ExternalAddressNotMirroredTitle", cut.Markup);
    }

    [Fact]
    public void AMirroredSource_SaysTheCodeIsReadFromTheMirror()
    {
        var cut = RenderNotice("https://aetheus.example/git/1/app.git", hasRepository: true, readsFromMirror: true);

        Assert.Contains("CodeReadFromProjectMirror", cut.Markup);
    }

    [Fact]
    public void CreatingTheMirror_PostsToTheProjectMirrorEndpoint_AndRaisesOnMirrored()
    {
        _handler.SetJsonResponse(HttpMethod.Post, "api/external-repos/project/1/mirror-address",
            new ExternalRepoDto { ProjectId = 1, IsProjectSource = true, MirrorStatus = GitMirrorStatus.Ready });
        var mirrored = false;
        var cut = RenderNotice(External, mirrored: () => mirrored = true);

        cut.Find("#project-create-mirror").Click();

        cut.WaitForAssertion(() => Assert.True(mirrored));
        Assert.Contains(_handler.RequestsSnapshot(), request =>
            request.Method == "POST" && request.Url.EndsWith("api/external-repos/project/1/mirror-address", StringComparison.Ordinal));
    }

    [Fact]
    public void ARefusedMirror_ShowsTheServerMessage_AndRaisesNothing()
    {
        _handler.SetRawResponse(HttpMethod.Post, "api/external-repos/project/1/mirror-address",
            """{"message":"Could not reach the external repository: Remote unreachable."}""", "application/json",
            HttpStatusCode.BadRequest);
        var mirrored = false;
        var cut = RenderNotice(External, mirrored: () => mirrored = true);

        cut.Find("#project-create-mirror").Click();

        cut.WaitForAssertion(() => Assert.Contains("Could not reach the external repository", cut.Markup));
        Assert.False(mirrored);
    }
}
