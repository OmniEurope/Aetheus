// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Http;

namespace Aetheus.WebAnalytics.Tests;

public sealed class AnalyticsPrivacyTests
{
    [Fact]
    public void NormalizeRoute_RemovesIdentifierValuesAndRejectsForbiddenInputs()
    {
        Assert.Equal("/orders/{id}", AnalyticsPrivacyPolicy.NormalizeRoute("/Orders/42"));
        Assert.Equal(
            "/users/{id}",
            AnalyticsPrivacyPolicy.NormalizeRoute("/users/6b40f4fd-53ac-4f84-8c9e-9fac6feec6e1"));
        Assert.Throws<InvalidOperationException>(() => AnalyticsPrivacyPolicy.NormalizeRoute("/orders?id=42"));
        Assert.Throws<InvalidOperationException>(() => AnalyticsPrivacyPolicy.NormalizeRoute("/users/alice@example.com"));
        Assert.Throws<InvalidOperationException>(() => AnalyticsPrivacyPolicy.NormalizeRoute("//example.com/orders"));
        Assert.Throws<InvalidOperationException>(() => AnalyticsPrivacyPolicy.NormalizeRoute(@"\orders\42"));
    }

    [Fact]
    public void Pseudonymizer_SeparatesApplicationsAndPeriods()
    {
        var source = new AnalyticsBrowserEvent
        {
            SchemaVersion = 1,
            EventId = Guid.NewGuid(),
            OccurredAtUtc = new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero),
            Kind = "page_view",
            Route = "/"
        };
        var context = Context("203.0.113.10", "ExampleBrowser/1");
        var first = new AnalyticsPseudonymizer(Options(1)).Create(context, source, "/");
        var second = new AnalyticsPseudonymizer(Options(2)).Create(context, source, "/");
        var tomorrow = new AnalyticsPseudonymizer(Options(1)).Create(
            context,
            source with { OccurredAtUtc = source.OccurredAtUtc.AddDays(1) },
            "/");

