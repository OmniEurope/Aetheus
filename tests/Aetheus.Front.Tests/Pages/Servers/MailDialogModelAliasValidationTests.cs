// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>Adding a mail alias needs a domain, a source and a destination that are both real addresses; each
/// refusal names the field the form highlights.</summary>
public sealed class MailDialogModelAliasValidationTests
{
    private static List<ValidationResult> Validate(MailDialogModel model)
    {
        model.L = new BunitTestHelper.StubLocalizer();
        return [.. model.Validate(new ValidationContext(model))];
    }

    private static MailDialogModel Alias(string source, string destination, int domainId = 1) => new()
    {
        Mode = MailDialogMode.AddAlias,
        Source = source,
        Destination = destination,
        DomainId = domainId
    };

    [Fact]
    public void AValidAlias_HasNoError() =>
        Assert.Empty(Validate(Alias("contact@example.org", "boss@example.com")));

    [Theory]
    [InlineData("", "boss@example.com", 1)]
    [InlineData("contact@example.org", "  ", 1)]
    [InlineData("contact@example.org", "boss@example.com", 0)]
    public void AnAliasMissingAPart_IsRequiredOnAllThreeFields(string source, string destination, int domainId)
    {
        var error = Assert.Single(Validate(Alias(source, destination, domainId)));

        Assert.Equal("Required", error.ErrorMessage);
        Assert.Equal(["Source", "Destination", "DomainId"], error.MemberNames);
    }

    [Fact]
    public void AnInvalidSource_IsFlaggedOnSourceOnly()
    {
        var error = Assert.Single(Validate(Alias("not an address", "boss@example.com")));

        Assert.Equal("MailEmailInvalid", error.ErrorMessage);
        Assert.Equal(["Source"], error.MemberNames);
    }

    [Fact]
    public void AnInvalidDestination_IsFlaggedOnDestinationOnly()
    {
        var error = Assert.Single(Validate(Alias("contact@example.org", "nobody")));

        Assert.Equal("MailEmailInvalid", error.ErrorMessage);
        Assert.Equal(["Destination"], error.MemberNames);
    }

    [Fact]
    public void BothAddressesInvalid_ReportsBothFields()
    {
        var errors = Validate(Alias("one", "two"));

        Assert.Equal(2, errors.Count);
        Assert.Equal(["Source", "Destination"], errors.Select(e => Assert.Single(e.MemberNames)));
    }

    [Fact]
    public void AnotherMode_DoesNotValidateTheAliasFields() =>
        Assert.DoesNotContain(
            Validate(new MailDialogModel { Mode = MailDialogMode.RotateDkim, NewSelector = "next", Source = "bad" }),
            error => error.MemberNames.Contains("Source"));
}
