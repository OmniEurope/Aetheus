// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Settings;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Settings;

/// <summary>R1-25: OE's own default content width is the centred column, Aetheus' is the full width. A
/// user with no appearance, and one whose saved appearance predates the width option, get full width;
/// a column the user chose stays.</summary>
public sealed class LegacyAppearanceMigrationWidthTests : BunitContext
{
    public LegacyAppearanceMigrationWidthTests() => BunitTestHelper.RegisterServices(this);

    private OmniAppearanceState State => Services.GetRequiredService<OmniAppearanceState>();

    private LegacyAppearanceMigration Migration => Services.GetRequiredService<LegacyAppearanceMigration>();

    [Fact]
    public async Task AUserWithNoAppearance_GetsFullWidth()
    {
        await Migration.RunAsync(Xunit.TestContext.Current.CancellationToken);

        Assert.True(State.FullWidth);
    }

    [Fact]
    public async Task TheSignedOutDefault_IsFullWidth()
    {
        await Migration.ApplyDefaultAsync();

        Assert.True(State.FullWidth);
    }

}
