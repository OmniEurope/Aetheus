// SPDX-License-Identifier: EUPL-1.2
using Microsoft.AspNetCore.Mvc.Testing;
using RehearsalBlazor;

namespace RehearsalBlazor.Tests;

/// <summary>Runs the application itself in memory and asks it what a visitor and a probe ask: its health, its version and its page.</summary>
public sealed class ApplicationTests
{
    private WebApplicationFactory<Program> factory = null!;
    private HttpClient client = null!;

    [OneTimeSetUp]
    public void StartApplication()
    {
        factory = new WebApplicationFactory<Program>();
        client = factory.CreateClient();
    }

    [OneTimeTearDown]
    public void StopApplication()
    {
        client.Dispose();
        factory.Dispose();
    }

    [Test]
    public async Task Health_AnswersHealthy()
    {
        var response = await client.GetAsync("/health");

        Assert.That(response.IsSuccessStatusCode, Is.True);
        Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("Healthy"));
    }

    [Test]
    public async Task Version_AnswersTheVersionTheApplicationRunsWith()
    {
        var response = await client.GetAsync("/version");

        Assert.That(response.IsSuccessStatusCode, Is.True);
        Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo(VersionInfo.Current));
    }

    [Test]
    public async Task HomePage_ShowsItsTitleAndTheVersion()
    {
        var response = await client.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();

        Assert.That(response.IsSuccessStatusCode, Is.True);
        Assert.That(html, Does.Contain("<h1>Rehearsal Blazor</h1>"));
        Assert.That(html, Does.Contain($"Version {VersionInfo.Current}"));
    }

    [Test]
    public async Task UnknownPath_IsNotFound()
    {
        var response = await client.GetAsync("/does-not-exist");

        Assert.That((int)response.StatusCode, Is.EqualTo(404));
    }
}
