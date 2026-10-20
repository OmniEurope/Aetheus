// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Notifications;
using Aetheus.Front.Components.Projects.ProjectDetailSections;
using Aetheus.Front.Layout;

namespace Aetheus.Front.Tests.Shared;

/// <summary>Covers pure formatting helpers for identifiers, requirement warnings and notification
/// details.</summary>
public sealed class Recette20260924Tests
{
    [Theory]
    [InlineData("c-384f63d4a8c182e07de2e9444c25e0a5a9a28e8e", "c-384f63d4")]
    [InlineData("c-01299a281983331e947da3044db2128d9c348641-c338fb59646e", "c-01299a28")]
    [InlineData("vc-384f63d4a8c182e07de2e9444c25e0a5a9a28e8e", "vc-384f63d4")]
    [InlineData("384f63d4a8c182e07de2e9444c25e0a5a9a28e8e", "384f63d4")]
    [InlineData("1.1.57", "1.1.57")]
    [InlineData("c-384f63", "c-384f63")]
    [InlineData("", "")]
    public void ShortId_KeepsPrefixAndEightFirst(string value, string expected) =>
        Assert.Equal(expected, ShortId.Shorten(value));

    [Theory]
    [InlineData("light", OmniAppearance.Light)]
    [InlineData("system", OmniAppearance.System)]
    [InlineData("dark", OmniAppearance.Dark)]
    [InlineData(null, OmniAppearance.Dark)]
    [InlineData("bogus", OmniAppearance.Dark)]
    public void ThemePreference_UnknownReadsAsDark(string? stored, OmniAppearance expected) =>
        Assert.Equal(expected, Aetheus.Front.Components.Settings.SiteAppearance.ParseAppearance(stored));

    [Fact]
    public void UnmetSignature_IgnoresOrderAndChangesWithTheList()
    {
        UnmetRequirementDto lib = new() { Kind = "library", Name = "app-config" };
        UnmetRequirementDto vault = new() { Kind = "vault", Name = "app-secrets" };
        Assert.Equal(ProjectOverviewSection.UnmetSignature([lib, vault]), ProjectOverviewSection.UnmetSignature([vault, lib]));
        Assert.NotEqual(ProjectOverviewSection.UnmetSignature([lib]), ProjectOverviewSection.UnmetSignature([lib, vault]));
    }

    [Fact]
    public void PayloadElements_ListsTopLevelPropertiesInOrder()
    {
        var elements = InboxNotificationDialog.PayloadElements("""{"project":"Aetheus","run":42,"extra":null}""");
        Assert.Equal([("project", "Aetheus"), ("run", "42"), ("extra", "")], elements);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    public void PayloadElements_ShowsANonObjectPayloadAsOneRawElement(string payload) =>
        Assert.Single(InboxNotificationDialog.PayloadElements(payload));

    [Fact]
    public void PayloadElements_EmptyPayloadHasNoElement() =>
        Assert.Empty(InboxNotificationDialog.PayloadElements("  "));
}
