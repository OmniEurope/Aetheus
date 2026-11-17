// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>R-38 (project oe1): what the wizard's production card and the completion on an existing production pipeline show
/// about the Apache templates the project holds nowhere, and the one action that creates them when the Compose file could not
/// be read. The request they send is proved in <c>PipelineSetupProductionApacheTests</c>.</summary>
public sealed class PipelineProductionApacheTests : BunitContext
{
    private const string Analysis = "api/pipelines/setup/production-analysis";
    private const string Apply = "api/pipelines/setup/production-apply";
    private const string Apache = "api/pipelines/setup/production-apache";
    private const string Root = "[data-testid='setup-production-apache']";
    private static readonly string[] AllFour =
    [
        ".pipeline/configs/apache/api-http.conf", ".pipeline/configs/apache/api-https.conf",
        ".pipeline/configs/apache/app-http.conf", ".pipeline/configs/apache/app-https.conf"
    ];

    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineProductionApacheTests() => _handler = BunitTestHelper.RegisterServices(this);

    private static PipelineProductionAnalysisDto Unreadable() => new()
    {
        Readable = false,
        ExternalSource = true,
        SourceProblem = ProductionSourceProblem.ExternalNotSynchronized,
        MissingApacheTemplates = [.. AllFour]
    };

    private async Task<PipelineSetupProduction> LoadedAsync(PipelineProductionAnalysisDto analysis, bool monolith = false)
    {
        _handler.SetJsonResponse(HttpMethod.Post, Analysis, analysis);
        var setup = new PipelineSetupProduction(Services.GetRequiredService<ApiClient>(), () => { }, () => "main", () => monolith);
        await setup.LoadAsync(21);
        return setup;
    }

    private IEnumerable<(string Method, string Url, string? Body)> Calls(string urlEnd) => _handler.RequestDetails
        .Where(request => request.Method == HttpMethod.Post.Method && request.Url.EndsWith(urlEnd, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public async Task WithoutAReadableComposeFile_TheFourFilesAreListed_AndTheUserIsAskedBeforeAnythingIsCreated()
    {
        var setup = await LoadedAsync(Unreadable());

        var cut = Render<PipelineProductionApache>(parameters => parameters.Add(item => item.Setup, setup));

        Assert.Equal(AllFour, cut.FindAll("[data-testid='setup-production-apache-file']").Select(item => item.TextContent));
        Assert.Contains("PipelineSetupApacheAsk", cut.Find(Root).TextContent, StringComparison.Ordinal);
        Assert.True(cut.Find("input#setup-production-apache-create").HasAttribute("checked"));
        // A front and an API: the HTTPS virtual hosts depend on the strategy, so it is asked too.
        Assert.NotEmpty(cut.FindAll("#setup-production-apache-strategy"));
        Assert.Empty(Calls(Apache));
    }

    [Fact]
    public async Task AMonolith_IsNotAskedAStrategy()
    {
        var setup = await LoadedAsync(Unreadable(), monolith: true);

        var cut = Render<PipelineProductionApache>(parameters => parameters.Add(item => item.Setup, setup));

        Assert.Empty(cut.FindAll("#setup-production-apache-strategy"));
    }

    [Fact]
    public async Task DecliningTheCheckBox_IsKeptInTheSetup_AndHidesTheStrategy()
    {
        var setup = await LoadedAsync(Unreadable());
        var cut = Render<PipelineProductionApache>(parameters => parameters.Add(item => item.Setup, setup));

        await cut.Find("input#setup-production-apache-create").ChangeAsync(new ChangeEventArgs { Value = false });

        Assert.False(setup.CreateApacheTemplates);
        Assert.Empty(cut.FindAll("#setup-production-apache-strategy"));
    }

    [Fact]
    public async Task WithAReadableComposeFile_TheFilesAreOnlyListed_NothingIsAsked()
    {
        const string compose = "deploy/compose/remote.compose.yml";
        var setup = await LoadedAsync(new PipelineProductionAnalysisDto
        {
            Readable = true,
            ComposeFiles = [compose],
            ComposeFile = compose,
            MissingApacheTemplates = [.. AllFour]
        });

        var cut = Render<PipelineProductionApache>(parameters => parameters.Add(item => item.Setup, setup));

        Assert.Equal(4, cut.FindAll("[data-testid='setup-production-apache-file']").Count);
        Assert.Contains("PipelineSetupApacheWillCreate", cut.Find(Root).TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("input#setup-production-apache-create"));
    }

    [Fact]
    public async Task NothingMissing_ShowsNothing()
    {
        var setup = await LoadedAsync(Unreadable() with { MissingApacheTemplates = [] });

        var cut = Render<PipelineProductionApache>(parameters => parameters.Add(item => item.Setup, setup));

        Assert.Empty(cut.FindAll(Root));
    }

    [Fact]
    public async Task OnAnExistingProductionPipeline_TheCompletionCreatesTheApacheTemplatesEvenThoughTheComposeFileIsStillUnreadable()
    {
        _handler.SetJsonResponse(HttpMethod.Post, Analysis, Unreadable());
        _handler.SetJsonResponse(HttpMethod.Post, Apache, new ProjectConfigRepositoryDto
        {
            Id = 9,
            Name = "oe1-config",
            Slug = "oe1-config",
            Created = true,
            FilesAdded = [.. AllFour]
        });
        var pipeline = new PipelineDto
        {
            Id = 190,
            Name = "oe1-deploy-prod",
            ProjectId = 21,
            SourceBranch = "develop",
            YamlDefinition = "name: oe1-deploy-prod\nextends: application-deploy-prod-front-back@1\ntrigger: manual\nstages: []"
        };
        var cut = Render<PipelineProductionCompletion>(parameters => parameters.Add(item => item.Pipeline, pipeline));
        cut.WaitForAssertion(() => Assert.Equal(4, cut.FindAll("[data-testid='setup-production-apache-file']").Count));

        await cut.Find("[data-testid='pipeline-production-complete']").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => Assert.Single(Calls(Apache)));
        Assert.Empty(Calls(Apply));
        // The Compose file is still what blocks the rest of the preparation, and the completion says so.
        cut.WaitForAssertion(() => Assert.Equal("ExternalDeployCauseNotSynchronized",
            cut.Find("[data-testid='pipeline-production-completion-cause']").TextContent));
    }
}
