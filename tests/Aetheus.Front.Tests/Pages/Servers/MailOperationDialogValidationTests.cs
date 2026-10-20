// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Aetheus.Front.Resources;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>Rendered dialog tests use the application's French resources to verify localized
/// validation, optional certificate contact and domain-completed account names.</summary>
public sealed class MailOperationDialogValidationTests : BunitContext
{
    private static readonly MailDomainDto ExampleCom = new() { Id = 1, Name = "example.com" };
    private static readonly MailDomainDto ExampleOrg = new() { Id = 2, Name = "example.org" };

    private readonly CultureInfo _previousUiCulture = CultureInfo.CurrentUICulture;
    private readonly CultureInfo _previousCulture = CultureInfo.CurrentCulture;

    public MailOperationDialogValidationTests()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        Services.AddSingleton(RealLocalizer());
    }

    protected override void Dispose(bool disposing)
    {
        CultureInfo.CurrentUICulture = _previousUiCulture;
        CultureInfo.CurrentCulture = _previousCulture;
        base.Dispose(disposing);
    }

    /// <summary>The localizer the application registers (<c>AddLocalization</c>), reading the real resource files.</summary>
    internal static IStringLocalizer<AppStrings> RealLocalizer() =>
        new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider()
            .GetRequiredService<IStringLocalizer<AppStrings>>();

    private ImmediateDialogService DialogService => (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();

    private IRenderedComponent<MailOperationDialog> RenderDialog(MailDialogMode mode, MailDialogModel model, params MailDomainDto[] domains)
    {
        var cut = Render<MailOperationDialog>(p => p
            .Add(x => x.Mode, mode)
            .Add(x => x.Model, model)
            .Add(x => x.Domains, domains));
        BunitTestHelper.Interactive(this);
        return cut;
    }

    [Fact]
    public void R1_5_AnInvalidAccount_ShowsFrenchSentences_NeverTheResourceKeys()
    {
        var cut = RenderDialog(MailDialogMode.AddAccount,
            new MailDialogModel { Email = "con tact", Password = "court" }, ExampleCom);

        cut.Find("form").Submit();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Indiquez un nom comme contact, ou une adresse complète comme contact@exemple.fr.", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("Le mot de passe doit faire de 12 à 100 caractères, sans retour à la ligne ni tabulation.", cut.Markup, StringComparison.Ordinal);
        });
        Assert.DoesNotContain("MailAccountAddressInvalid", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("MailPasswordInvalid", cut.Markup, StringComparison.Ordinal);
        Assert.Equal(0, DialogService.CloseCount);
    }

    [Fact]
    public void R1_5_ThePasswordRule_IsWrittenOutWhereTheAccountPasswordIsEntered()
    {
        var cut = RenderDialog(MailDialogMode.AddAccount, new MailDialogModel(), ExampleCom);

        Assert.Contains("De 12 à 100 caractères. Lettres, chiffres, espaces et symboles sont tous acceptés ; "
            + "les retours à la ligne et les tabulations ne le sont pas.", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void R1_5_AnEmptyAcmeAddressStaysValid_ButAWrongOneReadsInFrench()
    {
        var cut = RenderDialog(MailDialogMode.RequestCertificate, new MailDialogModel { Hostname = "mx.example.com", Email = "nope" });

        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Contains("Indiquez une adresse e-mail valide.", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("MailEmailInvalid", cut.Markup, StringComparison.Ordinal);
        Assert.Equal(0, DialogService.CloseCount);
    }

    [Fact]
    public void R1_6_ABlankLetsEncryptAddress_IsAccepted_AndTheFieldSaysWhatItIsFor()
    {
        var cut = RenderDialog(MailDialogMode.RequestCertificate, new MailDialogModel { Hostname = "mx.example.com", Email = string.Empty });

        Assert.Contains("Facultatif. Elle ne reçoit que les avis que Let's Encrypt envoie avant l'expiration du certificat.",
            System.Net.WebUtility.HtmlDecode(cut.Markup), StringComparison.Ordinal);
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Equal(1, DialogService.CloseCount));
        var result = Assert.IsType<MailDialogModel>(DialogService.LastCloseResult);
        Assert.Null(result.AcmeEmail);
    }

    [Fact]
    public void R1_15_ANameAlone_IsCompletedWithTheDomainPickedNextToTheField()
    {
        var cut = RenderDialog(MailDialogMode.AddAccount, new MailDialogModel(), ExampleCom, ExampleOrg);

        var picker = Assert.Single(cut.FindComponents<OmniDropDown<string>>());
        Assert.Equal(["@example.com", "@example.org"], picker.Instance.Options.Select(option => option.Text));
        Assert.Equal("example.com", picker.Instance.Value);
        cut.Find(".mail-account-domain select, select.mail-account-domain").Change("1"); // OE writes the option index as the value: 1 is example.org.
        cut.Find("#oe-formfield-pages-servers-serverdetailsections-mailoperationdialog-3").Input("no-reply");
        cut.Find("#oe-formfield-pages-servers-serverdetailsections-mailoperationdialog-4").Input("douze-caracteres");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Equal(1, DialogService.CloseCount));
        Assert.Equal("no-reply@example.org", Assert.IsType<MailDialogModel>(DialogService.LastCloseResult).Email);
    }

    [Fact]
    public void R1_15_AFullAddress_IsKeptAsTyped()
    {
        var cut = RenderDialog(MailDialogMode.AddAccount,
            new MailDialogModel { Email = "boss@example.com", Password = "douze-caracteres" }, ExampleCom, ExampleOrg);
        cut.Find(".mail-account-domain select, select.mail-account-domain").Change("1"); // OE writes the option index as the value: 1 is example.org.

        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Equal(1, DialogService.CloseCount));
        Assert.Equal("boss@example.com", Assert.IsType<MailDialogModel>(DialogService.LastCloseResult).Email);
    }

    [Fact]
    public void R1_15_WithASingleDomain_TheDomainIsShownAfterTheField_WithoutAPicker()
    {
        var cut = RenderDialog(MailDialogMode.AddAccount, new MailDialogModel { Email = "contact", Password = "douze-caracteres" }, ExampleCom);

        Assert.Empty(cut.FindComponents<OmniDropDown<string>>());
        Assert.Equal("@example.com", cut.Find(".mail-account-domain").TextContent.Trim());
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Equal(1, DialogService.CloseCount));
        Assert.Equal("contact@example.com", Assert.IsType<MailDialogModel>(DialogService.LastCloseResult).Email);
    }

    [Fact]
    public void R1_5_ATeamspeakConfirmationMismatch_ReadsInFrench()
    {
        var cut = Render<TeamspeakOperationDialog>(p => p
            .Add(x => x.Mode, TeamspeakDialogMode.SnapshotDeploy)
            .Add(x => x.ServerName, "ts-srv")
            .Add(x => x.Model, new TeamspeakDialogModel { SnapshotBlob = "blob", Confirmation = "autre" }));
        BunitTestHelper.Interactive(this);

        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Contains("Le texte saisi ne correspond pas au nom du serveur.", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("SnapshotConfirmationMismatch", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void R1_5_AModelValidatedWithoutItsLocalizer_FailsLoudly_InsteadOfAnsweringKeys()
    {
        var model = new MailDialogModel { Mode = MailDialogMode.AddDomain, Domain = string.Empty };

        Assert.Throws<InvalidOperationException>(() => model.Validate(new ValidationContext(model)).ToList());
    }
}
