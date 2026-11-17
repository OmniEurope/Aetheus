// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;
using Microsoft.Playwright.NUnit;

namespace RehearsalBlazor.E2E;

/// <summary>Browser tests of the deployed application: the QA pipeline hands the front's address in QA_FRONT_URL.</summary>
[Parallelizable(ParallelScope.Self)]
public sealed class HomePageTests : PageTest
{
    private static string FrontUrl =>
        Environment.GetEnvironmentVariable("QA_FRONT_URL") is { Length: > 0 } url
            ? url
            : throw new InvalidOperationException("QA_FRONT_URL is not set: the QA pipeline gives the address of the deployed front.");

    [Test]
    public async Task Home_ShowsTheTitleAndTheDeployedVersion()
    {
        await Page.GotoAsync(FrontUrl);
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Rehearsal Blazor" })).ToBeVisibleAsync();
        await Expect(Page.GetByText(new Regex(@"^Version qa-\d+$"))).ToBeVisibleAsync();
    }

    [Test]
    public async Task Health_AnswersHealthy()
    {
        var response = await Page.APIRequest.GetAsync($"{FrontUrl}/health");
        Assert.That(response.Status, Is.EqualTo(200));
        Assert.That(await response.TextAsync(), Is.EqualTo("Healthy"));
    }
}
