// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.VariableLibraries;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages.VariableLibraries;

/// <summary>Deleting a <c>PORT_*</c> entry offers to release its reservation: only for a real port, only
/// once the reader confirms, and the answer says whether the reservation was this library's.</summary>
public sealed class VariableLibraryEditPortReleaseTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public VariableLibraryEditPortReleaseTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto> { Items = [], TotalCount = 0 });
        _handler.SetJsonResponse("api/variable-libraries/1", new VariableLibraryDetailDto { Id = 1, Name = "lib", ProjectId = 1, Entries = [] });
    }

    private ImmediateDialogService Dialog => (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();

    private async Task OfferAsync(VariableEntryDto? deleted)
    {
        var cut = Render<VariableLibraryEdit>(p => p.Add(x => x.Id, 1));
        await cut.InvokeAsync(() => cut.Instance.OfferPortReleaseAsync(deleted));
    }

    private bool ReleaseRequested() => _handler.Requests.Any(r => r.Url.Contains("/ports/", StringComparison.Ordinal));

    [Theory]
    [InlineData("DB_URL", "8080")]
    [InlineData("PORT_HTTP", "http")]
    [InlineData("PORT_HTTP", "0")]
    [InlineData("PORT_HTTP", "70000")]
    [InlineData("PORT_HTTP", "+80")]
    public async Task ANonPortEntry_OffersNothing(string key, string value)
    {
        await OfferAsync(new VariableEntryDto { Id = 10, Key = key, Value = value });

        Assert.Null(Dialog.LastConfirmMessage);
        Assert.False(ReleaseRequested());
    }

    [Fact]
    public async Task NoEntry_OffersNothing()
    {
        await OfferAsync(null);

        Assert.Null(Dialog.LastConfirmMessage);
    }

    [Fact]
    public async Task ADeclinedOffer_ReleasesNothing()
    {
        Dialog.ConfirmResult = false;

        await OfferAsync(new VariableEntryDto { Id = 10, Key = "PORT_HTTP", Value = "8080" });

        Assert.NotNull(Dialog.LastConfirmMessage);
        Assert.False(ReleaseRequested());
    }

    [Fact]
    public async Task AReleasedReservation_IsReportedAsSuccess()
    {
        Dialog.ConfirmResult = true;
        _handler.SetJsonResponse("api/variable-libraries/1/ports/8080", 1);

        await OfferAsync(new VariableEntryDto { Id = 10, Key = "PORT_HTTP", Value = "8080" });

        Assert.True(ReleaseRequested());
        Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniSeverity.Success);
    }

    [Fact]
    public async Task AReservationHeldBySomebodyElse_IsReportedAsAWarning()
    {
        Dialog.ConfirmResult = true;
        _handler.SetJsonResponse("api/variable-libraries/1/ports/8080", 0);

        await OfferAsync(new VariableEntryDto { Id = 10, Key = "PORT_HTTP", Value = "8080" });

        Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniSeverity.Warning);
        Assert.DoesNotContain(Services.Toasts(), toast => toast.Severity == OmniSeverity.Success);
    }
}
