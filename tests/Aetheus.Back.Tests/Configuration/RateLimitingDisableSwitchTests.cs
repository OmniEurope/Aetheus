// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Aetheus.Back.Tests.Configuration;

/// <summary>R-51: RateLimiting:Disabled=true is refused at startup outside Development.</summary>
public sealed class RateLimitingDisableSwitchTests
{
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("qa")]
    public void SwitchOnOutsideDevelopment_RefusesToRegister(string environment)
    {
        var builder = Builder(environment, disabled: "true");

        var error = Assert.Throws<InvalidOperationException>(() =>
            builder.Services.AddAetheusRateLimiting(builder.Configuration, builder.Environment.IsDevelopment()));
        Assert.Contains("RateLimiting:Disabled", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Production", "false", true)]
    [InlineData("Production", null, true)]
    [InlineData("Development", "false", true)]
    [InlineData("Development", "true", false)]
    public void AllowedCombinations_Register(string environment, string? disabled, bool expectsGlobalLimiter)
    {
        var builder = Builder(environment, disabled);

        builder.Services.AddAetheusRateLimiting(builder.Configuration, builder.Environment.IsDevelopment());

        using var provider = builder.Services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<RateLimiterOptions>>().Value;
        Assert.Equal(expectsGlobalLimiter, options.GlobalLimiter is not null);
    }

    private static WebApplicationBuilder Builder(string environment, string? disabled)
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["RateLimiting:Disabled"] = disabled });
        return builder;
    }
}
