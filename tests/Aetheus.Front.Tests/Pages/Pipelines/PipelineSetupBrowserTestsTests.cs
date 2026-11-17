// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Front.Components.Pipelines;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>Recette R-59 and R-67 (user decision 2026-11-03): the wizard declares by itself what its QA pipelines run, so none is
/// left to fill in by hand. The QA pipeline gets the command of the Playwright or Cypress tests the repository holds, or
/// <c>none</c> when the repository was read and holds none; the extended QA pipeline gets <c>none</c> (nothing in a repository
/// says what a project's long scenarios are). What was not read is not declared: the run then says it is missing.</summary>
public sealed class PipelineSetupBrowserTestsTests : BunitContext
{
    private const string E2e = "APPLICATION_QA_E2E_COMMANDS";
    private const string Extended = "APPLICATION_QA_EXTENDED_COMMANDS";
    private const string Image = "APPLICATION_QA_E2E_IMAGE";

    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineSetupBrowserTestsTests() => _handler = BunitTestHelper.RegisterServices(this);

    private static PipelineSetupDetectionDto Read(bool? found, string? tool = null, string? directory = null) =>
        new() { HasBrowserTests = found, BrowserTestTool = tool, BrowserTestDirectory = directory };

    [Fact]
    public void ThePlaywrightCommand_RunsFromTheDirectoryOfThePackage_AgainstTheDeployedFront()
    {
        var command = PipelineSetupBrowserTests.Commands(Read(true, "playwright", "web"));

        Assert.NotNull(command);
        Assert.StartsWith("cd \"$QA_SOURCE/web\" && ", command, StringComparison.Ordinal);
        Assert.Contains("npx playwright install chromium", command, StringComparison.Ordinal);
        Assert.Contains("PLAYWRIGHT_BASE_URL=\"$QA_FRONT_URL\"", command, StringComparison.Ordinal);
        Assert.EndsWith("npx playwright test", command, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', command);
    }

    [Fact]
    public void TheCypressCommand_RunsFromTheRoot_WhenThePackageIsThere()
    {
        var command = PipelineSetupBrowserTests.Commands(Read(true, "cypress", string.Empty));

        Assert.NotNull(command);
        Assert.StartsWith("cd \"$QA_SOURCE\" && ", command, StringComparison.Ordinal);
        Assert.Contains("CYPRESS_BASE_URL=\"$QA_FRONT_URL\"", command, StringComparison.Ordinal);
        Assert.EndsWith("npx cypress run", command, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, null, null, "none")]
    [InlineData(null, null, null, null)]
    [InlineData(true, "playwright", "a b", null)]
    [InlineData(true, "playwright", "web\"; rm -rf /", null)]
    [InlineData(true, "selenium", "web", null)]
    public void WhatIsNotKnown_IsNotDeclared_AndAnUnsafeDirectoryIsNeverWrittenIntoACommand(
        bool? found, string? tool, string? directory, string? expected)
    {
        Assert.Equal(expected, PipelineSetupBrowserTests.Commands(Read(found, tool, directory)));
    }

    [Fact]
    public void WithNoDetectionAtAll_NothingIsDeclared()
    {
        Assert.Null(PipelineSetupBrowserTests.Commands(null));
    }

    private static PipelineSetupYamlInput Input(string? e2e, string? extended = null) => new(
        "atlas", (true, true, false), "package-web-analytics-dotnet",
        Rollback: true, Quality: true, Security: true, Qa: true, QaExtended: true, SecurityHistory: false,
        ["ci", "qa", "qa-extended", "candidate", "nightly"], QaE2eCommands: e2e, QaExtendedCommands: extended);

    [Fact]
    public void TheBuilder_WritesTheDeclarationOnTheQaPipelinesOnly()
    {
        var input = Input("none", "none");

        var qa = PipelineSetupYamlBuilder.Build("atlas-qa", "application-qa", 3, input);
        var extended = PipelineSetupYamlBuilder.Build("atlas-qa-extended", "application-qa-extended", 3, input);
        var candidate = PipelineSetupYamlBuilder.Build("atlas-candidate", "application-candidate", 3, input);

        Assert.Contains($"{E2e}: \"none\"", qa, StringComparison.Ordinal);
        Assert.DoesNotContain(Extended, qa, StringComparison.Ordinal);
        Assert.Contains($"{Extended}: \"none\"", extended, StringComparison.Ordinal);
        Assert.DoesNotContain(E2e, extended, StringComparison.Ordinal);
        Assert.DoesNotContain(E2e, candidate, StringComparison.Ordinal);
        Assert.DoesNotContain(Extended, candidate, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBuilder_WritesNothing_ForWhatWasNotDeclared()
    {
        var qa = PipelineSetupYamlBuilder.Build("atlas-qa", "application-qa", 3, Input(e2e: null));
        var extended = PipelineSetupYamlBuilder.Build("atlas-qa-extended", "application-qa-extended", 3, Input(e2e: null));

        Assert.DoesNotContain(E2e, qa, StringComparison.Ordinal);
        Assert.DoesNotContain(Extended, extended, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBuilder_EscapesTheCommandAsOneDoubleQuotedYamlLine()
    {
        var command = PipelineSetupBrowserTests.Commands(Read(true, "playwright", "web"))!;

        var yaml = PipelineSetupYamlBuilder.Build("atlas-qa", "application-qa", 3, Input(command));

        var line = Assert.Single(yaml.Split('\n'), text => text.TrimStart().StartsWith(E2e, StringComparison.Ordinal));
        Assert.StartsWith($"  {E2e}: \"cd \\\"$QA_SOURCE/web\\\" && ", line, StringComparison.Ordinal);
        Assert.EndsWith("npx playwright test\"", line, StringComparison.Ordinal);
    }

    /// <summary>R-20 (user decision 2026-11-10): the browser tests run in the official image of their tool, pinned by digest, which
    /// carries the browser and its system libraries; the wizard writes it beside the commands, so nothing is installed on the
    /// agent (a non-root agent could not install <c>libgbm.so.1</c>).</summary>
    [Theory]
    [InlineData("playwright", "mcr.microsoft.com/playwright:")]
    [InlineData("cypress", "cypress/browsers:")]
    public void TheBrowserImage_IsTheOfficialPinnedImageOfTheTool(string tool, string repository)
    {
        var image = PipelineSetupBrowserTests.Image(Read(true, tool, "web"));

        Assert.NotNull(image);
        Assert.StartsWith(repository, image, StringComparison.Ordinal);
        Assert.Matches(@"@sha256:[0-9a-f]{64}\z", image);
    }

    [Fact]
    public void TheBrowserImage_OfADotNetPlaywrightProject_IsThePlaywrightDotNetImage()
    {
        var image = PipelineSetupBrowserTests.Image(DotNet());

        Assert.NotNull(image);
        Assert.StartsWith("mcr.microsoft.com/playwright/dotnet:", image, StringComparison.Ordinal);
        Assert.Matches(@"@sha256:[0-9a-f]{64}\z", image);
    }

    [Theory]
    [InlineData(false, null, null)]
    [InlineData(null, null, null)]
    [InlineData(true, "playwright", "web\"; rm -rf /")]
    [InlineData(true, "selenium", "web")]
    public void WhereNoCommandIsDeclared_NoImageIsDeclared(bool? found, string? tool, string? directory)
    {
        Assert.Null(PipelineSetupBrowserTests.Image(Read(found, tool, directory)));
        Assert.Null(PipelineSetupBrowserTests.Image(null));
    }

    [Fact]
    public void TheBuilder_WritesTheImageBesideTheCommandsOnTheQaPipelineOnly()
    {
        var input = Input("none", "none") with
        {
            QaE2eCommands = "cd \"$QA_SOURCE\" && npx playwright test",
            QaE2eImage = "mcr.microsoft.com/playwright:vX@sha256:abc"
        };

        var qa = PipelineSetupYamlBuilder.Build("atlas-qa", "application-qa", 3, input);
        var extended = PipelineSetupYamlBuilder.Build("atlas-qa-extended", "application-qa-extended", 3, input);
        var withoutCommands = PipelineSetupYamlBuilder.Build("atlas-qa", "application-qa", 3, input with { QaE2eCommands = null });

        Assert.Contains($"{Image}: \"mcr.microsoft.com/playwright:vX@sha256:abc\"", qa, StringComparison.Ordinal);
        Assert.DoesNotContain(Image, extended, StringComparison.Ordinal);
        Assert.DoesNotContain(Image, withoutCommands, StringComparison.Ordinal);
    }

    private static PipelineSetupDetectionDto DotNet(
        string project = "tests/App.E2E/App.E2E.csproj", string? framework = "net10.0", bool? testingPlatform = true) => new()
        {
            HasBrowserTests = true,
            BrowserTestTool = "playwright-dotnet",
            BrowserTestDirectory = project.Contains('/', StringComparison.Ordinal) ? project[..project.LastIndexOf('/')] : string.Empty,
            BrowserTestProject = project,
            BrowserTestFramework = framework,
            BrowserTestUsesTestingPlatform = testingPlatform
        };

    [Fact]
    public void TheDotNetPlaywrightCommand_BuildsInstallsTheBrowserAndRunsTheTestsAgainstTheStack_NeverNone()
    {
        var command = PipelineSetupBrowserTests.Commands(DotNet());

        Assert.NotNull(command);
        Assert.NotEqual("none", command);
        Assert.StartsWith("set -eu\n", command, StringComparison.Ordinal);
        Assert.Contains("E2E_PROJECT=\"tests/App.E2E/App.E2E.csproj\"", command, StringComparison.Ordinal);
        Assert.Contains("cd \"$QA_SOURCE\"", command, StringComparison.Ordinal);
        Assert.Contains("\"$DOTNET\" build \"$E2E_PROJECT\" --configuration Release", command, StringComparison.Ordinal);
        Assert.Contains("PLAYWRIGHT_SCRIPT=\"tests/App.E2E/bin/Release/net10.0/playwright.ps1\"", command, StringComparison.Ordinal);
        Assert.Contains("pwsh -NoProfile -File \"$PLAYWRIGHT_SCRIPT\" install chromium", command, StringComparison.Ordinal);
        // R-20: the browser's system libraries come with the image the commands run in, never from the agent.
        Assert.DoesNotContain("--with-deps", command, StringComparison.Ordinal);
        Assert.DoesNotContain("tool install PowerShell", command, StringComparison.Ordinal);
        Assert.DoesNotContain("id -u", command, StringComparison.Ordinal);
        Assert.Contains("export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 QA_FRONT_URL QA_BACK_URL", command, StringComparison.Ordinal);
        Assert.Contains("--no-build", command, StringComparison.Ordinal);
        Assert.Contains("test -n \"$(find \"$RESULTS\" -name '*.trx' -size +0", command, StringComparison.Ordinal);
        Assert.EndsWith("exit \"$STATUS\"", command, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDotNetPlaywrightCommand_FollowsTheRunnerOfTheRepository()
    {
        var testingPlatform = PipelineSetupBrowserTests.Commands(DotNet(testingPlatform: true))!;
        var vsTest = PipelineSetupBrowserTests.Commands(DotNet(testingPlatform: null))!;

        Assert.Contains("test --project \"$E2E_PROJECT\"", testingPlatform, StringComparison.Ordinal);
        Assert.Contains("--report-trx --report-trx-filename e2e.trx", testingPlatform, StringComparison.Ordinal);
        Assert.Contains("test \"$E2E_PROJECT\" --configuration Release --no-build", vsTest, StringComparison.Ordinal);
        Assert.Contains("--logger \"trx;LogFileName=e2e.trx\"", vsTest, StringComparison.Ordinal);
        Assert.DoesNotContain("--report-trx", vsTest, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDotNetPlaywrightCommand_WithNoKnownFramework_FindsTheScriptInTheBuildFolder()
    {
        var command = PipelineSetupBrowserTests.Commands(DotNet("E2E.csproj", framework: null))!;

        Assert.Contains("PLAYWRIGHT_SCRIPT=\"$(find \"bin/Release\" -name playwright.ps1 | head -n 1)\"", command, StringComparison.Ordinal);
        Assert.Contains("test -n \"$PLAYWRIGHT_SCRIPT\"", command, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("tests/a b/E2E.csproj", "net10.0")]
    [InlineData("tests/E2E.csproj\"; rm -rf /", "net10.0")]
    [InlineData("tests/E2E/E2E.csproj", "net10.0\"; rm -rf /")]
    [InlineData(null, "net10.0")]
    public void AnUnsafeDotNetProjectOrFramework_IsNeverWrittenIntoACommand(string? project, string framework)
    {
        var detection = DotNet() with { BrowserTestProject = project, BrowserTestFramework = framework };

        Assert.Null(PipelineSetupBrowserTests.Commands(detection));
    }

    [Fact]
    public void TheBuilder_WritesAMultiLineCommandAsABlockScalar_ThatReadsBackAsTheSameCommand()
    {
        var command = PipelineSetupBrowserTests.Commands(DotNet())!;

        var yaml = PipelineSetupYamlBuilder.Build("atlas-qa", "application-qa", 3, Input(command));

        Assert.Contains($"\n  {E2e}: |\n    set -eu\n", yaml, StringComparison.Ordinal);
        var parsed = new YamlDotNet.Serialization.DeserializerBuilder().Build()
            .Deserialize<Dictionary<string, object>>(yaml);
        var variables = (Dictionary<object, object>)parsed["variables"];
        Assert.Equal(command, ((string)variables[E2e]).TrimEnd('\n'));
        Assert.Equal("atlas", (string)variables["APPLICATION_PREFIX"]);
    }

    private async Task<CreatePipelineRequest[]> CreateWithDetectionAsync(PipelineSetupDetectionDto detection, bool extended)
    {
        _handler.SetPaginatedJsonResponse("api/projects", [new ProjectDto { Id = 7, Name = "Atlas", DefaultBranch = "develop" }]);
        _handler.SetJsonResponse("api/pipelines/templates", Templates());
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines", new PipelineDto { Id = 91 });
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines/setup/readiness",
            new PipelineSetupReadinessDto { RepositoryInspected = true, InspectedBranch = "develop", Detection = detection });
        Services.GetRequiredService<NavigationManager>().NavigateTo("http://localhost/pipelines/setup?projectId=7");
        var cut = Render<PipelineSetupWizard>();
        cut.WaitForAssertion(() => Assert.Contains("Atlas", cut.Markup, StringComparison.Ordinal));
        var wizard = cut.FindComponent<WizardDialog>();
        await cut.InvokeAsync(wizard.Instance.NextStep);
        await PipelineSetupWizardTests.ChooseArchitectureAsync(cut, "application", sameProject: false);
        await cut.InvokeAsync(wizard.Instance.NextStep);
        if (extended) await cut.Find("[data-testid='setup-option-qa-extended']").ClickAsync(new MouseEventArgs());
        await cut.InvokeAsync(wizard.Instance.NextStep);
        cut.WaitForAssertion(() => Assert.Contains("setup-summary", cut.Markup, StringComparison.Ordinal));
        await cut.Find(".omni-wizard__actions button.omni-wizard__finish").ClickAsync(new MouseEventArgs());
        var expected = extended ? 8 : 7;
        cut.WaitForAssertion(() => Assert.Equal(expected, Created().Length));
        return Created();
    }

    private CreatePipelineRequest[] Created() => _handler.RequestDetails
        .Where(request => request.Method == HttpMethod.Post.Method
            && request.Url.EndsWith("api/pipelines", StringComparison.OrdinalIgnoreCase))
        .Select(request => JsonSerializer.Deserialize<CreatePipelineRequest>(request.Body!,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
        .ToArray();

    private static string YamlOf(CreatePipelineRequest[] created, string name) =>
        Assert.Single(created, request => request.Name == name).YamlDefinition;

    [Fact]
    public async Task ARepositoryWithPlaywrightTests_GivesTheQaPipelineTheirCommand_AndAnExtendedQaGetsNone()
    {
        var created = await CreateWithDetectionAsync(Read(true, "playwright", "e2e"), extended: true);

        Assert.Contains($"{E2e}: \"cd \\\"$QA_SOURCE/e2e\\\" && ", YamlOf(created, "atlas-qa"), StringComparison.Ordinal);
        Assert.Contains("npx playwright test", YamlOf(created, "atlas-qa"), StringComparison.Ordinal);
        Assert.Contains($"{Extended}: \"none\"", YamlOf(created, "atlas-qa-extended"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARepositoryWithADotNetPlaywrightProject_GivesTheQaPipelineARealCommand_NotNone()
    {
        var created = await CreateWithDetectionAsync(DotNet(), extended: false);

        var qa = YamlOf(created, "atlas-qa");
        Assert.DoesNotContain($"{E2e}: \"none\"", qa, StringComparison.Ordinal);
        Assert.Contains($"{E2e}: |", qa, StringComparison.Ordinal);
        Assert.Contains("playwright.ps1", qa, StringComparison.Ordinal);
        Assert.Contains("--no-build", qa, StringComparison.Ordinal);
        Assert.Contains($"{Image}: \"mcr.microsoft.com/playwright/dotnet:", qa, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheQaPipelineOfARepositoryReadWithoutBrowserTests_DeclaresNoImage()
    {
        var created = await CreateWithDetectionAsync(Read(false), extended: false);

        Assert.DoesNotContain(Image, YamlOf(created, "atlas-qa"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARepositoryReadToTheEndWithoutBrowserTests_DeclaresNoneInsteadOfLeavingTheRunToWarn()
    {
        var created = await CreateWithDetectionAsync(Read(false), extended: false);

        Assert.Contains($"{E2e}: \"none\"", YamlOf(created, "atlas-qa"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARepositoryThatWasNotReadToTheEnd_DeclaresNothing_SoTheRunKeepsItsWarning()
    {
        var created = await CreateWithDetectionAsync(Read(null), extended: false);

        Assert.DoesNotContain(E2e, YamlOf(created, "atlas-qa"), StringComparison.Ordinal);
    }

    private static List<PipelineTemplateSummaryDto> Templates() =>
    [
        .. new[]
        {
            "application-ci", "application-quality", "application-security", "application-qa", "application-qa-extended",
            "application-candidate", "application-nightly", "application-deploy-prod", "application-deploy-prod-front-back"
        }.Select(name => new PipelineTemplateSummaryDto
        {
            Id = name.GetHashCode(StringComparison.Ordinal),
            Name = name,
            Version = 1
        })
    ];
}
