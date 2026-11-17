// SPDX-License-Identifier: EUPL-1.2
using Microsoft.Playwright;

namespace Aetheus.E2E;

/// <summary>
/// R-29: the browser half of the pipeline rehearsal (<c>launch-windows.ps1 -pr</c>, <c>scripts/pipeline-rehearsal.ps1</c>). The
/// rehearsal has already created the project, pushed its repository and linked the simulated VPS; this test creates the
/// pipelines through the real setup wizard (its summary, the creation of the libraries and vaults, the production setup
/// read from the Compose file, then "Create pipelines"), exactly as a user does. The rehearsal then runs those pipelines.
/// Explicit: it needs the rehearsal's stack and project, so it never runs in the ordinary E2E suite.
/// </summary>
[Explicit("Run by the pipeline rehearsal only (launch-windows.ps1 -pr): needs its project and simulated VPS.")]
[Category("Rehearsal")]
public sealed class PipelineRehearsalWizardTests : E2ETestBase
{
    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{name} is not set: this test is run by the pipeline rehearsal only.");

    /// <summary>The values the rehearsal types where the production setup asks for one (<c>NAME=value;NAME=value</c>).</summary>
    private static Dictionary<string, string> ProductionValues() =>
        (Environment.GetEnvironmentVariable("REHEARSAL_PRODUCTION_VALUES") ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => pair[0], pair => pair.Length > 1 ? pair[1] : string.Empty, StringComparer.Ordinal);

    private static string ResultsDirectory =>
        Environment.GetEnvironmentVariable("AETHEUS_E2E_RESULTS_DIR") is { Length: > 0 } directory ? directory : Path.GetTempPath();

    private Task ScreenshotAsync(string name) =>
        Page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(ResultsDirectory, $"rehearsal-{name}.png"), FullPage = true });

    [Test]
    public async Task Wizard_CreatesThePipelinesOfTheRehearsalProject()
    {
        var projectId = int.Parse(Required("REHEARSAL_PROJECT_ID"), System.Globalization.CultureInfo.InvariantCulture);
        var address = $"/pipelines/setup?projectId={projectId}&prefix={Uri.EscapeDataString(Required("REHEARSAL_PREFIX"))}"
            + $"&options={Uri.EscapeDataString(Required("REHEARSAL_WIZARD_OPTIONS"))}&architecture=application&sameProject=yes"
            + $"&branch={Uri.EscapeDataString(Required("REHEARSAL_BRANCH"))}&step=summary";
        try
        {
            await NavigateToAsync(address);
            var recap = Page.GetByTestId("setup-recap");
            await Expect(recap).ToBeVisibleAsync(new() { Timeout = 120_000 });
            await Expect(Page.GetByTestId("setup-todo-state")).ToBeVisibleAsync(new() { Timeout = 120_000 });

            // The libraries and vaults the templates read: created with the wizard's own button when some are missing.
            var provision = Page.GetByTestId("setup-provision").GetByRole(AriaRole.Button);
            if (await provision.IsVisibleAsync())
            {
                await provision.ClickAsync();
                await Expect(Page.GetByTestId("setup-provision")).ToHaveCountAsync(0, new() { Timeout = 120_000 });
            }

            // The production setup read from the Compose file: every value only the operator knows is typed.
            await Expect(Page.GetByTestId("setup-production")).ToBeVisibleAsync(new() { Timeout = 120_000 });
            await FillProductionValuesAsync();
            await ScreenshotAsync("summary");

            var create = Page.GetByRole(AriaRole.Button, new() { Name = "Create pipelines", Exact = true });
            await Expect(create).ToBeEnabledAsync(new() { Timeout = 120_000 });
            await create.ClickAsync();
            await Page.WaitForURLAsync($"**/projects/{projectId}", new() { Timeout = 300_000 });
            await ScreenshotAsync("created");
        }
        catch
        {
            await ScreenshotAsync("failure");
            throw;
        }
    }

    private async Task FillProductionValuesAsync()
    {
        var values = ProductionValues();
        var fields = Page.Locator("[data-testid=setup-production-to-fill] input[id^='setup-production-value-']");
        var missing = new List<string>();
        for (var index = 0; index < await fields.CountAsync(); index++)
        {
            var field = fields.Nth(index);
            var name = (await field.GetAttributeAsync("id"))!["setup-production-value-".Length..];
            if (!values.TryGetValue(name, out var value))
            {
                missing.Add(name);
                continue;
            }
            await field.FillAsync(value);
            await Expect(Page.GetByTestId($"setup-production-kind-{name}")).ToHaveTextAsync("Value typed", new() { Timeout = 30_000 });
        }
        Assert.That(missing, Is.Empty, "The production setup asks for values the rehearsal does not give (REHEARSAL_PRODUCTION_VALUES).");
    }
}
