// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Aetheus.Back.Tests.Analysis;

/// <summary>An enabled Dependency-Track needs an API key and an absolute HTTPS endpoint, HTTP being
/// accepted only on loopback; a disabled one is not checked.</summary>
public sealed class AnalysisModuleEndpointValidationTests
{
    private static DependencyTrackOptions Resolve(bool enabled, string baseUrl, string apiKey)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{DependencyTrackOptions.SectionName}:Enabled"] = enabled.ToString(),
            [$"{DependencyTrackOptions.SectionName}:BaseUrl"] = baseUrl,
            [$"{DependencyTrackOptions.SectionName}:ApiKey"] = apiKey,
        }).Build();
        using var provider = new ServiceCollection().AddAnalysisModule(configuration).BuildServiceProvider();
        return provider.GetRequiredService<IOptions<DependencyTrackOptions>>().Value;
    }

    [Theory]
    [InlineData("https://dtrack.example", "key")]
    [InlineData("http://127.0.0.1:8081", "key")]
    [InlineData("http://localhost:8081", "key")]
    public void AnEnabledTrackerWithAKeyAndASafeEndpoint_IsAccepted(string baseUrl, string apiKey) =>
        Assert.Equal(baseUrl, Resolve(true, baseUrl, apiKey).BaseUrl);

    [Theory]
    [InlineData("https://dtrack.example", "  ")]
    [InlineData("dtrack.example", "key")]
    [InlineData("http://dtrack.example", "key")]
    [InlineData("ftp://127.0.0.1", "key")]
    public void AnEnabledTrackerWithoutKeyOrWithAnUnsafeEndpoint_IsRefused(string baseUrl, string apiKey)
    {
        var error = Assert.Throws<OptionsValidationException>(() => Resolve(true, baseUrl, apiKey));

        Assert.Contains("absolute HTTPS endpoint", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADisabledTracker_IsNotCheckedForItsEndpoint() =>
        Assert.False(Resolve(false, "not a url", "").Enabled);
}