        Assert.NotEqual(first.DailyPseudonym, second.DailyPseudonym);
        Assert.NotEqual(first.DailyPseudonym, tomorrow.DailyPseudonym);
        Assert.NotEqual(first.SessionPseudonym, tomorrow.SessionPseudonym);
        Assert.Equal(first.MonthlyPseudonym, tomorrow.MonthlyPseudonym);
        var otherAgent = new AnalyticsPseudonymizer(Options(1)).Create(
            Context("203.0.113.10", "AnotherBrowser/9"),
            source,
            "/");
        Assert.Equal(first.DailyPseudonym, otherAgent.DailyPseudonym);
        Assert.Null(first.AuthenticatedPseudonym);
    }

    [Fact]
    public void Pseudonymizer_UsesAccountIdentityWithoutLinkingAnonymousIdentity()
    {
        var options = Options(1);
        var pseudonymizer = new AnalyticsPseudonymizer(options);
        var source = new AnalyticsBrowserEvent
        {
            SchemaVersion = 1,
            EventId = Guid.NewGuid(),
            OccurredAtUtc = new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero),
            Kind = "page_view",
            Route = "/"
        };
        var anonymous = pseudonymizer.Create(Context("203.0.113.10", "ExampleBrowser/1"), source, "/");
        var authenticatedContext = Context("203.0.113.10", "ExampleBrowser/1");
        authenticatedContext.User = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "user-7")],
                "test"));
        var authenticated = pseudonymizer.Create(authenticatedContext, source, "/");

        Assert.NotNull(authenticated.AuthenticatedPseudonym);
        Assert.NotEqual(anonymous.DailyPseudonym, authenticated.DailyPseudonym);
    }

    [Fact]
    public void R471_TwoDeclaredAccountsBehindOneNetwork_AreTwoVisitors_WhenTheHostAcceptsTheIdentifier()
    {
        // The static front server sees neither the session nor, behind its proxy, more than one
        // address: two people signed in from one network were one visitor and one authenticated visitor.
        var options = Options(1);
        options.AcceptDeclaredUserId = true;
        var pseudonymizer = new AnalyticsPseudonymizer(options);
        var source = new AnalyticsBrowserEvent
        {
            SchemaVersion = 1,
            EventId = Guid.NewGuid(),
            OccurredAtUtc = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero),
            Kind = "page_view",
            Route = "/"
        };

        var first = pseudonymizer.Create(Context("203.0.113.10", "ExampleBrowser/1"), source with { AuthenticatedUserId = "account-first" }, "/");
        var second = pseudonymizer.Create(Context("203.0.113.10", "ExampleBrowser/1"), source with { AuthenticatedUserId = "account-second" }, "/");
        // The same account from another network (a phone) is the same visitor.
        var firstElsewhere = pseudonymizer.Create(Context("198.51.100.7", "ExampleBrowser/1"), source with { AuthenticatedUserId = "account-first" }, "/");

        Assert.NotEqual(first.MonthlyPseudonym, second.MonthlyPseudonym);
        Assert.NotEqual(first.AuthenticatedPseudonym, second.AuthenticatedPseudonym);
        Assert.Equal(first.MonthlyPseudonym, firstElsewhere.MonthlyPseudonym);
        Assert.Equal(first.AuthenticatedPseudonym, firstElsewhere.AuthenticatedPseudonym);
        // Recette R1-68: but signed in at two places it is two sessions of Current visitors, and two
        // accounts at one place are two sessions; a second tab at the same place is the same session.
        var firstAgain = pseudonymizer.Create(Context("203.0.113.10", "ExampleBrowser/1"), source with { EventId = Guid.NewGuid(), AuthenticatedUserId = "account-first" }, "/");
        Assert.NotEqual(first.SessionPseudonym, firstElsewhere.SessionPseudonym);
        Assert.NotEqual(first.SessionPseudonym, second.SessionPseudonym);
        Assert.Equal(first.SessionPseudonym, firstAgain.SessionPseudonym);
        // The identifier is never exported: only pseudonyms derived from it are.
        Assert.DoesNotContain("account-first", System.Text.Json.JsonSerializer.Serialize(first), StringComparison.Ordinal);
    }

    [Fact]
    public void R471_ADeclaredIdentifier_IsIgnored_UnlessTheHostTurnsItOn()
    {
        var pseudonymizer = new AnalyticsPseudonymizer(Options(1));
        var source = new AnalyticsBrowserEvent
        {
            SchemaVersion = 1,
            EventId = Guid.NewGuid(),
            OccurredAtUtc = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero),
            Kind = "page_view",
            Route = "/"
        };
        var context = Context("203.0.113.10", "ExampleBrowser/1");

        var anonymous = pseudonymizer.Create(context, source, "/");
        var declared = pseudonymizer.Create(context, source with { AuthenticatedUserId = "account-first" }, "/");

        Assert.Equal(anonymous.MonthlyPseudonym, declared.MonthlyPseudonym);
        Assert.Null(declared.AuthenticatedPseudonym);
    }

    [Fact]
    public void Pseudonymizer_CountsADeclaredSignInPerMonthWithoutAnAccount()
    {
        // The static front server never sees who is signed in, so "authenticated visitors" stayed at 0
        // in production. The app now only says yes or no; the count is keyed on the network prefix and
        // on the month, never on a stable identity that would follow a network across months.
        var pseudonymizer = new AnalyticsPseudonymizer(Options(1));
        var source = new AnalyticsBrowserEvent
        {
            SchemaVersion = 1,
            EventId = Guid.NewGuid(),
            OccurredAtUtc = new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero),
            Kind = "page_view",
            Route = "/",
            SignedIn = true
        };
        var context = Context("203.0.113.10", "ExampleBrowser/1");

        var signedIn = pseudonymizer.Create(context, source, "/");
        var laterSameMonth = pseudonymizer.Create(context, source with { OccurredAtUtc = source.OccurredAtUtc.AddDays(3) }, "/");
        var nextMonth = pseudonymizer.Create(context, source with { OccurredAtUtc = source.OccurredAtUtc.AddMonths(1) }, "/");
        var anonymous = pseudonymizer.Create(context, source with { SignedIn = null }, "/");

        Assert.NotNull(signedIn.AuthenticatedPseudonym);
        Assert.Equal(signedIn.AuthenticatedPseudonym, laterSameMonth.AuthenticatedPseudonym);
        Assert.NotEqual(signedIn.AuthenticatedPseudonym, nextMonth.AuthenticatedPseudonym);
        Assert.Null(anonymous.AuthenticatedPseudonym);
        // The declaration changes nothing else: same visitor pseudonyms as the anonymous event.
        Assert.Equal(anonymous.DailyPseudonym, signedIn.DailyPseudonym);
        Assert.Equal(anonymous.SessionPseudonym, signedIn.SessionPseudonym);
    }

    [Fact]
    public void PrivacySignals_OptOutWithoutAnalyticsStorage()
    {
        var context = Context("203.0.113.10", "ExampleBrowser/1");
        context.Request.Headers["Sec-GPC"] = "1";

        Assert.True(AnalyticsPrivacyPolicy.IsOptedOut(context, Options(1)));
        Assert.Equal(24, PrivacyTranslations.SupportedLanguageCount);
    }

    [Theory]
    [InlineData("192.168.1.0/24", "192.168.1.42", true)]
    [InlineData("192.168.1.0/24", "192.168.10.42", false)]
    [InlineData("203.0.113.10", "203.0.113.10", true)]
    [InlineData("203.0.113.10", "203.0.113.100", false)]
    [InlineData("2001:db8::/32", "2001:db8:1::7", true)]
    [InlineData("2001:db8::/32", "2001:db9::7", false)]
    public void ExcludedIpNetworksUseAddressMathNotTextPrefixes(
        string network,
        string address,
        bool expected)
    {
        var options = Options(1);
        options.ExcludedIpNetworks = [network];

        Assert.Equal(
            expected,
            AnalyticsPrivacyPolicy.IsExcludedRequest(Context(address, "ExampleBrowser/1"), options));
    }

    [Fact]
    public void InvalidExcludedIpNetworkFailsConfigurationValidation()
    {
        var options = Options(1);
        options.ExcludedIpNetworks = ["192.168."];

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public async Task AuthenticatedOptOut_UsesHostAccountResolver()
    {
        var context = Context("203.0.113.10", "ExampleBrowser/1");
        context.User = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "user-7")],
                "test"));
        var options = Options(1);
        options.AuthenticatedOptOutResolver = _ => ValueTask.FromResult(true);

        Assert.True(await AnalyticsPrivacyPolicy.IsOptedOutAsync(context, options));
    }

    [Fact]
    public void PrivacyTranslations_ProvideCompleteLabelsAndFallbackForAllTwentyFourLanguages()
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            Assert.Equal(24, PrivacyTranslations.SupportedLanguageCount);
            Assert.Equal(24, PrivacyTranslations.SupportedLabelCount);
            foreach (var language in PrivacyTranslations.SupportedLanguages)
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
                Assert.False(string.IsNullOrWhiteSpace(PrivacyTranslations.Current.Title));
                Assert.False(string.IsNullOrWhiteSpace(PrivacyTranslations.CurrentLabels.LegalBasis));
                Assert.Equal(language, PrivacyTranslations.CurrentLanguage);
            }

            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ja");
            Assert.Equal("en", PrivacyTranslations.CurrentLanguage);
            Assert.Equal("Audience measurement", PrivacyTranslations.Current.Title);
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    private static DefaultHttpContext Context(string address, string userAgent)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(address);
        context.Request.Headers.UserAgent = userAgent;
        return context;
    }

    private static AetheusWebAnalyticsOptions Options(int applicationId) => new()
    {
        Enabled = true,
        ApplicationId = applicationId,
        SiteId = $"app-{applicationId}",
        IngestEndpoint = new Uri("https://aetheus.example/api/ingest/web-analytics/v1/events"),
        IngestKey = "ingest",
        PseudonymizationKey = new string('0', 32),
        EnablePrivacyPage = false
    };
}
