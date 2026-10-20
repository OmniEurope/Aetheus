// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Configuration;
using Microsoft.Extensions.Configuration;

namespace Aetheus.Back.Tests;

/// <summary>The HTTPS redirection runs only where the middleware could find a port to redirect
/// to.</summary>
public sealed class HttpsPortConfigurationTests
{
    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(item => item.Key, item => (string?)item.Value))
            .Build();

    [Fact]
    public void TheProductionContainer_HttpOnlyBehindApache_HasNoPort() =>
        Assert.False(HttpsPortConfiguration.IsKnown(Configuration(("urls", "http://+:8080"))));

    [Theory]
    [InlineData("HTTPS_PORT", "443")]
    [InlineData("ANCM_HTTPS_PORT", "44300")]
    [InlineData("HTTPS_PORTS", "8443")]
    [InlineData("urls", "http://+:8080;https://+:8443")]
    [InlineData("Kestrel:Endpoints:Https:Url", "https://0.0.0.0:5001")]
    public void AnyPortTheMiddlewareWouldRead_EnablesIt(string key, string value) =>
        Assert.True(HttpsPortConfiguration.IsKnown(Configuration((key, value))));
}
