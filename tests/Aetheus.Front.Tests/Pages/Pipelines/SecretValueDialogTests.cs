// SPDX-License-Identifier: EUPL-1.2
using Bunit;

namespace Aetheus.Front.Tests.Pages;

public class SecretValueDialogTests : BunitContext
{
    public SecretValueDialogTests()
    {
        BunitTestHelper.RegisterServices(this);
    }

    [Fact]
    public void Renders_PasswordField()
    {
        var cut = Render<SecretValueDialog>(p => p.Add(x => x.SecretKey, "DB_PASSWORD"));
        Assert.Contains("NewValue", cut.Markup);
    }

    [Fact]
    public void SaveButton_Disabled_WhenEmpty()
    {
        var cut = Render<SecretValueDialog>(p => p.Add(x => x.SecretKey, "API_KEY"));
        var buttons = cut.FindAll("button");
        var saveBtn = buttons.FirstOrDefault(b => b.TextContent.Contains("Save"));
        Assert.NotNull(saveBtn);
        Assert.True(saveBtn.HasAttribute("disabled"));
    }

    /// <summary>Rotating or updating a secret offers Generate again, a text button with a menu; its
    /// click applies the key's recipe (DB_PASSWORD: 24 bytes as 48 hex characters) and fills the
    /// field, which enables Save.</summary>
    [Fact]
    public async Task Generate_FillsTheValueWithTheKeysRecipe_AndOffersOtherWays()
    {
        var cut = Render<SecretValueDialog>(p => p.Add(x => x.SecretKey, "DB_PASSWORD"));

        var split = cut.Find(".omni-split-button");
        Assert.Contains("Generate", split.TextContent);
        await cut.Find(".omni-split-button__main").ClickAsync(new());

        var value = cut.Find("#secret-value-input").GetAttribute("value");
        Assert.Matches("^[0-9a-f]{48}$", value);
        Assert.False(cut.FindAll("button").Single(b => b.TextContent.Contains("Save")).HasAttribute("disabled"));

        await cut.Find(".omni-split-button__toggle").ClickAsync(new());
        var choices = cut.FindAll(".omni-split-button__menu [role='menuitem'], .omni-split-button__menu button");
        Assert.Contains(choices, item => item.TextContent.Contains("SecretGenerateCharacters32"));
        Assert.Contains(choices, item => item.TextContent.Contains("SecretGenerateCharacters64"));
        Assert.Contains(choices, item => item.TextContent.Contains("SecretGenerateHex32Bytes"));
    }

    [Fact]
    public void Renders_CancelButton()
    {
        var cut = Render<SecretValueDialog>(p => p.Add(x => x.SecretKey, "KEY"));
        var buttons = cut.FindAll("button");
        var cancelBtn = buttons.FirstOrDefault(b => b.TextContent.Contains("GoBack"));
        Assert.NotNull(cancelBtn);
    }

    [Fact]
    public void Renders_WithSecretKey()
    {
        var cut = Render<SecretValueDialog>(p => p.Add(x => x.SecretKey, "MY_SECRET"));
        // The dialog renders the masked password input and its label.
        Assert.Contains("NewValue", cut.Markup);
        Assert.NotNull(cut.Find("#secret-value-input"));
    }

    [Fact]
    public void SecretInput_IsMasked_AndValueNeverRenderedAsPlaintext()
    {
        const string secret = "sup3r-s3cret-value";
        var cut = Render<SecretValueDialog>(p => p.Add(x => x.SecretKey, "MY_SECRET"));

        var input = cut.Find("#secret-value-input");
        // A masked text field, not a password field, so no password manager offers to save it or
        // fills the login around it (OE OmniPassword IgnorePasswordManagers). The mask is the
        // stylesheet class of the unrevealed input.
        Assert.Equal("text", input.GetAttribute("type"));
        Assert.Equal("off", input.GetAttribute("autocomplete"));
        Assert.Equal("true", input.GetAttribute("data-lpignore"));
        Assert.Contains("omni-password__input--masked", input.ClassList);

        // Type a secret, then confirm it never leaks into any visible text node (label, span, ...).
        // A password input masks its own value attribute in the browser, so the only safe surface is
        // the masked input itself - the secret must not appear as rendered text anywhere.
        input.Input(secret);
        foreach (var node in cut.FindAll("*"))
            Assert.DoesNotContain(secret, node.TextContent);
    }
}
