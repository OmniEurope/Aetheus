// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Bunit;
using Microsoft.Extensions.Localization;

namespace Aetheus.Front.Tests.Pages;

/// <summary>The run page names the cancellation author recorded by the backend, whether a user or
/// the system.</summary>
public sealed class PipelineRunCancelledByTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineRunCancelledByTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetJsonResponse(HttpMethod.Get, "api/releases/by-run/", new List<ReleaseDto>());
        _handler.SetPaginatedJsonResponse<PipelineRunDto>(HttpMethod.Get, "/runs?pageSize=50", []);
        _handler.SetPaginatedJsonResponse<AiRunResultDto>(HttpMethod.Get, "api/ai/results", []);
        _handler.SetResponse(HttpMethod.Get, "api/analysis/runs/", System.Net.HttpStatusCode.NoContent);
    }

    [Theory]
    [InlineData("alice", "RunCancelledBy")]
    [InlineData("system", "RunCancelledBySystem")]
    public void ACancelledRun_SaysWhoCancelledIt(string cancelledBy, string expectedKey)
    {
        _handler.SetJsonResponse("api/pipelines/runs/40", new PipelineRunDto
        {
            Id = 40,
            PipelineName = "release-fast",
            Status = PipelineStatus.Cancelled,
            CancellationRequested = true,
            CancelledBy = cancelledBy,
            CancellationReason = "whatever",
            StartedAt = new DateTime(2026, 10, 3, 10, 0, 0, DateTimeKind.Utc),
            CompletedAt = new DateTime(2026, 10, 3, 10, 5, 0, DateTimeKind.Utc)
        });

        var cut = Render<PipelineRun>(parameters => parameters.Add(page => page.RunId, 40));

        cut.WaitForAssertion(() => Assert.Equal(expectedKey, cut.Find(".run-cancelled-by").TextContent.Trim()));
    }

    [Fact]
    public void ARunCancelledBeforeTheAuthorWasRecorded_SaysNothingAboutIt()
    {
        _handler.SetJsonResponse("api/pipelines/runs/41", new PipelineRunDto
        {
            Id = 41,
            PipelineName = "old-cancelled",
            Status = PipelineStatus.Cancelled,
            StartedAt = new DateTime(2026, 10, 3, 10, 0, 0, DateTimeKind.Utc),
            CompletedAt = new DateTime(2026, 10, 3, 10, 5, 0, DateTimeKind.Utc)
        });

        var cut = Render<PipelineRun>(parameters => parameters.Add(page => page.RunId, 41));

        cut.WaitForState(() => cut.Markup.Contains("old-cancelled"));
        Assert.Empty(cut.FindAll(".run-cancelled-by"));
    }

    [Theory]
    [InlineData("en", "alice", "Cancelled by alice.")]
    [InlineData("fr-FR", "alice", "Annulée par alice.")]
    [InlineData("fr-FR", "system", "Annulée par Aetheus (annulation automatique).")]
    public void TheAuthorLine_ReadsInBothLanguages(string culture, string cancelledBy, string expected)
    {
        var previous = System.Globalization.CultureInfo.CurrentUICulture;
        var previousFormat = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo(culture);
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo(culture);
            var resources = new System.Resources.ResourceManager("Aetheus.Front.Resources.AppStrings", typeof(Aetheus.Front.Resources.AppStrings).Assembly);

            var text = PipelineRunPresentation.CancelledByText(
                new PipelineRunDto { Status = PipelineStatus.Cancelled, CancelledBy = cancelledBy },
                new ResourceLocalizer(resources));

            Assert.Equal(expected, text);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = previous;
            System.Globalization.CultureInfo.CurrentCulture = previousFormat;
        }
    }

    private sealed class ResourceLocalizer(System.Resources.ResourceManager resources) : IStringLocalizer
    {
        public LocalizedString this[string name] => new(name, resources.GetString(name) ?? name);
        public LocalizedString this[string name, params object[] arguments] => new(name, string.Format(this[name].Value, arguments));
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }
}
